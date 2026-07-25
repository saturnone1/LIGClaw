namespace LIGClaw.Desktop.Infrastructure.Sidecar;

public enum SidecarState
{
    Stopped,
    Starting,
    Connected,
    Restarting,
    Faulted,
}

public sealed record SidecarStatus(SidecarState State, string Message, DateTimeOffset ChangedAtUtc);
