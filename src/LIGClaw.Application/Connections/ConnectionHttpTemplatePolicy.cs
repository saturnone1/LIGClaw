using System.Text;

namespace LIGClaw.Application.Connections;

public sealed record ConnectionHttpParameter(string Name, string Value);

public sealed record ConnectionHttpTemplateResult(
    string RelativeTarget,
    string CanonicalMaterial);

public static class ConnectionHttpTemplatePolicy
{
    public const int MaximumPathParameters = 16;
    public const int MaximumQueryParameters = 32;
    public const int MaximumRelativeTargetBytes = 2_048;

    public static bool TryBuild(
        string method,
        string pathTemplate,
        IReadOnlyList<ConnectionHttpParameter> pathParameters,
        IReadOnlyList<ConnectionHttpParameter> queryParameters,
        out ConnectionHttpTemplateResult? result,
        out string? errorCode)
    {
        result = null;
        errorCode = "connection.http.input_invalid";
        if (!StringComparer.Ordinal.Equals(method, "GET") ||
            pathTemplate is null || pathParameters is null || queryParameters is null ||
            !HasRuneCount(pathTemplate, 1, 512) ||
            pathParameters.Count > MaximumPathParameters ||
            queryParameters.Count > MaximumQueryParameters ||
            pathTemplate[0] == '/' ||
            pathTemplate.AsSpan().IndexOfAny("\0\\?#%") >= 0)
            return false;

        var pathByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in pathParameters)
        {
            if (parameter is null || !IsParameterName(parameter.Name) ||
                parameter.Value is null || !HasRuneCount(parameter.Value, 0, 2_048) ||
                !pathByName.TryAdd(parameter.Name, parameter.Value))
                return false;
        }

        var usedPathNames = new HashSet<string>(StringComparer.Ordinal);
        var segments = pathTemplate.Split('/');
        if (segments.Any(segment => segment.Length == 0)) return false;
        var encodedSegments = new string[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Length >= 3 && segment[0] == '{' && segment[^1] == '}')
            {
                var name = segment[1..^1];
                if (!IsParameterName(name) || !usedPathNames.Add(name) ||
                    !pathByName.TryGetValue(name, out var value) ||
                    !TryEscape(value, out encodedSegments[index]) ||
                    encodedSegments[index].Length == 0 ||
                    encodedSegments[index] is "." or "..")
                    return false;
                continue;
            }
            if (segment is "." or ".." || segment.Any(character => !IsUnreserved(character)))
                return false;
            encodedSegments[index] = segment;
        }
        if (usedPathNames.Count != pathByName.Count) return false;

        var encodedQuery = new List<string>(queryParameters.Count);
        var queryNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in queryParameters)
        {
            if (parameter is null || !IsParameterName(parameter.Name) ||
                parameter.Value is null || !HasRuneCount(parameter.Value, 0, 2_048) ||
                !queryNames.Add(parameter.Name) ||
                !TryEscape(parameter.Name, out var name) || !TryEscape(parameter.Value, out var value))
                return false;
            encodedQuery.Add($"{name}={value}");
        }

        var relativeTarget = string.Join('/', encodedSegments);
        if (encodedQuery.Count > 0) relativeTarget = $"{relativeTarget}?{string.Join('&', encodedQuery)}";
        if (Encoding.UTF8.GetByteCount(relativeTarget) > MaximumRelativeTargetBytes) return false;

        result = new ConnectionHttpTemplateResult(relativeTarget, $"GET\n{relativeTarget}");
        errorCode = null;
        return true;
    }

    private static bool TryEscape(string value, out string encoded)
    {
        encoded = string.Empty;
        try
        {
            encoded = Uri.EscapeDataString(value);
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool IsParameterName(string value) =>
        value.Length is >= 1 and <= 64 &&
        IsAsciiLetter(value[0]) &&
        value.AsSpan(1).IndexOfAnyExcept(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.-") < 0;

    private static bool IsAsciiLetter(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool HasRuneCount(string value, int minimum, int maximum)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            if (++count > maximum) return false;
        }
        return count >= minimum;
    }

    private static bool IsUnreserved(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.' or '_' or '~';
}
