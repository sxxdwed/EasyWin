using EasyWin.Core.Catalogs;
using EasyWin.Core.Models;
using EasyWin.Core.Security;
using EasyWin.Core.Serialization;

namespace EasyWin.Deployment.PostInstall;

public sealed record PreparedAppSet(string ConfigurationRoot, string PayloadRoot, IReadOnlyList<ApplicationPackage> Applications, long Bytes)
{
    public IReadOnlyList<ManifestFileEntry> Files { get; init; } = [];

    public async Task ValidateAsync(IReadOnlyList<string> selected, CancellationToken token)
    {
        if (!selected.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(Applications.Select(a => a.Id).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("PackageDownloadInvalid"));
        var hashes = new Sha256HashService();
        var actual = Directory.EnumerateFiles(PayloadRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(PayloadRoot, f).Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase);
        if (Files.Count == 0 || Bytes != Files.Sum(f => f.LengthBytes) ||
            !actual.SequenceEqual(Files.Select(f => f.RelativePath).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("PackageDownloadInvalid"));
        foreach (var file in Files)
        {
            string path = PathValidator.ResolveUnderRoot(PayloadRoot, file.RelativePath, true);
            if (!(await hashes.VerifyFileAsync(path, file.Sha256, file.LengthBytes, token).ConfigureAwait(false)).IsValid)
                throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("PackageDownloadInvalid"));
        }
        foreach (var app in Applications)
        {
            string file = PathValidator.ResolveUnderRoot(PayloadRoot, app.Installer, true);
            var result = await hashes.VerifyFileAsync(file, app.Sha256, app.SizeBytes, token).ConfigureAwait(false);
            if (!result.IsValid) throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("PackageDownloadInvalid"));
        }
    }
}

public sealed class PreparedApplications(VerifiedAppAcquisition downloads, IAuthenticodeVerifier signatures)
{
    public async Task<PreparedAppSet> PrepareAsync(IReadOnlyList<string> selected, string configRoot, string sourcePayload, string work, CancellationToken token)
    {
        var json = new SystemTextJsonSerializer();
        var catalog = await new CatalogService(json).LoadApplicationsAsync(Path.Combine(configRoot, "apps", "catalog.json"), token).ConfigureAwait(false);
        string payload = Path.Combine(work, Guid.NewGuid().ToString("N"));
        string config = Path.Combine(payload, "config");
        Directory.CreateDirectory(config);
        foreach (string file in Directory.EnumerateFiles(configRoot, "*.json", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(configRoot, file);
            _ = PathValidator.ResolveUnderRoot(configRoot, relative, true);
            string destination = PathValidator.ResolveUnderRoot(config, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        var ready = new List<ApplicationPackage>();
        foreach (string id in selected.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var app = catalog.Applications.Single(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (VerifiedAppAcquisition.Source(app.Id) is not null)
            {
                ready.Add(await downloads.AcquireAsync(app, payload, token).ConfigureAwait(false));
                continue;
            }
            string source = PathValidator.ResolveUnderRoot(sourcePayload, app.Installer, true);
            string publisher = app.Id switch {
                "chrome" => "Google LLC", "firefox" => "Mozilla Corporation", "vcredist" or "directx" => "Microsoft Corporation",
                "nvidia-app" => "NVIDIA Corporation", "amd-software" => "Advanced Micro Devices, Inc.", "msi-center" => "MICRO-STAR INTERNATIONAL CO., LTD.",
                _ => throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("PackageSourceMissing")) };
            if (!(await new Sha256HashService().VerifyFileAsync(source, app.Sha256, app.SizeBytes, token).ConfigureAwait(false)).IsValid)
                throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("PackageDownloadInvalid"));
            await signatures.VerifyAsync(source, publisher, token).ConfigureAwait(false);
            // Preserve companion files required by offline installers, confined to their package directory.
            string sourceDirectory = Path.GetDirectoryName(source)!;
            string targetDirectory = Path.GetDirectoryName(PathValidator.ResolveUnderRoot(payload, app.Installer))!;
            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDirectory, file);
                _ = PathValidator.ResolveUnderRoot(sourceDirectory, relative, true);
                string destination = PathValidator.ResolveUnderRoot(targetDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            ready.Add(app);
        }
        await json.SerializeToFileAsync(new ApplicationCatalog(catalog.Version, ready), Path.Combine(config, "apps", "catalog.json"), token).ConfigureAwait(false);
        var inventory = new List<ManifestFileEntry>();
        var hashes = new Sha256HashService();
        foreach (string file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(payload, file).Replace('\\', '/');
            _ = PathValidator.ResolveUnderRoot(payload, relative, true);
            inventory.Add(new ManifestFileEntry { RelativePath = relative, LengthBytes = new FileInfo(file).Length,
                Sha256 = await hashes.ComputeSha256Async(file, token).ConfigureAwait(false) });
        }
        long bytes = inventory.Sum(f => f.LengthBytes);
        var result = new PreparedAppSet(config, payload, ready, bytes) { Files = inventory.ToArray() };
        await result.ValidateAsync(selected, token).ConfigureAwait(false);
        return result;
    }
}
