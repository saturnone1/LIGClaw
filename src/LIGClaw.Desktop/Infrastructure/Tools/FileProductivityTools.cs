using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record NewFileSystemTarget(FilePathTarget Parent, string Path, string DisplayName);

internal static class NewFileSystemTargetPolicy
{
    public static NewFileSystemTarget? Resolve(IFileOperationService files, string path)
    {
        var normalized = SafeFileOperationService.NormalizeAbsolutePath(path);
        if (normalized is null || FileMutationSafety.IsProtected(normalized) || File.Exists(normalized) || Directory.Exists(normalized))
            return null;
        var parentPath = Path.GetDirectoryName(normalized);
        if (parentPath is null) return null;
        var parent = files.ResolveExisting(parentPath, requireDirectory: true);
        if (parent is null || FileMutationSafety.IsProtected(parent.CanonicalPath)) return null;
        var name = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        var canonicalTarget = Path.Combine(parent.CanonicalPath, name);
        return File.Exists(canonicalTarget) || Directory.Exists(canonicalTarget)
            ? null
            : new NewFileSystemTarget(parent, canonicalTarget, name);
    }

    public static bool Revalidate(IFileOperationService files, NewFileSystemTarget target)
    {
        var parent = files.ResolveExisting(target.Parent.CanonicalPath, requireDirectory: true);
        return parent is not null && StringComparer.Ordinal.Equals(parent.Identity, target.Parent.Identity) &&
               !File.Exists(target.Path) && !Directory.Exists(target.Path);
    }
}

internal sealed class FileCreateDirectoryTool(IFileOperationService files, IUndoJournal undoJournal) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, NewFileSystemTarget> _prepared = new(StringComparer.OrdinalIgnoreCase);
    public string Name => "file.create_directory.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        var target = NewFileSystemTargetPolicy.Resolve(files, parameters.Value.Path);
        if (target is null) return null;
        lock (_sync) _prepared[parameters.Value.Path] = target;
        return new WindowsToolApprovalPrompt(
            "새 폴더를 만들까요?", $"{target.DisplayName} 폴더를 만듭니다.",
            $"경로: {target.Path}\n요청 이유: {parameters.Value.Reason}\n\n기존 항목을 덮어쓰지 않습니다.",
            GrantScope: $"directory:{target.Path}");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("폴더 경로나 요청 이유가 올바르지 않습니다.");
        NewFileSystemTarget? target;
        lock (_sync) _prepared.Remove(parameters.Value.Path, out target);
        if (target is null || !NewFileSystemTargetPolicy.Revalidate(files, target)) return Failure("승인 후 대상 위치가 변경되어 폴더를 만들지 않았어요.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(target.Path);
        var created = files.ResolveExisting(target.Path, requireDirectory: true) ?? throw new IOException("생성한 폴더를 확인할 수 없습니다.");
        var undoId = await TryCreateUndoAsync(undoJournal, created, cancellationToken).ConfigureAwait(false);
        return new WindowsToolExecutionResult(true, new Dictionary<string, object?> { ["created"] = true, ["path"] = created.CanonicalPath, ["undoId"] = undoId }, ActivitySummary: $"{target.DisplayName} 폴더를 만들었어요.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input) { var value = Read(input); if (value is not null) lock (_sync) _prepared.Remove(value.Value.Path); }
    private static (string Path, string Reason)? Read(IReadOnlyDictionary<string, object?> input) =>
        ToolInputReader.HasOnlyKeys(input, "path", "reason") && ToolInputReader.TryGetRequiredString(input, "path", 32_767, out var path) && ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason) ? (path, reason) : null;
    private static WindowsToolExecutionResult Failure(string error) => new(false, new Dictionary<string, object?>(), error);
    internal static async Task<string?> TryCreateUndoAsync(IUndoJournal journal, FilePathTarget created, CancellationToken cancellationToken)
    {
        try { return await journal.CreateUndoAsync("create", created.CanonicalPath, created.CanonicalPath, created.Identity, cancellationToken).ConfigureAwait(false); }
        catch (Exception) { return null; }
    }
}

internal sealed class FileWriteTextTool(IFileOperationService files, IUndoJournal undoJournal) : IWindowsToolAdapter
{
    private const int MaximumCharacters = 131_072;
    private readonly object _sync = new();
    private readonly Dictionary<string, PreparedWrite> _prepared = new(StringComparer.OrdinalIgnoreCase);
    public string Name => "file.write_text.v1";
    public string Risk => "R2";
    public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        var target = NewFileSystemTargetPolicy.Resolve(files, parameters.Value.Path);
        if (target is null) return null;
        lock (_sync) _prepared[parameters.Value.Path] = new PreparedWrite(target, Hash(parameters.Value.Text));
        return new WindowsToolApprovalPrompt(
            "새 텍스트 파일을 만들까요?", $"{target.DisplayName} 파일에 UTF-8 텍스트 {parameters.Value.Text.Length:N0}자를 씁니다.",
            $"경로: {target.Path}\n요청 이유: {parameters.Value.Reason}\n\n내용은 승인 화면과 실행 기록에 표시하지 않으며 기존 파일을 덮어쓰지 않습니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("파일 경로, 텍스트 또는 요청 이유가 올바르지 않습니다.");
        PreparedWrite? prepared;
        lock (_sync) _prepared.Remove(parameters.Value.Path, out prepared);
        if (prepared is null || !CryptographicOperations.FixedTimeEquals(prepared.TextHash, Hash(parameters.Value.Text)) || !NewFileSystemTargetPolicy.Revalidate(files, prepared.Target))
            return Failure("승인 후 파일 내용 또는 대상 위치가 변경되어 쓰지 않았어요.");
        var bytes = new UTF8Encoding(false, true).GetBytes(parameters.Value.Text);
        await using (var stream = new FileStream(prepared.Target.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        var created = files.ResolveExisting(prepared.Target.Path, requireDirectory: false) ?? throw new IOException("생성한 파일을 확인할 수 없습니다.");
        var undoId = await FileCreateDirectoryTool.TryCreateUndoAsync(undoJournal, created, cancellationToken).ConfigureAwait(false);
        return new WindowsToolExecutionResult(true, new Dictionary<string, object?> { ["created"] = true, ["path"] = created.CanonicalPath, ["bytesWritten"] = (long)bytes.Length, ["undoId"] = undoId }, ActivitySummary: $"{created.DisplayName} 텍스트 파일을 만들었어요.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input) { var value = Read(input); if (value is not null) lock (_sync) _prepared.Remove(value.Value.Path); }
    private static (string Path, string Text, string Reason)? Read(IReadOnlyDictionary<string, object?> input) =>
        ToolInputReader.HasOnlyKeys(input, "path", "text", "reason") && ToolInputReader.TryGetRequiredString(input, "path", 32_767, out var path) && ToolInputReader.TryGetStringExact(input, "text", MaximumCharacters, out var text) && ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason) ? (path, text, reason) : null;
    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));
    private static WindowsToolExecutionResult Failure(string error) => new(false, new Dictionary<string, object?>(), error);
    private sealed record PreparedWrite(NewFileSystemTarget Target, byte[] TextHash);
}

internal sealed class FileZipCreateTool(IFileOperationService files, IUndoJournal undoJournal) : IWindowsToolAdapter
{
    private const long MaximumSourceBytes = 256L * 1024 * 1024;
    private readonly object _sync = new();
    private readonly Dictionary<string, PreparedZip> _prepared = new(StringComparer.Ordinal);
    public string Name => "file.zip_create.v1";
    public string Risk => "R2";
    public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(120);

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        var target = NewFileSystemTargetPolicy.Resolve(files, parameters.DestinationPath);
        var sources = parameters.SourcePaths.Select(path => files.ResolveExisting(path, requireDirectory: false)).ToArray();
        if (target is null || !target.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || sources.Any(item => item is null)) return null;
        var concrete = sources.Cast<FilePathTarget>().ToArray();
        if (concrete.Select(item => item.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != concrete.Length ||
            concrete.Sum(item => new FileInfo(item.CanonicalPath).Length) > MaximumSourceBytes) return null;
        lock (_sync) _prepared[Fingerprint(parameters)] = new PreparedZip(target, concrete);
        return new WindowsToolApprovalPrompt("ZIP 파일을 만들까요?", $"파일 {concrete.Length}개를 {target.DisplayName}(으)로 압축합니다.", $"대상: {target.Path}\n요청 이유: {parameters.Reason}\n\n최대 20개·합계 256 MiB이며 기존 파일을 덮어쓰지 않습니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
    {
        var parameters = Read(input); if (parameters is null) return Failure("압축할 파일, 대상 또는 요청 이유가 올바르지 않습니다.");
        PreparedZip? prepared; lock (_sync) _prepared.Remove(Fingerprint(parameters), out prepared);
        if (prepared is null || !NewFileSystemTargetPolicy.Revalidate(files, prepared.Target)) return Failure("승인한 압축 대상 정보를 찾을 수 없어요.");
        foreach (var source in prepared.Sources)
        {
            var current = files.ResolveExisting(source.CanonicalPath, requireDirectory: false);
            if (current is null || !StringComparer.Ordinal.Equals(current.Identity, source.Identity)) return Failure("승인 후 원본 파일이 변경되어 압축하지 않았어요.");
        }
        var temp = prepared.Target.Path + $".ligclaw-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                foreach (var source in prepared.Sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(source.DisplayName, CompressionLevel.Optimal);
                    await using var targetStream = entry.Open();
                    await using var sourceStream = new FileStream(source.CanonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await sourceStream.CopyToAsync(targetStream, 64 * 1024, cancellationToken).ConfigureAwait(false);
                }
            }
            File.Move(temp, prepared.Target.Path, overwrite: false);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { } }
        var created = files.ResolveExisting(prepared.Target.Path, requireDirectory: false) ?? throw new IOException("생성한 ZIP을 확인할 수 없습니다.");
        var undoId = await FileCreateDirectoryTool.TryCreateUndoAsync(undoJournal, created, cancellationToken).ConfigureAwait(false);
        return new WindowsToolExecutionResult(true, new Dictionary<string, object?> { ["created"] = true, ["path"] = created.CanonicalPath, ["sourceCount"] = (long)prepared.Sources.Count, ["undoId"] = undoId }, ActivitySummary: $"파일 {prepared.Sources.Count}개를 압축했어요.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input) { var value = Read(input); if (value is not null) lock (_sync) _prepared.Remove(Fingerprint(value)); }
    private static Parameters? Read(IReadOnlyDictionary<string, object?> input) => ToolInputReader.HasOnlyKeys(input, "sourcePaths", "destinationPath", "reason") && ToolInputReader.TryGetRequiredStrings(input, "sourcePaths", 20, 32_767, out var sources) && ToolInputReader.TryGetRequiredString(input, "destinationPath", 32_767, out var destination) && ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason) ? new Parameters(sources, destination, reason) : null;
    private static string Fingerprint(Parameters value) => string.Join('\n', value.SourcePaths) + "\n=>" + value.DestinationPath;
    private static WindowsToolExecutionResult Failure(string error) => new(false, new Dictionary<string, object?>(), error);
    private sealed record Parameters(IReadOnlyList<string> SourcePaths, string DestinationPath, string Reason);
    private sealed record PreparedZip(NewFileSystemTarget Target, IReadOnlyList<FilePathTarget> Sources);
}

internal sealed class FileZipExtractTool(IFileOperationService files, IUndoJournal undoJournal) : IWindowsToolAdapter
{
    private const int MaximumEntries = 500;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private readonly object _sync = new();
    private readonly Dictionary<string, PreparedExtraction> _prepared = new(StringComparer.OrdinalIgnoreCase);
    public string Name => "file.zip_extract.v1";
    public string Risk => "R2";
    public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(120);

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input); if (parameters is null) return null;
        var zip = files.ResolveExisting(parameters.Value.ZipPath, requireDirectory: false);
        var target = NewFileSystemTargetPolicy.Resolve(files, parameters.Value.DestinationPath);
        if (zip is null || target is null || !zip.CanonicalPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        var entryCount = ValidateArchive(zip.CanonicalPath);
        if (entryCount is null) return null;
        lock (_sync) _prepared[parameters.Value.ZipPath] = new PreparedExtraction(zip, target, entryCount.Value);
        return new WindowsToolApprovalPrompt("ZIP 파일을 풀까요?", $"{zip.DisplayName}의 항목 {entryCount.Value}개를 새 폴더 {target.DisplayName}에 풉니다.", $"대상: {target.Path}\n요청 이유: {parameters.Value.Reason}\n\n경로 이탈·심볼릭 링크·500개 또는 512 MiB 초과 archive는 거부합니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
    {
        var parameters = Read(input); if (parameters is null) return Failure("ZIP 경로, 대상 또는 요청 이유가 올바르지 않습니다.");
        PreparedExtraction? prepared; lock (_sync) _prepared.Remove(parameters.Value.ZipPath, out prepared);
        var current = prepared is null ? null : files.ResolveExisting(prepared.Zip.CanonicalPath, requireDirectory: false);
        if (prepared is null || current is null || !StringComparer.Ordinal.Equals(current.Identity, prepared.Zip.Identity) || !NewFileSystemTargetPolicy.Revalidate(files, prepared.Target) || ValidateArchive(current.CanonicalPath) != prepared.EntryCount)
            return Failure("승인 후 ZIP 또는 대상 위치가 변경되어 압축을 풀지 않았어요.");
        var staging = Path.Combine(prepared.Target.Parent.CanonicalPath, $".ligclaw-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(current.CanonicalPath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputPath = SafeArchivePath(staging, entry.FullName) ?? throw new InvalidDataException("ZIP 항목 경로가 안전하지 않습니다.");
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(outputPath); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var source = entry.Open();
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                await source.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
            }
            Directory.Move(staging, prepared.Target.Path);
        }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (Exception) { } }
        var created = files.ResolveExisting(prepared.Target.Path, requireDirectory: true) ?? throw new IOException("압축 해제 폴더를 확인할 수 없습니다.");
        var undoId = await FileCreateDirectoryTool.TryCreateUndoAsync(undoJournal, created, cancellationToken).ConfigureAwait(false);
        return new WindowsToolExecutionResult(true, new Dictionary<string, object?> { ["extracted"] = true, ["path"] = created.CanonicalPath, ["entryCount"] = (long)prepared.EntryCount, ["undoId"] = undoId }, ActivitySummary: $"ZIP 항목 {prepared.EntryCount}개를 풀었어요.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input) { var value = Read(input); if (value is not null) lock (_sync) _prepared.Remove(value.Value.ZipPath); }
    private static int? ValidateArchive(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > MaximumEntries) return null;
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || SafeArchivePath("C:\\archive-root", entry.FullName) is null) return null;
                total = checked(total + entry.Length);
                if (total > MaximumExpandedBytes) return null;
            }
            return archive.Entries.Count;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException) { return null; }
    }
    private static string? SafeArchivePath(string root, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || Path.IsPathRooted(entryName)) return null;
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string candidate;
        try { candidate = Path.GetFullPath(Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }
    private static (string ZipPath, string DestinationPath, string Reason)? Read(IReadOnlyDictionary<string, object?> input) => ToolInputReader.HasOnlyKeys(input, "zipPath", "destinationPath", "reason") && ToolInputReader.TryGetRequiredString(input, "zipPath", 32_767, out var zip) && ToolInputReader.TryGetRequiredString(input, "destinationPath", 32_767, out var destination) && ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason) ? (zip, destination, reason) : null;
    private static WindowsToolExecutionResult Failure(string error) => new(false, new Dictionary<string, object?>(), error);
    private sealed record PreparedExtraction(FilePathTarget Zip, NewFileSystemTarget Target, int EntryCount);
}
