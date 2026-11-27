using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace LIGClaw.Domain;

public enum FeatureBuilderMode
{
    Create,
    Revise,
}

public enum FeatureBuilderSessionState
{
    Collecting,
    AwaitingInformation,
    Generating,
    ProposalReady,
    Completed,
    Cancelled,
    Interrupted,
}

public enum FeatureBuilderProposalState
{
    Pending,
    Applied,
    Rejected,
    Quarantined,
    Stale,
}

public enum FeatureBuilderTestRunKind
{
    StaticScan,
    DryRun,
}

public enum FeatureBuilderTestRunState
{
    Running,
    Passed,
    Failed,
    Interrupted,
}

public enum FeatureBuilderAnswerKind
{
    Text,
    SingleChoice,
}

public sealed record FeatureBuilderQuestion
{
    private IReadOnlyList<string> _choices = [];

    public FeatureBuilderQuestion(
        string questionId,
        string prompt,
        FeatureBuilderAnswerKind answerKind,
        IReadOnlyList<string>? choices = null)
    {
        QuestionId = questionId;
        Prompt = prompt;
        AnswerKind = answerKind;
        Choices = choices ?? [];
    }

    public string QuestionId { get; init; }
    public string Prompt { get; init; }
    public FeatureBuilderAnswerKind AnswerKind { get; init; }
    public IReadOnlyList<string> Choices
    {
        get => _choices;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _choices = Array.AsReadOnly(value.ToArray());
        }
    }
}

public sealed record FeatureBuilderAnswer(string QuestionId, string Value);

public sealed record FeatureBuilderRevisionTarget(
    CapabilityVersionRef BaseVersion,
    long ExpectedCatalogRevision);

public sealed record FeatureBuilderSession
{
    private IReadOnlyList<FeatureBuilderAnswer> _answers = [];
    private IReadOnlyList<FeatureBuilderQuestion> _questions = [];

    public FeatureBuilderSession(
        string sessionId,
        FeatureBuilderMode mode,
        FeatureBuilderSessionState state,
        long revision,
        string requirement,
        CapabilityContentHash requirementHash,
        IReadOnlyList<FeatureBuilderAnswer> answers,
        IReadOnlyList<FeatureBuilderQuestion> questions,
        FeatureBuilderRevisionTarget? revisionTarget,
        long expectedCatalogRevision,
        CapabilityContentHash inventoryHash,
        string? activeGenerationId,
        string? fixedErrorCode,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        SessionId = sessionId;
        Mode = mode;
        State = state;
        Revision = revision;
        Requirement = requirement;
        RequirementHash = requirementHash;
        Answers = answers;
        Questions = questions;
        RevisionTarget = revisionTarget;
        ExpectedCatalogRevision = expectedCatalogRevision;
        InventoryHash = inventoryHash;
        ActiveGenerationId = activeGenerationId;
        FixedErrorCode = fixedErrorCode;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string SessionId { get; init; }
    public FeatureBuilderMode Mode { get; init; }
    public FeatureBuilderSessionState State { get; init; }
    public long Revision { get; init; }
    public string Requirement { get; init; }
    public CapabilityContentHash RequirementHash { get; init; }
    public IReadOnlyList<FeatureBuilderAnswer> Answers
    {
        get => _answers;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _answers = Array.AsReadOnly(value.ToArray());
        }
    }
    public IReadOnlyList<FeatureBuilderQuestion> Questions
    {
        get => _questions;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _questions = Array.AsReadOnly(value.ToArray());
        }
    }
    public FeatureBuilderRevisionTarget? RevisionTarget { get; init; }
    public long ExpectedCatalogRevision { get; init; }
    public CapabilityContentHash InventoryHash { get; init; }
    public string? ActiveGenerationId { get; init; }
    public string? FixedErrorCode { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record FeatureBuilderProposal
{
    private IReadOnlyList<string> _knownLimitations = [];

    public FeatureBuilderProposal(
        string proposalId,
        string sessionId,
        long sessionRevision,
        string generationId,
        CapabilityContentHash requestHash,
        CapabilityContentHash responseHash,
        CapabilityContentHash inventoryHash,
        CapabilityVersionRef? baseVersion,
        long expectedCatalogRevision,
        CapabilityContentHash? candidateContentHash,
        CapabilityContentHash proposalHash,
        CapabilityVersionPayload? candidate,
        IReadOnlyList<string> knownLimitations,
        CapabilityContentHash? testEvidenceHash,
        FeatureBuilderProposalState state,
        long revision,
        string? fixedIssueCode,
        string? appliedDraftId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? terminalAtUtc)
    {
        ProposalId = proposalId;
        SessionId = sessionId;
        SessionRevision = sessionRevision;
        GenerationId = generationId;
        RequestHash = requestHash;
        ResponseHash = responseHash;
        InventoryHash = inventoryHash;
        BaseVersion = baseVersion;
        ExpectedCatalogRevision = expectedCatalogRevision;
        CandidateContentHash = candidateContentHash;
        ProposalHash = proposalHash;
        Candidate = candidate;
        KnownLimitations = knownLimitations;
        TestEvidenceHash = testEvidenceHash;
        State = state;
        Revision = revision;
        FixedIssueCode = fixedIssueCode;
        AppliedDraftId = appliedDraftId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        TerminalAtUtc = terminalAtUtc;
    }

    public string ProposalId { get; init; }
    public string SessionId { get; init; }
    public long SessionRevision { get; init; }
    public string GenerationId { get; init; }
    public CapabilityContentHash RequestHash { get; init; }
    public CapabilityContentHash ResponseHash { get; init; }
    public CapabilityContentHash InventoryHash { get; init; }
    public CapabilityVersionRef? BaseVersion { get; init; }
    public long ExpectedCatalogRevision { get; init; }
    public CapabilityContentHash? CandidateContentHash { get; init; }
    public CapabilityContentHash ProposalHash { get; init; }
    public CapabilityVersionPayload? Candidate { get; init; }
    public IReadOnlyList<string> KnownLimitations
    {
        get => _knownLimitations;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _knownLimitations = Array.AsReadOnly(value.ToArray());
        }
    }
    public CapabilityContentHash? TestEvidenceHash { get; init; }
    public FeatureBuilderProposalState State { get; init; }
    public long Revision { get; init; }
    public string? FixedIssueCode { get; init; }
    public string? AppliedDraftId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? TerminalAtUtc { get; init; }
}

public sealed record FeatureBuilderTestRun(
    string TestRunId,
    string ProposalId,
    FeatureBuilderTestRunKind Kind,
    int Attempt,
    CapabilityContentHash CandidateContentHash,
    string EngineVersion,
    FeatureBuilderTestRunState State,
    long Revision,
    string CanonicalResultJson,
    string? FixedErrorCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record FeatureBuilderValidationResult(bool IsValid, string? ErrorCode = null)
{
    public static FeatureBuilderValidationResult Valid { get; } = new(true);
    public static FeatureBuilderValidationResult Invalid(string errorCode) => new(false, errorCode);
}

public static partial class FeatureBuilderPolicy
{
    public const int MaximumRequirementCharacters = 8_192;
    public const int MaximumRequirementUtf8Bytes = 32 * 1_024;
    public const int MaximumAnswers = 16;
    public const int MaximumAnswersUtf8Bytes = 32 * 1_024;
    public const int MaximumAnswerCharacters = 8_000;
    public const int MinimumQuestionsPerTurn = 1;
    public const int MaximumQuestionsPerTurn = 3;
    public const int MaximumQuestionPromptCharacters = 512;
    public const int MaximumQuestionPromptUtf8Bytes = 2_048;
    public const int MinimumChoices = 2;
    public const int MaximumChoices = 8;
    public const int MaximumChoiceCharacters = 256;
    public const int MaximumChoiceUtf8Bytes = 1_024;
    public const int MaximumLimitations = 16;
    public const int MaximumLimitationCharacters = 512;

    public static FeatureBuilderValidationResult ValidateRequirement(string? requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement) ||
            requirement.Length > MaximumRequirementCharacters ||
            !TryGetUtf8ByteCount(requirement, out var bytes) ||
            bytes > MaximumRequirementUtf8Bytes)
            return FeatureBuilderValidationResult.Invalid("feature_builder.requirement.out_of_bounds");
        if (ContainsDirectSecret(requirement, sensitiveQuestion: false))
            return FeatureBuilderValidationResult.Invalid("feature_builder.requirement.secret_detected");
        return FeatureBuilderValidationResult.Valid;
    }

    public static FeatureBuilderValidationResult ValidateQuestions(
        IReadOnlyList<FeatureBuilderQuestion>? questions)
    {
        if (questions is null || questions.Count is < MinimumQuestionsPerTurn or > MaximumQuestionsPerTurn)
            return FeatureBuilderValidationResult.Invalid("feature_builder.questions.count_invalid");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in questions)
        {
            if (!IsQuestionId(question.QuestionId) || !ids.Add(question.QuestionId))
                return FeatureBuilderValidationResult.Invalid("feature_builder.question.id_invalid");
            if (string.IsNullOrWhiteSpace(question.Prompt) ||
                question.Prompt.Length > MaximumQuestionPromptCharacters ||
                !TryGetUtf8ByteCount(question.Prompt, out var promptBytes) ||
                promptBytes > MaximumQuestionPromptUtf8Bytes)
                return FeatureBuilderValidationResult.Invalid("feature_builder.question.prompt_out_of_bounds");
            if (ContainsSecretRequest(question.Prompt))
                return FeatureBuilderValidationResult.Invalid("feature_builder.question.secret_input_forbidden");
            if (IsSensitiveQuestionId(question.QuestionId))
                return FeatureBuilderValidationResult.Invalid("feature_builder.question.secret_input_forbidden");
            if (question.AnswerKind == FeatureBuilderAnswerKind.Text)
            {
                if (question.Choices.Count != 0)
                    return FeatureBuilderValidationResult.Invalid("feature_builder.question.choices_invalid");
            }
            else if (question.AnswerKind == FeatureBuilderAnswerKind.SingleChoice)
            {
                if (question.Choices.Count is < MinimumChoices or > MaximumChoices)
                    return FeatureBuilderValidationResult.Invalid("feature_builder.question.choices_invalid");
                var choices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var choice in question.Choices)
                {
                    if (ContainsSecretRequest(choice))
                        return FeatureBuilderValidationResult.Invalid("feature_builder.question.secret_input_forbidden");
                    if (string.IsNullOrWhiteSpace(choice) ||
                        choice.Length > MaximumChoiceCharacters ||
                        !TryGetUtf8ByteCount(choice, out var choiceBytes) ||
                        choiceBytes > MaximumChoiceUtf8Bytes ||
                        !choices.Add(choice))
                        return FeatureBuilderValidationResult.Invalid("feature_builder.question.choices_invalid");
                }
            }
            else
            {
                return FeatureBuilderValidationResult.Invalid("feature_builder.question.answer_kind_invalid");
            }
        }

        return FeatureBuilderValidationResult.Valid;
    }

    public static FeatureBuilderValidationResult ValidateSubmittedAnswers(
        IReadOnlyList<FeatureBuilderAnswer> existingAnswers,
        IReadOnlyList<FeatureBuilderQuestion> questions,
        IReadOnlyList<FeatureBuilderAnswer>? submittedAnswers)
    {
        ArgumentNullException.ThrowIfNull(existingAnswers);
        ArgumentNullException.ThrowIfNull(questions);
        var questionValidation = ValidateQuestions(questions);
        if (!questionValidation.IsValid) return questionValidation;
        if (existingAnswers.Count > MaximumAnswers ||
            existingAnswers.Select(answer => answer.QuestionId).Distinct(StringComparer.Ordinal).Count() !=
            existingAnswers.Count ||
            existingAnswers.Any(answer => !IsQuestionId(answer.QuestionId) ||
                                          string.IsNullOrWhiteSpace(answer.Value) ||
                                          answer.Value.Length > MaximumAnswerCharacters ||
                                          ContainsDirectSecret(answer.Value, IsSensitiveQuestionId(answer.QuestionId))))
            return FeatureBuilderValidationResult.Invalid("feature_builder.answers.transcript_invalid");
        if (submittedAnswers is null || submittedAnswers.Count != questions.Count ||
            existingAnswers.Count + submittedAnswers.Count > MaximumAnswers)
            return FeatureBuilderValidationResult.Invalid("feature_builder.answers.count_invalid");

        var allIds = new HashSet<string>(existingAnswers.Select(answer => answer.QuestionId), StringComparer.Ordinal);
        var pending = questions.ToDictionary(question => question.QuestionId, StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var answer in existingAnswers)
        {
            if (!TryGetUtf8ByteCount(answer.Value, out var bytes))
                return FeatureBuilderValidationResult.Invalid("feature_builder.answer.out_of_bounds");
            totalBytes += bytes;
        }

        foreach (var answer in submittedAnswers)
        {
            if (!pending.Remove(answer.QuestionId, out var question) || !allIds.Add(answer.QuestionId))
                return FeatureBuilderValidationResult.Invalid("feature_builder.answer.question_mismatch");
            if (string.IsNullOrWhiteSpace(answer.Value) ||
                answer.Value.Length > MaximumAnswerCharacters ||
                !TryGetUtf8ByteCount(answer.Value, out var bytes))
                return FeatureBuilderValidationResult.Invalid("feature_builder.answer.out_of_bounds");
            totalBytes += bytes;
            if (question.AnswerKind == FeatureBuilderAnswerKind.SingleChoice &&
                !question.Choices.Contains(answer.Value, StringComparer.Ordinal))
                return FeatureBuilderValidationResult.Invalid("feature_builder.answer.choice_invalid");
            if (ContainsDirectSecret(answer.Value, IsSensitiveQuestionId(question.QuestionId)))
                return FeatureBuilderValidationResult.Invalid("feature_builder.answer.secret_detected");
        }

        return pending.Count != 0 || totalBytes > MaximumAnswersUtf8Bytes
            ? FeatureBuilderValidationResult.Invalid("feature_builder.answers.total_out_of_bounds")
            : FeatureBuilderValidationResult.Valid;
    }

    public static FeatureBuilderValidationResult ValidateLimitations(IReadOnlyList<string>? limitations)
    {
        if (limitations is null || limitations.Count > MaximumLimitations ||
            limitations.Distinct(StringComparer.Ordinal).Count() != limitations.Count ||
            limitations.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > MaximumLimitationCharacters))
            return FeatureBuilderValidationResult.Invalid("feature_builder.limitations.invalid");
        return FeatureBuilderValidationResult.Valid;
    }

    public static bool CanTransition(FeatureBuilderSessionState from, FeatureBuilderSessionState to) =>
        (from, to) switch
        {
            (FeatureBuilderSessionState.Collecting, FeatureBuilderSessionState.Generating) => true,
            (FeatureBuilderSessionState.AwaitingInformation, FeatureBuilderSessionState.Generating) => true,
            (FeatureBuilderSessionState.Interrupted, FeatureBuilderSessionState.Generating) => true,
            (FeatureBuilderSessionState.Generating, FeatureBuilderSessionState.AwaitingInformation) => true,
            (FeatureBuilderSessionState.Generating, FeatureBuilderSessionState.ProposalReady) => true,
            (FeatureBuilderSessionState.Generating, FeatureBuilderSessionState.Completed) => true,
            (FeatureBuilderSessionState.Generating, FeatureBuilderSessionState.Interrupted) => true,
            (FeatureBuilderSessionState.ProposalReady, FeatureBuilderSessionState.Completed) => true,
            (FeatureBuilderSessionState.Collecting or
             FeatureBuilderSessionState.AwaitingInformation or
             FeatureBuilderSessionState.Generating or
             FeatureBuilderSessionState.ProposalReady or
             FeatureBuilderSessionState.Interrupted, FeatureBuilderSessionState.Cancelled) => true,
            _ => false,
        };

    public static bool CanTransition(FeatureBuilderProposalState from, FeatureBuilderProposalState to) =>
        from == FeatureBuilderProposalState.Pending &&
        to is FeatureBuilderProposalState.Applied or
            FeatureBuilderProposalState.Rejected or
            FeatureBuilderProposalState.Quarantined or
            FeatureBuilderProposalState.Stale;

    public static bool CanTransition(FeatureBuilderTestRunState from, FeatureBuilderTestRunState to) =>
        from == FeatureBuilderTestRunState.Running &&
        to is FeatureBuilderTestRunState.Passed or
            FeatureBuilderTestRunState.Failed or
            FeatureBuilderTestRunState.Interrupted;

    public static bool IsSessionId(string? value) => IsPrefixedHex(value, "fbs_", 32);
    public static bool IsGenerationId(string? value) => IsPrefixedHex(value, "fbgen_", 32);
    public static bool IsProposalId(string? value) => IsPrefixedHex(value, "proposal_", 48);
    public static bool IsTestRunId(string? value) => IsPrefixedHex(value, "fbtest_", 32);

    public static bool IsFixedCode(string? value) =>
        value is { Length: >= 3 and <= 128 } &&
        value.All(character => character is >= 'a' and <= 'z' ||
                               char.IsAsciiDigit(character) ||
                               character is '.' or '_');

    public static bool ContainsDirectSecret(string value, bool sensitiveQuestion)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (SecretPlaceholder().IsMatch(trimmed)) return false;
        if (SecretPlaceholderWithSuffix().IsMatch(value)) return true;
        if (sensitiveQuestion && trimmed.Length != 0) return true;
        // Keep a short non-secret sentinel so an assignment cannot consume following prose;
        // a suffix appended directly to the placeholder still remains long enough to reject.
        var scrubbed = SecretPlaceholderFragment().Replace(value, "none").Normalize(NormalizationForm.FormKC);
        var contextual = ContextualSecretAssignment().Match(scrubbed);
        return PrivateKey().IsMatch(scrubbed) ||
               TokenPrefix().IsMatch(scrubbed) ||
               AuthorizationHeader().IsMatch(scrubbed) ||
               SecretAssignment().IsMatch(scrubbed) ||
               Jwt().IsMatch(scrubbed) ||
               UriUserInfo().IsMatch(scrubbed) ||
               contextual.Success && !KnownMissingSecret().IsMatch(contextual.Groups[1].Value);
    }

    private static bool ContainsSecretRequest(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        return SecretRequestTerm().IsMatch(normalized) && SecretRequestVerb().IsMatch(normalized);
    }

    public static bool IsQuestionId(string? value) =>
        value is { Length: >= 3 and <= 66 } &&
        value.StartsWith("q_", StringComparison.Ordinal) &&
        char.IsAsciiLetterOrDigit(value[2]) &&
        char.IsAsciiLetterOrDigit(value[^1]) &&
        value.AsSpan(2).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_-") < 0;

    public static bool IsSensitiveQuestionId(string value)
    {
        Span<char> normalized = stackalloc char[value.Length];
        var length = 0;
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character)) normalized[length++] = char.ToLowerInvariant(character);
        }
        var text = normalized[..length];
        return text.Contains("password", StringComparison.Ordinal) ||
               text.Contains("passwd", StringComparison.Ordinal) ||
               text.Contains("secret", StringComparison.Ordinal) ||
               text.Contains("apikey", StringComparison.Ordinal) ||
               text.Contains("token", StringComparison.Ordinal) ||
               text.Contains("credential", StringComparison.Ordinal) ||
               text.Contains("privatekey", StringComparison.Ordinal);
    }

    private static bool IsPrefixedHex(string? value, string prefix, int digits) =>
        value is not null && value.Length == prefix.Length + digits &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool TryGetUtf8ByteCount(string value, out int byteCount)
    {
        try
        {
            byteCount = new UTF8Encoding(false, true).GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            byteCount = 0;
            return false;
        }
    }

    [GeneratedRegex(@"^(?:\$\{secret:[a-z0-9._-]{1,64}\}|\$\{fixture:[a-z0-9._-]{1,64}\}|<redacted>|\[REDACTED\])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretPlaceholder();

    [GeneratedRegex(@"\$\{secret:[a-z0-9._-]{1,64}\}|\$\{fixture:[a-z0-9._-]{1,64}\}|<redacted>|\[REDACTED\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretPlaceholderFragment();

    [GeneratedRegex(@"(?:\$\{secret:[a-z0-9._-]{1,64}\}|\$\{fixture:[a-z0-9._-]{1,64}\}|<redacted>|\[REDACTED\])[^\s,;.)}\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretPlaceholderWithSuffix();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"(?:sk[-_]|nvapi-|ghp_|github_pat_|xox[a-z]-|AKIA)[A-Za-z0-9_\-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPrefix();

    [GeneratedRegex(@"Authorization\s*:\s*(?:Bearer|Basic)\s+[A-Za-z0-9+/_.=\-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"(?:password|passwd|pwd|api[_-]?key|client[_-]?secret|token)\s*[:=]\s*[^\s\""']{6,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(?:api[ _-]?key|password|passphrase|access[ _-]?token|refresh[ _-]?token|bearer[ _-]?token|client[ _-]?secret|secret[ _-]?key|비밀번호|암호|인증[ _-]?토큰|액세스[ _-]?토큰|API[ _-]?키)[\""']?\s*(?::|=|\bis\b|은|는)\s*[\""']?([^\s\""',;}{]{4,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContextualSecretAssignment();

    [GeneratedRegex(@"^(?:none|null|missing|required|optional|needed|unknown|없음|누락|필요|미정)[.!?]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownMissingSecret();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"https?://[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriUserInfo();

    [GeneratedRegex(@"(?:api[ _-]?key|password|passphrase|access[ _-]?token|refresh[ _-]?token|bearer[ _-]?token|client[ _-]?secret|secret[ _-]?key|credential|비밀번호|암호|인증[ _-]?토큰|액세스[ _-]?토큰|API[ _-]?키)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretRequestTerm();

    [GeneratedRegex(@"(?:enter|input|provide|paste|send|type|reveal|tell|what\s+is|value|입력|제공|붙여|보내|알려|공개|값|무엇|뭐)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretRequestVerb();
}
