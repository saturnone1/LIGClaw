using System.Windows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop;

public partial class SettingsPage : System.Windows.Controls.UserControl
{
    private readonly MainWindow _host;
    private readonly StartupRegistrationService _startupRegistration = new();
    private readonly ModelProfileSectionController _modelProfiles = new(new ModelConnectionSettingsStore());
    private readonly McpSettingsSectionController _mcpSettings;
    private readonly SemanticMemorySettingsSectionController _semanticMemorySettings = new(new SemanticMemorySettingsStore());
    private readonly WebSearchSettingsSectionController _webSearchSettings = new(new WebSearchSettingsStore());
    private QuickAccessShortcut _initialShortcut;
    private bool _wasQuickAccessAvailable;
    private ModelConnectionSettings? _existingModelSettings;
    private SemanticMemorySettings? _existingSemanticMemorySettings;
    private ModelConnectionSettings? _lastSuccessfulConnectionTest;
    private readonly SettingsOperationGuard _operationGuard = new();
    private bool _initialized;
    private bool _loadingModelProfile;

    internal SettingsPage(MainWindow host)
    {
        _host = host;
        _mcpSettings = new McpSettingsSectionController(new McpConnectionSettingsStore(), host.ApplyMcpAsync);
        _wasQuickAccessAvailable = host.IsQuickAccessAvailable;
        _initialShortcut = host.CurrentQuickAccessShortcut;
        InitializeComponent();
        ShortcutComboBox.ItemsSource = QuickAccessShortcutCatalog.All;
        ShortcutComboBox.SelectedValue = _initialShortcut.Id;
        if (!_wasQuickAccessAvailable)
        {
            ShortcutStatusText.Text = "현재 단축키를 사용할 수 없어요. 다른 단축키를 선택해 주세요.";
            ShortcutStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
        Loaded += SettingsPage_Loaded;
        Unloaded += (_, _) => _operationGuard.Cancel();
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            await RefreshCapabilityGrantsAsync();
            SetControlsEnabled(!_operationGuard.IsBusy);
            CancelButton.IsEnabled = true;
            CancelButton.Content = _operationGuard.IsBusy ? "중단" : "대화로 돌아가기";
            return;
        }
        _initialized = true;
        try
        {
            StartWithWindowsCheckBox.IsChecked = await _startupRegistration.IsEnabledAsync();
        }
        catch (Exception)
        {
            SetStatus("Windows 자동 실행 설정을 불러오지 못했어요.", "DangerBrush");
        }

        await RefreshCapabilityGrantsAsync();

        try
        {
            var mcp = _mcpSettings.Load();
            McpEnabledCheckBox.IsChecked = mcp?.Enabled ?? false;
            McpDisplayNameTextBox.Text = mcp?.DisplayName ?? "지식 검색";
            McpUrlTextBox.Text = mcp?.Url ?? string.Empty;
            McpAllowedToolsTextBox.Text = string.Join(Environment.NewLine, mcp?.AllowedTools ?? []);
            McpSampleRagCheckBox.IsChecked = mcp?.Transport == "stdio";
        }
        catch (Exception)
        {
            SetStatus("저장된 MCP 연결 정보를 불러오지 못했어요.", "DangerBrush");
        }

        try
        {
            var state = _modelProfiles.Load();
            ModelProfileComboBox.ItemsSource = state.Profiles;
            _existingModelSettings = state.Current;
            if (_existingModelSettings is not null)
            {
                LoadModelProfile(_existingModelSettings);
                ModelProfileComboBox.SelectedValue = _existingModelSettings.ProfileId;
                FallbackProfileIdsTextBox.Text = string.Join(Environment.NewLine,
                    state.Routing?.Fallbacks.Select(profile => profile.ProfileId) ?? []);
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

        try
        {
            _existingSemanticMemorySettings = _semanticMemorySettings.Load();
            SemanticMemoryEnabledCheckBox.IsChecked = _existingSemanticMemorySettings.Enabled;
            SemanticMemoryBaseUrlTextBox.Text = _existingSemanticMemorySettings.BaseUrl;
            SemanticMemoryModelTextBox.Text = _existingSemanticMemorySettings.Model;
        }
        catch (Exception)
        {
            SetStatus("의미 기억 설정을 불러오지 못했어요.", "DangerBrush");
        }

        try
        {
            WebSearchUrlTemplateTextBox.Text = _webSearchSettings.Load()?.UrlTemplate ?? string.Empty;
        }
        catch (Exception)
        {
            SetStatus("Web 검색 설정을 불러오지 못했어요.", "DangerBrush");
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
            else if ((!_wasQuickAccessAvailable || !StringComparer.Ordinal.Equals(shortcut.Id, _initialShortcut.Id)))
            {
                try
                {
                    if (!_host.ApplyQuickAccessShortcut(shortcut))
                        failures.Add($"{shortcut.DisplayName}은 다른 프로그램에서 사용 중이에요.");
                    else
                    {
                        _initialShortcut = shortcut;
                        _wasQuickAccessAvailable = true;
                    }
                }
                catch (Exception)
                {
                    failures.Add("빠른 호출 단축키 설정을 저장하지 못했어요.");
                }
            }

            try
            {
                await _startupRegistration.SetEnabledAsync(StartWithWindowsCheckBox.IsChecked == true);
            }
            catch (Exception)
            {
                failures.Add("Windows 자동 실행 설정을 저장하지 못했어요.");
            }

            try
            {
                var semanticSettings = _semanticMemorySettings.Save(
                    new SemanticMemorySettingsInput(
                        SemanticMemoryEnabledCheckBox.IsChecked == true,
                        SemanticMemoryBaseUrlTextBox.Text,
                        SemanticMemoryModelTextBox.Text,
                        SemanticMemoryApiKeyPasswordBox.Password),
                    _existingSemanticMemorySettings);
                _existingSemanticMemorySettings = semanticSettings;
            }
            catch (SettingsSectionValidationException exception)
            {
                failures.Add(exception.Message);
            }
            catch (Exception)
            {
                failures.Add("의미 기억 설정을 저장하지 못했어요.");
            }

            try
            {
                _webSearchSettings.Save(WebSearchUrlTemplateTextBox.Text);
            }
            catch (SettingsSectionValidationException exception)
            {
                failures.Add(exception.Message);
            }
            catch (Exception)
            {
                failures.Add("Web 검색 설정을 저장하지 못했어요.");
            }

            if (HasModelConnectionInput())
            {
                ModelConnectionSettings? modelSettings = null;
                try
                {
                    var candidate = ReadModelSettings();
                    var result = _modelProfiles.Save(
                        candidate,
                        FallbackProfileIdsTextBox.Text,
                        _existingModelSettings,
                        _lastSuccessfulConnectionTest);
                    _existingModelSettings = result.Settings;
                    modelSettings = result.RequiresApply ? result.Settings : null;
                    ModelProfileComboBox.ItemsSource = result.Profiles;
                    ModelProfileComboBox.SelectedValue = result.Settings.ProfileId;
                }
                catch (SettingsSectionValidationException exception)
                {
                    failures.Add(exception.Message);
                }
                catch (Exception)
                {
                    failures.Add("모델 연결 정보를 안전하게 저장하지 못했어요.");
                }

                if (modelSettings is not null)
                {
                    try
                    {
                        await _host.ApplyProviderAsync(modelSettings, cancellationToken);
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


            try
            {
                var result = await _mcpSettings.SaveAsync(
                    new McpSettingsInput(
                        McpDisplayNameTextBox.Text,
                        McpUrlTextBox.Text,
                        McpEnabledCheckBox.IsChecked == true,
                        McpAllowedToolsTextBox.Text,
                        McpTokenPasswordBox.Password,
                        McpSampleRagCheckBox.IsChecked == true),
                    cancellationToken);
                if (result is not null)
                {
                    McpStatusText.Text = result.Status.IsConnected
                        ? $"발견 {result.Status.ToolCount}개: {string.Join(", ", result.Status.Tools)}" +
                          (result.Status.AllowedTools.Count == 0 ? " · 호출 허용 도구 없음" : $" · 호출 허용 {result.Status.AllowedTools.Count}개")
                        : result.Settings.Enabled ? "연결 실패 · 주소와 서버 상태를 확인해 주세요." : "사용 안 함";
                    McpStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
                        result.Status.IsConnected ? "SuccessBrush" : result.Settings.Enabled ? "DangerBrush" : "TextSecondaryBrush");
                    if (result.Settings.Enabled && !result.Status.IsConnected)
                        failures.Add("MCP 서버에 연결하지 못했어요. 모델 연결과 다른 기능은 계속 사용할 수 있습니다.");
                }
            }
            catch (SettingsSectionValidationException exception)
            {
                failures.Add(exception.Message);
            }
            catch (OperationCanceledException)
            {
                failures.Add("MCP 연결 적용을 중단했어요.");
            }
            catch (Exception)
            {
                failures.Add("MCP 연결 정보를 저장하거나 적용하지 못했어요.");
            }

            SetStatus(
                failures.Count == 0 ? "설정을 저장했어요." : string.Join(Environment.NewLine, failures.Distinct()),
                failures.Count == 0 ? "SuccessBrush" : "DangerBrush");
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
            var candidate = ReadModelSettings();
            var result = await _host.TestProviderAsync(candidate, cancellationToken);
            _lastSuccessfulConnectionTest = result.Success ? candidate : null;
            SetStatus(result.Message, result.Success ? "SuccessBrush" : "DangerBrush");
        }
        catch (SettingsSectionValidationException exception)
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

    private async void SaveDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "LIGClaw 진단 번들 저장",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"LIGClaw-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!TryBeginOperation("진단 번들을 만들고 있어요…", out var cancellationToken)) return;
        try
        {
            await _host.CreateDiagnosticBundleAsync(dialog.FileName, cancellationToken);
            SetStatus("민감정보를 제거한 진단 번들을 저장했어요.", "SuccessBrush");
        }
        catch (OperationCanceledException)
        {
            SetStatus("진단 번들 저장을 중단했어요.", "TextSecondaryBrush");
        }
        catch (Exception)
        {
            SetStatus("진단 번들을 저장하지 못했어요.", "DangerBrush");
        }
        finally
        {
            EndOperation();
        }
    }

    private async void BackupData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "LIGClaw 데이터 백업", Filter = "LIGClaw 데이터 (*.db)|*.db", DefaultExt = ".db", AddExtension = true, FileName = $"LIGClaw-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || !TryBeginOperation("데이터를 백업하고 있어요…", out var cancellationToken)) return;
        try { await _host.CreateDataBackupAsync(dialog.FileName, cancellationToken); SetStatus("검증된 데이터 백업을 저장했어요.", "SuccessBrush"); }
        catch (OperationCanceledException) { SetStatus("데이터 백업을 중단했어요.", "TextSecondaryBrush"); }
        catch { SetStatus("데이터를 백업하지 못했어요.", "DangerBrush"); }
        finally { EndOperation(); }
    }

    private async void RestoreData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "복원할 LIGClaw 데이터 선택", Filter = "LIGClaw 데이터 (*.db)|*.db", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (System.Windows.MessageBox.Show(Window.GetWindow(this), "선택한 백업을 검증한 뒤 다음 앱 시작 시 현재 데이터를 교체합니다. 현재 데이터는 복원 전 백업으로 보존됩니다. 계속할까요?", "데이터 복원", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!TryBeginOperation("백업을 검증하고 있어요…", out var cancellationToken)) return;
        try { await _host.StageDataRestoreAsync(dialog.FileName, cancellationToken); SetStatus("복원을 준비했습니다. LIGClaw를 완전히 종료한 뒤 다시 시작하면 적용됩니다.", "SuccessBrush"); }
        catch (OperationCanceledException) { SetStatus("데이터 복원 준비를 중단했어요.", "TextSecondaryBrush"); }
        catch { SetStatus("유효한 LIGClaw 백업이 아니어서 복원을 준비하지 못했어요.", "DangerBrush"); }
        finally { EndOperation(); }
    }

    private async void CleanupHistory_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(Window.GetWindow(this), "1년이 지난 승인·실행·완료 작업 이력을 정리합니다. 대화와 기억은 삭제하지 않습니다. 계속할까요?", "운영 이력 정리", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!TryBeginOperation("오래된 운영 이력을 정리하고 있어요…", out var cancellationToken)) return;
        try { var removed = await _host.CleanupOperationalHistoryAsync(DateTimeOffset.UtcNow.AddDays(-365), cancellationToken); SetStatus($"오래된 운영 이력 {removed:N0}건을 정리했어요.", "SuccessBrush"); }
        catch (OperationCanceledException) { SetStatus("운영 이력 정리를 중단했어요.", "TextSecondaryBrush"); }
        catch { SetStatus("운영 이력을 정리하지 못했어요.", "DangerBrush"); }
        finally { EndOperation(); }
    }

    private async void RevokeGrant_Click(object sender, RoutedEventArgs e)
    {
        if (CapabilityGrantList.SelectedItem is not CapabilityGrant grant)
        {
            SetStatus("철회할 권한을 선택해 주세요.", "TextSecondaryBrush");
            return;
        }
        try
        {
            await _host.RevokeCapabilityGrantAsync(grant.Id);
            await RefreshCapabilityGrantsAsync();
            SetStatus("선택한 항상 허용 권한을 철회했습니다.", "SuccessBrush");
        }
        catch (Exception)
        {
            SetStatus("권한을 철회하지 못했습니다.", "DangerBrush");
        }
    }

    private async Task RefreshCapabilityGrantsAsync()
    {
        try
        {
            CapabilityGrantList.ItemsSource = await _host.GetCapabilityGrantsAsync();
        }
        catch (Exception)
        {
            CapabilityGrantList.ItemsSource = Array.Empty<CapabilityGrant>();
        }
    }

    private bool HasModelConnectionInput() => ModelConnectionInputPolicy.HasAnyInput(
        BaseUrlTextBox.Text,
        ModelTextBox.Text,
        ApiKeyPasswordBox.Password,
        _existingModelSettings is not null);

    private ModelConnectionSettings ReadModelSettings()
    {
        ClearFieldErrors();
        try
        {
            return _modelProfiles.Validate(
                new ModelProfileInput(
                    BaseUrlTextBox.Text,
                    ModelTextBox.Text,
                    ApiKeyPasswordBox.Password,
                    ModelProfileIdTextBox.Text,
                    ModelProfileNameTextBox.Text),
                _existingModelSettings);
        }
        catch (SettingsSectionValidationException exception)
        {
            if (exception.Field == ModelProfileField.BaseUrl)
                ShowFieldError(BaseUrlErrorText, "http:// 또는 https://로 시작하는 주소를 입력해 주세요.");
            if (exception.Field == ModelProfileField.Model)
                ShowFieldError(ModelErrorText, "사용할 모델 이름을 입력해 주세요.");
            if (exception.Field == ModelProfileField.ApiKey)
                ShowFieldError(ApiKeyErrorText, "API Key를 입력해 주세요.");
            throw;
        }
    }

    private void LoadModelProfile(ModelConnectionSettings profile)
    {
        _loadingModelProfile = true;
        try
        {
            _existingModelSettings = profile;
            ModelProfileIdTextBox.Text = profile.ProfileId;
            ModelProfileNameTextBox.Text = profile.DisplayName;
            BaseUrlTextBox.Text = profile.BaseUrl;
            ModelTextBox.Text = profile.Model;
            ApiKeyPasswordBox.Clear();
        }
        finally { _loadingModelProfile = false; }
    }

    private void ModelProfileComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingModelProfile || ModelProfileComboBox.SelectedItem is not ModelConnectionSettings profile) return;
        LoadModelProfile(profile);
        _lastSuccessfulConnectionTest = null;
    }

    private void NewModelProfile_Click(object sender, RoutedEventArgs e)
    {
        _loadingModelProfile = true;
        try
        {
            ModelProfileComboBox.SelectedItem = null;
            _existingModelSettings = null;
            ModelProfileIdTextBox.Text = string.Empty;
            ModelProfileNameTextBox.Text = string.Empty;
            BaseUrlTextBox.Text = string.Empty;
            ModelTextBox.Text = string.Empty;
            ApiKeyPasswordBox.Clear();
            _lastSuccessfulConnectionTest = null;
        }
        finally { _loadingModelProfile = false; }
    }

    private void ClearFieldErrors()
    {
        foreach (var error in new[] { BaseUrlErrorText, ModelErrorText, ApiKeyErrorText })
        {
            error.Text = string.Empty;
            error.Visibility = Visibility.Collapsed;
        }
    }

    private static void ShowFieldError(System.Windows.Controls.TextBlock target, string message)
    {
        target.Text = message;
        target.Visibility = Visibility.Visible;
    }

    private bool TryBeginOperation(string message, out CancellationToken cancellationToken)
    {
        cancellationToken = default;
        if (!_operationGuard.TryBegin(out cancellationToken)) return false;
        SetControlsEnabled(false);
        CancelButton.IsEnabled = true;
        CancelButton.Content = "중단";
        SetStatus(message, "TextSecondaryBrush");
        return true;
    }

    private void EndOperation()
    {
        _operationGuard.End();
        if (!IsLoaded) return;
        SetControlsEnabled(true);
        CancelButton.Content = "대화로 돌아가기";
    }

    private void SetControlsEnabled(bool enabled)
    {
        StartWithWindowsCheckBox.IsEnabled = enabled;
        BaseUrlTextBox.IsEnabled = enabled;
        ModelProfileComboBox.IsEnabled = enabled;
        ModelProfileIdTextBox.IsEnabled = enabled;
        ModelProfileNameTextBox.IsEnabled = enabled;
        FallbackProfileIdsTextBox.IsEnabled = enabled;
        ModelTextBox.IsEnabled = enabled;
        ApiKeyPasswordBox.IsEnabled = enabled;
        McpEnabledCheckBox.IsEnabled = enabled;
        McpDisplayNameTextBox.IsEnabled = enabled;
        McpUrlTextBox.IsEnabled = enabled;
        McpAllowedToolsTextBox.IsEnabled = enabled;
        McpTokenPasswordBox.IsEnabled = enabled;
        McpSampleRagCheckBox.IsEnabled = enabled;
        SemanticMemoryEnabledCheckBox.IsEnabled = enabled;
        SemanticMemoryBaseUrlTextBox.IsEnabled = enabled;
        SemanticMemoryModelTextBox.IsEnabled = enabled;
        SemanticMemoryApiKeyPasswordBox.IsEnabled = enabled;
        WebSearchUrlTemplateTextBox.IsEnabled = enabled;
        ShortcutComboBox.IsEnabled = enabled;
        TestConnectionButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        SaveDiagnosticsButton.IsEnabled = enabled;
        CapabilityGrantList.IsEnabled = enabled;
        RevokeGrantButton.IsEnabled = enabled;
    }

    private void SetStatus(string message, string brushResource)
    {
        SettingsStatusText.Foreground = (System.Windows.Media.Brush)FindResource(brushResource);
        SettingsStatusText.Text = message;
    }

    private void BaseUrlTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        BaseUrlErrorText.Visibility = Visibility.Collapsed;
        var showWarning = ModelConnectionInputPolicy.ShouldWarnAboutPlaintextHttp(BaseUrlTextBox.Text);
        ConnectionSecurityWarningText.Visibility = showWarning ? Visibility.Visible : Visibility.Collapsed;
        InvalidateSuccessfulConnectionTest();
    }

    private void ModelInput_Changed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ModelTextBox)) ModelErrorText.Visibility = Visibility.Collapsed;
        if (ReferenceEquals(sender, ApiKeyPasswordBox)) ApiKeyErrorText.Visibility = Visibility.Collapsed;
        InvalidateSuccessfulConnectionTest();
    }

    private void InvalidateSuccessfulConnectionTest()
    {
        if (!_initialized || _lastSuccessfulConnectionTest is null) return;
        _lastSuccessfulConnectionTest = null;
        SetStatus("연결 정보가 변경되었습니다. 저장하기 전에 다시 테스트해 주세요.", "TextSecondaryBrush");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_operationGuard.IsBusy)
        {
            _operationGuard.Cancel();
            return;
        }
        _host.NavigateHome();
    }
}
