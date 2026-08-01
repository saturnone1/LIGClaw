using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record WifiSsidReadResult(
    string Status,
    IReadOnlyDictionary<Guid, string> Ssids);

internal interface IWifiSsidReader
{
    WifiSsidReadResult Read();
}

internal sealed class WindowsWifiSsidReader : IWifiSsidReader
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorAccessDenied = 5;
    private const int InterfaceListHeaderBytes = 8;
    private const int InterfaceInfoBytes = 532;
    private const int InterfaceStateOffset = 528;
    private const int ConnectedState = 1;
    private const int CurrentConnectionOpcode = 7;
    private const int SsidLengthOffset = 520;
    private const int SsidBytesOffset = 524;
    private const int MinimumConnectionBytes = 556;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public WifiSsidReadResult Read()
    {
        IntPtr client = IntPtr.Zero;
        IntPtr interfaces = IntPtr.Zero;
        try
        {
            if (NativeMethods.WlanOpenHandle(2, IntPtr.Zero, out _, out client) != ErrorSuccess)
                return Unavailable();
            if (NativeMethods.WlanEnumInterfaces(client, IntPtr.Zero, out interfaces) != ErrorSuccess ||
                interfaces == IntPtr.Zero)
                return Unavailable();
            var count = Marshal.ReadInt32(interfaces, 0);
            if (count is < 0 or > 64) return Unavailable();
            if (count == 0) return new WifiSsidReadResult("not_applicable", new Dictionary<Guid, string>());
            var ssids = new Dictionary<Guid, string>();
            for (var index = 0; index < count; index++)
            {
                var item = IntPtr.Add(interfaces, InterfaceListHeaderBytes + index * InterfaceInfoBytes);
                var interfaceId = Marshal.PtrToStructure<Guid>(item);
                if (Marshal.ReadInt32(item, InterfaceStateOffset) != ConnectedState) continue;
                var result = NativeMethods.WlanQueryInterface(
                    client,
                    ref interfaceId,
                    CurrentConnectionOpcode,
                    IntPtr.Zero,
                    out var dataSize,
                    out var data,
                    out _);
                if (result == ErrorAccessDenied)
                    return new WifiSsidReadResult("permission_required", new Dictionary<Guid, string>());
                if (result != ErrorSuccess || data == IntPtr.Zero) continue;
                try
                {
                    var ssid = ReadSsid(data, dataSize);
                    if (ssid is not null) ssids[interfaceId] = ssid;
                }
                finally
                {
                    NativeMethods.WlanFreeMemory(data);
                }
            }
            return new WifiSsidReadResult("available", ssids);
        }
        catch (Exception exception) when (exception is
            DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or
            AccessViolationException or SEHException or ArgumentException)
        {
            return Unavailable();
        }
        finally
        {
            if (interfaces != IntPtr.Zero) NativeMethods.WlanFreeMemory(interfaces);
            if (client != IntPtr.Zero) _ = NativeMethods.WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    internal static string? DecodeSsid(byte[] bytes, int length)
    {
        if (length is < 1 or > 32 || bytes.Length < length) return null;
        try
        {
            var value = StrictUtf8.GetString(bytes, 0, length);
            return value.Length is < 1 or > 32 || value.Any(char.IsControl) ? null : value;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? ReadSsid(IntPtr data, uint dataSize)
    {
        if (dataSize < MinimumConnectionBytes) return null;
        var length = Marshal.ReadInt32(data, SsidLengthOffset);
        if (length is < 1 or > 32) return null;
        var bytes = new byte[length];
        Marshal.Copy(IntPtr.Add(data, SsidBytesOffset), bytes, 0, length);
        return DecodeSsid(bytes, length);
    }

    private static WifiSsidReadResult Unavailable() =>
        new("unavailable", new Dictionary<Guid, string>());

    private static class NativeMethods
    {
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanOpenHandle(
            uint clientVersion,
            IntPtr reserved,
            out uint negotiatedVersion,
            out IntPtr clientHandle);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanEnumInterfaces(
            IntPtr clientHandle,
            IntPtr reserved,
            out IntPtr interfaceList);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanQueryInterface(
            IntPtr clientHandle,
            ref Guid interfaceGuid,
            int opcode,
            IntPtr reserved,
            out uint dataSize,
            out IntPtr data,
            out int opcodeValueType);

        [DllImport("wlanapi.dll")]
        internal static extern void WlanFreeMemory(IntPtr memory);
    }
}

internal sealed record NetworkDetailsAdapterSnapshot(
    string Name,
    string InterfaceType,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> DnsServers,
    IReadOnlyList<string> Gateways,
    string? WifiSsid);

internal sealed record NetworkDetailsSnapshot(
    string ProviderStatus,
    string WifiSsidStatus,
    bool NetworkAvailable,
    IReadOnlyList<NetworkDetailsAdapterSnapshot> Adapters,
    bool Truncated);

internal interface INetworkDetailsReader
{
    NetworkDetailsSnapshot Read();
}

internal sealed class WindowsNetworkDetailsReader(IWifiSsidReader? wifi = null) : INetworkDetailsReader
{
    private readonly IWifiSsidReader _wifi = wifi ?? new WindowsWifiSsidReader();

    public NetworkDetailsSnapshot Read()
    {
        try
        {
            var wifi = _wifi.Read();
            var truncated = false;
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .Where(adapter => adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .Select(adapter => ReadAdapter(adapter, wifi.Ssids, ref truncated))
                .Where(adapter => adapter is not null)
                .Cast<NetworkDetailsAdapterSnapshot>()
                .OrderBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (adapters.Length > 16) truncated = true;
            return new NetworkDetailsSnapshot(
                "available",
                wifi.Status,
                NetworkInterface.GetIsNetworkAvailable(),
                adapters.Take(16).ToArray(),
                truncated);
        }
        catch (Exception exception) when (exception is
            NetworkInformationException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return new NetworkDetailsSnapshot("unavailable", "unavailable", false, [], false);
        }
    }

    private static NetworkDetailsAdapterSnapshot? ReadAdapter(
        NetworkInterface adapter,
        IReadOnlyDictionary<Guid, string> ssids,
        ref bool truncated)
    {
        try
        {
            var properties = adapter.GetIPProperties();
            var addressSource = properties.UnicastAddresses
                .Where(address => address.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(address => $"{FormatAddress(address.Address)}/{address.PrefixLength}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var dnsSource = properties.DnsAddresses
                .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(FormatAddress)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var gatewaySource = properties.GatewayAddresses
                .Select(gateway => gateway.Address)
                .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(FormatAddress)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (addressSource.Length > 8 || dnsSource.Length > 4 || gatewaySource.Length > 4) truncated = true;
            string? ssid = null;
            if (Guid.TryParse(adapter.Id, out var interfaceId)) ssids.TryGetValue(interfaceId, out ssid);
            return new NetworkDetailsAdapterSnapshot(
                Limit(adapter.Name, 128),
                Limit(adapter.NetworkInterfaceType.ToString().ToLowerInvariant(), 64),
                addressSource.Take(8).ToArray(),
                dnsSource.Take(4).ToArray(),
                gatewaySource.Take(4).ToArray(),
                ssid);
        }
        catch (Exception exception) when (exception is
            NetworkInformationException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FormatAddress(IPAddress address) => new IPAddress(address.GetAddressBytes()).ToString();

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : string.Concat(value.AsSpan(0, maximum - 1), "…");
}

internal sealed class SystemGetNetworkDetailsTool(INetworkDetailsReader? reader = null) : IWindowsToolAdapter
{
    private readonly INetworkDetailsReader _reader = reader ?? new WindowsNetworkDetailsReader();

    public string Name => "system.get_network_details.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var reason = ReadReason(input);
        return reason is null
            ? null
            : new WindowsToolApprovalPrompt(
                "현재 네트워크 설정을 확인할까요?",
                "연결된 어댑터의 현재 IP·DNS·게이트웨이와 Wi-Fi 이름을 확인합니다.",
                $"요청 이유: {reason}\n\n현재 네트워크 주소와 Wi-Fi 이름은 위치나 사내망 문맥을 드러낼 수 있어 모델에 전달하기 전에 확인이 필요합니다. 비밀번호, Wi-Fi 보안 키, MAC/BSSID와 과거 연결 기록은 읽지 않습니다. Windows가 별도의 위치 권한 확인을 표시할 수 있습니다.",
                Risk: "R1");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadReason(input) is null)
            return Failure("네트워크 상세 조회 이유가 올바르지 않습니다.");
        var snapshot = await Task.Run(_reader.Read, CancellationToken.None)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var validAdapters = snapshot.Adapters
            .Where(adapter => !string.IsNullOrWhiteSpace(adapter.Name) && adapter.Name.Length <= 128 &&
                              !string.IsNullOrWhiteSpace(adapter.InterfaceType) && adapter.InterfaceType.Length <= 64)
            .Take(16)
            .ToArray();
        var truncated = snapshot.Truncated || validAdapters.Length != snapshot.Adapters.Count ||
                        validAdapters.Any(adapter => adapter.Addresses.Count > 8 ||
                                                     adapter.DnsServers.Count > 4 || adapter.Gateways.Count > 4);
        IReadOnlyDictionary<string, object?> output = new Dictionary<string, object?>
        {
            ["providerStatus"] = snapshot.ProviderStatus,
            ["wifiSsidStatus"] = snapshot.WifiSsidStatus,
            ["networkAvailable"] = snapshot.NetworkAvailable,
            ["adapters"] = validAdapters.Select(ToOutput).ToArray(),
            ["truncated"] = truncated,
        };
        return new WindowsToolExecutionResult(
            true,
            output,
            ActivitySummary: snapshot.ProviderStatus == "available"
                ? "현재 IP, DNS와 게이트웨이 설정을 확인했어요."
                : "네트워크 상세 정보 공급자를 사용할 수 없어요.");
    }

    private static string? ReadReason(IReadOnlyDictionary<string, object?> input) =>
        ToolInputReader.HasOnlyKeys(input, "reason") &&
        ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason)
            ? reason
            : null;

    private static IReadOnlyDictionary<string, object?> ToOutput(NetworkDetailsAdapterSnapshot adapter)
    {
        var output = new Dictionary<string, object?>
        {
            ["name"] = adapter.Name,
            ["interfaceType"] = adapter.InterfaceType,
            ["addresses"] = adapter.Addresses.Take(8).ToArray(),
            ["dnsServers"] = adapter.DnsServers.Take(4).ToArray(),
            ["gateways"] = adapter.Gateways.Take(4).ToArray(),
        };
        if (adapter.WifiSsid is { Length: >= 1 and <= 32 } ssid && !ssid.Any(char.IsControl))
            output["wifiSsid"] = ssid;
        return output;
    }

    private static WindowsToolExecutionResult Failure(string error) =>
        new(false, new Dictionary<string, object?>(), error);
}
