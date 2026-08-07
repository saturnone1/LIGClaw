using System.Diagnostics;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record FilePathTarget(
    string CanonicalPath,
    string DisplayName,
    bool IsDirectory,
    string Identity);

internal sealed record FileSearchMatch(string Path, string Name, bool IsDirectory);

internal sealed record FileUndoEntry(
    string UndoId,
    string Kind,
    string OriginalPath,
    string CurrentPath,
    string CurrentIdentity,
    DateTimeOffset CreatedAtUtc);

internal interface IUndoJournal
{
    Task<string> CreateUndoAsync(
        string kind,
        string originalPath,
        string currentPath,
        string currentIdentity,
        CancellationToken cancellationToken);
    Task<FileUndoEntry?> GetPendingUndoAsync(string undoId, CancellationToken cancellationToken);
    Task MarkUndoneAsync(string undoId, DateTimeOffset undoneAtUtc, CancellationToken cancellationToken);
}

internal interface IFileOperationService
{
    FilePathTarget? ResolveExisting(string path, bool? requireDirectory = null);
    IReadOnlyList<FileSearchMatch> Search(FilePathTarget root, string pattern, int maximumResults, CancellationToken cancellationToken, out bool truncated);
    Task OpenAsync(FilePathTarget target, CancellationToken cancellationToken);
    Task CopyAsync(FilePathTarget source, string destinationPath, CancellationToken cancellationToken);
    Task MoveAsync(FilePathTarget source, string destinationPath, CancellationToken cancellationToken);
    Task RecycleAsync(FilePathTarget target, CancellationToken cancellationToken);
    Task UndoAsync(FileUndoEntry entry, CancellationToken cancellationToken);
}

internal sealed class SafeFileOperationService : IFileOperationService
{
    private const int MaximumSearchDepth = 8;

    public FilePathTarget? ResolveExisting(string path, bool? requireDirectory = null)
    {
        var fullPath = NormalizeAbsolutePath(path);
        if (fullPath is null) return null;
        var isDirectory = Directory.Exists(fullPath);
        if (!isDirectory && !File.Exists(fullPath)) return null;
        if (requireDirectory is not null && requireDirectory.Value != isDirectory) return null;
        var canonicalPath = ResolveReparseSegments(fullPath);
        isDirectory = Directory.Exists(canonicalPath);
        if (!isDirectory && !File.Exists(canonicalPath)) return null;
        if (requireDirectory is not null && requireDirectory.Value != isDirectory) return null;
        var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(canonicalPath) : new FileInfo(canonicalPath);
        return new FilePathTarget(
            canonicalPath,
            info.Name.Length == 0 ? info.FullName : info.Name,
            isDirectory,
            BuildIdentity(info));
    }

    public IReadOnlyList<FileSearchMatch> Search(
        FilePathTarget root,
        string pattern,
        int maximumResults,
        CancellationToken cancellationToken,
        out bool truncated)
    {
        if (!root.IsDirectory || pattern.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("검색 위치 또는 패턴이 올바르지 않습니다.");
        var results = new List<FileSearchMatch>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root.CanonicalPath, 0));
        truncated = false;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFileSystemEntries(current.Path, pattern, System.IO.SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var match in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveExisting(match);
                if (target is null) continue;
                if (results.Count == maximumResults)
                {
                    truncated = true;
                    return results;
                }
                results.Add(new FileSearchMatch(target.CanonicalPath, target.DisplayName, target.IsDirectory));
            }
            if (current.Depth >= MaximumSearchDepth) continue;
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(current.Path))
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Enqueue((info.FullName, current.Depth + 1));
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // 검색 가능한 하위 폴더만 계속 처리한다.
            }
        }
        return results;
    }

    public Task OpenAsync(FilePathTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Process.Start(new ProcessStartInfo(target.CanonicalPath) { UseShellExecute = true })
            ?? throw new InvalidOperationException("Windows가 항목을 열지 못했습니다.");
        return Task.CompletedTask;
    }

    public Task CopyAsync(FilePathTarget source, string destinationPath, CancellationToken cancellationToken)
        => CopyFileAsync(source, destinationPath, cancellationToken);

    private static async Task CopyFileAsync(
        FilePathTarget source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source.IsDirectory) throw new InvalidOperationException("현재는 파일 복사만 지원합니다.");
        var createdDestination = false;
        try
        {
            await using var input = new FileStream(
                source.CanonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(
                destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            createdDestination = true;
            await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (createdDestination)
            {
                try { File.Delete(destinationPath); } catch (Exception) { }
            }
            throw;
        }
    }

    public Task MoveAsync(FilePathTarget source, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source.IsDirectory) throw new InvalidOperationException("현재는 파일 이동만 지원합니다.");
        File.Move(source.CanonicalPath, destinationPath, overwrite: false);
        return Task.CompletedTask;
    }

    public Task RecycleAsync(FilePathTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.IsDirectory)
            FileSystem.DeleteDirectory(target.CanonicalPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else
            FileSystem.DeleteFile(target.CanonicalPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        return Task.CompletedTask;
    }

    public Task UndoAsync(FileUndoEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = ResolveExisting(entry.CurrentPath)
            ?? throw new FileNotFoundException("되돌릴 현재 파일을 찾을 수 없습니다.");
        if (!StringComparer.Ordinal.Equals(current.Identity, entry.CurrentIdentity))
            throw new IOException("작업 후 파일이 변경되어 안전하게 되돌릴 수 없습니다.");
        if (entry.Kind is "copy" or "create")
        {
            if (current.IsDirectory) Directory.Delete(current.CanonicalPath, recursive: false);
            else File.Delete(current.CanonicalPath);
            return Task.CompletedTask;
        }
        if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
            throw new IOException("원래 위치에 같은 이름의 항목이 있어 되돌릴 수 없습니다.");
        File.Move(current.CanonicalPath, entry.OriginalPath, overwrite: false);
        return Task.CompletedTask;
    }

    internal static string? NormalizeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        var trimmed = path.Trim();
        if (trimmed.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("\\\\?\\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string ResolveReparseSegments(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? throw new IOException("경로 루트를 확인할 수 없습니다.");
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate) ? new FileInfo(candidate) : null;
            if (info is null)
            {
                current = candidate;
                continue;
            }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("연결 대상 경로를 확인할 수 없습니다.");
            else
                current = info.FullName;
        }
        return Path.GetFullPath(current);
    }

    private static string BuildIdentity(FileSystemInfo info)
    {
        info.Refresh();
        if (info is DirectoryInfo)
            return $"{info.FullName}\n{(long)info.Attributes}";
        var length = info is FileInfo file ? file.Length : 0;
        return $"{info.FullName}\n{(long)info.Attributes}\n{info.LastWriteTimeUtc.Ticks}\n{length}";
    }
}

internal static class FileMutationSafety
{
    public static bool IsProtected(string path)
    {
        var fullPath = SafeFileOperationService.NormalizeAbsolutePath(path);
        if (fullPath is null || fullPath.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        var root = Path.GetPathRoot(fullPath);
        if (root is null || StringComparer.OrdinalIgnoreCase.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(root)))
            return true;
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        return protectedRoots
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Any(candidate => IsWithin(fullPath, candidate));
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
