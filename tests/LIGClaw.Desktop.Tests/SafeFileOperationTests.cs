using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class SafeFileOperationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LIGClaw.Tests", Guid.NewGuid().ToString("N"));
    private readonly SafeFileOperationService _files = new();

    public SafeFileOperationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void RejectsRelativeAndDevicePaths()
    {
        Assert.Null(_files.ResolveExisting("relative.txt"));
        Assert.Null(_files.ResolveExisting("\\\\.\\PhysicalDrive0"));
        Assert.Null(_files.ResolveExisting("\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy1"));
        Assert.True(FileMutationSafety.IsProtected("\\\\server\\share\\file.txt"));
        Assert.True(FileMutationSafety.IsProtected(Path.GetPathRoot(_root)!));
    }

    [Fact]
    public void ResolvesLongPathsBeyondLegacyMaxPath()
    {
        var directory = _root;
        for (var index = 0; index < 12; index++)
            directory = Path.Combine(directory, $"long-segment-{index:D2}-abcdefghij");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fixture.txt");
        File.WriteAllText(path, "fixture");

        var target = _files.ResolveExisting(path, requireDirectory: false);

        Assert.NotNull(target);
        Assert.True(target.CanonicalPath.Length > 260);
    }

    [Fact]
    public void ResolvesAReparsePointToItsFinalTargetWhenTheOsAllowsCreation()
    {
        var targetDirectory = Directory.CreateDirectory(Path.Combine(_root, "target"));
        var file = Path.Combine(targetDirectory.FullName, "linked.txt");
        File.WriteAllText(file, "fixture");
        var link = Path.Combine(_root, "link");
        try
        {
            _ = Directory.CreateSymbolicLink(link, targetDirectory.FullName);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var resolved = _files.ResolveExisting(Path.Combine(link, "linked.txt"), requireDirectory: false);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(file), resolved.CanonicalPath, ignoreCase: true);
    }

    [Fact]
    public async Task CopyReportsAPostApprovalCollisionAsAPartialFailureWithoutOverwrite()
    {
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(_root, "source"));
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination"));
        var first = Path.Combine(sourceDirectory.FullName, "first.txt");
        var second = Path.Combine(sourceDirectory.FullName, "second.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        var journal = new FakeUndoJournal();
        var host = Host(new FileCopyTool(_files, journal));
        var invocation = TransferInvocation("file.copy.v1", [first, second], destination.FullName);

        var preview = host.CreateApprovalPrompt(invocation);
        Assert.True(preview.Success);
        Assert.Equal("R2", preview.Prompt!.Risk);
        Assert.True(preview.Prompt.CanUndo);
        File.WriteAllText(Path.Combine(destination.FullName, "second.txt"), "do not overwrite");
        var result = await host.ExecuteAsync(invocation);

        Assert.True(result.Success);
        Assert.Equal(1, result.Output["succeeded"]);
        Assert.Equal(1, result.Output["failed"]);
        Assert.Equal("first", File.ReadAllText(Path.Combine(destination.FullName, "first.txt")));
        Assert.Equal("do not overwrite", File.ReadAllText(Path.Combine(destination.FullName, "second.txt")));
        Assert.Single(journal.Entries);
    }

    [Fact]
    public async Task PermissionDenialIsReturnedAsAnItemFailure()
    {
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination"));
        var source = Path.Combine(_root, "denied.txt");
        File.WriteAllText(source, "fixture");
        var denied = new DenyingFileOperationService(_files);
        var host = Host(new FileCopyTool(denied, new FakeUndoJournal()));
        var invocation = TransferInvocation("file.copy.v1", [source], destination.FullName);

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        var result = await host.ExecuteAsync(invocation);

        Assert.True(result.Success);
        Assert.Equal(0, result.Output["succeeded"]);
        Assert.Equal(1, result.Output["failed"]);
        Assert.False(File.Exists(Path.Combine(destination.FullName, "denied.txt")));
    }

    [Fact]
    public void RejectsMoreThanTwentyMutationItemsBeforeApproval()
    {
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination"));
        var sources = Enumerable.Range(0, 21).Select(index =>
        {
            var path = Path.Combine(_root, $"{index}.txt");
            File.WriteAllText(path, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return path;
        }).ToArray();
        var host = Host(new FileMoveTool(_files, new FakeUndoJournal()));

        Assert.False(host.CreateApprovalPrompt(TransferInvocation("file.move.v1", sources, destination.FullName)).Success);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("LPT1.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void RenameRejectsWindowsReservedOrAmbiguousNames(string newName)
    {
        var source = Path.Combine(_root, "source.txt");
        File.WriteAllText(source, "fixture");
        var host = Host(new FileRenameTool(_files, new FakeUndoJournal()));
        var invocation = new ToolInvokeParams(
            "rename", "conversation", "run", "file.rename.v1", "R2",
            new Dictionary<string, object?>
            {
                ["path"] = source,
                ["newName"] = newName,
                ["reason"] = "테스트",
            });

        Assert.False(host.CreateApprovalPrompt(invocation).Success);
    }

    [Fact]
    public async Task MoveCanBeUndoneOnlyWhileTheMovedFileIdentityIsUnchanged()
    {
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination"));
        var source = Path.Combine(_root, "move.txt");
        File.WriteAllText(source, "move me");
        var journal = new FakeUndoJournal();
        var moveHost = Host(new FileMoveTool(_files, journal));
        var move = TransferInvocation("file.move.v1", [source], destination.FullName);
        Assert.True(moveHost.CreateApprovalPrompt(move).Success);
        Assert.True((await moveHost.ExecuteAsync(move)).Success);
        var entry = Assert.Single(journal.Entries);
        Assert.False(File.Exists(source));

        var undoHost = Host(new FileUndoTool(_files, journal));
        var undo = new ToolInvokeParams(
            "undo-call", "conversation", "run", "file.undo.v1", "R2",
            new Dictionary<string, object?> { ["undoId"] = entry.UndoId, ["reason"] = "사용자가 되돌리기를 요청했기 때문에" });
        Assert.True(undoHost.CreateApprovalPrompt(undo).Success);
        Assert.True((await undoHost.ExecuteAsync(undo)).Success);

        Assert.True(File.Exists(source));
        Assert.Null(await journal.GetPendingUndoAsync(entry.UndoId, CancellationToken.None));
    }

    [Fact]
    public async Task UndoRefusesAFileChangedAfterApproval()
    {
        var original = Path.Combine(_root, "original.txt");
        var current = Path.Combine(_root, "current.txt");
        File.WriteAllText(current, "before");
        var target = Assert.IsType<FilePathTarget>(_files.ResolveExisting(current, requireDirectory: false));
        var journal = new FakeUndoJournal();
        var undoId = await journal.CreateUndoAsync("move", original, current, target.Identity, CancellationToken.None);
        var host = Host(new FileUndoTool(_files, journal));
        var invocation = new ToolInvokeParams(
            "undo-call", "conversation", "run", "file.undo.v1", "R2",
            new Dictionary<string, object?> { ["undoId"] = undoId, ["reason"] = "테스트" });
        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        File.AppendAllText(current, " changed");

        var result = await host.ExecuteAsync(invocation);

        Assert.False(result.Success);
        Assert.False(File.Exists(original));
        Assert.True(File.Exists(current));
    }

    private static WindowsToolHost Host(IWindowsToolAdapter adapter) =>
        new(WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true), [adapter]);

    private static ToolInvokeParams TransferInvocation(string name, IReadOnlyList<string> sources, string destination) =>
        new(
            $"{name}-call", "conversation", "run", name, "R2",
            new Dictionary<string, object?>
            {
                ["sources"] = sources,
                ["destinationDirectory"] = destination,
                ["reason"] = "사용자가 요청했기 때문에",
            });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeUndoJournal : IUndoJournal
    {
        public List<FileUndoEntry> Entries { get; } = [];
        private readonly HashSet<string> _undone = new(StringComparer.Ordinal);

        public Task<string> CreateUndoAsync(
            string kind,
            string originalPath,
            string currentPath,
            string currentIdentity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Guid.NewGuid().ToString("N");
            Entries.Add(new FileUndoEntry(id, kind, originalPath, currentPath, currentIdentity, DateTimeOffset.UtcNow));
            return Task.FromResult(id);
        }

        public Task<FileUndoEntry?> GetPendingUndoAsync(string undoId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_undone.Contains(undoId)
                ? null
                : Entries.SingleOrDefault(entry => entry.UndoId == undoId));
        }

        public Task MarkUndoneAsync(string undoId, DateTimeOffset undoneAtUtc, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _undone.Add(undoId);
            return Task.CompletedTask;
        }
    }

    private sealed class DenyingFileOperationService(IFileOperationService inner) : IFileOperationService
    {
        public FilePathTarget? ResolveExisting(string path, bool? requireDirectory = null) =>
            inner.ResolveExisting(path, requireDirectory);
        public IReadOnlyList<FileSearchMatch> Search(
            FilePathTarget root,
            string pattern,
            int maximumResults,
            CancellationToken cancellationToken,
            out bool truncated) => inner.Search(root, pattern, maximumResults, cancellationToken, out truncated);
        public Task OpenAsync(FilePathTarget target, CancellationToken cancellationToken) =>
            inner.OpenAsync(target, cancellationToken);
        public Task CopyAsync(FilePathTarget source, string destinationPath, CancellationToken cancellationToken) =>
            throw new UnauthorizedAccessException("fixture denial");
        public Task MoveAsync(FilePathTarget source, string destinationPath, CancellationToken cancellationToken) =>
            inner.MoveAsync(source, destinationPath, cancellationToken);
        public Task RecycleAsync(FilePathTarget target, CancellationToken cancellationToken) =>
            inner.RecycleAsync(target, cancellationToken);
        public Task UndoAsync(FileUndoEntry entry, CancellationToken cancellationToken) =>
            inner.UndoAsync(entry, cancellationToken);
    }
}
