using System.IO;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class FileSearchTool(IFileOperationService files) : IWindowsToolAdapter
{
    public string Name => "file.search.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
    public int Priority => 0;

    public Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (!ToolInputReader.HasOnlyKeys(input, "rootPath", "pattern") ||
            !ToolInputReader.TryGetRequiredString(input, "rootPath", 32_767, out var rootPath) ||
            !ToolInputReader.TryGetRequiredString(input, "pattern", 128, out var pattern))
            return Task.FromResult(Failure("검색 위치 또는 패턴이 올바르지 않습니다."));
        var root = files.ResolveExisting(rootPath, requireDirectory: true);
        if (root is null) return Task.FromResult(Failure("검색할 폴더를 찾을 수 없습니다."));
        var matches = files.Search(root, pattern, 100, cancellationToken, out var truncated)
            .Select(match => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["path"] = match.Path,
                ["name"] = match.Name,
                ["isDirectory"] = match.IsDirectory,
            })
            .ToArray();
        return Task.FromResult(new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?>
            {
                ["rootPath"] = root.CanonicalPath,
                ["matches"] = matches,
                ["truncated"] = truncated,
            },
            ActivitySummary: $"{root.DisplayName}에서 항목 {matches.Length}개를 찾았어요."));
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}

internal sealed class FileOpenTool(IFileOperationService files) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _prepared = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "file.open.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        var target = files.ResolveExisting(parameters.Value.Path);
        if (target is null) return null;
        lock (_sync) _prepared[parameters.Value.Path] = target.Identity;
        return new WindowsToolApprovalPrompt(
            target.IsDirectory ? "폴더를 열까요?" : "파일을 열까요?",
            $"{target.DisplayName}을(를) 기본 앱으로 엽니다.",
            $"경로: {target.CanonicalPath}\n요청 이유: {parameters.Value.Reason}");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("경로 또는 요청 이유가 올바르지 않습니다.");
        string? identity;
        lock (_sync) _prepared.Remove(parameters.Value.Path, out identity);
        var target = files.ResolveExisting(parameters.Value.Path);
        if (identity is null || target is null || !StringComparer.Ordinal.Equals(identity, target.Identity))
            return Failure("승인 후 대상이 변경되어 열지 않았어요.");
        await files.OpenAsync(target, cancellationToken).ConfigureAwait(false);
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?>
            {
                ["opened"] = true,
                ["displayName"] = target.DisplayName,
                ["isDirectory"] = target.IsDirectory,
            },
            ActivitySummary: $"{target.DisplayName}을(를) 열었어요.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return;
        lock (_sync) _prepared.Remove(parameters.Value.Path);
    }

    private static (string Path, string Reason)? Read(IReadOnlyDictionary<string, object?> input)
    {
        if (!ToolInputReader.HasOnlyKeys(input, "path", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "path", 32_767, out var path) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason))
            return null;
        return (path, reason);
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}

internal abstract class FileBatchTransferTool(IFileOperationService files, IUndoJournal undoJournal) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PreparedTransfer> _prepared = new(StringComparer.Ordinal);

    public abstract string Name { get; }
    public string Risk => "R2";
    public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);
    protected abstract string Heading { get; }
    protected abstract string Verb { get; }
    protected abstract string CompletedVerb { get; }
    protected abstract string UndoKind { get; }
    protected abstract bool MutatesSource { get; }

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        var destination = files.ResolveExisting(parameters.DestinationDirectory, requireDirectory: true);
        if (destination is null || FileMutationSafety.IsProtected(destination.CanonicalPath)) return null;
        var sources = parameters.Sources.Select(path => files.ResolveExisting(path, requireDirectory: false)).ToArray();
        if (sources.Any(source => source is null)) return null;
        var concrete = sources.Cast<FilePathTarget>().ToArray();
        if (MutatesSource && concrete.Any(source => FileMutationSafety.IsProtected(source.CanonicalPath))) return null;
        if (concrete.Select(source => source.CanonicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != concrete.Length)
            return null;
        var destinationPaths = concrete.Select(source => Path.Combine(destination.CanonicalPath, source.DisplayName)).ToArray();
        if (destinationPaths.Any(path => File.Exists(path) || Directory.Exists(path))) return null;
        var prepared = new PreparedTransfer(destination, concrete, destinationPaths);
        lock (_sync) _prepared[Fingerprint(parameters)] = prepared;
        return new WindowsToolApprovalPrompt(
            Heading,
            $"파일 {concrete.Length}개를 {destination.DisplayName} 폴더로 {Verb}.",
            $"대상 폴더: {destination.CanonicalPath}\n\n{string.Join("\n", concrete.Select(source => $"• {source.CanonicalPath}"))}\n\n덮어쓰지 않으며 최대 20개만 처리합니다.\n요청 이유: {parameters.Reason}");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("파일 목록, 대상 폴더 또는 요청 이유가 올바르지 않습니다.");
        PreparedTransfer? prepared;
        lock (_sync) _prepared.Remove(Fingerprint(parameters), out prepared);
        if (prepared is null) return Failure("승인한 파일 작업 정보를 찾을 수 없어요.");
        var destination = files.ResolveExisting(parameters.DestinationDirectory, requireDirectory: true);
        if (destination is null || !StringComparer.Ordinal.Equals(destination.Identity, prepared.Destination.Identity))
            return Failure("승인 후 대상 폴더가 변경되어 실행하지 않았어요.");

        var results = new List<IReadOnlyDictionary<string, object?>>();
        var succeeded = 0;
        for (var index = 0; index < prepared.Sources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var approvedSource = prepared.Sources[index];
            var current = files.ResolveExisting(approvedSource.CanonicalPath, requireDirectory: false);
            var destinationPath = prepared.DestinationPaths[index];
            if (current is null || !StringComparer.Ordinal.Equals(current.Identity, approvedSource.Identity))
            {
                results.Add(Item(approvedSource.DisplayName, false, "승인 후 파일이 변경됐어요.", null));
                continue;
            }
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                results.Add(Item(current.DisplayName, false, "대상 위치에 같은 이름이 있어요.", null));
                continue;
            }
            try
            {
                await TransferAsync(current, destinationPath, cancellationToken).ConfigureAwait(false);
                var after = files.ResolveExisting(destinationPath, requireDirectory: false)
                    ?? throw new IOException("작업 결과 파일을 확인할 수 없습니다.");
                string? undoId = null;
                try
                {
                    undoId = await undoJournal.CreateUndoAsync(
                        UndoKind,
                        current.CanonicalPath,
                        after.CanonicalPath,
                        after.Identity,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 파일 작업 결과는 유지하고 undo 제공 여부만 결과에 반영한다.
                }
                succeeded++;
                results.Add(Item(current.DisplayName, true, null, undoId));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                results.Add(Item(current.DisplayName, false, "권한 또는 파일 상태 때문에 처리하지 못했어요.", null));
            }
        }
        var failed = results.Count - succeeded;
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?>
            {
                ["succeeded"] = succeeded,
                ["failed"] = failed,
                ["items"] = results,
            },
            ActivitySummary: $"파일 {succeeded}개를 {CompletedVerb}. 실패 {failed}개.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return;
        lock (_sync) _prepared.Remove(Fingerprint(parameters));
    }

    protected abstract Task TransferAsync(FilePathTarget source, string destinationPath, CancellationToken cancellationToken);

    protected IFileOperationService Files => files;

    private static BatchTransferInput? Read(IReadOnlyDictionary<string, object?> input)
    {
        if (!ToolInputReader.HasOnlyKeys(input, "sources", "destinationDirectory", "reason") ||
            !ToolInputReader.TryGetRequiredStrings(input, "sources", 20, 32_767, out var sources) ||
            !ToolInputReader.TryGetRequiredString(input, "destinationDirectory", 32_767, out var destination) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason))
            return null;
        return new BatchTransferInput(sources, destination, reason);
    }

    private static string Fingerprint(BatchTransferInput input) =>
        $"{string.Join('\n', input.Sources)}\n-->{input.DestinationDirectory}";

    private static IReadOnlyDictionary<string, object?> Item(string name, bool success, string? error, string? undoId) =>
        new Dictionary<string, object?>
        {
            ["name"] = name,
            ["success"] = success,
            ["error"] = error,
            ["undoId"] = undoId,
        };

    protected static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);

    private sealed record BatchTransferInput(IReadOnlyList<string> Sources, string DestinationDirectory, string Reason);
    private sealed record PreparedTransfer(
        FilePathTarget Destination,
        IReadOnlyList<FilePathTarget> Sources,
        IReadOnlyList<string> DestinationPaths);
}

internal sealed class FileCopyTool(IFileOperationService files, IUndoJournal undoJournal) :
    FileBatchTransferTool(files, undoJournal)
{
    public override string Name => "file.copy.v1";
    protected override string Heading => "파일을 복사할까요?";
    protected override string Verb => "복사합니다";
    protected override string CompletedVerb => "복사했어요";
    protected override string UndoKind => "copy";
    protected override bool MutatesSource => false;
    protected override Task TransferAsync(FilePathTarget source, string destinationPath, CancellationToken cancellationToken) =>
        Files.CopyAsync(source, destinationPath, cancellationToken);
}

internal sealed class FileMoveTool(IFileOperationService files, IUndoJournal undoJournal) :
    FileBatchTransferTool(files, undoJournal)
{
    public override string Name => "file.move.v1";
    protected override string Heading => "파일을 이동할까요?";
    protected override string Verb => "이동합니다";
    protected override string CompletedVerb => "이동했어요";
    protected override string UndoKind => "move";
    protected override bool MutatesSource => true;
    protected override Task TransferAsync(FilePathTarget source, string destinationPath, CancellationToken cancellationToken) =>
        Files.MoveAsync(source, destinationPath, cancellationToken);
}

internal sealed class FileRenameTool(IFileOperationService files, IUndoJournal undoJournal) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PreparedRename> _prepared = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "file.rename.v1";
    public string Risk => "R2";
    public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null || !IsSafeName(parameters.Value.NewName)) return null;
        var source = files.ResolveExisting(parameters.Value.Path, requireDirectory: false);
        if (source is null || FileMutationSafety.IsProtected(source.CanonicalPath)) return null;
        var destination = Path.Combine(Path.GetDirectoryName(source.CanonicalPath)!, parameters.Value.NewName);
        if (File.Exists(destination) || Directory.Exists(destination)) return null;
        lock (_sync) _prepared[parameters.Value.Path] = new PreparedRename(source, destination);
        return new WindowsToolApprovalPrompt(
            "파일 이름을 바꿀까요?",
            $"{source.DisplayName}을(를) {parameters.Value.NewName}(으)로 바꿉니다.",
            $"현재 경로: {source.CanonicalPath}\n요청 이유: {parameters.Value.Reason}\n\n같은 이름이 있으면 실행하지 않습니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("파일 경로, 새 이름 또는 요청 이유가 올바르지 않습니다.");
        PreparedRename? prepared;
        lock (_sync) _prepared.Remove(parameters.Value.Path, out prepared);
        var current = files.ResolveExisting(parameters.Value.Path, requireDirectory: false);
        if (prepared is null || current is null || !StringComparer.Ordinal.Equals(current.Identity, prepared.Source.Identity))
            return Failure("승인 후 파일이 변경되어 이름을 바꾸지 않았어요.");
        if (File.Exists(prepared.DestinationPath) || Directory.Exists(prepared.DestinationPath))
            return Failure("같은 이름의 항목이 있어 이름을 바꾸지 않았어요.");
        await files.MoveAsync(current, prepared.DestinationPath, cancellationToken).ConfigureAwait(false);
        var after = files.ResolveExisting(prepared.DestinationPath, requireDirectory: false)
            ?? throw new IOException("이름 변경 결과를 확인할 수 없습니다.");
        string? undoId = null;
        try
        {
            undoId = await undoJournal.CreateUndoAsync(
                "rename", current.CanonicalPath, after.CanonicalPath, after.Identity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 이름 변경은 완료됐으며 undo 제공 실패만 결과에 반영한다.
        }
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?> { ["renamed"] = true, ["displayName"] = after.DisplayName, ["undoId"] = undoId },
            ActivitySummary: $"{current.DisplayName}의 이름을 {after.DisplayName}(으)로 바꿨어요.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return;
        lock (_sync) _prepared.Remove(parameters.Value.Path);
    }

    private static (string Path, string NewName, string Reason)? Read(IReadOnlyDictionary<string, object?> input)
    {
        if (!ToolInputReader.HasOnlyKeys(input, "path", "newName", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "path", 32_767, out var path) ||
            !ToolInputReader.TryGetRequiredStringExact(input, "newName", 255, out var newName) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason))
            return null;
        return (path, newName, reason);
    }

    private static bool IsSafeName(string name) =>
        name is not "." and not ".." &&
        !name.EndsWith('.') && !name.EndsWith(' ') &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !IsReservedDeviceName(Path.GetFileNameWithoutExtension(name));

    private static bool IsReservedDeviceName(string name) =>
        name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
        Enumerable.Range(1, 9).Any(index =>
            name.Equals($"COM{index}", StringComparison.OrdinalIgnoreCase) ||
            name.Equals($"LPT{index}", StringComparison.OrdinalIgnoreCase));

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);

    private sealed record PreparedRename(FilePathTarget Source, string DestinationPath);
}

internal sealed class FileRecycleTool(IFileOperationService files) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IReadOnlyList<FilePathTarget>> _prepared = new(StringComparer.Ordinal);

    public string Name => "file.recycle.v1";
    public string Risk => "R2";
    public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        var targets = parameters.Value.Paths.Select(path => files.ResolveExisting(path)).ToArray();
        if (targets.Any(target => target is null)) return null;
        var concrete = targets.Cast<FilePathTarget>().ToArray();
        if (concrete.Any(target => FileMutationSafety.IsProtected(target.CanonicalPath))) return null;
        lock (_sync) _prepared[Fingerprint(parameters.Value.Paths)] = concrete;
        return new WindowsToolApprovalPrompt(
            "휴지통으로 이동할까요?",
            $"항목 {concrete.Length}개를 Windows 휴지통으로 이동합니다.",
            $"{string.Join("\n", concrete.Select(target => $"• {target.CanonicalPath}"))}\n\n요청 이유: {parameters.Value.Reason}\n휴지통에서 직접 복원할 수 있습니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("항목 목록 또는 요청 이유가 올바르지 않습니다.");
        IReadOnlyList<FilePathTarget>? prepared;
        lock (_sync) _prepared.Remove(Fingerprint(parameters.Value.Paths), out prepared);
        if (prepared is null) return Failure("승인한 휴지통 이동 정보를 찾을 수 없어요.");
        var results = new List<IReadOnlyDictionary<string, object?>>();
        var succeeded = 0;
        foreach (var approved in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = files.ResolveExisting(approved.CanonicalPath);
            if (current is null || !StringComparer.Ordinal.Equals(current.Identity, approved.Identity))
            {
                results.Add(Item(approved.DisplayName, false, "승인 후 항목이 변경됐어요."));
                continue;
            }
            try
            {
                await files.RecycleAsync(current, cancellationToken).ConfigureAwait(false);
                succeeded++;
                results.Add(Item(current.DisplayName, true, null));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or OperationCanceledException)
            {
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                results.Add(Item(current.DisplayName, false, "권한 또는 파일 상태 때문에 처리하지 못했어요."));
            }
        }
        var failed = results.Count - succeeded;
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?> { ["succeeded"] = succeeded, ["failed"] = failed, ["items"] = results },
            ActivitySummary: $"항목 {succeeded}개를 휴지통으로 이동했어요. 실패 {failed}개.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return;
        lock (_sync) _prepared.Remove(Fingerprint(parameters.Value.Paths));
    }

    private static (IReadOnlyList<string> Paths, string Reason)? Read(IReadOnlyDictionary<string, object?> input)
    {
        if (!ToolInputReader.HasOnlyKeys(input, "paths", "reason") ||
            !ToolInputReader.TryGetRequiredStrings(input, "paths", 20, 32_767, out var paths) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason))
            return null;
        return (paths, reason);
    }

    private static string Fingerprint(IReadOnlyList<string> paths) => string.Join('\n', paths);

    private static IReadOnlyDictionary<string, object?> Item(string name, bool success, string? error) =>
        new Dictionary<string, object?> { ["name"] = name, ["success"] = success, ["error"] = error };

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}

internal sealed class FileUndoTool(IFileOperationService files, IUndoJournal undoJournal) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, FileUndoEntry> _prepared = new(StringComparer.Ordinal);

    public string Name => "file.undo.v1";
    public string Risk => "R2";
    public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        var entry = undoJournal.GetPendingUndoAsync(parameters.Value.UndoId, CancellationToken.None).GetAwaiter().GetResult();
        if (entry is null) return null;
        var current = files.ResolveExisting(entry.CurrentPath, requireDirectory: false);
        if (current is null || !StringComparer.Ordinal.Equals(current.Identity, entry.CurrentIdentity)) return null;
        lock (_sync) _prepared[entry.UndoId] = entry;
        return new WindowsToolApprovalPrompt(
            "파일 작업을 되돌릴까요?",
            $"{current.DisplayName}의 이전 작업을 되돌립니다.",
            $"현재 경로: {entry.CurrentPath}\n원래 경로: {entry.OriginalPath}\n요청 이유: {parameters.Value.Reason}\n\n현재 파일이 변경되지 않은 경우에만 실행합니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("되돌리기 ID 또는 요청 이유가 올바르지 않습니다.");
        FileUndoEntry? entry;
        lock (_sync) _prepared.Remove(parameters.Value.UndoId, out entry);
        if (entry is null) return Failure("승인한 되돌리기 정보를 찾을 수 없어요.");
        var pending = await undoJournal.GetPendingUndoAsync(entry.UndoId, cancellationToken).ConfigureAwait(false);
        if (pending is null || pending != entry) return Failure("되돌리기 기록이 변경되어 실행하지 않았어요.");
        var displayName = Path.GetFileName(entry.CurrentPath);
        await files.UndoAsync(entry, cancellationToken).ConfigureAwait(false);
        await undoJournal.MarkUndoneAsync(entry.UndoId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?> { ["undone"] = true, ["displayName"] = displayName },
            ActivitySummary: $"{displayName}의 파일 작업을 되돌렸어요.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return;
        lock (_sync) _prepared.Remove(parameters.Value.UndoId);
    }

    private static (string UndoId, string Reason)? Read(IReadOnlyDictionary<string, object?> input)
    {
        if (!ToolInputReader.HasOnlyKeys(input, "undoId", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "undoId", 32, out var undoId) ||
            undoId.Length != 32 || undoId.Any(character => !char.IsAsciiHexDigitLower(character)) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason))
            return null;
        return (undoId, reason);
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
