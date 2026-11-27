namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed class PushToTalkInteractionController(IPushToTalkRecognizer recognizer)
{
    private readonly object _sync = new();
    private Task<PushToTalkResult>? _startTask;
    private bool _active;
    private bool _ending;

    internal bool IsActive
    {
        get { lock (_sync) return _active; }
    }

    internal bool IsEnding
    {
        get { lock (_sync) return _ending; }
    }

    internal async Task<PushToTalkResult> PressAsync()
    {
        Task<PushToTalkResult> startTask;
        lock (_sync)
        {
            if (_active)
                return new(PushToTalkStatus.Failed, Error: "이미 음성을 듣고 있어요.");

            _active = true;
            _ending = false;
            _startTask = recognizer.StartAsync();
            startTask = _startTask;
        }

        PushToTalkResult result;
        try
        {
            result = await startTask;
        }
        catch
        {
            Reset();
            throw;
        }

        lock (_sync)
        {
            if (_ending)
                return new(PushToTalkStatus.NoSpeech);
            if (result.Status != PushToTalkStatus.Listening && !_ending) ResetUnsafe();
        }
        return result;
    }

    internal async Task<PushToTalkResult?> ReleaseAsync()
    {
        Task<PushToTalkResult>? startTask;
        lock (_sync)
        {
            if (!_active || _ending) return null;
            _ending = true;
            startTask = _startTask;
        }

        try
        {
            var start = startTask is null
                ? new PushToTalkResult(PushToTalkStatus.Failed, Error: "음성 입력을 시작하지 못했어요.")
                : await startTask;
            return start.Status == PushToTalkStatus.Listening
                ? await recognizer.StopAsync()
                : start;
        }
        finally
        {
            Reset();
        }
    }

    internal async Task CancelAsync()
    {
        Task<PushToTalkResult>? startTask;
        lock (_sync)
        {
            if (!_active) return;
            _ending = true;
            startTask = _startTask;
        }

        try
        {
            if (startTask is not null)
            {
                try
                {
                    await startTask;
                }
                catch
                {
                    return;
                }
            }
            await recognizer.CancelAsync();
        }
        finally
        {
            Reset();
        }
    }

    private void Reset()
    {
        lock (_sync) ResetUnsafe();
    }

    private void ResetUnsafe()
    {
        _active = false;
        _ending = false;
        _startTask = null;
    }
}
