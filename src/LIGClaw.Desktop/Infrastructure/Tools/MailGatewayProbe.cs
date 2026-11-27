using LIGClaw.Application.Connections;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record MailGatewayProbeResult(
    bool Succeeded,
    string? ErrorCode = null)
{
    public override string ToString() =>
        $"MailGatewayProbeResult(Succeeded={Succeeded}, ErrorCode={ErrorCode})";
}

internal interface IMailGatewayProbe
{
    Task<MailGatewayProbeResult> ProbeAsync(
        MailGatewayConnectionProfile profile,
        CancellationToken cancellationToken);
}
