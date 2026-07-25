using System.ComponentModel;
using System.Windows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop;

public partial class SettingsWindow : Window
{
    private readonly StartupRegistrationService _startupRegistration = new();
    private readonly ModelConnectionSettingsStore _modelSettingsStore = new();
    private readonly QuickAccessShortcut _initialShortcut;
    private readonly bool _wasQuickAccessAvailable;
    private ModelConnectionSettings? _existingModelSettings;
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;

    internal SettingsWindow(bool isQuickAccessAvailable, QuickAccessShortcut currentShortcut)
    {
        _wasQuickAccessAvailable = isQuickAccessAvailable;
        _initialShortcut = currentShortcut;
        InitializeComponent();
        ShortcutComboBox.ItemsSource = QuickAccessShortcutCatalog.All;
        ShortcutComboBox.SelectedValue = currentShortcut.Id;
        if (!isQuickAccessAvailable)
        {
            ShortcutStatusText.Text = "현재 단축키를 사용할 수 없어요. 다른 단축키를 선택해 주세요.";
            ShortcutStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }

        Loaded += SettingsWindow_Loaded;
        Closing += SettingsWindow_Closing;
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            StartWithWindowsCheckBox.IsChecked = _startupRegistration.IsEnabled();
        }
        catch (Exception)
        {
            SetStatus("Windows 자동 실행 설정을 불러오지 못했어요.", "DangerBrush");
        }

        try
        {
            _existingModelSettings = _modelSettingsStore.Load();
            if (_existingModelSettings is not null)
            {
                BaseUrlTextBox.Text = _existingModelSettings.BaseUrl;
                ModelTextBox.Text = _existingModelSettings.Model;
            }
            else
            {
                SavedKeyHint.Text = "API 키는 Windows 자격 증명 관리자에 안전하게 저장됩니다.";
            }
        }
        catch (Exception)
        {
            SetStatus("저장된 모델 연결 정보를 불러오지 못했어요.", "DangerBrush");
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation("설정을 저장하고 있어요…", out var cancellationToken)) return;
        var failures = new List<string>();
        try
        {
            if (ShortcutComboBox.SelectedItem is not QuickAccessShortcut shortcut)
            {
                failures.Add("빠른 호출 단축키를 선택해 주세요.");
            }
            else if ((!_wasQuickAccessAvailable || !StringComparer.Ordinal.Equals(shortcut.Id, _initialShortcut.Id)) &&
                     Owner is MainWindow shortcutOwner)
            {
                try
                {
                    if (!shortcutOwner.ApplyQuickAccessShortcut(shortcut))
                        failures.Add($"{shortcut.DisplayName}은 다른 프로그램에서 사용 중이에요.");
                }
                catch (Exception)
                {
                    failures.Add("빠른 호출 단축키 설정을 저장하지 못했어요.");
                }
            }

            try
            {
                _startupRegistration.SetEnabled(StartWithWindowsCheckBox.IsChecked == true);
            }
            catch (Exception)
            {
                failures.Add("Windows 자동 실행 설정을 저장하지 못했어요.");
            }

            if (HasModelConnectionInput())
            {
                ModelConnectionSettings? modelSettings = null;
                try
                {
                    var candidate = ReadModelSettings();
                    _modelSettingsStore.Save(candidate);
                    modelSettings = candidate;
                }
                catch (ModelSettingsValidationException exception)
                {
                    failures.Add(exception.Message);
                }
                catch (Exception)
                {
                    failures.Add("모델 연결 정보를 안전하게 저장하지 못했어요.");
                }

                if (modelSettings is not null && Owner is MainWindow mainWindow)
                {
                    try
                    {
                        await mainWindow.ApplyProviderAsync(modelSettings, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        failures.Add("모델 연결 적용을 중단했어요. 저장된 정보는 다시 연결할 때 적용됩니다.");
                    }
                    catch (Exception)
                    {
                        failures.Add("연결 정보는 저장했지만 현재 도우미에 적용하지 못했어요. 다시 연결하면 자동 적용됩니다.");
                    }
                }
            }

            if (failures.Count == 0)
            {
                DialogResult = true;
                return;
            }

            SetStatus(string.Join(Environment.NewLine, failures.Distinct()), "DangerBrush");
        }
        finally
        {
            EndOperation();
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation("연결을 확인하고 있어요…", out var cancellationToken)) return;
        try
        {
            var result = Owner is MainWindow mainWindow
                ? await mainWindow.TestProviderAsync(ReadModelSettings(), cancellationToken)
                : new ProviderTestResult(false, "연결 창을 다시 열어 주세요.");
            SetStatus(result.Message, result.Success ? "SuccessBrush" : "DangerBrush");
        }
        catch (ModelSettingsValidationException exception)
        {
            SetStatus(exception.Message, "DangerBrush");
        }
        catch (OperationCanceledException)
        {
            SetStatus("연결 테스트를 중단했어요.", "TextSecondaryBrush");
        }
        catch (Exception)
        {
            SetStatus("연결하지 못했습니다. 입력값과 네트워크를 확인해 주세요.", "DangerBrush");
        }
        finally
        {
            EndOperation();
        }
    }

    private bool HasModelConnectionInput() => ModelConnectionInputPolicy.HasAnyInput(
        BaseUrlTextBox.Text,
        ModelTextBox.Text,
        ApiKeyPasswordBox.Password,
        _existingModelSettings is not null);

    private ModelConnectionSettings ReadModelSettings()
    {
        var baseUrl = BaseUrlTextBox.Text.Trim().TrimEnd('/');
        var model = ModelTextBox.Text.Trim();
        var apiKey = string.IsNullOrEmpty(ApiKeyPasswordBox.Password)
            ? _existingModelSettings?.ApiKey
            : ApiKeyPasswordBox.Password;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ModelSettingsValidationException("Base URL을 http:// 또는 https://로 시작하는 주소로 입력해 주세요.");
        if (string.IsNullOrWhiteSpace(model))
            throw new ModelSettingsValidationException("사용할 모델 이름을 입력해 주세요.");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ModelSettingsValidationException("API Key를 입력해 주세요.");
        return new ModelConnectionSettings(baseUrl, apiKey, model);
    }

    private bool TryBeginOperation(string message, out CancellationToken cancellationToken)
    {
        cancellationToken = default;
        if (_isBusy) return false;
        _isBusy = true;
        _operationCancellation = new CancellationTokenSource();
        cancellationToken = _operationCancellation.Token;
        SetControlsEnabled(false);
        CancelButton.IsEnabled = true;
        CancelButton.Content = "중단";
        SetStatus(message, "TextSecondaryBrush");
        return true;
    }

    private void EndOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _isBusy = false;
        if (!IsLoaded) return;
        SetControlsEnabled(true);
        CancelButton.Content = "취소";
    }

    private void SetControlsEnabled(bool enabled)
    {
        StartWithWindowsCheckBox.IsEnabled = enabled;
        BaseUrlTextBox.IsEnabled = enabled;
        ModelTextBox.IsEnabled = enabled;
        ApiKeyPasswordBox.IsEnabled = enabled;
        ShortcutComboBox.IsEnabled = enabled;
        TestConnectionButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
    }

    private void SetStatus(string message, string brushResource)
    {
        SettingsStatusText.Foreground = (System.Windows.Media.Brush)FindResource(brushResource);
        SettingsStatusText.Text = message;
    }

    private void BaseUrlTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var showWarning = ModelConnectionInputPolicy.ShouldWarnAboutPlaintextHttp(BaseUrlTextBox.Text);
        ConnectionSecurityWarningText.Visibility = showWarning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            _operationCancellation?.Cancel();
            return;
        }
        Close();
    }

    private void SettingsWindow_Closing(object? sender, CancelEventArgs e) => _operationCancellation?.Cancel();

    private sealed class ModelSettingsValidationException(string message) : Exception(message);
}
