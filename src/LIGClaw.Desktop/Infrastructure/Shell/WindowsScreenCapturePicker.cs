using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Interop;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal enum ScreenCapturePickerStatus
{
    Captured,
    Cancelled,
    NotSupported,
    Failed,
}

internal sealed record ScreenCapturePickerResult(
    ScreenCapturePickerStatus Status,
    byte[]? EncodedImage = null,
    int WidthPixels = 0,
    int HeightPixels = 0,
    string? Error = null)
{
    public void ClearImage()
    {
        if (EncodedImage is { Length: > 0 }) CryptographicOperations.ZeroMemory(EncodedImage);
    }
}

internal static class ScreenCapturePolicy
{
    internal static string? ValidateDimensions(int widthPixels, int heightPixels)
    {
        if (widthPixels is < 1 or > PreparedSensitiveContextStore.MaximumDimensionPixels ||
            heightPixels is < 1 or > PreparedSensitiveContextStore.MaximumDimensionPixels ||
            (long)widthPixels * heightPixels > PreparedSensitiveContextStore.MaximumPixelCount)
            return "선택한 화면이 안전하게 처리할 수 있는 크기를 넘었어요.";
        return null;
    }
}

internal interface IScreenCapturePicker
{
    Task<ScreenCapturePickerResult> PickSingleFrameAsync(
        System.Windows.Window owner,
        CancellationToken cancellationToken = default);
}

internal sealed class WindowsScreenCapturePicker : IScreenCapturePicker
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    public async Task<ScreenCapturePickerResult> PickSingleFrameAsync(
        System.Windows.Window owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) || !GraphicsCaptureSession.IsSupported())
            return new(ScreenCapturePickerStatus.NotSupported, Error: "이 PC에서는 Windows 화면 선택 기능을 사용할 수 없어요.");

        try
        {
            var picker = new GraphicsCapturePicker();
            var ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHandle);
            var item = await picker.PickSingleItemAsync();
            if (item is null) return new(ScreenCapturePickerStatus.Cancelled);

            var dimensionError = ScreenCapturePolicy.ValidateDimensions(item.Size.Width, item.Size.Height);
            if (dimensionError is not null)
                return new(ScreenCapturePickerStatus.Failed, Error: dimensionError);

            return await CaptureFrameAsync(item, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (COMException exception) when (exception.HResult is unchecked((int)0x800704C7))
        {
            return new(ScreenCapturePickerStatus.Cancelled);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or NotSupportedException)
        {
            return new(ScreenCapturePickerStatus.Failed, Error: "선택한 화면을 가져오지 못했어요. 잠시 후 다시 시도해 주세요.");
        }
    }

    private static async Task<ScreenCapturePickerResult> CaptureFrameAsync(
        GraphicsCaptureItem item,
        CancellationToken cancellationToken)
    {
        using var device = Direct3DDevice.Create();
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device.Value,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            item.Size);
        using var session = framePool.CreateCaptureSession(item);
        var frameSource = new TaskCompletionSource<Direct3D11CaptureFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            if (!frameSource.TrySetResult(frame)) frame.Dispose();
        }

        framePool.FrameArrived += OnFrameArrived;
        try
        {
            session.StartCapture();
            using var timeout = new CancellationTokenSource(FrameTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            Direct3D11CaptureFrame frame;
            try
            {
                frame = await frameSource.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new(ScreenCapturePickerStatus.Failed, Error: "화면 응답을 기다리는 시간이 초과됐어요.");
            }

            using (frame)
            {
                var size = frame.ContentSize;
                var dimensionError = ScreenCapturePolicy.ValidateDimensions(size.Width, size.Height);
                if (dimensionError is not null)
                    return new(ScreenCapturePickerStatus.Failed, Error: dimensionError);

                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                    frame.Surface,
                    BitmapAlphaMode.Premultiplied);
                var image = await EncodePngAsync(bitmap, cancellationToken);
                if (image is null)
                    return new(ScreenCapturePickerStatus.Failed, Error: "화면 이미지가 안전한 크기 제한을 넘었어요.");
                return new(ScreenCapturePickerStatus.Captured, image, size.Width, size.Height);
            }
        }
        finally
        {
            framePool.FrameArrived -= OnFrameArrived;
        }
    }

    private static async Task<byte[]?> EncodePngAsync(
        SoftwareBitmap bitmap,
        CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Size is 0 or > PreparedSensitiveContextStore.MaximumEncodedImageBytes) return null;

        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var length = checked((uint)stream.Size);
        _ = await reader.LoadAsync(length);
        var bytes = new byte[length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private sealed class Direct3DDevice : IDisposable
    {
        private IDirect3DDevice? _value;

        private Direct3DDevice(IDirect3DDevice value) => _value = value;

        public IDirect3DDevice Value => _value ?? throw new ObjectDisposedException(nameof(Direct3DDevice));

        public static Direct3DDevice Create()
        {
            var result = NativeMethods.D3D11CreateDevice(
                IntPtr.Zero,
                D3DDriverType.Hardware,
                IntPtr.Zero,
                D3D11CreateDeviceFlags.BgraSupport,
                IntPtr.Zero,
                0,
                7,
                out var d3dDevice,
                out _,
                out var immediateContext);
            if (result < 0)
            {
                result = NativeMethods.D3D11CreateDevice(
                    IntPtr.Zero,
                    D3DDriverType.Warp,
                    IntPtr.Zero,
                    D3D11CreateDeviceFlags.BgraSupport,
                    IntPtr.Zero,
                    0,
                    7,
                    out d3dDevice,
                    out _,
                    out immediateContext);
            }

            Marshal.ThrowExceptionForHR(result);
            try
            {
                var iid = NativeMethods.IidIdxgiDevice;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in iid, out var dxgiDevice));
                try
                {
                    Marshal.ThrowExceptionForHR(
                        NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));
                    try
                    {
                        return new Direct3DDevice(MarshalInterface<IDirect3DDevice>.FromAbi(inspectable));
                    }
                    finally
                    {
                        _ = Marshal.Release(inspectable);
                    }
                }
                finally
                {
                    _ = Marshal.Release(dxgiDevice);
                }
            }
            finally
            {
                if (immediateContext != IntPtr.Zero) _ = Marshal.Release(immediateContext);
                if (d3dDevice != IntPtr.Zero) _ = Marshal.Release(d3dDevice);
            }
        }

        public void Dispose()
        {
            if (_value is null) return;
            if (_value is IDisposable disposable) disposable.Dispose();
            _value = null;
        }
    }

    private enum D3DDriverType : uint
    {
        Hardware = 1,
        Warp = 5,
    }

    [Flags]
    private enum D3D11CreateDeviceFlags : uint
    {
        BgraSupport = 0x20,
    }

    private static class NativeMethods
    {
        internal static readonly Guid IidIdxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

        [DllImport("d3d11.dll")]
        internal static extern int D3D11CreateDevice(
            IntPtr adapter,
            D3DDriverType driverType,
            IntPtr software,
            D3D11CreateDeviceFlags flags,
            IntPtr featureLevels,
            uint featureLevelCount,
            uint sdkVersion,
            out IntPtr device,
            out uint featureLevel,
            out IntPtr immediateContext);

        [DllImport("d3d11.dll")]
        internal static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice,
            out IntPtr graphicsDevice);
    }
}
