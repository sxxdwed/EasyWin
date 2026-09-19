using System.Globalization;
using System.Text.RegularExpressions;
using EasyWin.Core.Models;

namespace EasyWin.Deployment.Safety;

public sealed class DeploymentSafetyException : InvalidOperationException
{
    public DeploymentSafetyException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public static partial class DeploymentGuard
{
    private static readonly HashSet<char> ReservedDriveLetters = ['A', 'B'];

    public static int DiskNumber(int value)
    {
        if (value is < 0 or > 1024)
        {
            throw new DeploymentSafetyException("disk.number.invalid", "The physical disk number is outside the supported range.");
        }

        return value;
    }

    public static void FixedInternalDisk(DiskIdentity disk, string description)
    {
        ArgumentNullException.ThrowIfNull(disk);
        if (disk.BusType is not (DiskBusType.Nvme or DiskBusType.Sata or DiskBusType.Sas or DiskBusType.Scsi or DiskBusType.Raid or DiskBusType.Ata))
        {
            throw new DeploymentSafetyException("disk.bus.unsupported", $"{description} must be an internal SATA/NVMe/SAS/SCSI/RAID disk; detected {disk.BusType}.");
        }
    }

    public static int PartitionNumber(int value)
    {
        if (value is < 1 or > 1024)
        {
            throw new DeploymentSafetyException("partition.number.invalid", "The partition number is outside the supported range.");
        }

        return value;
    }

    public static Guid RequiredGuid(Guid value, string description)
    {
        if (value == Guid.Empty)
        {
            throw new DeploymentSafetyException("guid.empty", $"{description} must not be empty.");
        }

        return value;
    }

    public static char DriveLetter(char value)
    {
        var letter = char.ToUpperInvariant(value);
        if (letter is < 'A' or > 'Z' || ReservedDriveLetters.Contains(letter))
        {
            throw new DeploymentSafetyException("drive.invalid", "The drive letter must be between C and Z.");
        }

        return letter;
    }

    public static string Label(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!VolumeLabelRegex().IsMatch(value))
        {
            throw new DeploymentSafetyException("label.invalid", "The volume label contains unsupported characters.");
        }

        return value;
    }

    public static string AbsolutePath(string path, string description, bool mustExist = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new DeploymentSafetyException("path.not_absolute", $"{description} must be an absolute path.");
        }

        var normalized = Path.GetFullPath(path);
        if (mustExist && !File.Exists(normalized) && !Directory.Exists(normalized))
        {
            throw new DeploymentSafetyException("path.not_found", $"{description} does not exist: {normalized}");
        }

        return normalized;
    }

    public static string PathInsideRoot(string root, string candidate, string description, bool mustExist = false)
    {
        var normalizedRoot = AbsolutePath(root, "Allowed root", true)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = AbsolutePath(candidate, description, mustExist);
        if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeploymentSafetyException("path.outside_root", $"{description} is outside the allowed root.");
        }

        return normalizedCandidate;
    }

    public static string Sha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToUpperInvariant();
        if (!Sha256Regex().IsMatch(normalized))
        {
            throw new DeploymentSafetyException("hash.invalid", "SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        return normalized;
    }

    public static string SafeIdentifier(string value, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!IdentifierRegex().IsMatch(value))
        {
            throw new DeploymentSafetyException("identifier.invalid", $"{description} contains unsupported characters.");
        }

        return value;
    }

    public static long BytesAtLeast(long value, long minimum, string description)
    {
        if (value < minimum)
        {
            throw new DeploymentSafetyException(
                "size.too_small",
                string.Create(CultureInfo.InvariantCulture, $"{description} must be at least {minimum} bytes."));
        }

        return value;
    }

    [GeneratedRegex("^[A-Za-z0-9 _.-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeLabelRegex();

    [GeneratedRegex("^[A-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}
