using System.IO;
using LIGClaw.Application.Capabilities;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Sidecar;
using LIGClaw.Domain;
using ApplicationCapabilityRuntimeSnapshot = LIGClaw.Application.Capabilities.CapabilityRuntimeSnapshot;
using ContractCapabilityRuntimeCandidate = LIGClaw.Contracts.Generated.CapabilityRuntimeCandidate;
using ContractCapabilityRuntimeSnapshot = LIGClaw.Contracts.Generated.CapabilityRuntimeSnapshot;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record ConversationStartOutcome(
    bool RefreshConversations,
    string? Diagnostic);

internal sealed record ConversationCancelOutcome(
    bool Requested,
    string Status,
    string? Diagnostic = null);

internal sealed class ConversationOrchestrationController
{
    internal const string CapabilitySnapshotFallbackDiagnostic =
        "기능 실행 정보를 준비하지 못해 일반 대화 방식으로 시작합니다.";

    private readonly ConversationRunController _conversationRun;
    private readonly IConversationSidecar _sidecar;
    private readonly IConversationRunStore _store;
    private readonly Func<IReadOnlyDictionary<string, object?>?> _loadProviderRouting;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private Func<string, CancellationToken, Task<ApplicationCapabilityRuntimeSnapshot>>?
        _createCapabilitySnapshot;

    public ConversationOrchestrationController(
        ConversationRunController conversationRun,
        IConversationSidecar sidecar,
        IConversationRunStore store,
        Func<IReadOnlyDictionary<string, object?>?> loadProviderRouting,
        Func<DateTimeOffset>? getUtcNow = null,
        Func<string, CancellationToken, Task<ApplicationCapabilityRuntimeSnapshot>>?
            createCapabilitySnapshot = null)
    {
        _conversationRun = conversationRun;
        _sidecar = sidecar;
        _store = store;
        _loadProviderRouting = loadProviderRouting;
        _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        _createCapabilitySnapshot = createCapabilitySnapshot;
    }

    public ConversationRunIdentity BeginRun() => _conversationRun.BeginRun();

    public void ConfigureCapabilitySnapshotFactory(
        Func<string, CancellationToken, Task<ApplicationCapabilityRuntimeSnapshot>> createCapabilitySnapshot)
    {
        ArgumentNullException.ThrowIfNull(createCapabilitySnapshot);
        Volatile.Write(ref _createCapabilitySnapshot, createCapabilitySnapshot);
    }

    public async Task<ConversationStartOutcome> StartAsync(
        ConversationRunIdentity identity,
        string input,
        bool persistenceAvailable)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? history = null;
        string? diagnostic = null;
        var refreshConversations = false;
        if (persistenceAvailable)
        {
            try
            {
                await _store.StartRunAsync(
                    identity.ConversationId,
                    identity.RunId,
                    input,
                    _getUtcNow());
                var context = await _store.GetConversationContextAsync(
                    identity.ConversationId);
                history = context
                    .Select(message => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                    {
                        ["role"] = message.Role,
                        ["content"] = message.Content,
                    })
                    .ToArray();
                refreshConversations = true;
            }
            catch (Exception exception)
            {
                diagnostic = $"대화 시작 기록 실패: {exception.GetType().Name}";
            }
        }

        var capabilitySnapshot = ApplicationCapabilityRuntimeSnapshot.Empty;
        var snapshotFactory = Volatile.Read(ref _createCapabilitySnapshot);
        if (snapshotFactory is not null)
        {
            try
            {
                capabilitySnapshot = await snapshotFactory(input, identity.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (identity.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                capabilitySnapshot = ApplicationCapabilityRuntimeSnapshot.Empty;
                diagnostic = AppendDiagnostic(diagnostic, CapabilitySnapshotFallbackDiagnostic);
            }
        }

        try
        {
            _conversationRun.BindCapabilitySnapshot(
                identity.ConversationId,
                identity.RunId,
                capabilitySnapshot);
        }
        catch (OperationCanceledException) when (identity.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            capabilitySnapshot = ApplicationCapabilityRuntimeSnapshot.Empty;
            diagnostic = AppendDiagnostic(diagnostic, CapabilitySnapshotFallbackDiagnostic);
            _conversationRun.BindCapabilitySnapshot(
                identity.ConversationId,
                identity.RunId,
                capabilitySnapshot);
        }

        var started = await _sidecar.StartConversationAsync(
            identity.ConversationId,
            identity.RunId,
            input,
            "cline",
            history,
            identity.CancellationToken,
            _loadProviderRouting(),
            ToContract(capabilitySnapshot));
        if (!started.Accepted || !StringComparer.Ordinal.Equals(started.RunId, identity.RunId))
            throw new InvalidDataException("Sidecar가 요청 실행 ID를 확인하지 못했습니다.");
        return new ConversationStartOutcome(refreshConversations, diagnostic);
    }

    public bool TryRequestCancellation(out ActiveConversationRun? activeRun) =>
        _conversationRun.TryRequestCancellation(out activeRun);

    public async Task<ConversationCancelOutcome> CancelAsync(ActiveConversationRun activeRun)
    {
        ArgumentNullException.ThrowIfNull(activeRun);
        try
        {
            var result = await _sidecar.CancelConversationAsync(activeRun.ConversationId);
            return new ConversationCancelOutcome(
                true,
                result.Cancelled ? "요청을 중단하고 있어요…" : "이미 처리가 끝났어요.");
        }
        catch (Exception exception)
        {
            return new ConversationCancelOutcome(
                true,
                "중단 요청을 전달하지 못했어요. 현재 요청 상태를 확인하고 있어요…",
                $"요청 중단 실패: {exception.GetType().Name}");
        }
    }

    public async Task<string?> PersistEventAsync(AgentEvent agentEvent, bool persistenceAvailable)
    {
        if (!persistenceAvailable) return null;
        try
        {
            await _store.AppendEventAsync(agentEvent);
            return null;
        }
        catch (Exception exception)
        {
            return $"대화 이벤트 기록 실패: {exception.GetType().Name}";
        }
    }

    public async Task<string?> MarkRunAsync(
        string conversationId,
        string runId,
        string status,
        bool persistenceAvailable)
    {
        if (!persistenceAvailable) return null;
        try
        {
            await _store.MarkRunAsync(conversationId, runId, status, _getUtcNow());
            return null;
        }
        catch (Exception exception)
        {
            return $"대화 상태 기록 실패: {exception.GetType().Name}";
        }
    }

    private static ContractCapabilityRuntimeSnapshot ToContract(
        ApplicationCapabilityRuntimeSnapshot snapshot) =>
        new(
            snapshot.SchemaVersion,
            snapshot.SnapshotHash.Value,
            snapshot.Candidates.Select(candidate => new ContractCapabilityRuntimeCandidate(
                candidate.ExactVersion.Id.Value,
                candidate.ExactVersion.Version.Value,
                candidate.ExactVersion.ContentHash.Value,
                candidate.CatalogRevision,
                candidate.ModelToolName,
                candidate.DisplayName,
                candidate.Description,
                FormatKind(candidate.Kind),
                candidate.IntentExamples,
                candidate.Instructions,
                candidate.CanonicalInputSchemaJson,
                candidate.RequiredTools,
                candidate.ConnectionBindingHash,
                candidate.MaximumRisk)).ToArray());

    private static string FormatKind(CapabilityKind kind) => kind switch
    {
        CapabilityKind.Prompt => "prompt",
        CapabilityKind.Workflow => "workflow",
        CapabilityKind.Connector => "connector",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string AppendDiagnostic(string? current, string next) =>
        string.IsNullOrEmpty(current) || current.Contains(next, StringComparison.Ordinal)
            ? string.IsNullOrEmpty(current) ? next : current
            : $"{current}{Environment.NewLine}{next}";
}
