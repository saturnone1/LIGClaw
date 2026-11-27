using System.Runtime.InteropServices;

namespace LIGClaw.Desktop.Infrastructure.Platform;

internal static class WindowsPlatformDetector
{
    private const byte WorkstationProductType = 1;

    public static WindowsPlatformProfile Detect()
    {
        var version = new OsVersionInfo
        {
            Size = (uint)Marshal.SizeOf<OsVersionInfo>(),
        };
        var status = RtlGetVersion(ref version);
        if (status != 0)
            throw new InvalidOperationException($"Windows 버전을 확인하지 못했습니다. NTSTATUS: 0x{status:X8}");

        return WindowsPlatformProfile.Classify(
            checked((int)version.MajorVersion),
            checked((int)version.MinorVersion),
            checked((int)version.BuildNumber),
            version.ProductType == WorkstationProductType);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfo
    {
        public uint Size;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ServicePack;

        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref OsVersionInfo versionInformation);
}
