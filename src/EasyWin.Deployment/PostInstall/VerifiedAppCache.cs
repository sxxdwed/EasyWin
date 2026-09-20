using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyWin.Core.Localization;
using EasyWin.Core.Models;
using EasyWin.Core.Security;
using EasyWin.Core.Serialization;

namespace EasyWin.Deployment.PostInstall;

public sealed class VerifiedAppCache(VerifiedAppAcquisition downloads, IAuthenticodeVerifier signatures)
{
    public static string DefaultRoot => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "EasyWin", "Cache", "Apps");

    public async Task<ApplicationPackage> GetAsync(ApplicationPackage app, string cacheRoot, string destinationRoot, CancellationToken token)
    {
        var provider = AppProviders.Find(app.Id) ?? throw new InvalidDataException(DeploymentStrings.Get("PackageSourceMissing"));
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider.Url.AbsoluteUri + "|" + provider.Publisher)));
        string folder = PathValidator.ResolveUnderRoot(cacheRoot, key);
        Directory.CreateDirectory(folder);
        string file = PathValidator.ResolveUnderRoot(folder, "installer.exe");
        string receipt = PathValidator.ResolveUnderRoot(folder, "receipt.json");
        // Interprocess exclusion: never consume partially replaced cache data.
        await using var lease = new FileStream(PathValidator.ResolveUnderRoot(folder, "cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        ApplicationPackage? cached = null;
        if (File.Exists(file) && File.Exists(receipt))
        {
            try
            {
                cached = await new SystemTextJsonSerializer().DeserializeFileAsync<ApplicationPackage>(receipt, token).ConfigureAwait(false);
                if (cached is null || cached.Id != app.Id || cached.ExpectedPublisher != provider.Publisher ||
                    !(await new Sha256HashService().VerifyFileAsync(file, cached.Sha256, cached.SizeBytes, token).ConfigureAwait(false)).IsValid ||
                    (provider.ExpectedSha256 is not null && !provider.ExpectedSha256.Equals(cached.Sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException(DeploymentStrings.Get("PackageDownloadInvalid"));
                InstallerFileValidation.ValidateExecutable(file, provider.MaxBytes);
                await signatures.VerifyAsync(file, provider.Publisher, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or IOException or ArgumentException)
            { cached = null; }
        }
        if (cached is null)
        {
            // Only these two exact owned cache files can be discarded; never recurse or touch user payloads.
            if (File.Exists(file)) File.Delete(file);
            if (File.Exists(receipt)) File.Delete(receipt);
            cached = await downloads.AcquireAsync(app with { Installer = "installer.exe" }, folder, token).ConfigureAwait(false);
            await new SystemTextJsonSerializer().SerializeToFileAsync(cached, receipt, token).ConfigureAwait(false);
        }
        string destination = PathValidator.ResolveUnderRoot(destinationRoot, app.Installer);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, false);
        // Recheck the copy, not just the cached source.
        if (!(await new Sha256HashService().VerifyFileAsync(destination, cached.Sha256, cached.SizeBytes, token).ConfigureAwait(false)).IsValid)
            throw new InvalidDataException(DeploymentStrings.Get("PackageDownloadInvalid"));
        return app with { Sha256 = cached.Sha256, SizeBytes = cached.SizeBytes, ExpectedPublisher = provider.Publisher,
            Arguments = provider.Arguments, RequiresInternet = provider.RequiresInternet,
            SuccessExitCodes = app.Id is "nvidia-app" or "amd-software" ? [0, 3010] : app.SuccessExitCodes };
    }
}
