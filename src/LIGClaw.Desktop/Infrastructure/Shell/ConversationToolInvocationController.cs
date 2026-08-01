using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record ConversationToolInvocationOutcome(
    WindowsToolExecutionResult Execution,
    string RunStatus,
    string? Diagnostic = null);

internal sealed class ConversationToolInvocationController(
    ConversationRunController conversationRun)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string PreparingStatus => "Windows 작업을 준비하고 있어요…";

    public async Task<ConversationToolInvocationOutcome> ExecuteAsync(
        ToolInvokeParams invocation,
        Func<ToolInvokeParams, CancellationToken, Task<WindowsToolExecutionResult>>? execute)
    {
        if (!conversationRun.TryGetCancellationToken(
                invocation.ConversationId,
                invocation.RunId,
                out var cancellationToken))
        {
            return Failure(
                "현재 요청과 일치하지 않아 Windows 작업을 실행하지 않았어요.",
                "현재 run과 일치하지 않는 Tool 요청을 거부했습니다.");
        }

        var enteredGate = false;
        try
        {
            await _gate.WaitAsync(cancellationToken);
            enteredGate = true;
            if (execute is null)
                return Failure("Windows 플랫폼을 확인할 수 없어 기능을 실행하지 못했어요.");

            var execution = await execute(invocation, cancellationToken);
            return new ConversationToolInvocationOutcome(
                execution,
                execution.Success
                    ? "Windows 작업 결과를 확인하고 있어요…"
                    : "Windows 작업을 완료하지 못했어요.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("요청이 중단되어 Windows 작업을 실행하지 않았어요.");
        }
        catch (Exception exception)
        {
            return Failure(
                "Windows 기능 요청을 안전하게 처리하지 못했어요.",
                $"Tool 요청 처리 실패: {exception.Message}");
        }
        finally
        {
            if (enteredGate) _gate.Release();
        }
    }

    private static ConversationToolInvocationOutcome Failure(
        string error,
        string? diagnostic = null) =>
        new(
            new WindowsToolExecutionResult(
                false,
                new Dictionary<string, object?>(),
                error),
            "Windows 작업을 완료하지 못했어요.",
            diagnostic);
}
