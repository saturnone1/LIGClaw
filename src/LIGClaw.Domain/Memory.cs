namespace LIGClaw.Domain;

public sealed record PersonalMemory(
    string Id,
    string Kind,
    string Key,
    string Value,
    string Sensitivity,
    string Source,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record PersonalMemoryDraft(
    string Kind,
    string Key,
    string Value,
    string Sensitivity,
    string Source,
    DateTimeOffset? ExpiresAtUtc);

public static class PersonalMemoryPolicy
{
    private static readonly HashSet<string> Kinds = ["alias", "preference", "note"];
    private static readonly HashSet<string> Sensitivities = ["general", "personal", "sensitive"];
    private static readonly string[] ForbiddenCredentialTerms =
        ["password", "passwd", "api key", "apikey", "access token", "refresh token", "secret key", "비밀번호", "암호", "인증 토큰"];
    private static readonly string[] ForbiddenCredentialValueMarkers =
        ["nvapi-", "sk-", "ghp_", "github_pat_", "xoxb-", "bearer ", "password=", "passwd=", "api_key=", "apikey=", "-----begin private key"];

    public static bool IsValid(PersonalMemoryDraft draft, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Kinds.Contains(draft.Kind) &&
               Sensitivities.Contains(draft.Sensitivity) &&
               draft.Key.Length is >= 1 and <= 80 &&
               draft.Value.Length is >= 1 and <= 2000 &&
               draft.Source.Length is >= 1 and <= 160 &&
               (draft.ExpiresAtUtc is null || draft.ExpiresAtUtc > nowUtc) &&
               !ForbiddenCredentialTerms.Any(term => draft.Key.Contains(term, StringComparison.OrdinalIgnoreCase)) &&
               !ForbiddenCredentialValueMarkers.Any(marker => draft.Value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
