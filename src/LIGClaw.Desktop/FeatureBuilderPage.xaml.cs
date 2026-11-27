using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows;
using LIGClaw.Application.Capabilities;
using LIGClaw.Domain;

namespace LIGClaw.Desktop;

public partial class FeatureBuilderPage : System.Windows.Controls.UserControl
{
    private readonly IFeatureBuilderManagementService _management;
    private readonly Func<CancellationToken, Task<FeatureBuilderInventory>> _inventoryProvider;
    private readonly Func<FeatureBuilderProposal, CancellationToken, Task<FeatureBuilderManagementResult>> _testRunner;
    private readonly ObservableCollection<QuestionInput> _questionInputs = [];
    private CancellationTokenSource? _operationCancellation;
    private FeatureBuilderInventory? _inventory;
    private FeatureBuilderSession? _session;
    private FeatureBuilderProposal? _proposal;
    private bool _testsPassed;
    private bool _busy;

    internal FeatureBuilderPage(
        IFeatureBuilderManagementService management,
        Func<CancellationToken, Task<FeatureBuilderInventory>> inventoryProvider,
        Func<FeatureBuilderProposal, CancellationToken, Task<FeatureBuilderManagementResult>> testRunner,
        string? initialRequirement = null)
    {
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _inventoryProvider = inventoryProvider ?? throw new ArgumentNullException(nameof(inventoryProvider));
        _testRunner = testRunner ?? throw new ArgumentNullException(nameof(testRunner));
        InitializeComponent();
        QuestionList.ItemsSource = _questionInputs;
        Loaded += (_, _) => RequirementTextBox.Focus();
        Unloaded += (_, _) => Interlocked.Exchange(ref _operationCancellation, null)?.Cancel();
        ApplyInitialRequirement(initialRequirement);
        UpdateControlState();
    }

    internal event EventHandler? BackRequested;
    internal event EventHandler<CapabilityVersionRef>? DraftReadyForReview;

    private async void Generate_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiAsync(StartGenerationAsync);

    private async void Answer_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiAsync(SubmitAnswersAsync);

    private async void Test_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiAsync(RunTestsAsync);

    private async void Apply_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiAsync(ApplyAsync);

    private async void Reject_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiAsync(RejectAsync);

    private void Back_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    internal async Task StartGenerationAsync()
    {
        if (_busy) return;
        if (_session?.State == FeatureBuilderSessionState.Interrupted && _inventory is not null)
        {
            await RunOperationAsync(cancellationToken => GenerateForSessionAsync([], cancellationToken));
            return;
        }

        var requirement = RequirementTextBox.Text.Trim();
        var validation = FeatureBuilderPolicy.ValidateRequirement(requirement);
        if (!validation.IsValid)
        {
            SetPageStatus(DescribeRequirementFailure(validation.ErrorCode), isError: true);
            RequirementTextBox.Focus();
            return;
        }

        await RunOperationAsync(async cancellationToken =>
        {
            ResetGeneratedView();
            SetPageStatus("사용 가능한 로컬 기능을 확인하고 있어요…", isError: false);
            _inventory = await _inventoryProvider(cancellationToken);
            var started = await _management.StartSessionAsync(
                new StartFeatureBuilderSessionCommand(
                    NewGuidId("fbs_"),
                    requirement,
                    _inventory,
                    ExpectedCatalogRevision: 0,
                    RevisionTarget: null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            if (started.Outcome != FeatureBuilderManagementOutcome.Succeeded || started.Session is null)
            {
                HandleFailure(started, "생성 세션을 시작하지 못했습니다.");
                return;
            }

            _session = started.Session;
            RequirementTextBox.IsReadOnly = true;
            await GenerateForSessionAsync([], cancellationToken);
        });
    }

    internal async Task SubmitAnswersAsync()
    {
        if (_busy || _session?.State != FeatureBuilderSessionState.AwaitingInformation || _inventory is null)
            return;

        var answers = _questionInputs.Select(input =>
                new FeatureBuilderAnswer(input.QuestionId, input.Answer?.Trim() ?? string.Empty))
            .ToArray();
        var validation = FeatureBuilderPolicy.ValidateSubmittedAnswers(
            _session.Answers,
            _session.Questions,
            answers);
        if (!validation.IsValid)
        {
            SetPageStatus(DescribeAnswerFailure(validation.ErrorCode), isError: true);
            return;
        }

        await RunOperationAsync(cancellationToken => GenerateForSessionAsync(answers, cancellationToken));
    }

    internal async Task RunTestsAsync()
    {
        if (_busy || _proposal is not { State: FeatureBuilderProposalState.Pending }) return;
        await RunOperationAsync(async cancellationToken =>
        {
            _testsPassed = false;
            SetProposalStatus("앱에서 안전 검사와 모의 실행을 진행하고 있어요…", isError: false);
            var result = await _testRunner(_proposal, cancellationToken);
            _testsPassed = result.Outcome == FeatureBuilderManagementOutcome.Succeeded &&
                           result.TestRun is { Kind: FeatureBuilderTestRunKind.DryRun, State: FeatureBuilderTestRunState.Passed };
            SetProposalStatus(
                _testsPassed
                    ? "검사를 통과했습니다. 검토 초안을 만들 수 있습니다. 이 단계에서는 기능이 활성화되지 않습니다."
                    : DescribeTestFailure(result),
                isError: !_testsPassed);
        });
    }

    internal async Task ApplyAsync()
    {
        if (_busy || !_testsPassed || _proposal is not { State: FeatureBuilderProposalState.Pending }) return;
        await RunOperationAsync(async cancellationToken =>
        {
            SetProposalStatus("검사 증거를 고정하고 검토 초안을 만들고 있어요…", isError: false);
            var result = await _management.ApplyProposalAsync(
                new ApplyFeatureBuilderProposalCommand(
                    _proposal.ProposalId,
                    _proposal.Revision,
                    NewGuidId("draft_builder_"),
                    DateTimeOffset.UtcNow),
                cancellationToken);
            if (result.Outcome == FeatureBuilderManagementOutcome.Succeeded && result.Draft is not null)
            {
                _proposal = result.Proposal ?? _proposal;
                SetProposalStatus(
                    "검토 초안으로 적용했습니다. 아직 사용되지는 않습니다. 기능 목록의 ‘검토’에서 내용을 확인하고 별도로 적용해야 합니다.",
                    isError: false);
                SetPageStatus("검토 초안이 준비됐습니다. 기능 화면에서 해당 버전을 불러옵니다.", isError: false);
                DraftReadyForReview?.Invoke(this, result.Draft.ExactVersion);
                return;
            }
            HandleFailure(result, "검토 초안을 만들지 못했습니다.", proposalStatus: true);
        });
    }

    internal async Task RejectAsync()
    {
        if (_busy || _proposal is not { State: FeatureBuilderProposalState.Pending }) return;
        await RunOperationAsync(async cancellationToken =>
        {
            var result = await _management.SetProposalDispositionAsync(
                new SetFeatureBuilderProposalDispositionCommand(
                    _proposal.ProposalId,
                    _proposal.Revision,
                    FeatureBuilderProposalState.Rejected,
                    "feature_builder.user_rejected",
                    DateTimeOffset.UtcNow),
                cancellationToken);
            if (result.Outcome == FeatureBuilderManagementOutcome.Succeeded)
            {
                _proposal = result.Proposal ?? _proposal;
                _session = result.Session ?? _session;
                _testsPassed = false;
                SetProposalStatus("제안을 거부했습니다. 어떤 기능 초안도 적용되지 않았습니다.", isError: false);
                SetPageStatus("생성 제안을 거부했습니다.", isError: false);
                return;
            }
            HandleFailure(result, "제안을 거부하지 못했습니다.", proposalStatus: true);
        });
    }

    private async Task GenerateForSessionAsync(
        IReadOnlyList<FeatureBuilderAnswer> answers,
        CancellationToken cancellationToken)
    {
        if (_session is null || _inventory is null) return;
        QuestionsSection.Visibility = Visibility.Collapsed;
        SetPageStatus("격리된 보조 실행기에서 제안을 만들고 있어요…", isError: false);
        var nowUtc = DateTimeOffset.UtcNow;
        var result = await _management.GenerateAsync(
            new GenerateFeatureBuilderProposalCommand(
                _session.SessionId,
                _session.Revision,
                NewGuidId("fbgen_"),
                answers,
                _inventory,
                nowUtc.AddSeconds(55),
                nowUtc),
            cancellationToken);
        _session = result.Session ?? _session;

        if (result.Outcome == FeatureBuilderManagementOutcome.Succeeded && result.Proposal is not null)
        {
            ShowProposal(result.Proposal);
            return;
        }
        if (result.Outcome == FeatureBuilderManagementOutcome.Succeeded &&
            result.Session?.State == FeatureBuilderSessionState.AwaitingInformation)
        {
            ShowQuestions(result.Session.Questions);
            return;
        }

        HandleFailure(result, result.Outcome switch
        {
            FeatureBuilderManagementOutcome.Refused => "안전 정책 또는 현재 로컬 기능 범위로는 이 요청을 만들 수 없습니다.",
            FeatureBuilderManagementOutcome.Quarantined => "생성 결과가 앱의 안전 검사를 통과하지 못해 격리됐습니다.",
            FeatureBuilderManagementOutcome.Interrupted => "생성이 중단됐습니다. 같은 요구사항으로 다시 시도할 수 있습니다.",
            _ => "기능 제안을 생성하지 못했습니다.",
        });
    }

    private void ShowQuestions(IReadOnlyList<FeatureBuilderQuestion> questions)
    {
        _questionInputs.Clear();
        foreach (var question in questions) _questionInputs.Add(new QuestionInput(question));
        QuestionsSection.Visibility = Visibility.Visible;
        ProposalSection.Visibility = Visibility.Collapsed;
        SetPageStatus($"추가 질문 {questions.Count}개에 답해 주세요.", isError: false);
        _ = Dispatcher.BeginInvoke(() => QuestionList.Focus());
    }

    private void ShowProposal(FeatureBuilderProposal proposal)
    {
        _proposal = proposal;
        _testsPassed = false;
        QuestionsSection.Visibility = Visibility.Collapsed;
        ProposalSection.Visibility = Visibility.Visible;
        var manifest = proposal.Candidate!.Manifest;
        ProposalNameText.Text = manifest.DisplayName;
        ProposalIdentityText.Text =
            $"{manifest.Id.Value} · 버전 {manifest.Version.Value} · {UserFacingText.CapabilityType(manifest.Kind)} · {UserFacingText.Risk(manifest.MaximumRisk)}";
        var tools = manifest.RequiredTools.Count == 0
            ? "필요한 작업 도구 없음"
            : $"필요한 작업 도구 {manifest.RequiredTools.Count}개: {string.Join(", ", manifest.RequiredTools.Select(UserFacingText.Tool))}";
        var connections = manifest.RequiredConnections.Count == 0
            ? "필요한 외부 연결 없음"
            : $"필요한 외부 연결 {manifest.RequiredConnections.Count}개: " +
              string.Join(", ", manifest.RequiredConnections.Select(item => UserFacingText.ConnectionKind(item.Kind)));
        ProposalDependencyText.Text = $"{tools} · {connections}";
        ProposalDescriptionText.Text = manifest.Description;
        LimitationsList.ItemsSource = proposal.KnownLimitations.Count == 0
            ? new[] { "보고된 제한 없음" }
            : proposal.KnownLimitations;
        SetProposalStatus(
            "제안은 아직 실행되거나 활성화되지 않았습니다. 검사를 실행한 뒤 검토 초안으로만 적용할 수 있습니다.",
            isError: false);
        SetPageStatus("생성 제안을 안전한 메타데이터로 요약했습니다.", isError: false);
        _ = Dispatcher.BeginInvoke(() => TestButton.Focus());
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy) return;
        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _operationCancellation, cancellation)?.Cancel();
        _busy = true;
        UpdateControlState();
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SetPageStatus("작업을 중단했습니다.", isError: false);
        }
        catch
        {
            SetPageStatus("기능 만들기 작업을 완료하지 못했습니다. 다시 시도하세요.", isError: true);
        }
        finally
        {
            Interlocked.CompareExchange(ref _operationCancellation, null, cancellation);
            _busy = false;
            UpdateControlState();
        }
    }

    private static async Task ExecuteUiAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void HandleFailure(
        FeatureBuilderManagementResult result,
        string fallback,
        bool proposalStatus = false)
    {
        var message = DescribeFailure(result, fallback);
        if (proposalStatus) SetProposalStatus(message, isError: true);
        SetPageStatus(message, isError: true);
    }

    private void UpdateControlState()
    {
        var proposalPending = _proposal?.State == FeatureBuilderProposalState.Pending;
        GenerateButton.IsEnabled = !_busy &&
            (_session is null || _session.State == FeatureBuilderSessionState.Interrupted);
        GenerateButton.Content = _session?.State == FeatureBuilderSessionState.Interrupted
            ? "생성 다시 시도"
            : "초안 생성";
        RequirementTextBox.IsEnabled = !_busy;
        AnswerButton.IsEnabled = !_busy && _session?.State == FeatureBuilderSessionState.AwaitingInformation;
        QuestionList.IsEnabled = !_busy;
        TestButton.IsEnabled = !_busy && proposalPending;
        RejectButton.IsEnabled = !_busy && proposalPending;
        ApplyButton.IsEnabled = !_busy && proposalPending && _testsPassed;
    }

    private void ResetGeneratedView()
    {
        _session = null;
        _proposal = null;
        _inventory = null;
        _testsPassed = false;
        _questionInputs.Clear();
        QuestionsSection.Visibility = Visibility.Collapsed;
        ProposalSection.Visibility = Visibility.Collapsed;
        RequirementTextBox.IsReadOnly = false;
    }

    private void ApplyInitialRequirement(string? initialRequirement)
    {
        if (initialRequirement is null)
        {
            SetPageStatus("기능 요구사항을 입력해 주세요.", isError: false);
            return;
        }

        var requirement = initialRequirement.Trim();
        var validation = FeatureBuilderPolicy.ValidateRequirement(requirement);
        if (!validation.IsValid)
        {
            RequirementTextBox.Clear();
            SetPageStatus(DescribeRequirementFailure(validation.ErrorCode), isError: true);
            return;
        }

        RequirementTextBox.Text = requirement;
        RequirementTextBox.CaretIndex = requirement.Length;
        SetPageStatus(
            "대화 입력을 가져왔습니다. 내용을 확인한 뒤 직접 초안 생성을 눌러 주세요.",
            isError: false);
    }

    private void SetPageStatus(string message, bool isError)
    {
        PageStatusText.Foreground = FindBrush(isError);
        PageStatusText.Text = message;
    }

    private void SetProposalStatus(string message, bool isError)
    {
        ProposalStatusText.Foreground = FindBrush(isError);
        ProposalStatusText.Text = message;
    }

    private System.Windows.Media.Brush FindBrush(bool isError) =>
        (System.Windows.Media.Brush)FindResource(isError ? "DangerBrush" : "TextSecondaryBrush");

    private static string DescribeRequirementFailure(string? errorCode) =>
        errorCode == "feature_builder.requirement.secret_detected"
            ? "요구사항에서 비밀번호나 접속 키로 보이는 내용을 제거하세요. 비밀 정보는 기존 연결 설정에서만 관리합니다."
            : "요구사항을 8,192자 이내로 구체적으로 입력하세요.";

    private static string DescribeAnswerFailure(string? errorCode) =>
        errorCode == "feature_builder.answer.secret_detected"
            ? "답변에 비밀값을 입력할 수 없습니다. 비밀이 아닌 요구사항만 적어 주세요."
            : "모든 질문에 허용된 형식으로 답해 주세요.";

    private static string DescribeTestFailure(FeatureBuilderManagementResult result) => result.Outcome switch
    {
        FeatureBuilderManagementOutcome.Interrupted => "검사가 중단됐습니다. 다시 실행하세요.",
        FeatureBuilderManagementOutcome.NotReady => "현재 제안을 검사할 수 없습니다. 제안 상태를 다시 확인하세요.",
        FeatureBuilderManagementOutcome.Stale => "제안이 변경되었습니다. 최신 상태에서 검사를 다시 실행하세요.",
        _ => "안전 검사 또는 모의 실행을 통과하지 못했습니다. 이 제안은 적용할 수 없습니다.",
    };

    private static string DescribeFailure(FeatureBuilderManagementResult result, string fallback)
    {
        if (result.ErrorCode?.Contains("secret", StringComparison.Ordinal) == true)
            return "비밀 정보를 포함할 수 없습니다. 접속 키·비밀번호·토큰을 제거하세요.";
        return result.Outcome switch
        {
            FeatureBuilderManagementOutcome.Stale => "작업 중 기능 또는 진행 상태가 바뀌었습니다. 목록에서 다시 시작하세요.",
            FeatureBuilderManagementOutcome.NotFound => "진행 중인 기능 만들기 작업이나 제안을 찾지 못했습니다. 다시 시작하세요.",
            FeatureBuilderManagementOutcome.NotReady => "현재 단계에서는 이 작업을 진행할 수 없습니다.",
            FeatureBuilderManagementOutcome.Refused => "안전 정책 또는 현재 로컬 기능 범위로는 이 요청을 만들 수 없습니다.",
            FeatureBuilderManagementOutcome.Quarantined => "생성 결과가 앱의 안전 검사를 통과하지 못해 격리됐습니다.",
            FeatureBuilderManagementOutcome.Interrupted => "작업이 중단됐습니다. 다시 시도하세요.",
            _ => fallback,
        };
    }

    private static string NewGuidId(string prefix) =>
        $"{prefix}{new Guid(RandomNumberGenerator.GetBytes(16)):N}";

    internal sealed class QuestionInput
    {
        internal QuestionInput(FeatureBuilderQuestion question)
        {
            QuestionId = question.QuestionId;
            Prompt = question.Prompt;
            Choices = question.Choices;
            TextAnswerVisibility = question.AnswerKind == FeatureBuilderAnswerKind.Text
                ? Visibility.Visible
                : Visibility.Collapsed;
            ChoiceAnswerVisibility = question.AnswerKind == FeatureBuilderAnswerKind.SingleChoice
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public string QuestionId { get; }
        public string Prompt { get; }
        public IReadOnlyList<string> Choices { get; }
        public string? Answer { get; set; }
        public Visibility TextAnswerVisibility { get; }
        public Visibility ChoiceAnswerVisibility { get; }
        public string InputAutomationName => $"{Prompt} 답변";
    }
}
