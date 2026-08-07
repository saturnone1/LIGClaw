using System.Runtime.InteropServices;
using System.Text;
using Windows.Media.SpeechRecognition;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal enum PushToTalkStatus
{
    Listening,
    Recognized,
    NoSpeech,
    PackageIdentityRequired,
    MicrophoneUnavailable,
    PermissionDenied,
    LanguageUnavailable,
    NetworkUnavailable,
    Failed,
}

internal sealed record PushToTalkResult(
    PushToTalkStatus Status,
    string? Text = null,
    bool WasTruncated = false,
    string? Error = null);

internal interface IPushToTalkRecognizer : IAsyncDisposable
{
    Task<PushToTalkResult> StartAsync(CancellationToken cancellationToken = default);
    Task<PushToTalkResult> StopAsync(CancellationToken cancellationToken = default);
    Task CancelAsync();
}

internal static class VoiceInputTextPolicy
{
    internal const int MaximumCharacters = 16_000;

    internal static bool AppendBounded(StringBuilder target, string? segment)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(segment)) return false;
        if (target.Length >= MaximumCharacters) return true;
        var normalized = string.Join(' ', segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0) return false;
        var separatorLength = target.Length == 0 ? 0 : 1;
        var available = MaximumCharacters - target.Length - separatorLength;
        if (available <= 0) return true;
        var end = Math.Min(normalized.Length, available);
        if (end < normalized.Length && end > 0 && char.IsHighSurrogate(normalized[end - 1])) end--;
        if (end == 0) return true;
        if (separatorLength > 0) target.Append(' ');
        target.Append(normalized.AsSpan(0, end));
        return end < normalized.Length;
    }
}

internal sealed class WindowsPushToTalkRecognizer : IPushToTalkRecognizer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _textSync = new();
    private StringBuilder _recognizedText = new();
    private SpeechRecognizer? _recognizer;
    private SpeechRecognitionResultStatus? _completionStatus;
    private bool _wasTruncated;
    private bool _disposed;

    public async Task<PushToTalkResult> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recognizer is not null)
                return new(PushToTalkStatus.Failed, Error: "이미 음성을 듣고 있어요.");
            if (!WindowsPackageIdentity.IsAvailable())
                return new(
                    PushToTalkStatus.PackageIdentityRequired,
                    Error: "설치된 LIGClaw에서만 Windows 음성 입력을 사용할 수 있어요.");

            SpeechRecognizer? recognizer = null;
            try
            {
                recognizer = new SpeechRecognizer();
                recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(
                    SpeechRecognitionScenario.Dictation,
                    "LIGClaw request"));
                var compilation = await recognizer.CompileConstraintsAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (compilation.Status != SpeechRecognitionResultStatus.Success)
                    return ForStatus(compilation.Status);

                recognizer.ContinuousRecognitionSession.ResultGenerated += Session_ResultGenerated;
                recognizer.ContinuousRecognitionSession.Completed += Session_Completed;
                lock (_textSync)
                {
                    _recognizedText.Clear();
                    _completionStatus = null;
                    _wasTruncated = false;
                }
                await recognizer.ContinuousRecognitionSession.StartAsync(
                    SpeechContinuousRecognitionMode.Default);
                cancellationToken.ThrowIfCancellationRequested();
                _recognizer = recognizer;
                recognizer = null;
                return new(PushToTalkStatus.Listening);
            }
            catch (UnauthorizedAccessException)
            {
                return new(
                    PushToTalkStatus.PermissionDenied,
                    Error: "Windows 설정에서 LIGClaw의 마이크 사용을 허용해 주세요.");
            }
            catch (COMException exception)
            {
                return ForHResult(exception.HResult);
            }
            catch (InvalidOperationException)
            {
                return new(PushToTalkStatus.Failed, Error: "Windows 음성 입력을 시작하지 못했어요.");
            }
            finally
            {
                if (recognizer is not null) CloseRecognizer(recognizer);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PushToTalkResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recognizer is null)
                return new(PushToTalkStatus.NoSpeech, Error: "듣고 있는 음성이 없어요.");
            var recognizer = _recognizer;
            try
            {
                await recognizer.ContinuousRecognitionSession.StopAsync();
                cancellationToken.ThrowIfCancellationRequested();
                lock (_textSync)
                {
                    return SnapshotResult();
                }
            }
            catch (UnauthorizedAccessException)
            {
                return new(
                    PushToTalkStatus.PermissionDenied,
                    Error: "Windows 설정에서 LIGClaw의 마이크 사용을 허용해 주세요.");
            }
            catch (COMException exception)
            {
                return ForHResult(exception.HResult);
            }
            catch (InvalidOperationException)
            {
                lock (_textSync) return SnapshotResult();
            }
            finally
            {
                ClearRecognizer();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_recognizer is null) return;
            try
            {
                await _recognizer.ContinuousRecognitionSession.CancelAsync();
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
            }
            finally
            {
                ClearRecognizer();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await CancelAsync();
        _disposed = true;
        _gate.Dispose();
    }

    private void Session_ResultGenerated(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        if (args.Result.Status != SpeechRecognitionResultStatus.Success ||
            args.Result.Confidence == SpeechRecognitionConfidence.Rejected) return;
        lock (_textSync)
        {
            _wasTruncated |= VoiceInputTextPolicy.AppendBounded(_recognizedText, args.Result.Text);
        }
    }

    private void Session_Completed(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionCompletedEventArgs args)
    {
        lock (_textSync) _completionStatus = args.Status;
    }

    private void ClearRecognizer()
    {
        if (_recognizer is not { } recognizer) return;
        _recognizer = null;
        recognizer.ContinuousRecognitionSession.ResultGenerated -= Session_ResultGenerated;
        recognizer.ContinuousRecognitionSession.Completed -= Session_Completed;
        CloseRecognizer(recognizer);
    }

    private PushToTalkResult SnapshotResult()
    {
        if (_recognizedText.Length > 0)
        {
            var text = _recognizedText.ToString();
            _recognizedText = new StringBuilder();
            return new(PushToTalkStatus.Recognized, text, _wasTruncated);
        }
        return _completionStatus is { } status && status != SpeechRecognitionResultStatus.Success
            ? ForStatus(status)
            : new(PushToTalkStatus.NoSpeech, Error: "인식된 음성이 없어요. 버튼을 누른 채 다시 말해 주세요.");
    }

    private static void CloseRecognizer(SpeechRecognizer recognizer)
    {
        if (recognizer is IDisposable disposable) disposable.Dispose();
    }

    private static PushToTalkResult ForHResult(int hResult) => hResult switch
    {
        unchecked((int)0x80070005) => new(
            PushToTalkStatus.PermissionDenied,
            Error: "Windows 설정에서 LIGClaw의 마이크 사용을 허용해 주세요."),
        unchecked((int)0x80070490) => new(
            PushToTalkStatus.MicrophoneUnavailable,
            Error: "사용할 수 있는 마이크를 찾지 못했어요."),
        _ => new(PushToTalkStatus.Failed, Error: "Windows 음성 입력을 시작하지 못했어요."),
    };

    private static PushToTalkResult ForStatus(SpeechRecognitionResultStatus status) => status switch
    {
        SpeechRecognitionResultStatus.MicrophoneUnavailable => new(
            PushToTalkStatus.MicrophoneUnavailable,
            Error: "사용할 수 있는 마이크를 찾지 못했어요."),
        SpeechRecognitionResultStatus.TopicLanguageNotSupported or
        SpeechRecognitionResultStatus.GrammarLanguageMismatch => new(
            PushToTalkStatus.LanguageUnavailable,
            Error: "Windows에 현재 언어의 음성 인식 기능을 추가해 주세요."),
        SpeechRecognitionResultStatus.NetworkFailure => new(
            PushToTalkStatus.NetworkUnavailable,
            Error: "Windows 온라인 음성 인식 상태와 네트워크를 확인해 주세요."),
        SpeechRecognitionResultStatus.UserCanceled => new(
            PushToTalkStatus.NoSpeech,
            Error: "음성 입력을 취소했어요."),
        SpeechRecognitionResultStatus.TimeoutExceeded or
        SpeechRecognitionResultStatus.PauseLimitExceeded => new(
            PushToTalkStatus.NoSpeech,
            Error: "인식된 음성이 없어요. 버튼을 누른 채 다시 말해 주세요."),
        _ => new(PushToTalkStatus.Failed, Error: "Windows 음성 인식을 준비하지 못했어요."),
    };
}
