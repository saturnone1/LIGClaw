using System.Text.RegularExpressions;

namespace LIGClaw.Desktop.Tests;

public sealed partial class XamlResourceTests
{
    [Fact]
    public void EveryNamedStaticResourceExistsInDesktopXaml()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var files = Directory.GetFiles(desktop, "*.xaml", SearchOption.AllDirectories);
        var contents = files.Select(File.ReadAllText).ToArray();
        var keys = contents
            .SelectMany(content => ResourceKeyRegex().Matches(content).Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        var references = contents
            .SelectMany(content => StaticResourceRegex().Matches(content).Select(match => match.Groups[1].Value))
            .Where(reference => !reference.StartsWith("{x:Type", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.DoesNotContain(references, reference => !keys.Contains(reference));
    }

    [Fact]
    public void RunBindingsToViewDataAreExplicitlyOneWay()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var bindings = Directory.GetFiles(desktop, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(file => RunBindingRegex().Matches(File.ReadAllText(file)).Select(match => match.Value))
            .ToArray();

        Assert.DoesNotContain(bindings, binding => !binding.Contains("Mode=OneWay", StringComparison.Ordinal));
    }

    [Fact]
    public void DesignSystemV2DefinesRequiredSemanticResources()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var resources = File.ReadAllText(Path.Combine(desktop, "Themes", "BrandTokens.xaml"))
            + File.ReadAllText(Path.Combine(desktop, "Themes", "Controls.xaml"));
        var keys = ResourceKeyRegex().Matches(resources)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var required = new[]
        {
            "FocusBrush",
            "InfoSubtleBrush",
            "WarningSubtleBrush",
            "DangerSubtleBrush",
            "NavigationTextBrush",
            "TypePageTitleSize",
            "PagePadding",
            "KeyboardFocusVisualStyle",
            "PageTitleStyle",
            "PageSubtitleStyle",
            "StatusPillStyle",
            "InfoBannerStyle",
            "WarningBannerStyle",
            "DangerBannerStyle",
            "ListRowStyle",
            "EmptyStateTextStyle",
        };

        Assert.DoesNotContain(required, key => !keys.Contains(key));
    }

    [Fact]
    public void PrecisionWorkspaceV3DefinesItsSharedShellAndDataResources()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var resources = File.ReadAllText(Path.Combine(desktop, "Themes", "BrandTokens.xaml"))
            + File.ReadAllText(Path.Combine(desktop, "Themes", "Controls.xaml"));
        var keys = ResourceKeyRegex().Matches(resources)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var required = new[]
        {
            "DeepNavyBrush",
            "SignalCyanBrush",
            "ConversationReadingWidth",
            "AppRailButtonStyle",
            "CommandHeaderStyle",
            "EyebrowTextStyle",
            "CommandBarStyle",
            "DataSurfaceStyle",
            "ActionCardStyle",
        };

        Assert.DoesNotContain(required, key => !keys.Contains(key));
    }

    [Fact]
    public void PrecisionWorkspaceShellUsesAnAdaptiveRailAndConversationSwitcher()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var mainXaml = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml"));
        var mainCode = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml.cs"));
        var layout = File.ReadAllText(Path.Combine(desktop, "Infrastructure", "Shell", "ShellLayoutPolicy.cs"));

        Assert.Contains("AppRailButtonStyle", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ConversationSwitcherPopup", mainXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"대화 내용 검색\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("SearchConversationsAsync", mainCode, StringComparison.Ordinal);
        Assert.Contains("ConversationReadingWidth", mainXaml, StringComparison.Ordinal);
        Assert.Contains("NewContentIndicator", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ExamplePrompt_Click", mainCode, StringComparison.Ordinal);
        Assert.Contains("ConversationInput.Text = prompt", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Send_Click(sender", mainCode, StringComparison.Ordinal);
        Assert.Contains("new ShellLayoutMetrics(true, 72, 16, false)", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalDialogDoesNotDefaultToAllowingAnAction()
    {
        var approvalXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "LIGClaw.Desktop",
            "ToolApprovalWindow.xaml"));

        Assert.Contains("DenyButton", approvalXaml, StringComparison.Ordinal);
        Assert.Matches("DenyButton[^>]*IsDefault=\"True\"", approvalXaml);
        Assert.DoesNotMatch("AllowOnce_Click[^>]*IsDefault=\"True\"", approvalXaml);
        Assert.DoesNotMatch("AllowAlways_Click[^>]*IsDefault=\"True\"", approvalXaml);
    }

    [Fact]
    public void Sensitive_screen_preview_defaults_to_cancel_and_never_offers_persistent_approval()
    {
        var previewXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "LIGClaw.Desktop",
            "SensitiveContextPreviewWindow.xaml"));

        Assert.Contains("x:Name=\"PreviewImage\"", previewXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OcrTextBox\"", previewXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CropOverlay\"", previewXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CropSelectionButton\"", previewXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"선택한 화면 영역 자르기\"", previewXaml, StringComparison.Ordinal);
        Assert.Matches("CancelButton[^>]*IsDefault=\"True\"", previewXaml);
        Assert.DoesNotMatch("ApproveButton[^>]*IsDefault=\"True\"", previewXaml);
        Assert.DoesNotContain("AllowAlways", previewXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowConversation", previewXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Composer_exposes_a_user_initiated_accessible_screen_picker()
    {
        var mainXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "LIGClaw.Desktop",
            "MainWindow.xaml"));

        Assert.Contains("x:Name=\"ScreenContextButton\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"화면 내용 가져오기\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ScreenContext_Click\"", mainXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposerExposesOptInAccessiblePushToTalk()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var mainXaml = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml"));
        var settingsXaml = File.ReadAllText(Path.Combine(desktop, "SettingsPage.xaml"));

        Assert.Contains("x:Name=\"VoiceInputButton\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"누르고 말해서 요청 입력\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonDown=\"VoiceInputButton_PreviewMouseLeftButtonDown\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("PreviewKeyDown=\"VoiceInputButton_PreviewKeyDown\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"VoiceInputEnabledCheckBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("기본은 꺼짐입니다", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("원시 오디오는 저장하지 않습니다", settingsXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptExposesAccessibleSelectionReadingAndImmediateStop()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var mainXaml = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml"));
        var mainCode = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"ReadSelectionButton\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"선택한 답변 읽기\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"Transcript_SelectionChanged\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"선택 영역 읽기\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ReadSelectionButton.Content = \"읽기 중지\"", mainCode, StringComparison.Ordinal);
        Assert.Contains("_textToSpeech.Stop()", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewXamlUsesSemanticResourcesInsteadOfRawHexColors()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var violations = Directory.GetFiles(desktop, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith("BrandTokens.xaml", StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => RawHexColorRegex().Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetFileName(file)}: {match.Value}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void ViewXamlUsesTypographyResourcesInsteadOfNumericFontSizes()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var violations = Directory.GetFiles(desktop, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith("BrandTokens.xaml", StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => DirectFontSizeRegex().Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetFileName(file)}: {match.Value}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void ManagementPagesAreHostedInsideTheMainShell()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var mainXaml = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml"));
        var mainCode = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"ShellPageHost\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ActivityNavigationButton\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("new ActivityView()", mainCode, StringComparison.Ordinal);
        Assert.Contains("new MemoryPage(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new SchedulePage(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new SettingsPage(", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new ActivityWindow", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new MemoryWindow", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new ScheduleWindow", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new SettingsWindow", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversationShellExposesAnExplicitThreadBoundaryAndUsesRunController()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var mainXaml = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml"));
        var mainCode = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml.cs"));

        Assert.Contains("AutomationProperties.Name=\"새 대화 시작\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("TurnCountDisplay", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ConversationRunController", mainCode, StringComparison.Ordinal);
        Assert.Contains("ConversationOrchestrationController", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistConversationRunStartAsync", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_activeConversationId = Guid.NewGuid", mainCode, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", mainCode, StringComparison.Ordinal);
        Assert.Contains("RenderPendingTranscript", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherPriority.Background", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UserInputControlsHaveAccessibleNames()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var violations = Directory.GetFiles(desktop, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(file => UserInputControlRegex().Matches(File.ReadAllText(file))
                .Where(match => !match.Value.Contains("AutomationProperties.Name", StringComparison.Ordinal))
                .Select(match => $"{Path.GetFileName(file)}: {match.Value}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void DesktopProjectUsesPerMonitorV2DpiAwareness()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var project = File.ReadAllText(Path.Combine(desktop, "LIGClaw.Desktop.csproj"));

        Assert.Contains("<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagementPagesExposeFiltersEmptyStatesAndLiveFeedback()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var activity = File.ReadAllText(Path.Combine(desktop, "ActivityView.xaml"));
        var memory = File.ReadAllText(Path.Combine(desktop, "MemoryPage.xaml"));
        var schedule = File.ReadAllText(Path.Combine(desktop, "SchedulePage.xaml"));

        Assert.Contains("실행 상태 필터", activity, StringComparison.Ordinal);
        Assert.Contains("실행 활동 정렬", activity, StringComparison.Ordinal);
        Assert.Contains("ActivityFeedbackText", activity, StringComparison.Ordinal);
        Assert.Contains("기억 검색", memory, StringComparison.Ordinal);
        Assert.Contains("기억 정렬", memory, StringComparison.Ordinal);
        Assert.Contains("검색 지우기", memory, StringComparison.Ordinal);
        Assert.Contains("PageStatusText", memory, StringComparison.Ordinal);
        Assert.Contains("완료·취소된 예약도 표시", schedule, StringComparison.Ordinal);
        Assert.Contains("예약 정렬", schedule, StringComparison.Ordinal);
        Assert.Contains("PageStatusText", schedule, StringComparison.Ordinal);
        Assert.All(new[] { activity, memory, schedule }, xaml =>
            Assert.Contains("EmptyStateTextStyle", xaml, StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsExposeTheValidatedTestThenSaveConnectionFlow()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var settings = File.ReadAllText(Path.Combine(desktop, "SettingsPage.xaml"));
        var settingsCode = File.ReadAllText(Path.Combine(desktop, "SettingsPage.xaml.cs"));
        var operationGuard = File.ReadAllText(Path.Combine(desktop, "SettingsOperationGuard.cs"));
        var modelSection = File.ReadAllText(Path.Combine(desktop, "ModelProfileSectionController.cs"));
        var mcpSection = File.ReadAllText(Path.Combine(desktop, "McpSettingsSectionController.cs"));

        Assert.Contains("1. 입력  →  2. 연결 테스트  →  3. 저장", settings, StringComparison.Ordinal);
        Assert.Contains("BaseUrlErrorText", settings, StringComparison.Ordinal);
        Assert.Contains("ModelErrorText", settings, StringComparison.Ordinal);
        Assert.Contains("ApiKeyErrorText", settings, StringComparison.Ordinal);
        Assert.Contains("RequiresSuccessfulTest", modelSection, StringComparison.Ordinal);
        Assert.Contains("MCP 지식 연결", settings, StringComparison.Ordinal);
        Assert.Contains("McpConnectionPolicy.TryValidate", mcpSection, StringComparison.Ordinal);
        Assert.Contains("SetControlsEnabled(!_operationGuard.IsBusy)", settingsCode, StringComparison.Ordinal);
        Assert.Contains("if (_activeOperation is not null) return false", operationGuard, StringComparison.Ordinal);
        Assert.Contains("_activeOperation?.Cancel()", operationGuard, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagementPageRefreshesAreCancelledWhenSupersededOrUnloaded()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        foreach (var fileName in new[] { "MemoryPage.xaml.cs", "SchedulePage.xaml.cs" })
        {
            var code = File.ReadAllText(Path.Combine(desktop, fileName));
            Assert.Contains("_refreshCancellation", code, StringComparison.Ordinal);
            Assert.Contains("Unloaded +=", code, StringComparison.Ordinal);
            Assert.Contains("catch (OperationCanceledException)", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ListForManagementAsync(query, results.Count, pageSize, CancellationToken.None)", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DesktopXamlDoesNotIntroduceMotionThatIgnoresReducedMotionPreferences()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var xaml = string.Join(Environment.NewLine,
            Directory.GetFiles(desktop, "*.xaml", SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.DoesNotContain("Storyboard", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DoubleAnimation", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LIGClaw.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("LIGClaw repository root를 찾지 못했습니다.");
    }

    [GeneratedRegex("x:Key=\"([^\"]+)\"")]
    private static partial Regex ResourceKeyRegex();

    [GeneratedRegex("\\{StaticResource\\s+([^}\\s]+)")]
    private static partial Regex StaticResourceRegex();

    [GeneratedRegex("""<Run\b[^>]*Text="\{Binding[^"]*\}"[^>]*/?>""")]
    private static partial Regex RunBindingRegex();

    [GeneratedRegex("#[0-9A-Fa-f]{6,8}")]
    private static partial Regex RawHexColorRegex();

    [GeneratedRegex("FontSize=\"[0-9]")]
    private static partial Regex DirectFontSizeRegex();

    [GeneratedRegex("<(?:TextBox|PasswordBox|ComboBox)\\b[^>]*>")]
    private static partial Regex UserInputControlRegex();
}
