using System.Security.Cryptography;
using System.Text;
using ThreadingTimer = System.Threading.Timer;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record PreparedSensitiveContextIdentity(
    string ConversationId,
    string RunId,
    string ToolCallId);

internal sealed record PreparedSensitiveContextDraft(
    string TargetKind,
    int WidthPixels,
    int HeightPixels,
    byte[] EncodedImage,
    byte[] OcrTextUtf8,
    int RedactionCount);

internal sealed record PreparedSensitiveContextPrepareResult(
    bool Success,
    string? Token = null,
    string? Error = null);

internal sealed record PreparedSensitiveContextTakeResult(
    bool Success,
    PreparedSensitiveContext? Context = null,
    string? Error = null);

internal sealed class PreparedSensitiveContext : IDisposable
{
    private byte[] _encodedImage;
    private byte[] _ocrTextUtf8;
    private bool _disposed;

    internal PreparedSensitiveContext(
        string token,
        PreparedSensitiveContextIdentity identity,
        PreparedSensitiveContextDraft draft,
        DateTimeOffset expiresAt)
    {
        Token = token;
        Identity = identity;
        TargetKind = draft.TargetKind;
        WidthPixels = draft.WidthPixels;
        HeightPixels = draft.HeightPixels;
        RedactionCount = draft.RedactionCount;
        ExpiresAt = expiresAt;
        _encodedImage = draft.EncodedImage;
        _ocrTextUtf8 = draft.OcrTextUtf8;
    }

    public string Token { get; }
    public PreparedSensitiveContextIdentity Identity { get; }
    public string TargetKind { get; }
    public int WidthPixels { get; }
    public int HeightPixels { get; }
    public int RedactionCount { get; }
    public DateTimeOffset ExpiresAt { get; }
    public ReadOnlyMemory<byte> EncodedImage => Read(_encodedImage);
    public ReadOnlyMemory<byte> OcrTextUtf8 => Read(_ocrTextUtf8);

    ~PreparedSensitiveContext()
    {
        DisposeBuffers();
    }

    public void Dispose()
    {
        if (_disposed) return;
        DisposeBuffers();
        GC.SuppressFinalize(this);
    }

    private ReadOnlyMemory<byte> Read(byte[] buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return buffer;
    }

    private void DisposeBuffers()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_encodedImage);
        CryptographicOperations.ZeroMemory(_ocrTextUtf8);
        _encodedImage = [];
        _ocrTextUtf8 = [];
    }
}

internal sealed class PreparedSensitiveContextStore : IDisposable
{
    internal const int MaximumDimensionPixels = 4_096;
    internal const int MaximumPixelCount = 16_000_000;
    internal const int MaximumEncodedImageBytes = 8 * 1024 * 1024;
    internal const int MaximumOcrTextBytes = 32 * 1024;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<Guid> _createToken;
    private readonly ThreadingTimer _expiryTimer;
    private PreparedSensitiveContext? _current;
    private bool _disposed;

    public PreparedSensitiveContextStore(
        Func<DateTimeOffset>? utcNow = null,
        Func<Guid>? createToken = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _createToken = createToken ?? Guid.NewGuid;
        _expiryTimer = new ThreadingTimer(
            _ => ExpireFromTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public PreparedSensitiveContextPrepareResult Prepare(
        PreparedSensitiveContextIdentity identity,
        PreparedSensitiveContextDraft draft)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(draft);
        var error = Validate(identity, draft);
        if (error is not null)
        {
            Clear(draft.EncodedImage);
            Clear(draft.OcrTextUtf8);
            return new PreparedSensitiveContextPrepareResult(false, Error: error);
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DisposeCurrent();
            var token = _createToken().ToString("N");
            _current = new PreparedSensitiveContext(token, identity, draft, _utcNow().Add(Lifetime));
            _ = _expiryTimer.Change(Lifetime, Timeout.InfiniteTimeSpan);
            return new PreparedSensitiveContextPrepareResult(true, token);
        }
    }

    public PreparedSensitiveContextTakeResult Take(
        PreparedSensitiveContextIdentity identity,
        string token)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ExpireCurrent())
                return new PreparedSensitiveContextTakeResult(false, Error: "준비한 화면 내용이 만료됐어요.");
            if (_current is null || !Matches(_current, identity, token))
                return new PreparedSensitiveContextTakeResult(false, Error: "준비한 화면 내용을 찾을 수 없어요.");
            var context = _current;
            _current = null;
            return new PreparedSensitiveContextTakeResult(true, context);
        }
    }

    public bool Discard(PreparedSensitiveContextIdentity identity, string token)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ = ExpireCurrent();
            if (_current is null || !Matches(_current, identity, token)) return false;
            DisposeCurrent();
            return true;
        }
    }

    public bool DiscardExpired()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ExpireCurrent();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeCurrent();
            _expiryTimer.Dispose();
        }
    }

    private bool ExpireCurrent()
    {
        if (_current is null || _utcNow() < _current.ExpiresAt) return false;
        DisposeCurrent();
        return true;
    }

    private void DisposeCurrent()
    {
        _ = _expiryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _current?.Dispose();
        _current = null;
    }

    private void ExpireFromTimer()
    {
        lock (_sync)
        {
            if (_disposed || _current is null) return;
            var remaining = _current.ExpiresAt - _utcNow();
            if (remaining > TimeSpan.Zero)
            {
                _ = _expiryTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            DisposeCurrent();
        }
    }

    private static bool Matches(
        PreparedSensitiveContext context,
        PreparedSensitiveContextIdentity identity,
        string token) =>
        !string.IsNullOrEmpty(token) &&
        StringComparer.Ordinal.Equals(context.Token, token) &&
        context.Identity == identity;

    private static string? Validate(
        PreparedSensitiveContextIdentity identity,
        PreparedSensitiveContextDraft draft)
    {
        if (!ValidId(identity.ConversationId) || !ValidId(identity.RunId) || !ValidId(identity.ToolCallId))
            return "화면 내용의 실행 식별자가 올바르지 않아요.";
        if (draft.TargetKind is not ("window" or "display" or "region" or "selection"))
            return "화면 캡처 대상 종류가 올바르지 않아요.";
        if (draft.WidthPixels is < 1 or > MaximumDimensionPixels ||
            draft.HeightPixels is < 1 or > MaximumDimensionPixels ||
            (long)draft.WidthPixels * draft.HeightPixels > MaximumPixelCount)
            return "화면 캡처 크기가 안전 한도를 넘었어요.";
        if (draft.EncodedImage.Length is < 1 or > MaximumEncodedImageBytes)
            return "화면 이미지 크기가 안전 한도를 넘었어요.";
        if (draft.OcrTextUtf8.Length > MaximumOcrTextBytes)
            return "화면 글자 인식 결과가 안전 한도를 넘었어요.";
        try
        {
            _ = StrictUtf8.GetCharCount(draft.OcrTextUtf8);
        }
        catch (DecoderFallbackException)
        {
            return "화면 글자 인식 결과의 문자 형식이 올바르지 않아요.";
        }
        if (draft.RedactionCount is < 0 or > 10_000)
            return "화면 마스킹 개수가 올바르지 않아요.";
        return null;
    }

    private static bool ValidId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);

    private static void Clear(byte[] buffer)
    {
        if (buffer.Length > 0) CryptographicOperations.ZeroMemory(buffer);
    }
}
