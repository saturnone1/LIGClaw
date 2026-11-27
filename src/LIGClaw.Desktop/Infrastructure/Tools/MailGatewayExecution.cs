using LIGClaw.Application.Connections;
using LIGClaw.Application.Mail;
using LIGClaw.Contracts.Generated;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal interface IMailGatewayExecutionResolver
{
    /// <summary>
    /// Resolves one Mail Gateway slot against an exact frozen Capability context. Preparation
    /// validates metadata without materializing a credential. Execution returns a freshly read
    /// credential while retaining a lease that the caller must hold through the network effect.
    /// </summary>
    Task<MailGatewayExecutionResolution> ResolveAsync(
        CapabilityInvocationContext capabilityContext,
        string connectionSlot,
        MailGatewayResolutionPurpose purpose,
        CancellationToken cancellationToken);
}

internal enum MailGatewayResolutionPurpose
{
    Preparation,
    Execution,
}

internal sealed record MailGatewayExecutionResolution(
    bool Success,
    MailGatewayRuntimeBinding? Binding = null,
    string? ErrorCode = null,
    IAsyncDisposable? ExecutionLease = null)
{
    public override string ToString() => "Mail Gateway execution resolution [redacted]";
}

/// <summary>
/// A short-lived Desktop-only binding. It must not be logged, serialized, persisted, or retained
/// by a prepared workflow effect. <see cref="Credential"/> is null for preparation and is freshly
/// materialized only for execution.
/// </summary>
internal sealed class MailGatewayRuntimeBinding
{
    public MailGatewayRuntimeBinding(
        string displayName,
        Uri baseUri,
        string sender,
        string frozenBindingHash,
        string localBindingHash,
        string executionFingerprint,
        MailGatewayCredential? credential = null)
    {
        if (displayName is not { Length: >= 1 and <= 80 } ||
            displayName.Trim() != displayName || displayName.Any(char.IsControl) ||
            baseUri is null || !baseUri.IsAbsoluteUri ||
            !MailGatewayConnectionProfilePolicy.TryNormalizeBaseUrl(
                baseUri.AbsoluteUri,
                out var canonicalBaseUrl) ||
            !StringComparer.Ordinal.Equals(canonicalBaseUrl, baseUri.AbsoluteUri) ||
            !MailAddressPolicy.IsValidAsciiMailbox(sender) ||
            !IsHash(frozenBindingHash) || !IsHash(localBindingHash) ||
            !IsHash(executionFingerprint))
            throw new ArgumentException("Mail Gateway runtime binding이 올바르지 않습니다.");

        DisplayName = displayName;
        BaseUri = baseUri;
        Sender = sender;
        FrozenBindingHash = frozenBindingHash;
        LocalBindingHash = localBindingHash;
        ExecutionFingerprint = executionFingerprint;
        Credential = credential;
    }

    public string DisplayName { get; }
    public Uri BaseUri { get; }
    public string Sender { get; }

    /// <summary>The aggregate hash for all frozen Capability connection requirements.</summary>
    public string FrozenBindingHash { get; }

    /// <summary>The selected local Mail Gateway binding hash.</summary>
    public string LocalBindingHash { get; }

    public string ExecutionFingerprint { get; }
    public MailGatewayCredential? Credential { get; }

    public override string ToString() => "Mail Gateway runtime binding [redacted]";

    private static bool IsHash(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");
}

internal sealed class MailGatewayCredential
{
    private readonly string value;

    public MailGatewayCredential(string value)
    {
        if (!MailGatewayBearerTokenPolicy.IsValid(value))
            throw new ArgumentException("Mail Gateway credential 형식이 올바르지 않습니다.", nameof(value));
        this.value = new string(value.AsSpan());
    }

    internal string Reveal() => value;

    public override string ToString() => "[redacted]";
}

internal static class MailGatewayBearerTokenPolicy
{
    public static bool IsValid(string? value) => ConnectionHttpBearerTokenPolicy.IsValid(value);
}
