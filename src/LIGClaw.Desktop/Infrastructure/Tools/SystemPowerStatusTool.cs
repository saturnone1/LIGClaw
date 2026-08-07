using System.Runtime.InteropServices;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record PowerStatusSnapshot(
    string ProviderStatus,
    string PowerSource,
    string BatteryPresence,
    string EnergySaverStatus,
    string? ChargingStatus = null,
    string? SafetyStatus = null,
    double? Percent = null,
    long? EstimatedRuntimeSeconds = null);

internal interface IPowerStatusReader
{
    PowerStatusSnapshot Read();
}

internal sealed class WindowsPowerStatusReader : IPowerStatusReader
{
    private const byte BatteryFlagLow = 2;
    private const byte BatteryFlagCritical = 4;
    private const byte BatteryFlagCharging = 8;
    private const byte BatteryFlagNoSystemBattery = 128;
    private const byte UnknownByte = 255;
    private const uint UnknownLifetime = uint.MaxValue;

    public PowerStatusSnapshot Read()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
            return new PowerStatusSnapshot("unavailable", "unknown", "unknown", "unknown");
        return Classify(
            status.AcLineStatus,
            status.BatteryFlag,
            status.BatteryLifePercent,
            status.SystemStatusFlag,
            status.BatteryLifeTime);
    }

    internal static PowerStatusSnapshot Classify(
        byte acLineStatus,
        byte batteryFlag,
        byte batteryPercent,
        byte systemStatusFlag,
        uint batteryLifeTime)
    {
        var powerSource = acLineStatus switch { 0 => "battery", 1 => "ac", _ => "unknown" };
        var energySaver = systemStatusFlag switch { 0 => "off", 1 => "on", _ => "unknown" };
        if (batteryFlag == UnknownByte)
            return new PowerStatusSnapshot("available", powerSource, "unknown", energySaver);
        if ((batteryFlag & BatteryFlagNoSystemBattery) != 0)
            return new PowerStatusSnapshot("available", powerSource, "not_present", energySaver);
        if (batteryPercent > 100)
            return new PowerStatusSnapshot("available", powerSource, "unknown", energySaver);

        var charging = (batteryFlag & BatteryFlagCharging) != 0
            ? "charging"
            : acLineStatus == 0 ? "discharging" : acLineStatus == 1 ? "not_charging" : "unknown";
        var safety = (batteryFlag & BatteryFlagCritical) != 0
            ? "critical"
            : (batteryFlag & BatteryFlagLow) != 0 ? "low" : "normal";
        return new PowerStatusSnapshot(
            "available",
            powerSource,
            "present",
            energySaver,
            charging,
            safety,
            batteryPercent,
            batteryLifeTime == UnknownLifetime ? null : batteryLifeTime);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        internal byte AcLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }
}

internal sealed class SystemGetPowerStatusTool(IPowerStatusReader? reader = null) : IWindowsToolAdapter
{
    private readonly IPowerStatusReader _reader = reader ?? new WindowsPowerStatusReader();

    public string Name => "system.get_power_status.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;

    public Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Count != 0)
            return Task.FromResult(new WindowsToolExecutionResult(
                false, new Dictionary<string, object?>(), "전원 상태 확인에는 입력값이 필요하지 않습니다."));

        var status = _reader.Read();
        var output = new Dictionary<string, object?>
        {
            ["providerStatus"] = status.ProviderStatus,
            ["powerSource"] = status.PowerSource,
            ["batteryPresence"] = status.BatteryPresence,
            ["energySaverStatus"] = status.EnergySaverStatus,
        };
        if (status.BatteryPresence == "present")
        {
            var battery = new Dictionary<string, object?>
            {
                ["chargingStatus"] = status.ChargingStatus,
                ["safetyStatus"] = status.SafetyStatus,
                ["percent"] = status.Percent,
            };
            if (status.EstimatedRuntimeSeconds is not null)
                battery["estimatedRuntimeSeconds"] = status.EstimatedRuntimeSeconds.Value;
            output["battery"] = battery;
        }
        var summary = status.BatteryPresence == "present" && status.Percent is not null
            ? $"전원 상태를 확인했어요. 배터리는 {status.Percent:0}%입니다."
            : "전원 상태를 확인했어요.";
        return Task.FromResult(new WindowsToolExecutionResult(true, output, ActivitySummary: summary));
    }
}
