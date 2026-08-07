using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record AudioOutputStatusSnapshot(string ProviderStatus, string DefaultOutputStatus);

internal sealed record DisplayStatusSnapshot(
    string ProviderStatus,
    int ActiveDisplayCount,
    bool Truncated,
    int? PrimaryWidthPixels = null,
    int? PrimaryHeightPixels = null);

internal sealed record PrinterStatusSnapshot(
    string ProviderStatus,
    int ObservedPrinterCount,
    string DefaultPrinterStatus,
    bool Truncated);

internal sealed record DeviceStatusSnapshot(
    AudioOutputStatusSnapshot AudioOutput,
    DisplayStatusSnapshot Displays,
    PrinterStatusSnapshot Printers);

internal interface IAudioOutputStatusReader
{
    AudioOutputStatusSnapshot Read();
}

internal interface IDisplayStatusReader
{
    DisplayStatusSnapshot Read();
}

internal interface IPrinterStatusReader
{
    PrinterStatusSnapshot Read();
}

internal interface IDeviceStatusReader
{
    DeviceStatusSnapshot Read();
}

internal sealed class WindowsAudioOutputStatusReader : IAudioOutputStatusReader
{
    private const int ErrorNotFound = unchecked((int)0x80070490);
    private const uint DeviceStateActive = 1;
    private const uint DeviceStateDisabled = 2;
    private const uint DeviceStateNotPresent = 4;
    private const uint DeviceStateUnplugged = 8;
    private static readonly Guid EnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public AudioOutputStatusSnapshot Read()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? endpoint = null;
        try
        {
            var type = Type.GetTypeFromCLSID(EnumeratorClassId, throwOnError: true)!;
            enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(type);
            if (enumerator is null) return Unavailable();
            var result = enumerator.GetDefaultAudioEndpoint(dataFlow: 0, role: 0, out endpoint);
            if (result == ErrorNotFound) return new AudioOutputStatusSnapshot("available", "not_present");
            if (result < 0 || endpoint is null) return Unavailable();
            return endpoint.GetState(out var state) < 0
                ? Unavailable()
                : new AudioOutputStatusSnapshot("available", ClassifyState(state));
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return Unavailable();
        }
        finally
        {
            Release(endpoint);
            Release(enumerator);
        }
    }

    internal static string ClassifyState(uint state) => state switch
    {
        DeviceStateActive => "active",
        DeviceStateDisabled => "disabled",
        DeviceStateNotPresent => "not_present",
        DeviceStateUnplugged => "unplugged",
        _ => "unknown",
    };

    private static AudioOutputStatusSnapshot Unavailable() => new("unavailable", "unknown");

    private static bool IsProviderFailure(Exception exception) => exception is
        COMException or InvalidCastException or InvalidOperationException or NotSupportedException or
        PlatformNotSupportedException or TargetInvocationException or TypeLoadException;

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.FinalReleaseComObject(value);
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint classContext, IntPtr activationParameters, out IntPtr interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(uint storageAccess, out IntPtr properties);

        [PreserveSig]
        int GetId(out IntPtr id);

        [PreserveSig]
        int GetState(out uint state);
    }
}

internal sealed class WindowsDisplayStatusReader : IDisplayStatusReader
{
    public DisplayStatusSnapshot Read()
    {
        try
        {
            return Summarize(Screen.AllScreens.Select(screen =>
                (screen.Primary, screen.Bounds.Width, screen.Bounds.Height)).ToArray());
        }
        catch (Exception exception) when (exception is
            TypeInitializationException or ExternalException or InvalidOperationException or
            NotSupportedException or PlatformNotSupportedException)
        {
            return new DisplayStatusSnapshot("unavailable", 0, false);
        }
    }

    internal static DisplayStatusSnapshot Summarize(
        IReadOnlyList<(bool Primary, int Width, int Height)> displays)
    {
        var primary = displays.FirstOrDefault(display => display.Primary);
        var validPrimary = primary.Primary && primary.Width is >= 1 and <= 32_768 &&
                           primary.Height is >= 1 and <= 32_768;
        return new DisplayStatusSnapshot(
            "available",
            Math.Min(displays.Count, 16),
            displays.Count > 16,
            validPrimary ? primary.Width : null,
            validPrimary ? primary.Height : null);
    }
}

internal sealed class WindowsPrinterStatusReader(IWmiQueryClient? wmi = null) : IPrinterStatusReader
{
    private readonly IWmiQueryClient _wmi = wmi ?? new WindowsWmiQueryClient();

    public PrinterStatusSnapshot Read()
    {
        try
        {
            var rows = _wmi.Query(
                "ROOT\\CIMV2",
                "SELECT Default, WorkOffline FROM Win32_Printer",
                ["Default", "WorkOffline"]);
            return Classify(rows);
        }
        catch (Exception exception) when (exception is
            COMException or UnauthorizedAccessException or InvalidOperationException or TargetInvocationException)
        {
            return new PrinterStatusSnapshot("unavailable", 0, "unknown", false);
        }
    }

    internal static PrinterStatusSnapshot Classify(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var defaultPrinter = rows.FirstOrDefault(row => ReadBoolean(row, "Default") == true);
        var defaultStatus = defaultPrinter is null
            ? "not_configured"
            : ReadBoolean(defaultPrinter, "WorkOffline") switch
            {
                true => "offline",
                false => "online",
                null => "unknown",
            };
        return new PrinterStatusSnapshot(
            "available",
            Math.Min(rows.Count, 32),
            defaultStatus,
            rows.Count >= 32);
    }

    private static bool? ReadBoolean(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null) return null;
        try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        catch (Exception exception) when (exception is FormatException or InvalidCastException) { return null; }
    }
}

internal sealed class WindowsDeviceStatusReader(
    IAudioOutputStatusReader? audio = null,
    IDisplayStatusReader? displays = null,
    IPrinterStatusReader? printers = null) : IDeviceStatusReader
{
    private readonly IAudioOutputStatusReader _audio = audio ?? new WindowsAudioOutputStatusReader();
    private readonly IDisplayStatusReader _displays = displays ?? new WindowsDisplayStatusReader();
    private readonly IPrinterStatusReader _printers = printers ?? new WindowsPrinterStatusReader();

    public DeviceStatusSnapshot Read() => new(_audio.Read(), _displays.Read(), _printers.Read());
}

internal sealed class SystemGetDeviceStatusTool(IDeviceStatusReader? reader = null) : IWindowsToolAdapter
{
    private readonly IDeviceStatusReader _reader = reader ?? new WindowsDeviceStatusReader();

    public string Name => "system.get_device_status.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Count != 0)
            return new WindowsToolExecutionResult(
                false, new Dictionary<string, object?>(), "장치 상태 확인에는 입력값이 필요하지 않습니다.");

        var snapshot = await Task.Run(_reader.Read, CancellationToken.None)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var audio = new Dictionary<string, object?>
        {
            ["providerStatus"] = NormalizeProvider(snapshot.AudioOutput.ProviderStatus),
            ["defaultOutputStatus"] = NormalizeAudioStatus(snapshot.AudioOutput.DefaultOutputStatus),
        };
        var displays = new Dictionary<string, object?>
        {
            ["providerStatus"] = NormalizeProvider(snapshot.Displays.ProviderStatus),
            ["activeDisplayCount"] = Math.Clamp(snapshot.Displays.ActiveDisplayCount, 0, 16),
            ["truncated"] = snapshot.Displays.Truncated || snapshot.Displays.ActiveDisplayCount > 16,
        };
        if (snapshot.Displays.PrimaryWidthPixels is int width && width is >= 1 and <= 32_768 &&
            snapshot.Displays.PrimaryHeightPixels is int height && height is >= 1 and <= 32_768)
        {
            displays["primaryDisplay"] = new Dictionary<string, object?>
            {
                ["widthPixels"] = width,
                ["heightPixels"] = height,
            };
        }
        var printers = new Dictionary<string, object?>
        {
            ["providerStatus"] = NormalizeProvider(snapshot.Printers.ProviderStatus),
            ["observedPrinterCount"] = Math.Clamp(snapshot.Printers.ObservedPrinterCount, 0, 32),
            ["defaultPrinterStatus"] = NormalizePrinterStatus(snapshot.Printers.DefaultPrinterStatus),
            ["truncated"] = snapshot.Printers.Truncated || snapshot.Printers.ObservedPrinterCount > 32,
        };
        IReadOnlyDictionary<string, object?> output = new Dictionary<string, object?>
        {
            ["audioOutput"] = audio,
            ["displays"] = displays,
            ["printers"] = printers,
        };
        return new WindowsToolExecutionResult(
            true,
            output,
            ActivitySummary: "기본 오디오 출력, 디스플레이와 프린터 상태를 확인했어요.");
    }

    private static string NormalizeProvider(string status) => status == "available" ? status : "unavailable";

    private static string NormalizeAudioStatus(string status) => status is
        "active" or "disabled" or "not_present" or "unplugged" ? status : "unknown";

    private static string NormalizePrinterStatus(string status) => status is
        "online" or "offline" or "not_configured" ? status : "unknown";
}
