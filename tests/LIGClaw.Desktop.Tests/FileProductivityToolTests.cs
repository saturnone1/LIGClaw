using System.IO.Compression;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class FileProductivityToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LIGClaw.Tests", Guid.NewGuid().ToString("N"));

    public FileProductivityToolTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Creates_a_new_directory_only_after_preview_and_records_undo()
    {
        var undo = new FakeUndoJournal();
        var host = Host(new FileCreateDirectoryTool(new SafeFileOperationService(), undo));
        var path = Path.Combine(_root, "Reports");
        var invocation = Invocation("file.create_directory.v1", "R1", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["reason"] = "보고서 폴더가 필요해서",
        });

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        var result = await host.ExecuteAsync(invocation);

        Assert.True(result.Success, result.Error);
        Assert.True(Directory.Exists(path));
        Assert.Equal("create", Assert.Single(undo.Created).Kind);
    }

    [Fact]
    public async Task Writes_new_utf8_text_without_exposing_content_in_preview_or_overwriting()
    {
        const string secretText = "내부 보고서 내용";
        var path = Path.Combine(_root, "report.txt");
        var host = Host(new FileWriteTextTool(new SafeFileOperationService(), new FakeUndoJournal()));
        var invocation = Invocation("file.write_text.v1", "R2", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["text"] = secretText,
            ["reason"] = "새 보고서를 작성하려고",
        });

        var preview = host.CreateApprovalPrompt(invocation);
        Assert.True(preview.Success);
        Assert.DoesNotContain(secretText, preview.Prompt!.Details, StringComparison.Ordinal);
        var result = await host.ExecuteAsync(invocation);

        Assert.True(result.Success, result.Error);
        Assert.Equal(secretText, await File.ReadAllTextAsync(path));

        var overwrite = Invocation("file.write_text.v1", "R2", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["text"] = "replacement",
            ["reason"] = "덮어쓰기 시도",
        });
        Assert.False(host.CreateApprovalPrompt(overwrite).Success);
        Assert.Equal(secretText, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Creates_and_extracts_a_bounded_zip_through_atomic_new_targets()
    {
        var first = Path.Combine(_root, "first.txt");
        var second = Path.Combine(_root, "second.txt");
        await File.WriteAllTextAsync(first, "one");
        await File.WriteAllTextAsync(second, "two");
        var archivePath = Path.Combine(_root, "bundle.zip");
        var files = new SafeFileOperationService();
        var undo = new FakeUndoJournal();
        var createHost = Host(new FileZipCreateTool(files, undo));
        var create = Invocation("file.zip_create.v1", "R2", new Dictionary<string, object?>
        {
            ["sourcePaths"] = new[] { first, second },
            ["destinationPath"] = archivePath,
            ["reason"] = "전달용 묶음",
        });

        Assert.True(createHost.CreateApprovalPrompt(create).Success);
        Assert.True((await createHost.ExecuteAsync(create)).Success);

        var destination = Path.Combine(_root, "Extracted");
        var extractHost = Host(new FileZipExtractTool(files, undo));
        var extract = Invocation("file.zip_extract.v1", "R2", new Dictionary<string, object?>
        {
            ["zipPath"] = archivePath,
            ["destinationPath"] = destination,
            ["reason"] = "내용 확인",
        });
        Assert.True(extractHost.CreateApprovalPrompt(extract).Success);
        var result = await extractHost.ExecuteAsync(extract);

        Assert.True(result.Success, result.Error);
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(destination, "first.txt")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(destination, "second.txt")));
    }

    [Fact]
    public void Rejects_zip_path_traversal_before_approval()
    {
        var archivePath = Path.Combine(_root, "unsafe.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../outside.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("unsafe");
        }
        var host = Host(new FileZipExtractTool(new SafeFileOperationService(), new FakeUndoJournal()));
        var invocation = Invocation("file.zip_extract.v1", "R2", new Dictionary<string, object?>
        {
            ["zipPath"] = archivePath,
            ["destinationPath"] = Path.Combine(_root, "UnsafeOut"),
            ["reason"] = "검사",
        });

        Assert.False(host.CreateApprovalPrompt(invocation).Success);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside.txt")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private static WindowsToolHost Host(IWindowsToolAdapter adapter) => new(
        WindowsPlatformProfile.Classify(10, 0, 26_100, isWorkstation: true), [adapter]);

    private static ToolInvokeParams Invocation(string name, string risk, IReadOnlyDictionary<string, object?> input) =>
        new(Guid.NewGuid().ToString("N"), "conversation", "run", name, risk, input);

    private sealed class FakeUndoJournal : IUndoJournal
    {
        public List<(string Kind, string CurrentPath)> Created { get; } = [];
        public Task<string> CreateUndoAsync(string kind, string originalPath, string currentPath, string currentIdentity, CancellationToken cancellationToken)
        {
            Created.Add((kind, currentPath));
            return Task.FromResult(Guid.NewGuid().ToString("N"));
        }
        public Task<FileUndoEntry?> GetPendingUndoAsync(string undoId, CancellationToken cancellationToken) => Task.FromResult<FileUndoEntry?>(null);
        public Task MarkUndoneAsync(string undoId, DateTimeOffset undoneAtUtc, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
