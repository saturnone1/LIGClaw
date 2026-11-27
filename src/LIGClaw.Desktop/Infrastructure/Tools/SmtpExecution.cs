using LIGClaw.Application.Connections;
using LIGClaw.Application.Mail;
using LIGClaw.Contracts.Generated;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal interface ISmtpExecutionResolver
{
    /// <summary>
    /// Resolves one SMTP slot against an exact frozen Capability context. Preparation validates
    /// metadata without materializing credentials. Execution returns a freshly read credential
    /// while retaining a lease that the caller must hold through the network effect.
    /// </summary>
    Task<SmtpExecutionResolution> ResolveAsync(
        CapabilityInvocationContext capabilityContext,
        string connectionSlot,
        SmtpResolutionPurpose purpose,
        CancellationToken cancellationToken);
}

internal enum SmtpResolutionPurpose
{
    Preparation,
    Execution,
}

internal sealed record SmtpExecutionResolution(
    bool Success,
    SmtpRuntimeBinding? Binding = null,
    string? ErrorCode = null,
    IAsyncDisposable? ExecutionLease = null)
{
    public override string ToString() => "SMTP execution resolution [redacted]";
}

/// <summary>
/// A short-lived Desktop-only binding. It must not be logged, serialized, persisted, or retained
/// by a prepared workflow effect. <see cref="Credential"/> is null for preparation and is freshly
/// materialized only for execution.
/// </summary>
internal sealed class SmtpRuntimeBinding : IDisposable
{
    public SmtpRuntimeBinding(
        string displayName,
        string host,
        int port,
        SmtpTlsMode tlsMode,
        SmtpAuthenticationMechanism authentication,
        SmtpCertificatePolicy certificatePolicy,
        string sender,
        string frozenBindingHash,
        string localBindingHash,
        string executionFingerprint,
        SmtpWireCredential? credential = null)
    {
        if (displayName is not { Length: >= 1 and <= 80 } ||
            displayName.Trim() != displayName || displayName.Any(char.IsControl) ||
            !SmtpConnectionProfilePolicy.TryNormalizeHost(host, out var canonicalHost) ||
            !StringComparer.Ordinal.Equals(host, canonicalHost) ||
            port is < SmtpConnectionProfilePolicy.MinimumPort or > SmtpConnectionProfilePolicy.MaximumPort ||
            tlsMode is not (SmtpTlsMode.StartTlsRequired or SmtpTlsMode.ImplicitTls) ||
            authentication != SmtpAuthenticationMechanism.Plain ||
            certificatePolicy != SmtpCertificatePolicy.SystemTrust ||
            !MailAddressPolicy.IsValidAsciiMailbox(sender) ||
            !IsHash(frozenBindingHash) || !IsHash(localBindingHash) ||
            !IsHash(executionFingerprint))
            throw new ArgumentException("SMTP runtime binding이 올바르지 않습니다.");

        DisplayName = displayName;
        Host = host;
        Port = port;
        TlsMode = tlsMode;
        Authentication = authentication;
        CertificatePolicy = certificatePolicy;
        Sender = sender;
        FrozenBindingHash = frozenBindingHash;
        LocalBindingHash = localBindingHash;
        ExecutionFingerprint = executionFingerprint;
        Credential = credential;
    }

    public string DisplayName { get; }
    public string Host { get; }
    public int Port { get; }
    public SmtpTlsMode TlsMode { get; }
    public SmtpAuthenticationMechanism Authentication { get; }
    public SmtpCertificatePolicy CertificatePolicy { get; }
    public string Sender { get; }
    public string FrozenBindingHash { get; }
    public string LocalBindingHash { get; }
    public string ExecutionFingerprint { get; }
    public SmtpWireCredential? Credential { get; }

    public override string ToString() => "SMTP runtime binding [redacted]";

    public void Dispose() => Credential?.Dispose();

    private static bool IsHash(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");
}
