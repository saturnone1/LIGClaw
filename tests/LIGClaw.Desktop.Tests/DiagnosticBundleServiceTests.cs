using System.IO.Compression;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class DiagnosticBundleServiceTests
{
    [Fact]
    public void RedactorRemovesCredentialsAndUserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var input = $"token=secret-value api_key: abc123 Authorization: Bearer bearer-secret " +
                    $"{{\"password\":\"json-secret\"}} nvapi-1234567890ABCDEFGHIJ {profile}\\file.txt";

        var result = DiagnosticBundleService.Redact(input);

        Assert.DoesNotContain("secret-value", result, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("json-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890ABCDEFGHIJ", result, StringComparison.Ordinal);
        Assert.DoesNotContain(profile, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BundleContainsOnlyBoundedSanitizedDiagnosticsAndManifest()
    {
        var directory = Directory.CreateTempSubdirectory("ligclaw-diagnostics-");
        try
        {
            var path = Path.Combine(directory.FullName, "bundle.zip");
            await new DiagnosticBundleService().CreateAsync(path, "test-platform", ["token=private"]);

            using var archive = ZipFile.OpenRead(path);
            Assert.Equal(["diagnostics.txt", "manifest.json"], archive.Entries.Select(entry => entry.FullName).Order().ToArray());
            using var reader = new StreamReader(archive.GetEntry("diagnostics.txt")!.Open());
            Assert.DoesNotContain("private", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
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
