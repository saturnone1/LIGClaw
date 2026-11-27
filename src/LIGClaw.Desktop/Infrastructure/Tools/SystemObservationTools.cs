using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record StorageVolumeSnapshot(
    string RootPath,
    string DriveType,
    string Status,
    string? FileSystem = null,
    long? TotalBytes = null,
    long? FreeBytes = null,
    double? UsedPercent = null);

internal interface IStorageStatusReader
{
    IReadOnlyList<StorageVolumeSnapshot> Read();
}

internal sealed class WindowsStorageStatusReader : IStorageStatusReader
{
    public IReadOnlyList<StorageVolumeSnapshot> Read() => DriveInfo.GetDrives()
        .Select(ReadVolume)
        .OrderBy(volume => volume.RootPath, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static StorageVolumeSnapshot ReadVolume(DriveInfo drive)
    {
        var driveType = drive.DriveType switch
        {
            DriveType.Fixed => "fixed",
            DriveType.Removable => "removable",
            DriveType.Network => "network",
            DriveType.CDRom => "optical",
            DriveType.Ram => "ram",
            _ => "unknown",
        };
        try
        {
            if (!drive.IsReady) return new StorageVolumeSnapshot(drive.Name, driveType, "not_ready");
            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            var usedPercent = total <= 0 ? 0 : Math.Clamp((double)(total - free) / total * 100, 0, 100);
            return new StorageVolumeSnapshot(
                drive.Name,
                driveType,
                "ready",
                drive.DriveFormat,
                total,
                free,
                Math.Round(usedPercent, 1));
        }
        catch (IOException)
        {
            return new StorageVolumeSnapshot(drive.Name, driveType, "unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageVolumeSnapshot(drive.Name, driveType, "unavailable");
        }
    }
}

internal sealed class SystemGetStorageStatusTool(IStorageStatusReader? reader = null) : IWindowsToolAdapter
{
    private readonly IStorageStatusReader _reader = reader ?? new WindowsStorageStatusReader();

    public string Name => "system.get_storage_status.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;

    public Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Count != 0) return Task.FromResult(NoInputExpected("저장소 상태 확인"));
        var volumes = _reader.Read().Select(ToOutput).ToArray();
        IReadOnlyDictionary<string, object?> output = new Dictionary<string, object?>
        {
            ["volumes"] = volumes,
            ["physicalHealthAvailable"] = false,
            ["healthNote"] = "논리 볼륨 용량과 준비 상태만 확인했습니다. 물리 디스크 SMART 상태는 이 Tool에서 조회하지 않습니다.",
        };
        return Task.FromResult(new WindowsToolExecutionResult(true, output, ActivitySummary: "저장소 상태를 확인했어요."));
    }

    private static IReadOnlyDictionary<string, object?> ToOutput(StorageVolumeSnapshot volume)
    {
        var output = new Dictionary<string, object?>
        {
            ["rootPath"] = volume.RootPath,
            ["driveType"] = volume.DriveType,
            ["status"] = volume.Status,
        };
        if (volume.FileSystem is not null) output["fileSystem"] = volume.FileSystem;
        if (volume.TotalBytes is not null) output["totalBytes"] = volume.TotalBytes.Value;
        if (volume.FreeBytes is not null) output["freeBytes"] = volume.FreeBytes.Value;
        if (volume.UsedPercent is not null) output["usedPercent"] = volume.UsedPercent.Value;
        return output;
    }

    private static WindowsToolExecutionResult NoInputExpected(string operation) =>
        new(false, new Dictionary<string, object?>(), $"{operation}에는 입력값이 필요하지 않습니다.");
}

internal sealed record ResourceStatusSnapshot(
    long UptimeSeconds,
    int LogicalProcessorCount,
    double CpuUsagePercent,
    long MemoryTotalBytes,
    long MemoryAvailableBytes,
    double MemoryUsedPercent);

internal interface IResourceStatusReader
{
    Task<ResourceStatusSnapshot> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsResourceStatusReader : IResourceStatusReader
{
    public async Task<ResourceStatusSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (!NativeMethods.GlobalMemoryStatusEx(out var memory))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var first = ReadCpuTimes();
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        var second = ReadCpuTimes();
        var totalDelta = second.Kernel - first.Kernel + second.User - first.User;
        var idleDelta = second.Idle - first.Idle;
        var cpuUsage = totalDelta == 0 ? 0 : Math.Clamp((double)(totalDelta - idleDelta) / totalDelta * 100, 0, 100);
        var memoryUsed = memory.TotalPhysical == 0
            ? 0
            : Math.Clamp((double)(memory.TotalPhysical - memory.AvailablePhysical) / memory.TotalPhysical * 100, 0, 100);
        return new ResourceStatusSnapshot(
            Math.Max(0, Environment.TickCount64 / 1000),
            Math.Max(1, Environment.ProcessorCount),
            Math.Round(cpuUsage, 1),
            checked((long)Math.Min(memory.TotalPhysical, long.MaxValue)),
            checked((long)Math.Min(memory.AvailablePhysical, long.MaxValue)),
            Math.Round(memoryUsed, 1));
    }

    private static CpuTimes ReadCpuTimes()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new CpuTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
    }

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

        [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusExNative(ref MemoryStatusEx buffer);

        internal static bool GlobalMemoryStatusEx(out MemoryStatusEx buffer)
        {
            buffer = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusExNative(ref buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
        internal readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }
}

internal sealed class SystemGetResourceStatusTool(IResourceStatusReader? reader = null) : IWindowsToolAdapter
{
    private readonly IResourceStatusReader _reader = reader ?? new WindowsResourceStatusReader();

    public string Name => "system.get_resource_status.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (input.Count != 0)
            return new WindowsToolExecutionResult(false, new Dictionary<string, object?>(), "리소스 상태 확인에는 입력값이 필요하지 않습니다.");
        var status = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, object?> output = new Dictionary<string, object?>
        {
            ["uptimeSeconds"] = status.UptimeSeconds,
            ["logicalProcessorCount"] = status.LogicalProcessorCount,
            ["cpuUsagePercent"] = status.CpuUsagePercent,
            ["memoryTotalBytes"] = status.MemoryTotalBytes,
            ["memoryAvailableBytes"] = status.MemoryAvailableBytes,
            ["memoryUsedPercent"] = status.MemoryUsedPercent,
        };
        return new WindowsToolExecutionResult(true, output, ActivitySummary: "CPU와 메모리 상태를 확인했어요.");
    }
}

internal sealed record NetworkAdapterSnapshot(string Name, string InterfaceType, string Status, long SpeedMbps);

internal interface INetworkStatusReader
{
    bool IsNetworkAvailable { get; }
    IReadOnlyList<NetworkAdapterSnapshot> ReadAdapters();
}

internal sealed class WindowsNetworkStatusReader : INetworkStatusReader
{
    public bool IsNetworkAvailable => NetworkInterface.GetIsNetworkAvailable();

    public IReadOnlyList<NetworkAdapterSnapshot> ReadAdapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
        .Select(adapter => new NetworkAdapterSnapshot(
            adapter.Name,
            adapter.NetworkInterfaceType.ToString().ToLowerInvariant(),
            adapter.OperationalStatus.ToString().ToLowerInvariant(),
            Math.Max(0, adapter.Speed / 1_000_000)))
        .OrderByDescending(adapter => StringComparer.Ordinal.Equals(adapter.Status, "up"))
        .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
        .Take(32)
        .ToArray();
}

internal sealed class SystemGetNetworkStatusTool(INetworkStatusReader? reader = null) : IWindowsToolAdapter
{
    private readonly INetworkStatusReader _reader = reader ?? new WindowsNetworkStatusReader();

    public string Name => "system.get_network_status.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;

    public Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Count != 0)
            return Task.FromResult(new WindowsToolExecutionResult(false, new Dictionary<string, object?>(), "네트워크 상태 확인에는 입력값이 필요하지 않습니다."));
        var adapters = _reader.ReadAdapters().Select(adapter => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["name"] = adapter.Name,
            ["interfaceType"] = adapter.InterfaceType,
            ["status"] = adapter.Status,
            ["speedMbps"] = adapter.SpeedMbps,
        }).ToArray();
        IReadOnlyDictionary<string, object?> output = new Dictionary<string, object?>
        {
            ["networkAvailable"] = _reader.IsNetworkAvailable,
            ["adapters"] = adapters,
        };
        return Task.FromResult(new WindowsToolExecutionResult(true, output, ActivitySummary: "네트워크 연결 상태를 확인했어요."));
    }
}
