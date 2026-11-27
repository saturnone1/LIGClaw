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
        Assert.Contains("x:Name=\"DetailsScrollViewer\"", approvalXaml, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", approvalXaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"760\" MaxHeight=\"500\"", approvalXaml, StringComparison.Ordinal);
        Assert.Contains("<RowDefinition Height=\"*\" />", approvalXaml, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"True\" IsTabStop=\"True\"", approvalXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.HelpText=", approvalXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ApprovalDetailsText\"", approvalXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"동작 상세 정보\"", approvalXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SizeToContent=\"Height\"", approvalXaml, StringComparison.Ordinal);
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
    public void ComposerExplicitlyHandsOffValidatedTextToTheGenericFeatureBuilder()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var mainXaml = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml"));
        var mainCode = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml.cs"));
        var sidecarSupervisor = File.ReadAllText(Path.Combine(
            desktop,
            "Infrastructure",
            "Sidecar",
            "SidecarSupervisor.cs"));
        var builderCode = File.ReadAllText(Path.Combine(desktop, "FeatureBuilderPage.xaml.cs"));

        Assert.Contains("x:Name=\"ComposerActions\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MakeCapabilityButton\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"이 요청을 기능으로 만들기\"",
            mainXaml,
            StringComparison.Ordinal);
        Assert.Contains("Click=\"MakeCapability_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("요청을 보내거나 기능 생성을 시작하지 않습니다", mainXaml, StringComparison.Ordinal);

        var handoff = Regex.Match(
            mainCode,
            @"private async void MakeCapability_Click[\s\S]*?(?=\r?\n    private static string DescribeFeatureBuilderHandoffFailure)",
            RegexOptions.CultureInvariant).Value;
        Assert.NotEmpty(handoff);
        Assert.Contains("FeatureBuilderPolicy.ValidateRequirement(requirement)", handoff, StringComparison.Ordinal);
        Assert.Contains("await OpenFeatureBuilderAsync(requirement)", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain("Send_Click", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain("ConversationInput.Clear", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain("StartGenerationAsync", handoff, StringComparison.Ordinal);

        Assert.Contains("CapabilitiesPage_BuilderRequested", mainCode, StringComparison.Ordinal);
        Assert.Contains("await OpenFeatureBuilderAsync()", mainCode, StringComparison.Ordinal);
        Assert.Contains("_capabilitiesPage ??= CreateCapabilitiesPage()", mainCode, StringComparison.Ordinal);
        var openBuilder = Regex.Match(
            mainCode,
            @"private async Task<bool> OpenFeatureBuilderAsync[\s\S]*?(?=\r?\n    private async void FeatureBuilderPage_BackRequested)",
            RegexOptions.CultureInvariant).Value;
        Assert.NotEmpty(openBuilder);
        Assert.DoesNotContain("_mailGatewayManagement", openBuilder, StringComparison.Ordinal);
        Assert.DoesNotContain("_smtpManagement", openBuilder, StringComparison.Ordinal);
        Assert.Contains("CapabilityConnectionKindPolicy.SupportedKinds", mainCode, StringComparison.Ordinal);
        Assert.Contains("ApplyInitialRequirement(initialRequirement)", builderCode, StringComparison.Ordinal);
        Assert.Contains("FeatureBuilderPolicy.ValidateRequirement(requirement)", builderCode, StringComparison.Ordinal);
        Assert.Contains("직접 초안 생성을 눌러 주세요", builderCode, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", sidecarSupervisor, StringComparison.Ordinal);
        Assert.Contains(
            "output suppressed by diagnostic content policy",
            sidecarSupervisor,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ModelRequestedFeatureBuilderOpenAcknowledgesBeforeNavigatingAndPreservesGenericRouting()
    {
        var mainCode = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "LIGClaw.Desktop",
            "MainWindow.xaml.cs"));

        var composition = Regex.Match(
            mainCode,
            @"_capabilityBuilderOpenController = new CapabilityBuilderOpenController\([\s\S]*?(?=_conversationOrchestration =)",
            RegexOptions.CultureInvariant).Value;
        Assert.NotEmpty(composition);
        Assert.Contains("_conversationRun", composition, StringComparison.Ordinal);
        Assert.Contains("_conversationStore.Conversations", composition, StringComparison.Ordinal);

        var routing = Regex.Match(
            mainCode,
            @"private async void Sidecar_ToolInvocationReceived[\s\S]*?(?=\r?\n    private async Task HandleCapabilityBuilderOpenInvocationAsync)",
            RegexOptions.CultureInvariant).Value;
        Assert.NotEmpty(routing);
        Assert.Contains("StringComparer.Ordinal.Equals(", routing, StringComparison.Ordinal);
        Assert.Contains("CapabilityBuilderOpenController.CanonicalToolName", routing, StringComparison.Ordinal);
        Assert.Contains("await HandleCapabilityBuilderOpenInvocationAsync(invocation);", routing, StringComparison.Ordinal);

        var agentJobIndex = routing.IndexOf("_agentJobExecutor", StringComparison.Ordinal);
        var subagentIndex = routing.IndexOf("_subagentOrchestrator", StringComparison.Ordinal);
        var builderIndex = routing.IndexOf(
            "CapabilityBuilderOpenController.CanonicalToolName",
            StringComparison.Ordinal);
        var genericIndex = routing.IndexOf("_conversationTools.ExecuteAsync", StringComparison.Ordinal);
        var genericResultIndex = routing.IndexOf(
            "SubmitToolExecutionResultAsync(invocation, execution)",
            StringComparison.Ordinal);
        Assert.True(agentJobIndex >= 0 && agentJobIndex < subagentIndex);
        Assert.True(subagentIndex < builderIndex);
        Assert.True(builderIndex < genericIndex);
        Assert.True(genericIndex < genericResultIndex);

        var modelOpen = Regex.Match(
            mainCode,
            @"private async Task HandleCapabilityBuilderOpenInvocationAsync[\s\S]*?(?=\r?\n    private async Task<ToolResultResult\?> SubmitToolExecutionResultAsync)",
            RegexOptions.CultureInvariant).Value;
        Assert.NotEmpty(modelOpen);
        var executeIndex = modelOpen.IndexOf(
            "_capabilityBuilderOpenController.ExecuteAsync(invocation)",
            StringComparison.Ordinal);
        var submitIndex = modelOpen.IndexOf(
            "SubmitToolExecutionResultAsync(invocation, execution)",
            StringComparison.Ordinal);
        var acceptedIndex = modelOpen.IndexOf("acknowledged.Accepted", StringComparison.Ordinal);
        var successIndex = modelOpen.IndexOf("execution.Success", StringComparison.Ordinal);
        var activeRecheckIndex = modelOpen.IndexOf(
            "_capabilityBuilderOpenController.CanOpenAfterAcknowledgement(request)",
            StringComparison.Ordinal);
        var openIndex = modelOpen.IndexOf(
            "OpenFeatureBuilderAsync(",
            StringComparison.Ordinal);
        Assert.True(executeIndex >= 0 && executeIndex < submitIndex);
        Assert.True(submitIndex < acceptedIndex && acceptedIndex < openIndex);
        Assert.True(submitIndex < successIndex && successIndex < openIndex);
        Assert.True(acceptedIndex < activeRecheckIndex && activeRecheckIndex < openIndex);
        Assert.Contains("acknowledged is null", modelOpen, StringComparison.Ordinal);
        Assert.Contains("outcome.RequestAfterAcknowledgement is not { } request", modelOpen, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(modelOpen, @"request\.Requirement").Cast<Match>());
        Assert.DoesNotContain("SetRunStatus(request", modelOpen, StringComparison.Ordinal);
        Assert.DoesNotContain("AddDiagnostic(request", modelOpen, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", modelOpen, StringComparison.Ordinal);

        var resultSubmission = Regex.Match(
            mainCode,
            @"private async Task<ToolResultResult\?> SubmitToolExecutionResultAsync[\s\S]*?(?=\r?\n    private async void Sidecar_WorkflowInvocationReceived)",
            RegexOptions.CultureInvariant).Value;
        Assert.NotEmpty(resultSubmission);
        Assert.Contains("_sidecar.SubmitToolResultAsync(new ToolResultParams(", resultSubmission, StringComparison.Ordinal);
        Assert.Contains("invocation.ToolCallId", resultSubmission, StringComparison.Ordinal);
        Assert.Contains("execution.Success", resultSubmission, StringComparison.Ordinal);
        Assert.Contains("execution.Output", resultSubmission, StringComparison.Ordinal);
        Assert.Contains("execution.Error", resultSubmission, StringComparison.Ordinal);
        Assert.DoesNotContain("Requirement", resultSubmission, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", resultSubmission, StringComparison.Ordinal);
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
        Assert.Contains("원본 음성은 저장하지 않습니다", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AutoReadEnabledCheckBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QuietStartComboBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QuietEndComboBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("기본은 꺼짐입니다", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("앱을 보고 있을 때만", settingsXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutineSuggestionsAreOptInDismissibleAndRequireEditorConfirmation()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var mainXaml = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml"));
        var mainCode = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml.cs"));
        var settingsXaml = File.ReadAllText(Path.Combine(desktop, "SettingsPage.xaml"));
        var editorCode = File.ReadAllText(Path.Combine(desktop, "AgentJobEditorWindow.xaml.cs"));

        Assert.Contains("x:Name=\"RoutineSuggestionsEnabledCheckBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("기본은 꺼짐입니다. 켜면 이 PC의 완료된 요청", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RoutineSuggestionCard\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("이번에는 닫기", mainXaml, StringComparison.Ordinal);
        Assert.Contains("다시 제안하지 않기", mainXaml, StringComparison.Ordinal);
        Assert.Contains("candidate is null || _conversationRun.IsRunning", mainCode, StringComparison.Ordinal);
        Assert.Contains("new AgentJobEditorWindow(_agentJobRepository, candidate)", mainCode, StringComparison.Ordinal);
        Assert.Contains("\"routine-suggestion\"", editorCode, StringComparison.Ordinal);
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
        Assert.Contains("x:Name=\"CapabilitiesNavigationButton\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConnectionsNavigationButton\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("new ActivityView()", mainCode, StringComparison.Ordinal);
        Assert.Contains("new CapabilitiesPage(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new FeatureBuilderPage(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new ConnectionsPage(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new MemoryPage(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new SchedulePage(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new SettingsPage(", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new ActivityWindow", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new MemoryWindow", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new ScheduleWindow", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new SettingsWindow", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("FeatureBuilderNavigationButton", mainXaml, StringComparison.Ordinal);
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
        Assert.Contains("CapabilityRuntimeSnapshotBuilder", mainCode, StringComparison.Ordinal);
        Assert.Contains("ConfigureCapabilitySnapshotFactory", mainCode, StringComparison.Ordinal);
        Assert.Contains("_conversationStore.CapabilityRuntimeCandidates", mainCode, StringComparison.Ordinal);
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
        var capabilities = File.ReadAllText(Path.Combine(desktop, "CapabilitiesPage.xaml"));
        var connections = File.ReadAllText(Path.Combine(desktop, "ConnectionsPage.xaml"));
        var memory = File.ReadAllText(Path.Combine(desktop, "MemoryPage.xaml"));
        var schedule = File.ReadAllText(Path.Combine(desktop, "SchedulePage.xaml"));

        Assert.Contains("실행 상태 필터", activity, StringComparison.Ordinal);
        Assert.Contains("실행 활동 정렬", activity, StringComparison.Ordinal);
        Assert.Contains("ActivityFeedbackText", activity, StringComparison.Ordinal);
        Assert.Contains("기능 상태 필터", capabilities, StringComparison.Ordinal);
        Assert.Contains("기능 정렬", capabilities, StringComparison.Ordinal);
        Assert.Contains("PageStatusText", capabilities, StringComparison.Ordinal);
        Assert.Contains("설치된 기능 목록", capabilities, StringComparison.Ordinal);
        Assert.Contains("기능 카드 대시보드", capabilities, StringComparison.Ordinal);
        Assert.Contains("<WrapPanel IsItemsHost=\"True\"", capabilities, StringComparison.Ordinal);
        Assert.Contains("AutomationName", capabilities, StringComparison.Ordinal);
        Assert.Contains("저장된 연결 설정 목록", connections, StringComparison.Ordinal);
        Assert.Contains("PageStatusText", connections, StringComparison.Ordinal);
        Assert.Contains("기억 검색", memory, StringComparison.Ordinal);
        Assert.Contains("기억 정렬", memory, StringComparison.Ordinal);
        Assert.Contains("검색 지우기", memory, StringComparison.Ordinal);
        Assert.Contains("PageStatusText", memory, StringComparison.Ordinal);
        Assert.Contains("완료·취소된 예약도 표시", schedule, StringComparison.Ordinal);
        Assert.Contains("예약 정렬", schedule, StringComparison.Ordinal);
        Assert.Contains("PageStatusText", schedule, StringComparison.Ordinal);
        Assert.All(new[] { activity, capabilities, connections, memory, schedule }, xaml =>
            Assert.Contains("EmptyStateTextStyle", xaml, StringComparison.Ordinal));
    }

    [Fact]
    public void ConnectionManagementKeepsSecretsWriteOnlyAndUsesTheRealProbeBoundary()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var pageXaml = File.ReadAllText(Path.Combine(desktop, "ConnectionsPage.xaml"));
        var pageCode = File.ReadAllText(Path.Combine(desktop, "ConnectionsPage.xaml.cs"));
        var mainCode = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"ProfileIdTextBox\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"True\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProfileKindBox\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SenderTextBox\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BearerTokenBox\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SmtpHostTextBox\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SmtpPortTextBox\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SmtpTlsModeBox\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SmtpUsernameBox\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SmtpPasswordBox\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("사용자명·암호 인증 · 윈도우 인증서 신뢰", pageXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=\"{Binding", pageXaml, StringComparison.Ordinal);
        Assert.Contains("ConnectionSecretMutation.Keep", pageCode, StringComparison.Ordinal);
        Assert.Contains("ConnectionSecretMutation.Replace", pageCode, StringComparison.Ordinal);
        Assert.Contains("ConnectionSecretMutation.Delete", pageCode, StringComparison.Ordinal);
        Assert.Contains("BearerTokenBox.Clear();", pageCode, StringComparison.Ordinal);
        Assert.Contains("_credentials.ExistsAsync", pageCode, StringComparison.Ordinal);
        Assert.Contains("ConnectionCredentialKind.BearerToken", pageCode, StringComparison.Ordinal);
        Assert.Contains("ConnectionCredentialKind.MailGatewayBearerToken", pageCode, StringComparison.Ordinal);
        Assert.Contains("ConnectionCredentialKind.SmtpAuth", pageCode, StringComparison.Ordinal);
        Assert.Contains("SaveMailGatewayProfileAsync", pageCode, StringComparison.Ordinal);
        Assert.Contains("SaveSmtpProfileAsync", pageCode, StringComparison.Ordinal);
        Assert.Contains("ProbeHttpProfileAsync", pageCode, StringComparison.Ordinal);
        Assert.Contains("ProbeMailGatewayProfileAsync", pageCode, StringComparison.Ordinal);
        Assert.Contains("ProbeSmtpProfileAsync", pageCode, StringComparison.Ordinal);
        Assert.Contains("FindFocusTarget(items, targetId, targetKind)", pageCode, StringComparison.Ordinal);
        Assert.Contains("if (unique is not null) return null", pageCode, StringComparison.Ordinal);
        Assert.Contains("_editingProfile is null && ProfileKindBox.Items.Count > 1", pageCode, StringComparison.Ordinal);
        Assert.Contains("BoundStatus(message)", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordProbeAsync", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_credentials.ReadAsync", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadSmtpAsync", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AddDiagnostic", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsCredentialStore", pageCode, StringComparison.Ordinal);
        Assert.Contains("new MailGatewayConnectionReadinessResolver(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new SmtpConnectionReadinessResolver(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new CompositeConnectionReadinessResolver(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new MailGatewayProbeAdapter(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new SmtpConnectionProbeAdapter(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new DesktopMailGatewayExecutionResolver(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new DesktopSmtpExecutionResolver(", mainCode, StringComparison.Ordinal);
        Assert.Contains("_connectionCredentials.ReadSmtpAsync", mainCode, StringComparison.Ordinal);
        Assert.Contains("smtpResolver: smtpResolver", mainCode, StringComparison.Ordinal);
        Assert.Contains("smtpWireClient: smtpWireClient", mainCode, StringComparison.Ordinal);
        Assert.Contains("mailOutbox: _conversationStore.MailOutbox", mainCode, StringComparison.Ordinal);
        Assert.Contains("_conversationStore.MailGatewayConnections", mainCode, StringComparison.Ordinal);
        Assert.Contains("_conversationStore.SmtpConnections", mainCode, StringComparison.Ordinal);
        Assert.Contains("CapabilityConnectionKindPolicy.SupportedKinds", mainCode, StringComparison.Ordinal);
        Assert.Matches(
            @"var\s+smtpWireClient\s*=\s*new\s+SmtpWireClient\(\s*\);[\s\S]*?new\s+SmtpConnectionProbeAdapter\(\s*_connectionCredentials\.ReadSmtpAsync,\s*smtpWireClient\.ProbeAsync\s*\)",
            mainCode);
        Assert.Matches(
            @"new\s+CompositeConnectionReadinessResolver\(\s*httpConnectionReadiness,\s*mailGatewayReadiness,\s*smtpReadiness\s*\)",
            mainCode);
    }

    [Fact]
    public void MainWindowSharesOneExecutionLeaseAcrossHttpMailAndSmtpBoundaries()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var mainCode = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml.cs"));

        Assert.Single(
            Regex.Matches(
                mainCode,
                @"new\s+ConnectionExecutionLeaseCoordinator\s*\(\s*\)",
                RegexOptions.CultureInvariant).Cast<Match>());
        var sharedLeaseReferences = Regex.Matches(
                mainCode,
                @"\bconnectionExecutionLeases\b",
                RegexOptions.CultureInvariant);
        Assert.True(
            sharedLeaseReferences.Count == 7,
            $"Expected the one shared lease declaration plus six injections, but found {sharedLeaseReferences.Count} references.");
        Assert.Matches(
            @"_connectionManagement\s*=\s*new\s+ConnectionManagementService\([\s\S]*?executionLeases:\s*connectionExecutionLeases\s*\);",
            mainCode);
        Assert.Matches(
            @"var\s+connectionHttpResolver\s*=\s*new\s+DesktopConnectionHttpExecutionResolver\([\s\S]*?,\s*connectionExecutionLeases\s*\);",
            mainCode);
        Assert.Matches(
            @"_mailGatewayManagement\s*=\s*new\s+MailGatewayConnectionManagementService\([\s\S]*?executionLeaseCoordinator:\s*connectionExecutionLeases\s*\);",
            mainCode);
        Assert.Matches(
            @"var\s+mailGatewayResolver\s*=\s*new\s+DesktopMailGatewayExecutionResolver\([\s\S]*?,\s*connectionExecutionLeases\s*\);",
            mainCode);
        Assert.Matches(
            @"_smtpManagement\s*=\s*new\s+SmtpConnectionManagementService\([\s\S]*?executionLeaseCoordinator:\s*connectionExecutionLeases\s*\);",
            mainCode);
        Assert.Matches(
            @"var\s+smtpResolver\s*=\s*new\s+DesktopSmtpExecutionResolver\([\s\S]*?,\s*connectionExecutionLeases\s*\);",
            mainCode);
    }

    [Fact]
    public void CapabilityManagementRequiresReviewAndKeepsInfrastructureBehindOneService()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var pageXaml = File.ReadAllText(Path.Combine(desktop, "CapabilitiesPage.xaml"));
        var pageCode = File.ReadAllText(Path.Combine(desktop, "CapabilitiesPage.xaml.cs"));
        var reviewXaml = File.ReadAllText(Path.Combine(desktop, "CapabilityReviewWindow.xaml"));

        Assert.Contains("Content=\"검토\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"이전 버전 검토\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"True\"", reviewXaml, StringComparison.Ordinal);
        Assert.Matches("CloseButton[^>]*IsCancel=\"True\"[^>]*IsDefault=\"True\"", reviewXaml);
        Assert.DoesNotMatch("ActivateButton[^>]*IsDefault=\"True\"", reviewXaml);
        Assert.DoesNotMatch("RollbackButton[^>]*IsDefault=\"True\"", reviewXaml);
        Assert.Contains("ICapabilityManagementService", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ConversationStore", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Sidecar", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqlite", pageCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ZipArchive", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Credential", pageCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FeatureBuilderCreatesOnlyASecretFreeReviewDraftInsideCapabilitiesNavigation()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var capabilitiesXaml = File.ReadAllText(Path.Combine(desktop, "CapabilitiesPage.xaml"));
        var capabilitiesCode = File.ReadAllText(Path.Combine(desktop, "CapabilitiesPage.xaml.cs"));
        var builderXaml = File.ReadAllText(Path.Combine(desktop, "FeatureBuilderPage.xaml"));
        var builderCode = File.ReadAllText(Path.Combine(desktop, "FeatureBuilderPage.xaml.cs"));
        var mainXaml = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml"));
        var mainCode = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml.cs"));

        Assert.Contains("Content=\"새 기능 만들기\"", capabilitiesXaml, StringComparison.Ordinal);
        Assert.Contains("BuilderRequested", capabilitiesCode, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", builderXaml, StringComparison.Ordinal);
        Assert.Contains("MaxLength=\"8192\"", builderXaml, StringComparison.Ordinal);
        Assert.Contains("비밀값 입력 칸은 제공하지 않습니다", builderXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"검사 실행\"", builderXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"검토 초안으로 적용\"", builderXaml, StringComparison.Ordinal);
        Assert.Contains("별도로 적용해야 합니다", builderCode, StringComparison.Ordinal);
        Assert.Contains(
            "DraftReadyForReview?.Invoke(this, result.Draft.ExactVersion)",
            builderCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<PasswordBox", builderXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SidecarSupervisor", builderCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqlite", builderCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FeatureBuilderRawProposalArtifact", builderCode, StringComparison.Ordinal);

        Assert.Contains("new DesktopFeatureBuilderProposalScanner(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new FeatureBuilderManagementService(", mainCode, StringComparison.Ordinal);
        Assert.Contains("new DesktopFeatureBuilderTestRunner(", mainCode, StringComparison.Ordinal);
        Assert.Contains("BuildFeatureBuilderInventoryAsync", mainCode, StringComparison.Ordinal);
        Assert.Contains("ShowShellPage(_featureBuilderPage, CapabilitiesNavigationButton)", mainCode, StringComparison.Ordinal);
        Assert.Contains(
            "_featureBuilderPage.DraftReadyForReview += FeatureBuilderPage_DraftReadyForReview",
            mainCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "await _capabilitiesPage.RefreshAsync(completionMessage, focusVersion)",
            mainCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FeatureBuilderNavigationButton", mainXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityReviewBindsRequiredConnectionsOnlyToLocalProfilesAndGatesMutationOnReadiness()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var reviewXaml = File.ReadAllText(Path.Combine(desktop, "CapabilityReviewWindow.xaml"));
        var reviewCode = File.ReadAllText(Path.Combine(desktop, "CapabilityReviewWindow.xaml.cs"));
        var pageCode = File.ReadAllText(Path.Combine(desktop, "CapabilitiesPage.xaml.cs"));

        Assert.Contains("x:Name=\"ConnectionBindingSection\"", reviewXaml, StringComparison.Ordinal);
        Assert.Contains("기능 연결 선택 목록", reviewXaml, StringComparison.Ordinal);
        Assert.Contains("연결 선택 저장", reviewXaml, StringComparison.Ordinal);
        Assert.Contains("manifest.RequiredConnections", reviewCode, StringComparison.Ordinal);
        Assert.Contains("_connectionManagement!.BindAsync", reviewCode, StringComparison.Ordinal);
        Assert.Contains("_mailGatewayConnectionManagement!.BindAsync", reviewCode, StringComparison.Ordinal);
        Assert.Contains("_smtpConnectionManagement!.BindAsync", reviewCode, StringComparison.Ordinal);
        Assert.Contains("CapabilityConnectionKindPolicy.MailApi", reviewCode, StringComparison.Ordinal);
        Assert.Contains("CapabilityConnectionKindPolicy.Smtp", reviewCode, StringComparison.Ordinal);
        Assert.Contains("IMailGatewayConnectionRepository", reviewCode, StringComparison.Ordinal);
        Assert.Contains("ISmtpConnectionRepository", reviewCode, StringComparison.Ordinal);
        Assert.Contains("_connectionReadiness.ResolveAsync", reviewCode, StringComparison.Ordinal);
        Assert.Contains("GetBindingsAsync", reviewCode, StringComparison.Ordinal);
        Assert.Contains("_connectionsReady", reviewCode, StringComparison.Ordinal);
        Assert.Contains("ConnectionManagementOutcome.Stale", reviewCode, StringComparison.Ordinal);
        Assert.Contains("RefreshConnectionBindingsAsync", reviewCode, StringComparison.Ordinal);
        Assert.Contains("IConnectionProfileRepository", pageCode, StringComparison.Ordinal);
        Assert.Contains("IMailGatewayConnectionRepository", pageCode, StringComparison.Ordinal);
        Assert.Contains("ISmtpConnectionRepository", pageCode, StringComparison.Ordinal);
        Assert.Contains("MailGatewayConnectionManagementService", pageCode, StringComparison.Ordinal);
        Assert.Contains("SmtpConnectionManagementService", pageCode, StringComparison.Ordinal);
        Assert.Contains("ICapabilityConnectionReadinessResolver", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsCredentialStore", reviewCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAsync", reviewCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CanonicalManifestJson =", reviewCode, StringComparison.Ordinal);
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
        Assert.Contains("외부 지식 도구 연결", settings, StringComparison.Ordinal);
        Assert.Contains("McpConnectionPolicy.TryValidate", mcpSection, StringComparison.Ordinal);
        Assert.Contains("SetControlsEnabled(!_operationGuard.IsBusy)", settingsCode, StringComparison.Ordinal);
        Assert.Contains("ClearRoutineSuggestionHistory_Click", settingsCode, StringComparison.Ordinal);
        Assert.Contains("if (_activeOperation is not null) return false", operationGuard, StringComparison.Ordinal);
        Assert.Contains("_activeOperation?.Cancel()", operationGuard, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagementPageRefreshesAreCancelledWhenSupersededOrUnloaded()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        foreach (var fileName in new[] { "CapabilitiesPage.xaml.cs", "ConnectionsPage.xaml.cs", "MemoryPage.xaml.cs", "SchedulePage.xaml.cs" })
        {
            var code = File.ReadAllText(Path.Combine(desktop, fileName));
            Assert.Contains("_refreshCancellation", code, StringComparison.Ordinal);
            Assert.Contains("Unloaded +=", code, StringComparison.Ordinal);
            Assert.Contains("catch (OperationCanceledException)", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ListForManagementAsync(query, results.Count, pageSize, CancellationToken.None)", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FluentWorkspaceV4UsesPinnedWpfUiAcrossEveryDesktopWindow()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var project = File.ReadAllText(Path.Combine(desktop, "LIGClaw.Desktop.csproj"));
        var app = File.ReadAllText(Path.Combine(desktop, "App.xaml"));
        var controls = File.ReadAllText(Path.Combine(desktop, "Themes", "Controls.xaml"));

        Assert.Contains("<PackageReference Include=\"WPF-UI\" Version=\"4.3.0\" />", project,
            StringComparison.Ordinal);
        Assert.Contains("<Content Include=\"ThirdPartyNotices.txt\">", project,
            StringComparison.Ordinal);
        var notices = File.ReadAllText(Path.Combine(desktop, "ThirdPartyNotices.txt"));
        Assert.Contains("WPF-UI 4.3.0", notices, StringComparison.Ordinal);
        Assert.Contains("MIT License", notices, StringComparison.Ordinal);
        var themeIndex = app.IndexOf("<ui:ThemesDictionary Theme=\"Light\" />", StringComparison.Ordinal);
        var controlsIndex = app.IndexOf("<ui:ControlsDictionary />", StringComparison.Ordinal);
        var brandIndex = app.IndexOf("Themes/BrandTokens.xaml", StringComparison.Ordinal);
        var overridesIndex = app.IndexOf("Themes/Controls.xaml", StringComparison.Ordinal);
        Assert.True(themeIndex >= 0 && themeIndex < controlsIndex);
        Assert.True(controlsIndex < brandIndex && brandIndex < overridesIndex);
        Assert.DoesNotContain("PresentationFramework.Fluent", app, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowBackdropType", controls, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowCornerPreference", controls, StringComparison.Ordinal);
        Assert.DoesNotContain("SymbolIcon", controls, StringComparison.Ordinal);

        var windowFiles = new[]
        {
            "MainWindow.xaml",
            "AgentJobEditorWindow.xaml",
            "AgentJobHistoryWindow.xaml",
            "CapabilityReviewWindow.xaml",
            "DiagnosticsWindow.xaml",
            "MemoryEditorWindow.xaml",
            "ScheduleEditorWindow.xaml",
            "SensitiveContextPreviewWindow.xaml",
            "ToolApprovalWindow.xaml",
        };
        foreach (var fileName in windowFiles)
        {
            var xaml = File.ReadAllText(Path.Combine(desktop, fileName));
            Assert.StartsWith("<ui:FluentWindow", xaml, StringComparison.Ordinal);
            Assert.Contains("xmlns:ui=\"http://schemas.lepo.co/wpfui/2022/xaml\"", xaml,
                StringComparison.Ordinal);
            Assert.Single(Regex.Matches(xaml, @"<ui:TitleBar\b").Cast<Match>());
            Assert.Contains("<ui:TitleBar", xaml, StringComparison.Ordinal);
            Assert.Contains("Height=\"32\"", xaml, StringComparison.Ordinal);
            Assert.EndsWith("</ui:FluentWindow>", xaml.TrimEnd(), StringComparison.Ordinal);
        }

        var mainWindow = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml"));
        Assert.Contains("x:Name=\"MainTitleBar\"", mainWindow, StringComparison.Ordinal);

        foreach (var baseStyle in new[]
        {
            "FluentAppWindowBaseStyle",
            "FluentButtonBaseStyle",
            "FluentTextBoxBaseStyle",
            "FluentRichTextBoxBaseStyle",
            "FluentComboBoxBaseStyle",
            "FluentPasswordBoxBaseStyle",
            "FluentListBoxBaseStyle",
        })
        {
            Assert.Contains($"x:Key=\"{baseStyle}\"", controls, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SettingsUseNativeFluentToggleSwitchesForBinaryPreferences()
    {
        var settings = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "LIGClaw.Desktop",
            "SettingsPage.xaml"));
        var toggleNames = new[]
        {
            "StartWithWindowsCheckBox",
            "VoiceInputEnabledCheckBox",
            "AutoReadEnabledCheckBox",
            "RoutineSuggestionsEnabledCheckBox",
            "SemanticMemoryEnabledCheckBox",
            "McpEnabledCheckBox",
        };

        foreach (var name in toggleNames)
        {
            Assert.Matches($"<ui:ToggleSwitch[^>]*x:Name=\"{name}\"", settings);
        }
        Assert.DoesNotContain("Style=\"{StaticResource ToggleSwitchStyle}\"", settings,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NamedInputsAndIconOnlyButtonsExposeAccessibleNames()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "src", "LIGClaw.Desktop");
        var xamlNamespace = System.Xml.Linq.XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var inputTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "TextBox",
            "RichTextBox",
            "PasswordBox",
            "ComboBox",
            "CheckBox",
            "ToggleSwitch",
        };
        var gaps = new List<string>();

        foreach (var file in Directory.GetFiles(desktop, "*.xaml", SearchOption.AllDirectories))
        {
            var document = System.Xml.Linq.XDocument.Load(file, System.Xml.Linq.LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                var controlName = element.Attribute(xamlNamespace + "Name")?.Value;
                if (string.IsNullOrWhiteSpace(controlName)) continue;

                var automationName = element.Attribute("AutomationProperties.Name")?.Value;
                if (inputTypes.Contains(element.Name.LocalName))
                {
                    var content = element.Attribute("Content")?.Value;
                    if (string.IsNullOrWhiteSpace(automationName) && string.IsNullOrWhiteSpace(content))
                    {
                        gaps.Add($"{Path.GetFileName(file)}:{controlName}");
                    }
                    continue;
                }

                if (!element.Name.LocalName.Equals("Button", StringComparison.Ordinal)) continue;
                var buttonContent = element.Attribute("Content")?.Value;
                var hasTextDescendant = element.Descendants().Any(descendant =>
                    descendant.Name.LocalName.Equals("TextBlock", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(descendant.Attribute("Text")?.Value));
                if (string.IsNullOrWhiteSpace(automationName)
                    && string.IsNullOrWhiteSpace(buttonContent)
                    && !hasTextDescendant)
                {
                    gaps.Add($"{Path.GetFileName(file)}:{controlName}");
                }
            }
        }

        Assert.Empty(gaps);
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
