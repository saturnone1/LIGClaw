using LIGClaw.Contracts.Generated;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal interface IConnectionHttpExecutionResolver
{
    /// <summary>
    /// Resolves exactly the supplied frozen Capability context and slot. Every call must re-read
    /// current binding state. The opaque execution fingerprint must cover the target identity,
    /// authentication kind, target revision, and credential revision without containing a secret
    /// or an unsalted hash of a low-entropy secret. Preparation must verify readiness without
    /// materializing a credential; execution must return a freshly read current credential.
    /// </summary>
    Task<ConnectionHttpExecutionResolution> ResolveAsync(
        CapabilityInvocationContext capabilityContext,
        string connectionSlot,
        ConnectionHttpResolutionPurpose purpose,
        CancellationToken cancellationToken);
}

internal enum ConnectionHttpResolutionPurpose
{
    Preparation,
    Execution,
}

internal sealed record ConnectionHttpExecutionResolution(
    bool Success,
    ConnectionHttpRuntimeBinding? Binding = null,
    string? ErrorCode = null,
    IAsyncDisposable? ExecutionLease = null);

/// <summary>
/// Serializes final aggregate connection-state validation and the resulting effect across every
/// Desktop-owned HTTP, Mail Gateway, or SMTP profile, credential, probe, and binding mutation. A mixed-kind
/// Capability therefore cannot observe one aggregate and execute after the other kind changes.
/// The lease is process-local and never crosses IPC, persistence, or the workflow ticket boundary.
/// </summary>
internal sealed class ConnectionExecutionLeaseCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? current = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref current, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}

internal enum ConnectionHttpAuthenticationKind
{
    None,
    Bearer,
}

/// <summary>
/// A short-lived Desktop-only result. It may contain a credential and must never be logged,
/// serialized, persisted, or retained by a prepared workflow effect. A resolver must return a
/// freshly read current credential on execution re-resolution and must never derive
/// <see cref="ExecutionFingerprint"/> from the raw credential value.
/// </summary>
internal sealed class ConnectionHttpRuntimeBinding
{
    public ConnectionHttpRuntimeBinding(
        string displayName,
        Uri baseUri,
        string frozenBindingHash,
        string executionFingerprint,
        ConnectionHttpAuthenticationKind authenticationKind,
        ConnectionHttpCredential? credential = null)
    {
        DisplayName = displayName;
        BaseUri = baseUri;
        FrozenBindingHash = frozenBindingHash;
        ExecutionFingerprint = executionFingerprint;
        AuthenticationKind = authenticationKind;
        Credential = credential;
    }

    public string DisplayName { get; }
    public Uri BaseUri { get; }
    public string FrozenBindingHash { get; }
    public string ExecutionFingerprint { get; }
    public ConnectionHttpAuthenticationKind AuthenticationKind { get; }
    public ConnectionHttpCredential? Credential { get; }

    public override string ToString() => "Connection HTTP runtime binding [redacted]";
}

internal sealed class ConnectionHttpCredential
{
    private readonly string value;

    public ConnectionHttpCredential(string value) => this.value = string.Concat(value);

    internal string Reveal() => value;

    public override string ToString() => "[redacted]";
}

internal static class ConnectionHttpBearerTokenPolicy
{
    public static bool IsValid(string? value)
    {
        if (value is not { Length: >= 8 and <= 1_280 }) return false;
        var padding = false;
        var sawTokenCharacter = false;
        foreach (var character in value)
        {
            if (character == '=')
            {
                if (!sawTokenCharacter) return false;
                padding = true;
                continue;
            }
            if (padding || character is not (
                    >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or
                    '-' or '.' or '_' or '~' or '+' or '/'))
                return false;
            sawTokenCharacter = true;
        }
        return sawTokenCharacter;
    }
}
