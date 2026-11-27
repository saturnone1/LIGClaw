using System.IO;
using System.IO.Pipes;
using System.Text;

namespace LIGClaw.Desktop.Infrastructure.Shell;

public sealed class ActivationRequestedEventArgs(string? payload) : EventArgs
{
    public string? Payload { get; } = payload;
}

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const int MaximumPayloadBytes = 32 * 1024;
    private readonly string _pipeName;
    private readonly Mutex? _mutex;
    private readonly CancellationTokenSource? _serverCancellation;
    private readonly Task? _serverTask;
    private int _disposed;

    public SingleInstanceCoordinator(string instanceId = "LIGClaw")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var safeId = string.Concat(instanceId.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        _pipeName = $"{safeId}.Activate";
        var mutex = new Mutex(true, $@"Local\{safeId}.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return;
        }

        _mutex = mutex;
        IsPrimary = true;
        _serverCancellation = new CancellationTokenSource();
        _serverTask = RunServerAsync(_serverCancellation.Token);
    }

    public bool IsPrimary { get; }
    public event EventHandler<ActivationRequestedEventArgs>? ActivationRequested;

    public void SignalPrimary(string? payload = null)
    {
        if (IsPrimary) return;
        var bytes = payload is null ? [] : Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > MaximumPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payload));
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
        pipe.Connect(2_000);
        pipe.Write(BitConverter.GetBytes(bytes.Length));
        pipe.Write(bytes);
        pipe.Flush();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _serverCancellation?.Cancel();
        try { _serverTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException exception) when (exception.InnerExceptions.All(item => item is OperationCanceledException)) { }
        _serverCancellation?.Dispose();
        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex.Dispose();
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var header = new byte[sizeof(int)];
                if (!await ReadExactlyAsync(pipe, header, cancellationToken).ConfigureAwait(false)) continue;
                var length = BitConverter.ToInt32(header);
                if (length is < 0 or > MaximumPayloadBytes) continue;
                var bytes = new byte[length];
                if (!await ReadExactlyAsync(pipe, bytes, cancellationToken).ConfigureAwait(false)) continue;
                var payload = length == 0 ? null : Encoding.UTF8.GetString(bytes);
                if (Volatile.Read(ref _disposed) == 0)
                    ActivationRequested?.Invoke(this, new ActivationRequestedEventArgs(payload));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }
}
