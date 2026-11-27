using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class WindowsContextToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ligclaw-app-search-{Guid.NewGuid():N}");

    [Fact]
    public async Task SearchesStartMenuNamesWithoutReturningLaunchTargets()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Programs"));
        await File.WriteAllTextAsync(Path.Combine(_root, "Programs", "계산기.lnk"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(_root, "Programs", "고급 계산 도구.lnk"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(_root, "Programs", "메모장.lnk"), "fixture");
        var host = new WindowsToolHost(
            Profile(),
            [new AppSearchInstalledTool(new StartMenuAppCatalog([_root]))]);

        var result = await host.ExecuteAsync(new ToolInvokeParams(
            "call", "conversation", "run", "app.search_installed.v1", "R0",
            new Dictionary<string, object?> { ["query"] = "계산" }));

        Assert.True(result.Success);
        var apps = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(result.Output["apps"]);
        Assert.Equal(["계산기", "고급 계산 도구"], apps.Select(app => app["displayName"]));
        Assert.All(apps, app => Assert.Single(app));
        Assert.False(Assert.IsType<bool>(result.Output["truncated"]));
    }

    [Fact]
    public async Task SearchesPackagedAppsFolderRegistrationsWithoutExposingTheirAppIds()
    {
        var catalog = new StartMenuAppCatalog(
            [],
            () => [new RegisteredAppEntry(
                "apps-folder:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
                "계산기",
                "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]);
        var host = new WindowsToolHost(Profile(), [new AppSearchInstalledTool(catalog)]);

        var result = await host.ExecuteAsync(new ToolInvokeParams(
            "call", "conversation", "run", "app.search_installed.v1", "R0",
            new Dictionary<string, object?> { ["query"] = "계산기" }));

        Assert.True(result.Success);
        var app = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(result.Output["apps"]));
        Assert.Equal("계산기", app["displayName"]);
        Assert.Single(app);
    }

    [Fact]
    public async Task ExplorerContextReturnsOnlyTheApprovedBoundedSnapshot()
    {
        var snapshot = new ExplorerContextSnapshot(
            "C:\\Work",
            [
                new ExplorerSelectedItem("C:\\Work\\report.docx", "report.docx", false),
                new ExplorerSelectedItem("C:\\Work\\Archive", "Archive", true),
            ],
            true);
        var reader = new FakeExplorerContextReader(snapshot);
        var host = new WindowsToolHost(Profile(), [new ExplorerGetContextTool(reader)]);
        var invocation = new ToolInvokeParams(
            "call", "conversation", "run", "explorer.get_context.v1", "R1",
            new Dictionary<string, object?> { ["reason"] = "현재 선택 항목을 확인하기 위해" });

        var preview = host.CreateApprovalPrompt(invocation);
        reader.Context = new ExplorerContextSnapshot("C:\\Changed", [], false);
        var result = await host.ExecuteAsync(invocation);

        Assert.True(preview.Success);
        Assert.Contains("C:\\Work\\report.docx", preview.Prompt!.Details, StringComparison.Ordinal);
        Assert.True(result.Success);
        Assert.Equal("C:\\Work", result.Output["folderPath"]);
        var selected = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(result.Output["selectedItems"]);
        Assert.Equal(2, selected.Count);
        Assert.True(Assert.IsType<bool>(result.Output["selectionTruncated"]));
        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task ExplorerContextCannotExecuteAfterApprovalIsDiscarded()
    {
        var host = new WindowsToolHost(
            Profile(),
            [new ExplorerGetContextTool(new FakeExplorerContextReader(
                new ExplorerContextSnapshot("C:\\Work", [], false)))]);
        var invocation = new ToolInvokeParams(
            "call", "conversation", "run", "explorer.get_context.v1", "R1",
            new Dictionary<string, object?> { ["reason"] = "현재 폴더를 확인하기 위해" });

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        host.DiscardApproval(invocation);
        var result = await host.ExecuteAsync(invocation);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ReadsMinimalFileMetadataWithoutContent()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "metadata.txt");
        await File.WriteAllTextAsync(path, "private content");
        var host = new WindowsToolHost(Profile(), [new FileGetMetadataTool(new SafeFileOperationService())]);

        var result = await host.ExecuteAsync(new ToolInvokeParams(
            "call", "conversation", "run", "file.get_metadata.v1", "R0",
            new Dictionary<string, object?> { ["path"] = path }));

        Assert.True(result.Success);
        Assert.Equal("metadata.txt", result.Output["name"]);
        Assert.Equal((long)15, result.Output["sizeBytes"]);
        Assert.DoesNotContain(result.Output.Values, value => StringComparer.Ordinal.Equals(value as string, "private content"));
    }

    [Fact]
    public async Task ReadsApprovedUtfTextAndRejectsAChangedFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "report.txt");
        await File.WriteAllTextAsync(path, "첫 번째 내용");
        var host = new WindowsToolHost(
            Profile(),
            [new FileReadTextTool(new SafeFileOperationService(), new BoundedFileTextReader())]);
        var invocation = FileReadInvocation(path);

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        var first = await host.ExecuteAsync(invocation);
        Assert.True(first.Success);
        Assert.Equal("첫 번째 내용", first.Output["text"]);

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        await File.WriteAllTextAsync(path, "승인 뒤 변경된 더 긴 내용");
        var changed = await host.ExecuteAsync(invocation);
        Assert.False(changed.Success);
    }

    [Fact]
    public async Task RejectsBinaryContentAfterApproval()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "binary.dat");
        await File.WriteAllBytesAsync(path, [0x10, 0x00, 0x20, 0x30]);
        var host = new WindowsToolHost(
            Profile(),
            [new FileReadTextTool(new SafeFileOperationService(), new BoundedFileTextReader())]);
        var invocation = FileReadInvocation(path);

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        var result = await host.ExecuteAsync(invocation);

        Assert.False(result.Success);
        Assert.DoesNotContain("binary.dat", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BoundsTextReadsTo128KiB()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "large.txt");
        await File.WriteAllTextAsync(path, new string('a', 128 * 1024 + 10));

        var result = await new BoundedFileTextReader().ReadAsync(path, 128 * 1024, CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(128 * 1024, result.Text.Length);
    }

    private static WindowsPlatformProfile Profile() =>
        WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);

    private static ToolInvokeParams FileReadInvocation(string path) => new(
        "call-read", "conversation", "run", "file.read_text.v1", "R1",
        new Dictionary<string, object?>
        {
            ["path"] = path,
            ["reason"] = "사용자가 파일 내용을 확인해 달라고 요청했기 때문에",
        });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class FakeExplorerContextReader(ExplorerContextSnapshot? context) : IExplorerContextReader
    {
        public ExplorerContextSnapshot? Context { get; set; } = context;
        public int ReadCount { get; private set; }
        public ExplorerContextSnapshot? ReadMostRecent()
        {
            ReadCount++;
            return Context;
        }
    }
}
