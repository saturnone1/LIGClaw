namespace LIGClaw.Desktop.Infrastructure.Shell;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly EventWaitHandle _activationEvent;
    private readonly Mutex? _mutex;
    private readonly RegisteredWaitHandle? _activationWait;
    private int _disposed;

    public SingleInstanceCoordinator(string instanceId = "LIGClaw")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var safeId = string.Concat(instanceId.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        _activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            $@"Local\{safeId}.Activate");

        var mutex = new Mutex(true, $@"Local\{safeId}.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return;
        }

        _mutex = mutex;
        IsPrimary = true;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            },
            null,
            Timeout.Infinite,
            false);
    }

    public bool IsPrimary { get; }

    public event EventHandler? ActivationRequested;

    public void SignalPrimary()
    {
        if (!IsPrimary) _activationEvent.Set();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _activationWait?.Unregister(null);
        _activationEvent.Dispose();
        if (_mutex is null) return;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The process is already shutting down or no longer owns the mutex.
        }
        _mutex.Dispose();
    }
}
