using System.IO;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal enum TextToSpeechStatus
{
    Playing,
    Empty,
    TooLong,
    VoiceUnavailable,
    Failed,
}

internal sealed record TextToSpeechResult(TextToSpeechStatus Status, string? Error = null);
internal sealed record TextToSpeechPlaybackEndedEventArgs(bool Failed);

internal interface ITextToSpeechPlayer : IAsyncDisposable
{
    event EventHandler<TextToSpeechPlaybackEndedEventArgs>? PlaybackEnded;
    Task<TextToSpeechResult> PlayAsync(string? text, CancellationToken cancellationToken = default);
    void Stop();
}

internal static class TextToSpeechTextPolicy
{
    internal const int MaximumCharacters = 20_000;

    internal static TextToSpeechResult Prepare(string? text, out string prepared)
    {
        prepared = string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (prepared.Length == 0)
            return new(TextToSpeechStatus.Empty, "읽을 글자를 먼저 선택해 주세요.");
        if (prepared.Length > MaximumCharacters)
        {
            prepared = string.Empty;
            return new(TextToSpeechStatus.TooLong, "선택한 글이 너무 길어요. 나누어 선택해 주세요.");
        }
        return new(TextToSpeechStatus.Playing);
    }
}

internal sealed class WindowsTextToSpeechPlayer : ITextToSpeechPlayer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private MediaPlayer? _player;
    private SpeechSynthesisStream? _stream;
    private long _generation;
    private bool _disposed;

    public event EventHandler<TextToSpeechPlaybackEndedEventArgs>? PlaybackEnded;

    public async Task<TextToSpeechResult> PlayAsync(
        string? text,
        CancellationToken cancellationToken = default)
    {
        var validation = TextToSpeechTextPolicy.Prepare(text, out var prepared);
        if (validation.Status != TextToSpeechStatus.Playing) return validation;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long generation;
            lock (_sync)
            {
                StopUnsafe();
                generation = ++_generation;
            }
            SpeechSynthesisStream? stream = null;
            try
            {
                using var synthesizer = new SpeechSynthesizer();
                stream = await synthesizer.SynthesizeTextToStreamAsync(prepared);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (generation != _generation)
                        throw new OperationCanceledException(cancellationToken);
                    _stream = stream;
                    stream = null;
                    var source = MediaSource.CreateFromStream(_stream, _stream.ContentType);
                    _player = new MediaPlayer { Source = source };
                    _player.MediaEnded += Player_MediaEnded;
                    _player.MediaFailed += Player_MediaFailed;
                    _player.Play();
                }
                return new(TextToSpeechStatus.Playing);
            }
            catch (FileNotFoundException)
            {
                lock (_sync) StopUnsafe();
                return new(TextToSpeechStatus.VoiceUnavailable, "Windows 음성 패키지를 찾지 못했어요.");
            }
            catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
            {
                lock (_sync) StopUnsafe();
                return new(TextToSpeechStatus.Failed, "Windows에서 선택한 글을 읽지 못했어요.");
            }
            finally
            {
                stream?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        if (_disposed) return;
        lock (_sync)
        {
            _generation++;
            StopUnsafe();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sync)
        {
            _generation++;
            StopUnsafe();
        }
        await _gate.WaitAsync();
        _gate.Release();
        _gate.Dispose();
    }

    private void Player_MediaEnded(MediaPlayer sender, object args)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _player)) return;
            StopUnsafe();
        }
        PlaybackEnded?.Invoke(this, new TextToSpeechPlaybackEndedEventArgs(Failed: false));
    }

    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _player)) return;
            StopUnsafe();
        }
        PlaybackEnded?.Invoke(this, new TextToSpeechPlaybackEndedEventArgs(Failed: true));
    }

    private void StopUnsafe()
    {
        if (_player is not null)
        {
            _player.MediaEnded -= Player_MediaEnded;
            _player.MediaFailed -= Player_MediaFailed;
            _player.Pause();
            _player.Source = null;
            _player.Dispose();
            _player = null;
        }
        _stream?.Dispose();
        _stream = null;
    }
}
