using EasyWin.Core.Models;
using EasyWin.Core.Security;
using EasyWin.Core.Serialization;

namespace EasyWin.Core.Catalogs;

public sealed record ApplicationCatalog(int Version, IReadOnlyList<ApplicationPackage> Applications);
public sealed record DriverCatalog(int Version, IReadOnlyList<DriverPackage> Packages);

public interface ICatalogService
{
    Task<InstallationProfile> LoadProfileAsync(string path, CancellationToken cancellationToken = default);
    Task<ApplicationCatalog> LoadApplicationsAsync(string path, CancellationToken cancellationToken = default);
    Task<DriverCatalog> LoadDriversAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class CatalogService(IJsonSerializer json) : ICatalogService
{
    public async Task<InstallationProfile> LoadProfileAsync(string path, CancellationToken cancellationToken = default)
    {
        var value = await json.DeserializeFileAsync<InstallationProfile>(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.DisplayName))
        {
            throw new InvalidDataException("Profile id and display name are required.");
        }

        if (!value.PreserveWindowsUpdate || !value.PreserveDefender || !value.PreserveMicrosoftStore || !value.PreserveRecovery || !value.PreserveServicingStack)
        {
            throw new InvalidDataException("EasyWin profiles may not disable protected Windows subsystems.");
        }

        if (value.RemoveProvisionedAppPackages.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.RemoveProvisionedAppPackages.Count ||
            value.RemoveProvisionedAppPackages.Any(package => !ProfileSafetyPolicy.RemovableProvisionedAppPackages.Contains(package)))
        {
            throw new InvalidDataException("The profile contains a duplicate or unsafe provisioned application removal.");
        }

        return value;
    }

    public async Task<ApplicationCatalog> LoadApplicationsAsync(string path, CancellationToken cancellationToken = default)
    {
        var value = await json.DeserializeFileAsync<ApplicationCatalog>(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
        EnsureUnique(value.Applications.Select(static item => item.Id), "application");
        foreach (ApplicationPackage app in value.Applications)
        {
            PathValidator.ValidateRelativePath(app.Installer);
            ValidateHashOrPlaceholder(app.Sha256, app.Id);
            if (app.SuccessExitCodes.Count == 0)
            {
                throw new InvalidDataException($"Application '{app.Id}' has no success exit codes.");
            }

            if (app.RequiredHardwareIdPrefixes.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException($"Application '{app.Id}' contains an empty hardware identifier prefix.");
            }
        }

        return value;
    }

    public async Task<DriverCatalog> LoadDriversAsync(string path, CancellationToken cancellationToken = default)
    {
        var value = await json.DeserializeFileAsync<DriverCatalog>(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
        EnsureUnique(value.Packages.Select(static item => item.Id), "driver");
        foreach (DriverPackage driver in value.Packages)
        {
            PathValidator.ValidateRelativePath(driver.InfPath);
            ValidateHashOrPlaceholder(driver.Sha256, driver.Id);
            if (driver.HardwareIds.Count == 0 && driver.CompatibleIds.Count == 0)
            {
                throw new InvalidDataException($"Driver '{driver.Id}' has no hardware identifiers.");
            }
        }

        return value;
    }

    private static void EnsureUnique(IEnumerable<string> ids, string kind)
    {
        var values = ids.ToArray();
        if (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            throw new InvalidDataException($"The {kind} catalog contains empty or duplicate ids.");
        }
    }

    private static void ValidateHashOrPlaceholder(string hash, string id)
    {
        if (!Sha256HashService.IsValidSha256(hash) && !string.Equals(hash, "REQUIRED_BEFORE_SELECTION", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Package '{id}' has an invalid SHA-256 value.");
        }
    }
}
