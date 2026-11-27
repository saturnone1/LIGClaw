using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class ExplorerActivationPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"LIGClaw-Explorer-{Guid.NewGuid():N}");

    public ExplorerActivationPolicyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ArgumentsNormalizeDeduplicateAndPreserveUserSelectionOrder()
    {
        var first = Path.Combine(_root, "첫 파일.txt");
        var second = Path.Combine(_root, "Folder");
        File.WriteAllText(first, "test");
        Directory.CreateDirectory(second);

        var activation = ExplorerActivationPolicy.FromArguments(
            ["--explorer-item", first, "--explorer-item", second, "--explorer-item", first]);

        Assert.NotNull(activation);
        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], activation.Paths);
    }

    [Fact]
    public void PayloadRoundTripsWithoutReadingFileContents()
    {
        var path = Path.Combine(_root, "secret.txt");
        File.WriteAllText(path, "must-not-appear");

        var payload = ExplorerActivationPolicy.Serialize(new ExplorerActivation([path, path]));
        var restored = ExplorerActivationPolicy.Deserialize(payload);
        var composer = ExplorerActivationPolicy.ToComposerText(restored!);

        Assert.Single(restored!.Paths);
        Assert.Contains(path, composer, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-appear", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-appear", composer, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMissingMalformedAndOversizedSelections()
    {
        Assert.Null(ExplorerActivationPolicy.FromArguments(["--explorer-item", Path.Combine(_root, "missing")]));
        Assert.Null(ExplorerActivationPolicy.Deserialize("{bad json"));
        var arguments = Enumerable.Range(0, ExplorerActivationPolicy.MaximumItems + 1)
            .SelectMany(index => new[] { "--explorer-item", _root })
            .ToArray();
        Assert.Null(ExplorerActivationPolicy.FromArguments(arguments));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
