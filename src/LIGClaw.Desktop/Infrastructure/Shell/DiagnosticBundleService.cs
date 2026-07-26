using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed partial class DiagnosticBundleService
{
    public async Task CreateAsync(
        string destinationPath,
        string platform,
        IEnumerable<string> diagnostics,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(fullPath), ".zip"))
            throw new InvalidOperationException("진단 번들은 .zip 파일로 저장해야 합니다.");
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var output = manifest.Open())
                {
                    await JsonSerializer.SerializeAsync(output, new
                    {
                        generatedAtUtc = DateTimeOffset.UtcNow,
                        appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                        protocolVersion = LIGClaw.Contracts.Generated.ContractMetadata.ProtocolVersion,
                        platform,
                        osVersion = Environment.OSVersion.VersionString,
                        processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                        dotnetVersion = Environment.Version.ToString(),
                    }, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                var log = archive.CreateEntry("diagnostics.txt", CompressionLevel.Optimal);
                await using var logStream = log.Open();
                await using var writer = new StreamWriter(logStream);
                foreach (var item in diagnostics.TakeLast(100))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(Redact(item).AsMemory(), cancellationToken).ConfigureAwait(false);
                }
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static string Redact(string value)
    {
        var redacted = JsonCredentialPattern().Replace(value, "$1[REDACTED]");
        redacted = AuthorizationPattern().Replace(redacted, "$1[REDACTED]");
        redacted = CredentialPattern().Replace(redacted, "$1[REDACTED]");
        redacted = NvidiaKeyPattern().Replace(redacted, "nvapi-[REDACTED]");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            redacted = redacted.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return redacted.Length <= 2_048 ? redacted : string.Concat(redacted.AsSpan(0, 2_048), "… [truncated]");
    }

    [GeneratedRegex("(?i)([\"'](?:authorization|api[_ -]?key|token|password)[\"']\\s*:\\s*[\"'])[^\"']+")]
    private static partial Regex JsonCredentialPattern();

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*)(?:bearer\\s+)?[^\\s,;]+")]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex("(?i)(api[_ -]?key|token|password)\\s*[:=]\\s*[^\\s,;]+")]
    private static partial Regex CredentialPattern();

    [GeneratedRegex("nvapi-[A-Za-z0-9_-]{12,}", RegexOptions.IgnoreCase)]
    private static partial Regex NvidiaKeyPattern();
}
