using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record PhysicalDiskHealthSnapshot(
    string Name,
    string MediaType,
    string Health,
    string OperationalStatus,
    long? SizeBytes);

internal sealed record BitLockerVolumeSnapshot(
    string DriveLetter,
    string ProtectionStatus,
    string EncryptionStatus);

internal sealed record DiskHealthSnapshot(
    string PhysicalDiskProviderStatus,
    IReadOnlyList<PhysicalDiskHealthSnapshot> PhysicalDisks,
    string BitLockerProviderStatus,
    IReadOnlyList<BitLockerVolumeSnapshot> BitLockerVolumes);

internal interface IDiskHealthReader
{
    DiskHealthSnapshot Read();
}

internal sealed record DefenderStatusSnapshot(
    string Status,
    bool? AntivirusEnabled = null,
    bool? RealTimeProtectionEnabled = null,
    long? SignatureAgeDays = null);

internal sealed record FirewallStatusSnapshot(
    string Status,
    bool? DomainEnabled = null,
    bool? PrivateEnabled = null,
    bool? PublicEnabled = null);

internal sealed record WindowsUpdateStatusSnapshot(
    string Status,
    string? LastSearchSuccessUtc = null,
    string? LastInstallationSuccessUtc = null);

internal sealed record SecurityStatusSnapshot(
    DefenderStatusSnapshot Defender,
    FirewallStatusSnapshot Firewall,
    WindowsUpdateStatusSnapshot WindowsUpdate);

internal interface ISecurityStatusReader
{
    SecurityStatusSnapshot Read();
}

internal interface IWmiQueryClient
{
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(
        string namespacePath,
        string query,
        IReadOnlyList<string> properties);
}

internal sealed class WindowsWmiQueryClient : IWmiQueryClient
{
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(
        string namespacePath,
        string query,
        IReadOnlyList<string> properties)
    {
        object? locator = null;
        object? services = null;
        object? results = null;
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!;
            locator = Activator.CreateInstance(locatorType)
                ?? throw new InvalidOperationException("Windows 관리 provider를 만들 수 없습니다.");
            services = Invoke(locator, "ConnectServer", ".", namespacePath);
            results = Invoke(services, "ExecQuery", query);
            if (results is not IEnumerable enumerable) return [];

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var item in enumerable)
            {
                if (item is null) continue;
                try
                {
                    var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                    var propertySet = ReadProperty(item, "Properties_");
                    if (propertySet is null) continue;
                    try
                    {
                        foreach (var propertyName in properties)
                        {
                            object? property = null;
                            try
                            {
                                property = Invoke(propertySet, "Item", propertyName);
                                row[propertyName] = property is null ? null : ReadProperty(property, "Value");
                            }
                            catch (COMException)
                            {
                                row[propertyName] = null;
                            }
                            finally
                            {
                                Release(property);
                            }
                        }
                    }
                    finally
                    {
                        Release(propertySet);
                    }
                    rows.Add(row);
                    if (rows.Count >= 32) break;
                }
                finally
                {
                    Release(item);
                }
            }
            return rows;
        }
        finally
        {
            Release(results);
            Release(services);
            Release(locator);
        }
    }

    private static object Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().InvokeMember(
            name,
            BindingFlags.InvokeMethod | BindingFlags.GetProperty,
            binder: null,
            target,
            arguments,
            CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException("Windows 관리 provider가 값을 반환하지 않았습니다.");

    private static object? ReadProperty(object target, string name) => target.GetType().InvokeMember(
        name,
        BindingFlags.GetProperty,
        binder: null,
        target,
        args: null,
        CultureInfo.InvariantCulture);

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.FinalReleaseComObject(value);
    }
}

internal sealed class WindowsDiskHealthReader(IWmiQueryClient? wmi = null) : IDiskHealthReader
{
    private readonly IWmiQueryClient _wmi = wmi ?? new WindowsWmiQueryClient();

    public DiskHealthSnapshot Read()
    {
        var (diskStatus, disks) = ReadPhysicalDisks();
        var (bitLockerStatus, volumes) = ReadBitLockerVolumes();
        return new DiskHealthSnapshot(diskStatus, disks, bitLockerStatus, volumes);
    }

    private (string Status, IReadOnlyList<PhysicalDiskHealthSnapshot> Items) ReadPhysicalDisks()
    {
        try
        {
            var rows = _wmi.Query(
                "ROOT\\Microsoft\\Windows\\Storage",
                "SELECT FriendlyName, MediaType, HealthStatus, OperationalStatus, Size FROM MSFT_PhysicalDisk",
                ["FriendlyName", "MediaType", "HealthStatus", "OperationalStatus", "Size"]);
            return ("available", rows.Select(ToPhysicalDisk).ToArray());
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return ("unavailable", []);
        }
    }

    private (string Status, IReadOnlyList<BitLockerVolumeSnapshot> Items) ReadBitLockerVolumes()
    {
        try
        {
            var rows = _wmi.Query(
                "ROOT\\CIMV2\\Security\\MicrosoftVolumeEncryption",
                "SELECT DriveLetter, ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume",
                ["DriveLetter", "ProtectionStatus", "ConversionStatus"]);
            return ("available", rows.Select(ToBitLockerVolume).ToArray());
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return ("unavailable", []);
        }
    }

    private static PhysicalDiskHealthSnapshot ToPhysicalDisk(IReadOnlyDictionary<string, object?> row) => new(
        BoundedText(row["FriendlyName"], "알 수 없는 디스크", 160),
        ReadCode(row["MediaType"]) switch { 3 => "hdd", 4 => "ssd", 5 => "scm", 0 => "unspecified", _ => "unknown" },
        ReadCode(row["HealthStatus"]) switch { 0 => "healthy", 1 => "warning", 2 => "unhealthy", _ => "unknown" },
        MapOperationalStatus(row["OperationalStatus"]),
        ReadNonNegativeInt64(row["Size"]));

    private static BitLockerVolumeSnapshot ToBitLockerVolume(IReadOnlyDictionary<string, object?> row) => new(
        BoundedText(row["DriveLetter"], "?", 8),
        ReadCode(row["ProtectionStatus"]) switch { 1 => "protected", 0 => "unprotected", _ => "unknown" },
        ReadCode(row["ConversionStatus"]) switch
        {
            0 => "fully_decrypted",
            1 => "fully_encrypted",
            2 => "encrypting",
            3 => "decrypting",
            4 or 5 => "paused",
            _ => "unknown",
        });

    private static string MapOperationalStatus(object? value)
    {
        var code = value is Array array && array.Length > 0 ? ReadCode(array.GetValue(0)) : ReadCode(value);
        return code switch { 2 => "ok", 3 or 4 or 13 => "degraded", 6 or 7 or 10 or 11 or 12 => "error", _ => "unknown" };
    }

    private static bool IsProviderFailure(Exception exception) =>
        exception is COMException or UnauthorizedAccessException or InvalidOperationException or TargetInvocationException;

    internal static int ReadCode(object? value)
    {
        try { return value is null ? -1 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) { return -1; }
    }

    internal static long? ReadNonNegativeInt64(object? value)
    {
        try
        {
            if (value is null) return null;
            var parsed = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return parsed < 0 ? null : parsed;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    internal static string BoundedText(object? value, string fallback, int maxLength)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrEmpty(text)) return fallback;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}

internal sealed class WindowsSecurityStatusReader(IWmiQueryClient? wmi = null) : ISecurityStatusReader
{
    private readonly IWmiQueryClient _wmi = wmi ?? new WindowsWmiQueryClient();

    public SecurityStatusSnapshot Read() => new(ReadDefender(), ReadFirewall(), ReadWindowsUpdate());

    private DefenderStatusSnapshot ReadDefender()
    {
        try
        {
            var row = _wmi.Query(
                "ROOT\\Microsoft\\Windows\\Defender",
                "SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge FROM MSFT_MpComputerStatus",
                ["AntivirusEnabled", "RealTimeProtectionEnabled", "AntivirusSignatureAge"]).FirstOrDefault();
            return row is null
                ? new DefenderStatusSnapshot("unavailable")
                : new DefenderStatusSnapshot(
                    "available",
                    ReadBoolean(row["AntivirusEnabled"]),
                    ReadBoolean(row["RealTimeProtectionEnabled"]),
                    WindowsDiskHealthReader.ReadNonNegativeInt64(row["AntivirusSignatureAge"]));
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return new DefenderStatusSnapshot("unavailable");
        }
    }

    private static FirewallStatusSnapshot ReadFirewall()
    {
        object? policy = null;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
            policy = Activator.CreateInstance(type);
            if (policy is null) return new FirewallStatusSnapshot("unavailable");
            return new FirewallStatusSnapshot(
                "available",
                ReadIndexedBoolean(policy, "FirewallEnabled", 1),
                ReadIndexedBoolean(policy, "FirewallEnabled", 2),
                ReadIndexedBoolean(policy, "FirewallEnabled", 4));
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return new FirewallStatusSnapshot("unavailable");
        }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy)) _ = Marshal.FinalReleaseComObject(policy);
        }
    }

    private static WindowsUpdateStatusSnapshot ReadWindowsUpdate()
    {
        object? automaticUpdate = null;
        object? results = null;
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate", throwOnError: true)!;
            automaticUpdate = Activator.CreateInstance(type);
            if (automaticUpdate is null) return new WindowsUpdateStatusSnapshot("unavailable");
            results = ReadProperty(automaticUpdate, "Results");
            if (results is null) return new WindowsUpdateStatusSnapshot("unavailable");
            return new WindowsUpdateStatusSnapshot(
                "available",
                ReadUtcDate(ReadProperty(results, "LastSearchSuccessDate")),
                ReadUtcDate(ReadProperty(results, "LastInstallationSuccessDate")));
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return new WindowsUpdateStatusSnapshot("unavailable");
        }
        finally
        {
            if (results is not null && Marshal.IsComObject(results)) _ = Marshal.FinalReleaseComObject(results);
            if (automaticUpdate is not null && Marshal.IsComObject(automaticUpdate)) _ = Marshal.FinalReleaseComObject(automaticUpdate);
        }
    }

    private static bool? ReadBoolean(object? value)
    {
        try { return value is null ? null : Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        catch (Exception exception) when (exception is FormatException or InvalidCastException) { return null; }
    }

    private static bool? ReadIndexedBoolean(object target, string property, int index)
    {
        var value = target.GetType().InvokeMember(
            property, BindingFlags.GetProperty, null, target, [index], CultureInfo.InvariantCulture);
        return ReadBoolean(value);
    }

    private static object? ReadProperty(object target, string property) => target.GetType().InvokeMember(
        property, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

    private static string? ReadUtcDate(object? value)
    {
        if (value is not DateTime date || date == DateTime.MinValue) return null;
        return date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static bool IsProviderFailure(Exception exception) =>
        exception is COMException or UnauthorizedAccessException or InvalidOperationException or TargetInvocationException;
}

internal sealed class SystemGetDiskHealthTool(IDiskHealthReader? reader = null) : IWindowsToolAdapter
{
    private readonly IDiskHealthReader _reader = reader ?? new WindowsDiskHealthReader();
    public string Name => "system.get_disk_health.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;

    public Task<WindowsToolExecutionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Count != 0) return Task.FromResult(Failure("디스크 상태 확인에는 입력값이 필요하지 않습니다."));
        var snapshot = _reader.Read();
        IReadOnlyDictionary<string, object?> output = new Dictionary<string, object?>
        {
            ["physicalDiskProviderStatus"] = snapshot.PhysicalDiskProviderStatus,
            ["physicalDisks"] = snapshot.PhysicalDisks.Select(ToOutput).ToArray(),
            ["bitLockerProviderStatus"] = snapshot.BitLockerProviderStatus,
            ["bitLockerVolumes"] = snapshot.BitLockerVolumes.Select(ToOutput).ToArray(),
        };
        return Task.FromResult(new WindowsToolExecutionResult(true, output, ActivitySummary: "물리 디스크와 BitLocker 상태를 확인했어요."));
    }

    private static IReadOnlyDictionary<string, object?> ToOutput(PhysicalDiskHealthSnapshot item)
    {
        var output = new Dictionary<string, object?>
        {
            ["name"] = item.Name,
            ["mediaType"] = item.MediaType,
            ["health"] = item.Health,
            ["operationalStatus"] = item.OperationalStatus,
        };
        if (item.SizeBytes is not null) output["sizeBytes"] = item.SizeBytes.Value;
        return output;
    }

    private static IReadOnlyDictionary<string, object?> ToOutput(BitLockerVolumeSnapshot item) => new Dictionary<string, object?>
    {
        ["driveLetter"] = item.DriveLetter,
        ["protectionStatus"] = item.ProtectionStatus,
        ["encryptionStatus"] = item.EncryptionStatus,
    };

    private static WindowsToolExecutionResult Failure(string error) => new(false, new Dictionary<string, object?>(), error);
}

internal sealed class SystemGetSecurityStatusTool(ISecurityStatusReader? reader = null) : IWindowsToolAdapter
{
    private readonly ISecurityStatusReader _reader = reader ?? new WindowsSecurityStatusReader();
    public string Name => "system.get_security_status.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;

    public Task<WindowsToolExecutionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Count != 0) return Task.FromResult(Failure("보안 상태 확인에는 입력값이 필요하지 않습니다."));
        var snapshot = _reader.Read();
        IReadOnlyDictionary<string, object?> output = new Dictionary<string, object?>
        {
            ["defender"] = DefenderOutput(snapshot.Defender),
            ["firewall"] = FirewallOutput(snapshot.Firewall),
            ["windowsUpdate"] = UpdateOutput(snapshot.WindowsUpdate),
        };
        return Task.FromResult(new WindowsToolExecutionResult(true, output, ActivitySummary: "Windows 보안과 업데이트 상태를 확인했어요."));
    }

    private static IReadOnlyDictionary<string, object?> DefenderOutput(DefenderStatusSnapshot item)
    {
        var output = new Dictionary<string, object?> { ["status"] = item.Status };
        if (item.AntivirusEnabled is not null) output["antivirusEnabled"] = item.AntivirusEnabled.Value;
        if (item.RealTimeProtectionEnabled is not null) output["realTimeProtectionEnabled"] = item.RealTimeProtectionEnabled.Value;
        if (item.SignatureAgeDays is not null) output["signatureAgeDays"] = item.SignatureAgeDays.Value;
        return output;
    }

    private static IReadOnlyDictionary<string, object?> FirewallOutput(FirewallStatusSnapshot item)
    {
        var output = new Dictionary<string, object?> { ["status"] = item.Status };
        if (item.DomainEnabled is not null) output["domainEnabled"] = item.DomainEnabled.Value;
        if (item.PrivateEnabled is not null) output["privateEnabled"] = item.PrivateEnabled.Value;
        if (item.PublicEnabled is not null) output["publicEnabled"] = item.PublicEnabled.Value;
        return output;
    }

    private static IReadOnlyDictionary<string, object?> UpdateOutput(WindowsUpdateStatusSnapshot item)
    {
        var output = new Dictionary<string, object?> { ["status"] = item.Status };
        if (item.LastSearchSuccessUtc is not null) output["lastSearchSuccessUtc"] = item.LastSearchSuccessUtc;
        if (item.LastInstallationSuccessUtc is not null) output["lastInstallationSuccessUtc"] = item.LastInstallationSuccessUtc;
        return output;
    }

    private static WindowsToolExecutionResult Failure(string error) => new(false, new Dictionary<string, object?>(), error);
}
