using System.Text.Json;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Disks;

public interface IPhysicalDiskService
{
    Task<IReadOnlyList<PhysicalDiskSnapshot>> GetDisksAsync(CancellationToken cancellationToken = default);

    Task<PhysicalDiskSnapshot> GetDiskAsync(int diskNumber, CancellationToken cancellationToken = default);
}

public sealed class PhysicalDiskService(IProcessRunner processRunner) : IPhysicalDiskService
{
    // No external values are interpolated into this script. PowerShell returns a typed JSON
    // snapshot; targeting decisions happen in managed code after identity validation.
    private const string QueryScript = """
        $ErrorActionPreference='Stop'
        @((Get-Disk | ForEach-Object {
          $d = $_
          $parts = @((Get-Partition -DiskNumber $d.Number -ErrorAction Stop | ForEach-Object {
            $p = $_
            $v = Get-Volume -Partition $p -ErrorAction SilentlyContinue
            $supported = Get-PartitionSupportedSize -DiskNumber $d.Number -PartitionNumber $p.PartitionNumber -ErrorAction SilentlyContinue
            [pscustomobject]@{
              PartitionNumber = [int]$p.PartitionNumber
              GptPartitionId = [string]$p.Guid
              GptTypeId = [string]$p.GptType
              OffsetBytes = [long]$p.Offset
              SizeBytes = [long]$p.Size
              ShrinkAvailableBytes = if ($supported -and $supported.SizeMin -le $p.Size) { [long]($p.Size - $supported.SizeMin) } else { [long]0 }
              FreeBytes = if ($v) { [long]$v.SizeRemaining } else { [long]0 }
              DriveLetter = [string]$p.DriveLetter
              VolumePath = [string](@($p.AccessPaths | Where-Object { $_ -like '\\?\Volume{*' }) | Select-Object -First 1)
              Label = [string]$v.FileSystemLabel
              FileSystem = [string]$v.FileSystem
              IsBoot = [bool]$p.IsBoot
              IsSystem = [bool]$p.IsSystem
              IsHidden = [bool]$p.IsHidden
              IsReadOnly = [bool]$p.IsReadOnly
            }
          }))
          [pscustomobject]@{
            Number = [int]$d.Number
            DeviceId = if ($d.Path) { [string]$d.Path } else { '\\.\PhysicalDrive' + $d.Number }
            SerialNumber = [string]$d.SerialNumber
            Model = [string]$d.FriendlyName
            SizeBytes = [long]$d.Size
            BusType = [string]$d.BusType
            UniqueId = [string]$d.UniqueId
            IsBoot = [bool]$d.IsBoot
            IsSystem = [bool]$d.IsSystem
            IsReadOnly = [bool]$d.IsReadOnly
            IsOffline = [bool]$d.IsOffline
            PartitionStyle = [string]$d.PartitionStyle
            Partitions = $parts
          }
        })) | ConvertTo-Json -Depth 6 -Compress
        """;

    public async Task<IReadOnlyList<PhysicalDiskSnapshot>> GetDisksAsync(CancellationToken cancellationToken = default)
    {
        var command = new CommandSpec(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-Command", QueryScript],
            timeout: TimeSpan.FromMinutes(2));
        var result = await processRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed("Enumerate physical disks", result);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var roots = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.EnumerateArray().ToArray(),
            JsonValueKind.Object => [document.RootElement],
            _ => throw new FormatException("The Windows Storage query returned invalid JSON.")
        };
        return roots.Select(ParseDisk).OrderBy(disk => disk.Identity.DiskNumber).ToArray();
    }

    public async Task<PhysicalDiskSnapshot> GetDiskAsync(int diskNumber, CancellationToken cancellationToken = default)
    {
        var number = DeploymentGuard.DiskNumber(diskNumber);
        var disks = await GetDisksAsync(cancellationToken).ConfigureAwait(false);
        return disks.SingleOrDefault(disk => disk.Identity.DiskNumber == number)
            ?? throw new InvalidOperationException($"Physical disk {number} was not found.");
    }

    private static PhysicalDiskSnapshot ParseDisk(JsonElement element)
    {
        var number = element.GetProperty("Number").GetInt32();
        var identity = new DiskIdentity
        {
            DiskNumber = number,
            DeviceId = GetString(element, "DeviceId") ?? $"\\\\.\\PhysicalDrive{number}",
            SerialNumber = GetString(element, "SerialNumber")?.Trim() ?? string.Empty,
            Model = GetString(element, "Model")?.Trim() ?? string.Empty,
            SizeBytes = element.GetProperty("SizeBytes").GetInt64(),
            BusType = ParseBusType(GetString(element, "BusType")),
            UniqueId = GetString(element, "UniqueId")?.Trim(),
            IsBootDisk = GetBoolean(element, "IsBoot"),
            IsSystemDisk = GetBoolean(element, "IsSystem")
        };

        var partitions = new List<PartitionInfo>();
        if (element.TryGetProperty("Partitions", out var partitionElements))
        {
            var values = partitionElements.ValueKind == JsonValueKind.Array
                ? partitionElements.EnumerateArray().ToArray()
                : partitionElements.ValueKind == JsonValueKind.Object ? [partitionElements] : [];
            partitions.AddRange(values.Select(partition => ParsePartition(number, partition)));
        }

        return new PhysicalDiskSnapshot(
            identity,
            GetBoolean(element, "IsReadOnly"),
            GetBoolean(element, "IsOffline"),
            GetString(element, "PartitionStyle") ?? "Unknown",
            partitions.OrderBy(partition => partition.OffsetBytes).ToArray())
        { UnallocatedExtents = UnallocatedStaging.FindExtents(identity.SizeBytes, partitions) };
    }

    private static PartitionInfo ParsePartition(int diskNumber, JsonElement element)
    {
        var gptType = ParseGuid(GetString(element, "GptTypeId"));
        return new PartitionInfo
        {
            DiskNumber = diskNumber,
            PartitionNumber = element.GetProperty("PartitionNumber").GetInt32(),
            GptPartitionId = ParseGuid(GetString(element, "GptPartitionId")),
            GptTypeId = gptType,
            OffsetBytes = element.GetProperty("OffsetBytes").GetInt64(),
            SizeBytes = element.GetProperty("SizeBytes").GetInt64(),
            ShrinkAvailableBytes = element.TryGetProperty("ShrinkAvailableBytes", out var shrink) && shrink.TryGetInt64(out var shrinkBytes) ? shrinkBytes : 0,
            FreeBytes = element.TryGetProperty("FreeBytes", out var free) && free.TryGetInt64(out var freeBytes) ? freeBytes : 0,
            DriveLetter = NullIfEmpty(GetString(element, "DriveLetter")),
            VolumePath = NullIfEmpty(GetString(element, "VolumePath")),
            Label = NullIfEmpty(GetString(element, "Label")),
            FileSystem = NullIfEmpty(GetString(element, "FileSystem")),
            Role = DetermineRole(gptType, GetString(element, "Label"), GetBoolean(element, "IsBoot"), GetBoolean(element, "IsSystem")),
            IsReadOnly = GetBoolean(element, "IsReadOnly")
        };
    }

    private static PartitionRole DetermineRole(Guid type, string? label, bool isBoot, bool isSystem)
    {
        if (type == Guid.Parse("c12a7328-f81f-11d2-ba4b-00a0c93ec93b") || isSystem)
        {
            return PartitionRole.EfiSystem;
        }

        if (type == Guid.Parse("e3c9e316-0b5c-4db8-817d-f92df00215ae"))
        {
            return PartitionRole.MicrosoftReserved;
        }

        if (type == Guid.Parse("de94bba4-06d1-4d40-a16a-bfd50179d6ac"))
        {
            return PartitionRole.Recovery;
        }

        if (string.Equals(label, "EASYWIN_DEPLOY", StringComparison.OrdinalIgnoreCase))
        {
            return PartitionRole.Deployment;
        }

        return isBoot ? PartitionRole.Windows : PartitionRole.Data;
    }

    private static DiskBusType ParseBusType(string? value) =>
        Enum.TryParse<DiskBusType>(value?.Replace(" ", string.Empty, StringComparison.Ordinal), true, out var parsed)
            ? parsed
            : DiskBusType.Unknown;

    private static Guid ParseGuid(string? value) => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static bool GetBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
