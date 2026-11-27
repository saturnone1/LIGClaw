using LIGClaw.Application.Connections;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class MailGatewayProbeAdapter : IMailGatewayProbe
{
    private readonly Func<ConnectionProfileId, ConnectionCredentialKind, CancellationToken, Task<string?>> readCredential;
    private readonly MailGatewayWireClient wireClient;

    public MailGatewayProbeAdapter(
        Func<ConnectionProfileId, ConnectionCredentialKind, CancellationToken, Task<string?>> readCredential)
        : this(readCredential, new MailGatewayWireClient())
    {
    }

    internal MailGatewayProbeAdapter(
        Func<ConnectionProfileId, ConnectionCredentialKind, CancellationToken, Task<string?>> readCredential,
        MailGatewayWireClient wireClient)
    {
        this.readCredential = readCredential ?? throw new ArgumentNullException(nameof(readCredential));
        this.wireClient = wireClient ?? throw new ArgumentNullException(nameof(wireClient));
    }

    public async Task<MailGatewayProbeResult> ProbeAsync(
        MailGatewayConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile is null || !profile.Enabled ||
            profile.State != ConnectionProfileLifecycleState.Stable ||
            !MailGatewayConnectionProfilePolicy.IsValid(profile) ||
            !Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var baseUri) ||
            !MailGatewayWireClient.IsValidRootHttpsBaseUri(baseUri))
            return Failed("mail.gateway.probe.profile_invalid");

        string? token;
        try
        {
            token = await readCredential(
                profile.Id,
                ConnectionCredentialKind.MailGatewayBearerToken,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failed("mail.gateway.probe.credential_unavailable");
        }
        if (!MailGatewayBearerTokenPolicy.IsValid(token))
            return Failed("mail.gateway.probe.credential_unavailable");

        try
        {
            var result = await wireClient.ProbeAsync(baseUri, token!, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && result.ErrorCode is null) return new MailGatewayProbeResult(true);
            return Failed(IsFixedErrorCode(result.ErrorCode)
                ? result.ErrorCode!
                : "mail.gateway.probe.transport_failed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failed("mail.gateway.probe.transport_failed");
        }
    }

    public override string ToString() => "MailGatewayProbeAdapter [redacted]";

    private static MailGatewayProbeResult Failed(string errorCode) => new(false, errorCode);

    private static bool IsFixedErrorCode(string? value) =>
        value is { Length: >= 3 and <= 128 } && value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' ||
                               char.IsAsciiDigit(character) || character is '.' or '_');
}
