namespace LIGClaw.Desktop.Infrastructure.Shell;

internal readonly record struct LatestRequest(
    long Version,
    CancellationToken CancellationToken,
    LatestRequestController Owner)
{
    public bool IsCurrent => Owner.IsCurrent(Version);
}

internal sealed class LatestRequestController : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private long _version;
    private bool _disposed;

    public LatestRequest Begin(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeCancellation?.Cancel();
            _activeCancellation?.Dispose();
            _activeCancellation = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
            var version = checked(++_version);
            return new LatestRequest(version, _activeCancellation.Token, this);
        }
    }

    public bool IsCurrent(long version)
    {
        lock (_sync)
        {
            return !_disposed && version == _version;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _activeCancellation?.Cancel();
            _activeCancellation?.Dispose();
            _activeCancellation = null;
        }
    }
}
