using System.Globalization;
using System.IO;
using System.Text;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record BoundedTextResult(string Text, bool Truncated);

internal interface IFileTextReader
{
    Task<BoundedTextResult> ReadAsync(string path, int maximumBytes, CancellationToken cancellationToken);
}

internal sealed class BoundedFileTextReader : IFileTextReader
{
    public async Task<BoundedTextResult> ReadAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[maximumBytes + 1];
        var count = 0;
        await using (var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
        }
        var truncated = count > maximumBytes;
        var contentLength = Math.Min(count, maximumBytes);
        var (encoding, preambleLength) = DetectEncoding(buffer.AsSpan(0, contentLength));
        if (preambleLength == 0 && buffer.AsSpan(0, contentLength).Contains((byte)0))
            throw new InvalidDataException("바이너리 파일은 텍스트로 읽지 않습니다.");
        try
        {
            var bytes = buffer.AsSpan(preambleLength, contentLength - preambleLength);
            var decoder = encoding.GetDecoder();
            var characters = new char[encoding.GetMaxCharCount(bytes.Length)];
            decoder.Convert(bytes, characters, flush: !truncated, out _, out var charactersUsed, out _);
            var text = new string(characters, 0, charactersUsed);
            if (text.Length > maximumBytes) text = text[..maximumBytes];
            return new BoundedTextResult(text, truncated);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("지원되는 UTF 텍스트 파일이 아닙니다.", exception);
        }
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false, true), 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(false, false, true), 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(true, false, true), 2);
        return (new UTF8Encoding(false, true), 0);
    }
}

internal sealed class FileGetMetadataTool(IFileOperationService files) : IWindowsToolAdapter
{
    public string Name => "file.get_metadata.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
    public int Priority => 0;

    public Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ToolInputReader.HasOnlyKeys(input, "path") ||
            !ToolInputReader.TryGetRequiredString(input, "path", 32_767, out var path))
            return Task.FromResult(Failure("메타데이터를 확인할 경로가 올바르지 않습니다."));
        var target = files.ResolveExisting(path);
        if (target is null) return Task.FromResult(Failure("메타데이터를 확인할 항목을 찾을 수 없습니다."));
        var info = target.IsDirectory
            ? (FileSystemInfo)new DirectoryInfo(target.CanonicalPath)
            : new FileInfo(target.CanonicalPath);
        info.Refresh();
        var size = info is FileInfo file ? file.Length : 0;
        return Task.FromResult(new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?>
            {
                ["path"] = target.CanonicalPath,
                ["name"] = target.DisplayName,
                ["isDirectory"] = target.IsDirectory,
                ["sizeBytes"] = size,
                ["lastWriteTimeUtc"] = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                ["readOnly"] = (info.Attributes & FileAttributes.ReadOnly) != 0,
            },
            ActivitySummary: $"{target.DisplayName}의 파일 메타데이터를 확인했어요."));
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}

internal sealed class FileReadTextTool(IFileOperationService files, IFileTextReader reader) : IWindowsToolAdapter
{
    private const int MaximumBytes = 128 * 1024;
    private readonly object _sync = new();
    private readonly Dictionary<string, FilePathTarget> _prepared = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "file.read_text.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        var target = files.ResolveExisting(parameters.Value.Path, requireDirectory: false);
        if (target is null) return null;
        lock (_sync) _prepared[parameters.Value.Path] = target;
        return new WindowsToolApprovalPrompt(
            "파일의 텍스트를 읽을까요?",
            $"{target.DisplayName}의 텍스트를 모델에 전달합니다.",
            $"경로: {target.CanonicalPath}\n요청 이유: {parameters.Value.Reason}\n\n최대 128 KiB만 읽고 바이너리 파일은 거부합니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("파일 경로 또는 요청 이유가 올바르지 않습니다.");
        FilePathTarget? prepared;
        lock (_sync) _prepared.Remove(parameters.Value.Path, out prepared);
        var current = files.ResolveExisting(parameters.Value.Path, requireDirectory: false);
        if (prepared is null || current is null || !StringComparer.Ordinal.Equals(prepared.Identity, current.Identity))
            return Failure("승인 후 파일이 변경되어 내용을 읽지 않았어요.");
        BoundedTextResult content;
        try
        {
            content = await reader.ReadAsync(current.CanonicalPath, MaximumBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return Failure("지원되는 UTF 텍스트 파일이 아니거나 바이너리 파일이에요.");
        }
        var after = files.ResolveExisting(parameters.Value.Path, requireDirectory: false);
        if (after is null || !StringComparer.Ordinal.Equals(current.Identity, after.Identity))
            return Failure("읽는 동안 파일이 변경되어 내용을 전달하지 않았어요.");
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?>
            {
                ["text"] = content.Text,
                ["length"] = content.Text.Length,
                ["truncated"] = content.Truncated,
            },
            ActivitySummary: $"{current.DisplayName}의 텍스트를 제한된 범위에서 읽었어요.");
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
