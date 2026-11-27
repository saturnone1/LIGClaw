using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record ConversationToolInvocationOutcome(
    WindowsToolExecutionResult Execution,
    string RunStatus,
    string? Diagnostic = null);

internal sealed class ConversationToolInvocationController(
    ConversationRunController conversationRun)
{
    private const string CapabilitySelectorName = "capability.select.v1";
    private static readonly HashSet<string> CapabilitySelectorInputFields =
    [
        "snapshotHash",
        "modelToolName",
        "capabilityId",
        "version",
        "contentHash",
        "catalogRevision",
        "connectionBindingHash",
    ];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string PreparingStatus => "윈도우 작업을 준비하고 있어요…";

    public async Task<ConversationToolInvocationOutcome> ExecuteAsync(
        ToolInvokeParams invocation,
        Func<ToolInvokeParams, ToolInvocationPreflight, CancellationToken, Task<WindowsToolExecutionResult>>? execute)
    {
        if (!conversationRun.TryGetCancellationToken(
                invocation.ConversationId,
                invocation.RunId,
                out var cancellationToken))
        {
            return Failure(
                "현재 요청과 일치하지 않아 윈도우 작업을 실행하지 않았어요.",
                "현재 실행과 일치하지 않는 작업 도구 요청을 거부했습니다.");
        }

        var enteredGate = false;
        try
        {
            await _gate.WaitAsync(cancellationToken);
            enteredGate = true;

            var preparation = conversationRun.PrepareControlPlaneInvocation(
                invocation.ConversationId,
                invocation.RunId,
                invocation.ToolCallId,
                invocation.Risk);
            if (!preparation.Allowed && !preparation.RequiresApproval)
            {
                return Failure(
                    "이미 처리했거나 올바르지 않은 작업 도구 요청은 다시 실행하지 않았어요.",
                    "작업 도구 실행 예약 정책이 요청을 거부했습니다.");
            }

            if (StringComparer.Ordinal.Equals(invocation.Name, CapabilitySelectorName))
                return ExecuteCapabilitySelection(invocation);

            CapabilityRunInvocationContext? capabilityContext;
            try
            {
                capabilityContext = invocation.CapabilityContext is null
                    ? null
                    : ToApplicationContext(invocation.CapabilityContext);
            }
            catch
            {
                return await RejectPreparedAsync(
                    invocation,
                    execute,
                    cancellationToken,
                    "기능 실행 정보가 올바르지 않아 윈도우 기능을 실행하지 않았어요.",
                    "기능의 작업 도구 실행 정보 해석을 거부했습니다.",
                    "capability.context.parse_denied").ConfigureAwait(false);
            }

            var capabilityAuthorization = conversationRun.AuthorizeCapabilityTool(
                invocation.ConversationId,
                invocation.RunId,
                invocation.Name,
                invocation.Risk,
                capabilityContext);
            if (!capabilityAuthorization.Allowed)
            {
                return await RejectPreparedAsync(
                    invocation,
                    execute,
                    cancellationToken,
                    "기능 정책에 따라 윈도우 기능을 실행하지 않았어요.",
                    $"Capability Tool 요청을 거부했습니다: {capabilityAuthorization.ErrorCode ?? "unknown"}.",
                    $"capability.preflight.denied:{capabilityAuthorization.ErrorCode ?? "unknown"}")
                    .ConfigureAwait(false);
            }

            if (execute is null)
                return Failure("윈도우 환경을 확인할 수 없어 기능을 실행하지 못했어요.");

            var execution = await execute(
                invocation,
                ToolInvocationPreflight.Allow,
                cancellationToken);
            return new ConversationToolInvocationOutcome(
                execution,
                execution.Success
                    ? "윈도우 작업 결과를 확인하고 있어요…"
                    : "윈도우 작업을 완료하지 못했어요.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("요청이 중단되어 윈도우 작업을 실행하지 않았어요.");
        }
        catch (Exception exception)
        {
            return Failure(
                "윈도우 기능 요청을 안전하게 처리하지 못했어요.",
                $"작업 도구 요청 처리 실패: {exception.GetType().Name}");
        }
        finally
        {
            if (enteredGate) _gate.Release();
        }
    }

    private ConversationToolInvocationOutcome ExecuteCapabilitySelection(ToolInvokeParams invocation)
    {
        var invocationAuthorization = conversationRun.ClaimPreparedInvocation(
            invocation.ConversationId,
            invocation.RunId,
            invocation.ToolCallId,
            invocation.Risk);
        if (!StringComparer.Ordinal.Equals(invocation.Risk, "R0"))
        {
            return Failure(
                "기능 선택 요청의 위험 수준이 올바르지 않아 거부했어요.",
                "기능 선택기의 위험 수준을 거부했습니다.");
        }
        if (!invocationAuthorization.Allowed)
        {
            return Failure(
                "기능 선택 요청을 다시 처리하지 않았어요.",
                "기능 선택기 실행 정책이 요청을 거부했습니다.");
        }

        CapabilityRunInvocationContext context;
        try
        {
            if (invocation.CapabilityContext is not null ||
                invocation.Input.Count != CapabilitySelectorInputFields.Count ||
                invocation.Input.Keys.Any(key => !CapabilitySelectorInputFields.Contains(key)))
                throw new FormatException();
            var selector = new CapabilitySelectV1(
                ReadRequiredString(invocation.Input, "snapshotHash"),
                ReadRequiredString(invocation.Input, "modelToolName"),
                ReadRequiredString(invocation.Input, "capabilityId"),
                ReadRequiredString(invocation.Input, "version"),
                ReadRequiredString(invocation.Input, "contentHash"),
                ReadRequiredInt64(invocation.Input, "catalogRevision"),
                ReadRequiredString(invocation.Input, "connectionBindingHash"));
            context = ToApplicationContext(selector);
        }
        catch
        {
            return Failure(
                "기능 선택 정보가 올바르지 않아 요청을 거부했어요.",
                "기능 선택기 입력 해석을 거부했습니다.");
        }

        var selection = conversationRun.SelectCapability(new CapabilityRunSelectionRequest(
            invocation.ConversationId,
            invocation.RunId,
            invocation.ToolCallId,
            context));
        if (!selection.Allowed || selection.Context is null)
        {
            return Failure(
                "현재 요청에서 해당 기능을 선택할 수 없어요.",
                $"Capability selector 요청을 거부했습니다: {selection.ErrorCode ?? "unknown"}.");
        }

        var contractContext = ToContractContext(selection.Context);
        var result = new CapabilitySelectV1Result(true, contractContext);
        return new ConversationToolInvocationOutcome(
            new WindowsToolExecutionResult(
                true,
                new Dictionary<string, object?>
                {
                    ["selected"] = result.Selected,
                    ["context"] = new Dictionary<string, object?>
                    {
                        ["snapshotHash"] = result.Context.SnapshotHash,
                        ["modelToolName"] = result.Context.ModelToolName,
                        ["capabilityId"] = result.Context.CapabilityId,
                        ["version"] = result.Context.Version,
                        ["contentHash"] = result.Context.ContentHash,
                        ["catalogRevision"] = result.Context.CatalogRevision,
                        ["connectionBindingHash"] = result.Context.ConnectionBindingHash,
                    },
                }),
            "기능을 선택하고 있어요…");
    }

    private static async Task<ConversationToolInvocationOutcome> RejectPreparedAsync(
        ToolInvokeParams invocation,
        Func<ToolInvokeParams, ToolInvocationPreflight, CancellationToken, Task<WindowsToolExecutionResult>>? execute,
        CancellationToken cancellationToken,
        string error,
        string diagnostic,
        string auditSummary)
    {
        if (execute is null) return Failure(error, diagnostic);
        _ = await execute(
            invocation,
            ToolInvocationPreflight.Deny(error, auditSummary),
            cancellationToken).ConfigureAwait(false);
        return Failure(error, diagnostic);
    }

    private static CapabilityRunInvocationContext ToApplicationContext(CapabilitySelectV1 selector) =>
        new(
            CapabilityRuntimeSnapshotHash.Parse(selector.SnapshotHash).Value,
            RequireModelToolName(selector.ModelToolName),
            new CapabilityVersionRef(
                CapabilityId.Parse(selector.CapabilityId),
                CapabilitySemanticVersion.Parse(selector.Version),
                CapabilityContentHash.Parse(selector.ContentHash)),
            RequireCatalogRevision(selector.CatalogRevision),
            RequireHash(selector.ConnectionBindingHash));

    private static CapabilityRunInvocationContext ToApplicationContext(
        CapabilityInvocationContext context) =>
        new(
            CapabilityRuntimeSnapshotHash.Parse(context.SnapshotHash).Value,
            RequireModelToolName(context.ModelToolName),
            new CapabilityVersionRef(
                CapabilityId.Parse(context.CapabilityId),
                CapabilitySemanticVersion.Parse(context.Version),
                CapabilityContentHash.Parse(context.ContentHash)),
            RequireCatalogRevision(context.CatalogRevision),
            RequireHash(context.ConnectionBindingHash));

    private static CapabilityInvocationContext ToContractContext(
        CapabilityRunInvocationContext context) =>
        new(
            context.SnapshotHash,
            context.ModelToolName,
            context.ExactVersion.Id.Value,
            context.ExactVersion.Version.Value,
            context.ExactVersion.ContentHash.Value,
            context.CatalogRevision,
            context.ConnectionBindingHash);

    private static string ReadRequiredString(
        IReadOnlyDictionary<string, object?> input,
        string name)
    {
        if (!input.TryGetValue(name, out var value)) throw new FormatException();
        var parsed = value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        return parsed ?? throw new FormatException();
    }

    private static long ReadRequiredInt64(
        IReadOnlyDictionary<string, object?> input,
        string name)
    {
        if (!input.TryGetValue(name, out var value)) throw new FormatException();
        return value switch
        {
            long number => number,
            int number => number,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var number) => number,
            _ => throw new FormatException(),
        };
    }

    private static string RequireModelToolName(string value)
    {
        if (value.Length != 28 || !value.StartsWith("cap_", StringComparison.Ordinal) ||
            value.AsSpan(4).ContainsAnyExcept("0123456789abcdef"))
            throw new FormatException();
        return value;
    }

    private static long RequireCatalogRevision(long value) =>
        value is >= 1 and <= 9_007_199_254_740_991 ? value : throw new FormatException();

    private static string RequireHash(string value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef")
            ? value
            : throw new FormatException();

    private static ConversationToolInvocationOutcome Failure(
        string error,
        string? diagnostic = null) =>
        new(
            new WindowsToolExecutionResult(
                false,
                new Dictionary<string, object?>(),
                error),
            "윈도우 작업을 완료하지 못했어요.",
            diagnostic);
}
