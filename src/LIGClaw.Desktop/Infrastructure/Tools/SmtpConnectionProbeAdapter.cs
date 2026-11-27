using LIGClaw.Application.Connections;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class SmtpConnectionProbeAdapter : ISmtpConnectionProbe
{
    private readonly Func<ConnectionProfileId, CancellationToken, Task<SmtpWireCredential?>> readCredential;
    private readonly Func<
        string,
        int,
        SmtpTlsMode,
        SmtpWireCredential,
        CancellationToken,
        Task<SmtpWireProbeResult>> probeWire;

    public SmtpConnectionProbeAdapter(
        Func<ConnectionProfileId, CancellationToken, Task<SmtpWireCredential?>> readCredential)
        : this(readCredential, new SmtpWireClient().ProbeAsync)
    {
    }

    internal SmtpConnectionProbeAdapter(
        Func<ConnectionProfileId, CancellationToken, Task<SmtpWireCredential?>> readCredential,
        Func<
            string,
            int,
            SmtpTlsMode,
            SmtpWireCredential,
            CancellationToken,
            Task<SmtpWireProbeResult>> probeWire)
    {
        this.readCredential = readCredential ?? throw new ArgumentNullException(nameof(readCredential));
        this.probeWire = probeWire ?? throw new ArgumentNullException(nameof(probeWire));
    }

    public async Task<SmtpConnectionProbeResult> ProbeAsync(
        SmtpConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile is null || !profile.Enabled ||
            profile.State != ConnectionProfileLifecycleState.Stable ||
            !SmtpConnectionProfilePolicy.IsValid(profile) ||
            profile.Authentication != SmtpAuthenticationMechanism.Plain ||
            profile.CertificatePolicy != SmtpCertificatePolicy.SystemTrust)
            return Failed("smtp.probe.profile_invalid");

        SmtpWireCredential? credential;
        try
        {
            credential = await readCredential(profile.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failed("smtp.probe.credential_unavailable");
        }
        if (credential is null) return Failed("smtp.probe.credential_unavailable");

        using (credential)
        {
            try
            {
                var result = await probeWire(
                    profile.Host,
                    profile.Port,
                    profile.TlsMode,
                    credential,
                    cancellationToken).ConfigureAwait(false);
                if (result is not null && result.Succeeded && result.ErrorCode is null)
                    return new SmtpConnectionProbeResult(true);
                return Failed(result is not null && IsFixedErrorCode(result.ErrorCode)
                    ? result.ErrorCode!
                    : "smtp.probe.transport_failed");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Failed("smtp.probe.transport_failed");
            }
        }
    }

    public override string ToString() => "SmtpConnectionProbeAdapter [redacted]";

    private static SmtpConnectionProbeResult Failed(string errorCode) => new(false, errorCode);

    private static bool IsFixedErrorCode(string? value) =>
        value is { Length: >= 3 and <= 128 } && value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' ||
                               char.IsAsciiDigit(character) || character is '.' or '_');
}
