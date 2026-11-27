namespace LIGClaw.Desktop;

internal sealed class SettingsOperationGuard : IDisposable
{
    private CancellationTokenSource? _activeOperation;

    public bool IsBusy => _activeOperation is not null;

    public bool TryBegin(out CancellationToken cancellationToken)
    {
        cancellationToken = default;
        if (_activeOperation is not null) return false;
        _activeOperation = new CancellationTokenSource();
        cancellationToken = _activeOperation.Token;
        return true;
    }

    public void Cancel() => _activeOperation?.Cancel();

    public void End()
    {
        _activeOperation?.Dispose();
        _activeOperation = null;
    }

    public void Dispose()
    {
        Cancel();
        End();
    }
}
