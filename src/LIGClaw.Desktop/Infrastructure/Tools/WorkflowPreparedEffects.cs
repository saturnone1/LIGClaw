using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal enum WorkflowEffectDisposition
{
    NotStarted,
    Succeeded,
    FailedKnown,
    Unknown,
}

internal sealed record WorkflowPreparedEffectExecution(
    WorkflowEffectDisposition Disposition,
    WindowsToolExecutionResult Result);

internal sealed record WorkflowEffectPreparationResult(
    bool Success,
    IPreparedWorkflowEffect? Effect = null,
    string? Error = null,
    string? ErrorCode = null);

internal interface IPreparedWorkflowEffect : IAsyncDisposable
{
    string ToolName { get; }
    string Risk { get; }
    string CanonicalPayloadHash { get; }
    string CanonicalBindingHash { get; }
    DateTimeOffset ExpiresAtUtc { get; }
    WindowsToolApprovalPrompt ApprovalPrompt { get; }
    Task<WorkflowPreparedEffectExecution> ExecuteOnceAsync(
        DateTimeOffset trustedNowUtc,
        CancellationToken cancellationToken);
}

internal interface IWorkflowOneShotToolAdapter : IWindowsToolAdapter
{
    Task<WorkflowEffectPreparationResult> PrepareWorkflowEffectAsync(
        LIGClaw.Contracts.Generated.ToolInvokeParams invocation,
        DateTimeOffset nowUtc,
        TimeSpan lifetime,
        CancellationToken cancellationToken);
}

internal interface IWorkflowOnlyObservationToolAdapter : IWindowsToolAdapter
{
}

internal static class WorkflowPreparedEffectHash
{
    public static string Compute(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt64BigEndian(length, bytes.LongLength);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }
}
