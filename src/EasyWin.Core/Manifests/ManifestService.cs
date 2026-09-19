using System.Security.Cryptography;
using EasyWin.Core.Models;
using EasyWin.Core.Security;
using EasyWin.Core.Serialization;

namespace EasyWin.Core.Manifests;

public interface IManifestService
{
    DeploymentManifest Seal(DeploymentManifest manifest);
    Task SaveAsync(DeploymentManifest manifest, string path, CancellationToken cancellationToken = default);
    Task<DeploymentManifest> LoadAndValidateAsync(string path, bool verifyFiles = true, CancellationToken cancellationToken = default);
    Task ValidateAsync(DeploymentManifest manifest, string stagingRoot, bool verifyFiles = true, CancellationToken cancellationToken = default);
}

public sealed class ManifestService(IJsonSerializer json, IHashService hashes) : IManifestService
{
    public DeploymentManifest Seal(DeploymentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateStructure(manifest);
        byte[] payload = CanonicalPayload(manifest);
        string digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        return manifest with { Integrity = new ManifestIntegrity { PayloadSha256 = digest } };
    }

    public Task SaveAsync(DeploymentManifest manifest, string path, CancellationToken cancellationToken = default) =>
        json.SerializeToFileAsync(Seal(manifest), path, cancellationToken);

    public async Task<DeploymentManifest> LoadAndValidateAsync(string path, bool verifyFiles = true, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        var manifest = await json.DeserializeFileAsync<DeploymentManifest>(fullPath, cancellationToken).ConfigureAwait(false);
        string root = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Manifest has no parent directory.");
        await ValidateAsync(manifest, root, verifyFiles, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    public async Task ValidateAsync(DeploymentManifest manifest, string stagingRoot, bool verifyFiles = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateStructure(manifest);
        string expected = Sha256HashService.NormalizeSha256(manifest.Integrity.PayloadSha256);
        string actual = Convert.ToHexString(SHA256.HashData(CanonicalPayload(manifest))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)))
        {
            throw new InvalidDataException("Manifest integrity hash is invalid.");
        }

        if (!verifyFiles)
        {
            return;
        }

        foreach (ManifestFileEntry entry in manifest.FileInventory.Where(static item => item.Required))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = PathValidator.ResolveUnderRoot(stagingRoot, entry.RelativePath, mustExist: true);
            FileHashVerificationResult result = await hashes.VerifyFileAsync(file, entry.Sha256, entry.LengthBytes, cancellationToken).ConfigureAwait(false);
            if (!result.IsValid)
            {
                throw new InvalidDataException($"Staged file validation failed for '{entry.RelativePath}': {result.Error}");
            }
        }
    }

    private static byte[] CanonicalPayload(DeploymentManifest manifest) =>
        DeterministicJson.SerializeCanonicalUtf8(manifest with { Integrity = new ManifestIntegrity() });

    private static void ValidateStructure(DeploymentManifest manifest)
    {
        if (!string.Equals(manifest.SchemaVersion, DeploymentManifestSchema.CurrentVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported manifest schema '{manifest.SchemaVersion}'.");
        }

        if (manifest.ManifestId == Guid.Empty || manifest.PlanId == Guid.Empty)
        {
            throw new InvalidDataException("Manifest and plan identifiers are required.");
        }

        _ = new Validation.DiskIdentityValidator().Validate(manifest.TargetDisk, manifest.TargetDisk) is { IsValid: true }
            ? true
            : throw new InvalidDataException("Target disk identity is incomplete.");
        if (manifest.AdditionalDisksToErase.Count > 1)
        {
            throw new InvalidDataException("At most one additional disk can be erased in a two-disk operation.");
        }

        if (manifest.AdditionalDisksToErase.Any(disk => disk.DiskNumber == manifest.TargetDisk.DiskNumber) ||
            manifest.AdditionalDisksToErase.Select(static disk => disk.DiskNumber).Distinct().Count() != manifest.AdditionalDisksToErase.Count)
        {
            throw new InvalidDataException("The target disk and additional erase disks must be distinct.");
        }

        foreach (DiskIdentity disk in manifest.AdditionalDisksToErase)
        {
            _ = new Validation.DiskIdentityValidator().Validate(disk, disk) is { IsValid: true }
                ? true
                : throw new InvalidDataException("An additional disk identity is incomplete.");
        }
        if (manifest.StagingPartition.GptPartitionId == Guid.Empty || manifest.StagingPartition.SizeBytes <= 0 || manifest.StagingPartition.OffsetBytes < 0)
        {
            throw new InvalidDataException("Staging partition identity is incomplete.");
        }

        if (manifest.StagingPartition.Disk != manifest.TargetDisk)
        {
            throw new InvalidDataException("Staging and target disk identities differ.");
        }

        PathValidator.ValidateRelativePath(manifest.Image.RelativePath);
        if (manifest.Image.ImageIndex < 1 || manifest.Image.LengthBytes <= 0 || !Sha256HashService.IsValidSha256(manifest.Image.Sha256))
        {
            throw new InvalidDataException("Windows image reference is invalid.");
        }

        var duplicate = manifest.FileInventory.GroupBy(static item => item.RelativePath, StringComparer.OrdinalIgnoreCase).FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate manifest path '{duplicate.Key}'.");
        }

        foreach (ManifestFileEntry entry in manifest.FileInventory)
        {
            PathValidator.ValidateRelativePath(entry.RelativePath);
            if (entry.LengthBytes < 0 || !Sha256HashService.IsValidSha256(entry.Sha256))
            {
                throw new InvalidDataException($"Invalid file inventory entry '{entry.RelativePath}'.");
            }
        }

        ManifestFileEntry? imageEntry = manifest.FileInventory.FirstOrDefault(entry =>
            string.Equals(entry.RelativePath, manifest.Image.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (imageEntry is null || !string.Equals(imageEntry.Sha256, manifest.Image.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Windows image is absent from the file inventory or its hash differs.");
        }
    }
}
