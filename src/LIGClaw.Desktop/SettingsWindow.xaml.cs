using System.Windows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop;

public partial class SettingsWindow : Window
{
    private readonly StartupRegistrationService _startupRegistration = new();
    private readonly ModelConnectionSettingsStore _modelSettingsStore = new();
    private ModelConnectionSettings? _existingModelSettings;

    public SettingsWindow(bool isQuickAccessAvailable)
    {
        InitializeComponent();
        if (!isQuickAccessAvailable)
        {
            ShortcutStatusText.Text = "다른 프로그램이 이 단축키를 사용 중이에요.";
            ShortcutStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }

        Loaded += SettingsWindow_Loaded;
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            StartWithWindowsCheckBox.IsChecked = _startupRegistration.IsEnabled();
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
            SettingsStatusText.Text = "시작 설정을 불러오지 못했어요.";
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var modelSettings = ReadModelSettings();
            _startupRegistration.SetEnabled(StartWithWindowsCheckBox.IsChecked == true);
            _modelSettingsStore.Save(modelSettings);
            if (Owner is MainWindow mainWindow) await mainWindow.ApplyProviderAsync(modelSettings);
            DialogResult = true;
        }
        catch (Exception)
        {
            SettingsStatusText.Text = "설정을 저장하지 못했어요. 잠시 후 다시 시도해 주세요.";
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SettingsStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            SettingsStatusText.Text = "연결을 확인하고 있어요…";
            var result = Owner is MainWindow mainWindow
                ? await mainWindow.TestProviderAsync(ReadModelSettings())
                : new ProviderTestResult(false, "연결 창을 다시 열어 주세요.");
            SettingsStatusText.Foreground = (System.Windows.Media.Brush)FindResource(result.Success ? "SuccessBrush" : "DangerBrush");
            SettingsStatusText.Text = result.Message;
        }
        catch (Exception)
        {
            SettingsStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            SettingsStatusText.Text = "연결하지 못했습니다. 입력값과 네트워크를 확인해 주세요.";
        }
    }

    private ModelConnectionSettings ReadModelSettings()
    {
        var baseUrl = BaseUrlTextBox.Text.Trim().TrimEnd('/');
        var model = ModelTextBox.Text.Trim();
        var apiKey = string.IsNullOrEmpty(ApiKeyPasswordBox.Password)
            ? _existingModelSettings?.ApiKey
            : ApiKeyPasswordBox.Password;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(model) ||
            string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Base URL, API Key, Model을 모두 확인해 주세요.");
        }
        return new ModelConnectionSettings(baseUrl, apiKey, model);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
