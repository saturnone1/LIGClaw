using System.IO;
using System.Text.Json;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record ExplorerActivation(IReadOnlyList<string> Paths);

internal static class ExplorerActivationPolicy
{
    internal const int MaximumItems = 20;
    internal const int MaximumPayloadBytes = 32 * 1024;
    private const string ArgumentName = "--explorer-item";

    internal static ExplorerActivation? FromArguments(IReadOnlyList<string> arguments)
    {
        var paths = new List<string>();
        var itemArguments = 0;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], ArgumentName, StringComparison.OrdinalIgnoreCase)) continue;
            if (++index >= arguments.Count) return null;
            if (++itemArguments > MaximumItems) return null;
            var path = NormalizeExistingPath(arguments[index]);
            if (path is null) return null;
            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
        }
        if (paths.Count == 0) return null;
        var activation = new ExplorerActivation(paths);
        return System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(activation)) <= MaximumPayloadBytes
            ? activation
            : null;
    }

    internal static string Serialize(ExplorerActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        var payload = JsonSerializer.Serialize(activation);
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(activation));
        return payload;
    }

    internal static ExplorerActivation? Deserialize(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || System.Text.Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
            return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<ExplorerActivation>(payload);
            if (parsed?.Paths is null || parsed.Paths.Count is < 1 or > MaximumItems) return null;
            var normalized = new List<string>(parsed.Paths.Count);
            foreach (var candidate in parsed.Paths)
            {
                var path = NormalizeExistingPath(candidate);
                if (path is null) return null;
                if (!normalized.Contains(path, StringComparer.OrdinalIgnoreCase)) normalized.Add(path);
            }
            return new ExplorerActivation(normalized);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string ToComposerText(ExplorerActivation activation)
    {
        var lines = activation.Paths.Select(path => $"- {path}");
        return "[Explorer에서 선택한 항목]\n" + string.Join('\n', lines);
    }

    private static string? NormalizeExistingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0) return null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) || Directory.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
