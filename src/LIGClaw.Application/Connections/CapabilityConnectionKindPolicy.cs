namespace LIGClaw.Application.Connections;

public static class CapabilityConnectionKindPolicy
{
    public const string Http = "http";
    public const string MailApi = "mail-api";
    public const string Smtp = "smtp";

    public static IReadOnlyList<string> SupportedKinds { get; } =
        Array.AsReadOnly([Http, MailApi, Smtp]);

    public static bool IsSupported(string? kind) =>
        kind is not null && SupportedKinds.Contains(kind, StringComparer.Ordinal);
}
