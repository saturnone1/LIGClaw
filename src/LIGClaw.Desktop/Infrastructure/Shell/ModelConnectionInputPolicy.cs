namespace LIGClaw.Desktop.Infrastructure.Shell;

internal static class ModelConnectionInputPolicy
{
    public static bool HasAnyInput(
        string? baseUrl,
        string? model,
        string? apiKey,
        bool hasStoredSettings) =>
        hasStoredSettings ||
        !string.IsNullOrWhiteSpace(baseUrl) ||
        !string.IsNullOrWhiteSpace(model) ||
        !string.IsNullOrWhiteSpace(apiKey);

    public static bool ShouldWarnAboutPlaintextHttp(string? baseUrl) =>
        Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttp &&
        !uri.IsLoopback;

    public static bool RequiresSuccessfulTest(
        ModelConnectionSettings candidate,
        ModelConnectionSettings? stored,
        ModelConnectionSettings? lastSuccessfulTest) =>
        !Equals(candidate, stored) && !Equals(candidate, lastSuccessfulTest);
}
