using LIGClaw.Application.Connections;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record SmtpConnectionProbeResult(
    bool Succeeded,
    string? ErrorCode = null)
{
    public override string ToString() =>
        $"SmtpConnectionProbeResult(Succeeded={Succeeded}, ErrorCode={ErrorCode})";
}

internal interface ISmtpConnectionProbe
{
    Task<SmtpConnectionProbeResult> ProbeAsync(
        SmtpConnectionProfile profile,
        CancellationToken cancellationToken);
}
