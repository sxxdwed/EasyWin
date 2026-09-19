using EasyWin.Core.Manifests;
using EasyWin.Core.Models;
using EasyWin.Core.Security;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Staging;

public sealed record StagingBuildRequest(
    ReinstallPlan Plan,
    StagingPartitionIdentity Partition,
    string WindowsImagePath,
    string WinPeMediaRoot,
    string PostInstallPayloadRoot,
    string ConfigurationRoot,
    Guid? BootEntryId = null,
    string? BcdBackupPath = null,
    string? PayloadRoot = null);

public interface IStagingService
{
    Task<(DeploymentManifest Manifest, string ManifestPath)> BuildAsync(StagingBuildRequest request, CancellationToken cancellationToken = default);
}

public sealed class StagingService(IHashService hashes, IManifestService manifests) : IStagingService
{
    public async Task<(DeploymentManifest Manifest, string ManifestPath)> BuildAsync(StagingBuildRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = DeploymentGuard.AbsolutePath(request.Partition.RootPath, "Staging root");
        Directory.CreateDirectory(root);
        string imageSource = DeploymentGuard.AbsolutePath(request.WindowsImagePath, "Windows image", true);
        string imageRelative = Path.Combine("Images", Path.GetFileName(imageSource)).Replace('\\', '/');
        await CopyFileAsync(imageSource, Path.Combine(root, imageRelative), cancellationToken).ConfigureAwait(false);
        CopyDirectory(DeploymentGuard.AbsolutePath(request.WinPeMediaRoot, "WinPE media", true), Path.Combine(root, "WinPE"));
        CopyDirectory(DeploymentGuard.AbsolutePath(request.PostInstallPayloadRoot, "PostInstall payload", true), Path.Combine(root, "PostInstall"));
        CopyDirectory(DeploymentGuard.AbsolutePath(request.ConfigurationRoot, "Configuration root", true), Path.Combine(root, "Config"));
        string? payloadRoot = string.IsNullOrWhiteSpace(request.PayloadRoot)
            ? Path.GetDirectoryName(Path.GetFullPath(request.ConfigurationRoot))
            : DeploymentGuard.AbsolutePath(request.PayloadRoot, "Package payload root", true);
        if (payloadRoot is not null)
        {
            CopyDirectoryIfPresent(Path.Combine(payloadRoot, "Apps"), Path.Combine(root, "Apps"));
            CopyDirectoryIfPresent(Path.Combine(payloadRoot, "Drivers"), Path.Combine(root, "Drivers"));
        }

        var inventory = new List<ManifestFileEntry>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var info = new FileInfo(file);
            inventory.Add(new ManifestFileEntry
            {
                RelativePath = relative,
                LengthBytes = info.Length,
                Sha256 = await hashes.ComputeSha256Async(file, cancellationToken).ConfigureAwait(false),
                Kind = Kind(relative),
                Required = true,
            });
        }

        ManifestFileEntry imageEntry = inventory.Single(item => item.RelativePath.Equals(imageRelative, StringComparison.OrdinalIgnoreCase));
        DateTimeOffset stagedAt = DateTimeOffset.UtcNow;
        DeploymentStage[] initialStages =
        [
            DeploymentStage.ValidateEnvironment,
            DeploymentStage.ValidateImage,
            DeploymentStage.ValidateTargetDisk,
            DeploymentStage.PrepareStaging,
        ];
        var manifest = new DeploymentManifest
        {
            PlanId = request.Plan.PlanId,
            ExecutionMode = request.Plan.ExecutionMode,
            TargetDisk = request.Plan.TargetDisk,
            DataLossAcknowledged = request.Plan.DataLossAcknowledged,
            FinalConfirmationAccepted = request.Plan.FinalConfirmationAccepted,
            AdditionalDisksToErase = request.Plan.AdditionalDisksToErase,
            StagingPartition = request.Partition,
            Image = new DeploymentImageReference
            {
                RelativePath = imageRelative,
                ImageIndex = request.Plan.Image.ImageIndex,
                Name = request.Plan.Image.Name,
                EditionId = request.Plan.Image.EditionId,
                Architecture = request.Plan.Image.Architecture,
                Container = request.Plan.Image.Container,
                Sha256 = imageEntry.Sha256,
                LengthBytes = imageEntry.LengthBytes,
            },
            Boot = new BootConfiguration
            {
                BootEntryId = request.BootEntryId,
                OneTimeBootConfigured = request.BootEntryId.HasValue,
                BcdBackupRelativePath = request.BcdBackupPath is null ? "Boot/bcd.backup" : Path.GetRelativePath(root, request.BcdBackupPath).Replace('\\', '/'),
            },
            ProfileId = request.Plan.ProfileId,
            ApplicationIds = request.Plan.ApplicationIds,
            DriverSelectionMode = request.Plan.DriverSelectionMode,
            DriverIds = request.Plan.DriverIds,
            Language = request.Plan.Language,
            TimeZone = request.Plan.TimeZone,
            ComputerName = request.Plan.ComputerName,
            FileInventory = inventory.OrderBy(static item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            State = new DeploymentState
            {
                CurrentStage = DeploymentStage.PrepareBoot,
                CompletedStages = initialStages,
                Checkpoints = initialStages.ToDictionary(static stage => stage.ToString(), _ => stagedAt, StringComparer.OrdinalIgnoreCase),
                LastSuccessfulStage = DeploymentStage.PrepareStaging,
                UpdatedAtUtc = stagedAt,
            },
        };
        string path = Path.Combine(root, "manifest.json");
        await manifests.SaveAsync(manifest, path, cancellationToken).ConfigureAwait(false);
        return (manifests.Seal(manifest), path);
    }

    private static ManifestFileKind Kind(string relative) => relative.StartsWith("Images/", StringComparison.OrdinalIgnoreCase) ? ManifestFileKind.WindowsImage : relative.EndsWith("boot.wim", StringComparison.OrdinalIgnoreCase) ? ManifestFileKind.WinPeImage : relative.EndsWith("boot.sdi", StringComparison.OrdinalIgnoreCase) ? ManifestFileKind.WinPeSdi : relative.StartsWith("Config/", StringComparison.OrdinalIgnoreCase) ? ManifestFileKind.Configuration : ManifestFileKind.EasyWinBinary;

    private static async Task CopyFileAsync(string source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void CopyDirectoryIfPresent(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            CopyDirectory(source, destination);
        }
    }
}
