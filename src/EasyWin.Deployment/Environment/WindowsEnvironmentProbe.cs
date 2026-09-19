using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using EasyWin.Deployment.Models;

namespace EasyWin.Deployment.Environment;

public interface IWindowsEnvironmentProbe
{
    EnvironmentSnapshot Capture(string volumePath);
}

public sealed class WindowsEnvironmentProbe : IWindowsEnvironmentProbe
{
    public EnvironmentSnapshot Capture(string volumePath)
    {
        var isWindows = OperatingSystem.IsWindows();
        var root = Path.GetPathRoot(Path.GetFullPath(volumePath))
            ?? throw new ArgumentException("A rooted volume path is required.", nameof(volumePath));
        var available = Directory.Exists(root) ? new DriveInfo(root).AvailableFreeSpace : 0;
        var (onAc, batteryPercent) = isWindows ? GetPower() : (true, 100);

        return new EnvironmentSnapshot(
            IsAdministrator: isWindows && IsAdministrator(),
            IsUefi: isWindows && IsUefi(),
            IsAcPowerConnected: onAc,
            BatteryPercent: batteryPercent,
            AvailableBytes: available,
            SystemDrive: root,
            IsWindows: isWindows,
            OperatingSystemVersion: System.Environment.OSVersion.Version);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsUefi()
    {
        if (!GetFirmwareType(out var firmwareType))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query firmware type.");
        }

        return firmwareType == FirmwareType.Uefi;
    }

    private static (bool OnAc, int BatteryPercent) GetPower()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query power status.");
        }

        var battery = status.BatteryLifePercent == byte.MaxValue ? 100 : status.BatteryLifePercent;
        return (status.ACLineStatus != 0, battery);
    }

    private enum FirmwareType : uint
    {
        Unknown = 0,
        Bios = 1,
        Uefi = 2,
        Maximum = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out FirmwareType firmwareType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);
}
