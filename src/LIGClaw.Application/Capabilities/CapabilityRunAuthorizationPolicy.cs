using LIGClaw.Domain;

namespace LIGClaw.Application.Capabilities;

public sealed record CapabilityRunInvocationContext(
    string SnapshotHash,
    string ModelToolName,
    CapabilityVersionRef ExactVersion,
    long CatalogRevision,
    string ConnectionBindingHash);

public sealed record CapabilityRunSelectionRequest(
    string ConversationId,
    string RunId,
    string ToolCallId,
    CapabilityRunInvocationContext Context);

public sealed record CapabilityRunAuthorizationDecision(
    bool Allowed,
    string? ErrorCode = null,
    CapabilityRunInvocationContext? Context = null);

public sealed class CapabilityRunAuthorizationPolicy
{
    private const long MaximumCatalogRevision = 9_007_199_254_740_991;
    private static readonly HashSet<string> KnownRisks = ["R0", "R1", "R2", "R3", "R4"];
    private readonly object _sync = new();
    private readonly HashSet<string> _selectorToolCallIds = new(StringComparer.Ordinal);
    private string? _conversationId;
    private string? _runId;
    private CapabilityRuntimeSnapshot? _snapshot;
    private CapabilityRuntimeCandidate? _selected;
    private string? _workflowCallId;

    public void BeginRun(string conversationId, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        lock (_sync)
        {
            if (_conversationId is not null)
                throw new InvalidOperationException("다른 Capability run authorization이 이미 활성 상태입니다.");
            _conversationId = conversationId;
            _runId = runId;
            _snapshot = null;
            _selected = null;
            _workflowCallId = null;
            _selectorToolCallIds.Clear();
        }
    }

    public void BindSnapshot(
        string conversationId,
        string runId,
        CapabilityRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var frozen = ValidateAndCopy(snapshot);
        lock (_sync)
        {
            EnsureActiveRun(conversationId, runId);
            if (_snapshot is not null)
                throw new InvalidOperationException("Capability runtime snapshot은 run당 한 번만 고정할 수 있습니다.");
            _snapshot = frozen;
        }
    }

    public CapabilityRunAuthorizationDecision Select(CapabilityRunSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);
        if (string.IsNullOrWhiteSpace(request.ToolCallId))
            return Deny("capability.selector.identity_invalid");

        lock (_sync)
        {
            if (!IsActiveRun(request.ConversationId, request.RunId))
                return Deny("capability.run.not_active");
            if (_snapshot is null)
                return Deny("capability.snapshot.not_bound");
            if (!_selectorToolCallIds.Add(request.ToolCallId))
                return Deny("capability.selector.duplicate_tool_call");
            if (_selected is not null)
                return Deny("capability.selection.conflict");
            if (!StringComparer.Ordinal.Equals(
                    request.Context.SnapshotHash,
                    _snapshot.SnapshotHash.Value))
                return Deny("capability.snapshot.stale");

            var candidate = _snapshot.Candidates.FirstOrDefault(item =>
                StringComparer.Ordinal.Equals(item.ModelToolName, request.Context.ModelToolName));
            if (candidate is null)
                return Deny("capability.candidate.not_found");
            if (!candidate.ExactVersion.Equals(request.Context.ExactVersion) ||
                candidate.CatalogRevision != request.Context.CatalogRevision ||
                !StringComparer.Ordinal.Equals(
                    candidate.ConnectionBindingHash,
                    request.Context.ConnectionBindingHash))
                return Deny("capability.candidate.stale");

            _selected = candidate;
            return new CapabilityRunAuthorizationDecision(
                true,
                Context: CreateContext(_snapshot, candidate));
        }
    }

    public CapabilityRunAuthorizationDecision AuthorizeTool(
        string conversationId,
        string runId,
        string toolName,
        string risk,
        CapabilityRunInvocationContext? context)
    {
        if (string.IsNullOrWhiteSpace(toolName) || !KnownRisks.Contains(risk))
            return Deny("capability.tool.identity_invalid");

        lock (_sync)
        {
            if (!IsActiveRun(conversationId, runId))
                return Deny("capability.run.not_active");
            if (_snapshot is null)
                return Deny("capability.snapshot.not_bound");
            if (_selected is null)
                return context is null
                    ? new CapabilityRunAuthorizationDecision(true)
                    : Deny("capability.selection.required");
            if (context is null)
                return Deny("capability.context.required");

            var expected = CreateContext(_snapshot, _selected);
            if (!ContextEquals(expected, context))
                return Deny("capability.context.stale");
            if (_selected.Kind == CapabilityKind.Workflow)
                return Deny("capability.workflow.direct_tool_denied");
            if (!_selected.RequiredTools.Contains(toolName, StringComparer.Ordinal))
                return Deny("capability.tool.not_allowed");
            if (RiskOrdinal(risk) > RiskOrdinal(_selected.MaximumRisk))
                return Deny("capability.risk.exceeded");
            return new CapabilityRunAuthorizationDecision(true, Context: expected);
        }
    }

    public CapabilityRunAuthorizationDecision AuthorizeWorkflow(
        string conversationId,
        string runId,
        string workflowCallId,
        CapabilityRunInvocationContext? context)
    {
        if (string.IsNullOrWhiteSpace(workflowCallId))
            return Deny("capability.workflow.identity_invalid");
        lock (_sync)
        {
            if (!IsActiveRun(conversationId, runId))
                return Deny("capability.run.not_active");
            if (_snapshot is null)
                return Deny("capability.snapshot.not_bound");
            if (_selected is null)
                return Deny("capability.selection.required");
            if (context is null)
                return Deny("capability.context.required");
            var expected = CreateContext(_snapshot, _selected);
            if (!ContextEquals(expected, context))
                return Deny("capability.context.stale");
            if (_selected.Kind != CapabilityKind.Workflow)
                return Deny("capability.workflow.kind_required");
            if (_workflowCallId is not null &&
                !StringComparer.Ordinal.Equals(_workflowCallId, workflowCallId))
                return Deny("capability.workflow.invocation_conflict");
            _workflowCallId = workflowCallId;
            return new CapabilityRunAuthorizationDecision(true, Context: expected);
        }
    }

    public void EndRun(string conversationId, string runId)
    {
        lock (_sync)
        {
            if (!IsActiveRun(conversationId, runId)) return;
            _conversationId = null;
            _runId = null;
            _snapshot = null;
            _selected = null;
            _workflowCallId = null;
            _selectorToolCallIds.Clear();
        }
    }

    private CapabilityRuntimeSnapshot ValidateAndCopy(CapabilityRuntimeSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != CapabilityRuntimeSnapshot.CurrentSchemaVersion ||
            snapshot.Candidates.Count > CapabilityRuntimeSnapshotBuilder.MaximumCandidateCount ||
            !CapabilityRuntimeHasher.ComputeSnapshotHash(snapshot.Candidates).Equals(snapshot.SnapshotHash))
            throw new InvalidDataException("Capability runtime snapshot 무결성 검증에 실패했습니다.");

        var candidates = new List<CapabilityRuntimeCandidate>(snapshot.Candidates.Count);
        var ids = new HashSet<CapabilityId>();
        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in snapshot.Candidates)
        {
            if (candidate.CatalogRevision is < 1 or > MaximumCatalogRevision ||
                candidate.Kind is not (CapabilityKind.Prompt or CapabilityKind.Workflow) ||
                candidate.DisplayName.Length is < 1 or > 80 ||
                candidate.Description.Length is < 1 or > 500 ||
                candidate.IntentExamples.Count is < 1 or > 16 ||
                candidate.IntentExamples.Any(value => value.Length is < 1 or > 160) ||
                candidate.IntentExamples.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    candidate.IntentExamples.Count ||
                candidate.Instructions.Length is < 1 or > CapabilityDefinitionPolicy.MaximumInstructionsCharacters ||
                !CapabilityRuntimeInputSchemaPolicy.HasBoundedObjectRoot(candidate.CanonicalInputSchemaJson) ||
                candidate.RequiredTools.Count > 32 ||
                candidate.RequiredTools.Any(value => value.Length is < 1 or > 128) ||
                candidate.RequiredTools.Distinct(StringComparer.Ordinal).Count() != candidate.RequiredTools.Count ||
                !IsHash(candidate.ConnectionBindingHash) ||
                !KnownRisks.Contains(candidate.MaximumRisk) ||
                !ids.Add(candidate.ExactVersion.Id) ||
                !toolNames.Add(candidate.ModelToolName) ||
                !StringComparer.Ordinal.Equals(
                    candidate.ModelToolName,
                    CapabilityRuntimeHasher.ComputeModelToolName(candidate.ExactVersion)))
                throw new InvalidDataException("Capability runtime candidate 무결성 검증에 실패했습니다.");
            candidates.Add(new CapabilityRuntimeCandidate(
                candidate.ExactVersion,
                candidate.CatalogRevision,
                candidate.ModelToolName,
                candidate.DisplayName,
                candidate.Description,
                candidate.Kind,
                candidate.IntentExamples,
                candidate.Instructions,
                candidate.CanonicalInputSchemaJson,
                candidate.RequiredTools,
                candidate.ConnectionBindingHash,
                candidate.MaximumRisk));
        }
        var frozen = CapabilityRuntimeSnapshot.Create(candidates);
        if (!CapabilityRuntimeTransportPolicy.IsWithinBudget(frozen))
            throw new InvalidDataException("Capability runtime snapshot 전송 크기 검증에 실패했습니다.");
        return frozen;
    }

    private void EnsureActiveRun(string conversationId, string runId)
    {
        if (!IsActiveRun(conversationId, runId))
            throw new InvalidOperationException("현재 run과 Capability snapshot identity가 일치하지 않습니다.");
    }

    private bool IsActiveRun(string conversationId, string runId) =>
        StringComparer.Ordinal.Equals(_conversationId, conversationId) &&
        StringComparer.Ordinal.Equals(_runId, runId);

    private static CapabilityRunInvocationContext CreateContext(
        CapabilityRuntimeSnapshot snapshot,
        CapabilityRuntimeCandidate candidate) => new(
        snapshot.SnapshotHash.Value,
        candidate.ModelToolName,
        candidate.ExactVersion,
        candidate.CatalogRevision,
        candidate.ConnectionBindingHash);

    private static bool ContextEquals(
        CapabilityRunInvocationContext left,
        CapabilityRunInvocationContext right) =>
        StringComparer.Ordinal.Equals(left.SnapshotHash, right.SnapshotHash) &&
        StringComparer.Ordinal.Equals(left.ModelToolName, right.ModelToolName) &&
        left.ExactVersion.Equals(right.ExactVersion) &&
        left.CatalogRevision == right.CatalogRevision &&
        StringComparer.Ordinal.Equals(left.ConnectionBindingHash, right.ConnectionBindingHash);

    private static bool IsHash(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");

    private static int RiskOrdinal(string risk) => risk switch
    {
        "R0" => 0,
        "R1" => 1,
        "R2" => 2,
        "R3" => 3,
        "R4" => 4,
        _ => int.MaxValue,
    };

    private static CapabilityRunAuthorizationDecision Deny(string errorCode) =>
        new(false, errorCode);
}
