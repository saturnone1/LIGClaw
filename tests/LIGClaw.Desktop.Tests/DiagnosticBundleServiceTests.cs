using System.IO.Compression;
using System.Text;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class DiagnosticBundleServiceTests
{
    [Fact]
    public void RedactorRemovesCredentialsAndUserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var input = $"token=secret-value api_key: abc123 Authorization: Bearer bearer-secret " +
                    $"nvapi-1234567890ABCDEFGHIJ {profile}\\file.txt";

        var result = DiagnosticBundleService.Redact(input);

        Assert.DoesNotContain("secret-value", result, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890ABCDEFGHIJ", result, StringComparison.Ordinal);
        Assert.DoesNotContain(profile, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BundleContainsOnlyBoundedSanitizedDiagnosticsAndManifest()
    {
        var directory = Directory.CreateTempSubdirectory("ligclaw-diagnostics-");
        try
        {
            const string safeDiagnostic = "sidecar_state=ready protocol=1.23";
            var sensitiveDiagnostics = new[]
            {
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-USERNAME-CANARY",
                    Diagnostic: "smtp_username=RAW-SMTP-DIAGNOSTIC-USERNAME-CANARY"),
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-PASSWORD-CANARY",
                    Diagnostic: "smtp_password=RAW-SMTP-DIAGNOSTIC-PASSWORD-CANARY"),
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-MAIL-CONTENT-CANARY",
                    Diagnostic: "mail_content=RAW-SMTP-DIAGNOSTIC-MAIL-CONTENT-CANARY"),
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-MULTILINE-BODY-TAIL-CANARY",
                    Diagnostic: "body=first line\r\nRAW-SMTP-DIAGNOSTIC-MULTILINE-BODY-TAIL-CANARY"),
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-ESCAPED-CREDENTIAL-CANARY",
                    Diagnostic: "{\\\"password\\\":\\\"RAW-SMTP-DIAGNOSTIC-ESCAPED-CREDENTIAL-CANARY\\\"}"),
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-ESCAPED-BODY-CANARY",
                    Diagnostic: "{\\\"body\\\":\\\"first\\nRAW-SMTP-DIAGNOSTIC-ESCAPED-BODY-CANARY\\\"}"),
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-RECIPIENT-CANARY",
                    Diagnostic: "recipient=RAW-SMTP-DIAGNOSTIC-RECIPIENT-CANARY"),
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-TO-CANARY",
                    Diagnostic: "{\"to\":\"RAW-SMTP-DIAGNOSTIC-TO-CANARY\"}"),
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-CC-CANARY",
                    Diagnostic: "cc: RAW-SMTP-DIAGNOSTIC-CC-CANARY"),
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-BCC-CANARY",
                    Diagnostic: "bcc=RAW-SMTP-DIAGNOSTIC-BCC-CANARY"),
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-SUBJECT-CANARY",
                    Diagnostic: "Subject: RAW-SMTP-DIAGNOSTIC-SUBJECT-CANARY"),
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-SENDER-CANARY",
                    Diagnostic: "sender=RAW-SMTP-DIAGNOSTIC-SENDER-CANARY"),
                (
                    Canary: "RAW-SMTP-DIAGNOSTIC-SERVER-REPLY-CANARY",
                    Diagnostic: "smtp_server_reply=550 RAW-SMTP-DIAGNOSTIC-SERVER-REPLY-CANARY"),
            };
            var path = Path.Combine(directory.FullName, "bundle.zip");
            await new DiagnosticBundleService().CreateAsync(
                path,
                "test-platform",
                [safeDiagnostic, "token=private", .. sensitiveDiagnostics.Select(item => item.Diagnostic)]);

            var archiveBytes = await File.ReadAllBytesAsync(path);
            using var archive = ZipFile.OpenRead(path);
            Assert.Equal(["diagnostics.txt", "manifest.json"], archive.Entries.Select(entry => entry.FullName).Order().ToArray());
            var diagnostics = await ReadEntryAsync(archive, "diagnostics.txt");
            var manifest = await ReadEntryAsync(archive, "manifest.json");
            Assert.DoesNotContain("private", diagnostics, StringComparison.Ordinal);
            var diagnosticLines = diagnostics.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains(safeDiagnostic, diagnosticLines);
            Assert.Equal(
                sensitiveDiagnostics.Length,
                diagnosticLines.Count(line =>
                    StringComparer.Ordinal.Equals(
                        line,
                        DiagnosticBundleService.SmtpMailDiagnosticPlaceholder)));
            foreach (var (canary, _) in sensitiveDiagnostics)
            {
                Assert.Equal(0, CountOccurrences(archiveBytes, Encoding.UTF8.GetBytes(canary)));
                Assert.DoesNotContain(canary, manifest, StringComparison.Ordinal);
                Assert.DoesNotContain(canary, diagnostics, StringComparison.Ordinal);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string entryName)
    {
        using var reader = new StreamReader(archive.GetEntry(entryName)!.Open());
        return await reader.ReadToEndAsync();
    }

    private static int CountOccurrences(byte[] value, byte[] canary)
    {
        var count = 0;
        for (var offset = 0; offset <= value.Length - canary.Length; offset++)
        {
            if (!value.AsSpan(offset, canary.Length).SequenceEqual(canary)) continue;
            count++;
            offset += canary.Length - 1;
        }
        return count;
    }

    [Fact]
    public async Task CancelledBundleCreationPreservesAnExistingArchiveAndRemovesTemporaryFiles()
    {
        var directory = Directory.CreateTempSubdirectory("ligclaw-diagnostics-cancel-");
        try
        {
            var path = Path.Combine(directory.FullName, "bundle.zip");
            await File.WriteAllTextAsync(path, "existing-content");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new DiagnosticBundleService().CreateAsync(path, "test-platform", ["safe"], cancellation.Token));

            Assert.Equal("existing-content", await File.ReadAllTextAsync(path));
            Assert.Equal(["bundle.zip"], Directory.GetFiles(directory.FullName).Select(file => Path.GetFileName(file)!).ToArray());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
