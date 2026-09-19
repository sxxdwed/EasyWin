using EasyWin.Core.Models;

namespace EasyWin.Tests;

internal static class TestData
{
    public static DiskIdentity Disk(string serial = "SERIAL-001") => new()
    {
        DiskNumber = 0,
        DeviceId = "\\\\.\\PhysicalDrive0",
        SerialNumber = serial,
        Model = "Test NVMe",
        SizeBytes = 1_000_204_886_016,
        BusType = DiskBusType.Nvme,
        UniqueId = "TEST-DISK-001",
        IsBootDisk = true,
        IsSystemDisk = true,
    };

    public static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "EasyWin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static string FindConfigRoot()
    {
        string current = AppContext.BaseDirectory;
        for (int index = 0; index < 10; index++)
        {
            string candidate = Path.Combine(current, "config");
            if (Directory.Exists(candidate)) return candidate;
            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null) break;
            current = parent.FullName;
        }

        throw new DirectoryNotFoundException("config root not found");
    }
}
