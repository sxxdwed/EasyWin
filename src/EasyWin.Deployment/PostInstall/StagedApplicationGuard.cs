using EasyWin.Core.Localization;
using EasyWin.Core.Models;
using EasyWin.Core.Security;

namespace EasyWin.Deployment.PostInstall;

public static class StagedApplicationGuard
{
    public static async Task ValidateCoreFilesAsync(DeploymentManifest manifest, string root, CancellationToken token)
    {
        // Called only after seal/structure validation. App payload failure is handled per app;
        // configuration, workers, drivers, Windows image and all other required files stay critical.
        foreach (var entry in manifest.FileInventory.Where(e => e.Required && !e.RelativePath.StartsWith("Apps/", StringComparison.OrdinalIgnoreCase)))
            await VerifyAsync(entry, root, token).ConfigureAwait(false);
    }

    public static async Task ValidateAppAsync(DeploymentManifest manifest, ApplicationPackage app, string root, CancellationToken token)
    {
        string relative = app.Installer.Replace('\\', '/');
        PathValidator.ValidateRelativePath(relative);
        if (!relative.StartsWith("Apps/", StringComparison.OrdinalIgnoreCase)) Fail();
        var entry = manifest.FileInventory.SingleOrDefault(e => e.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !entry.Sha256.Equals(app.Sha256, StringComparison.OrdinalIgnoreCase) || entry.LengthBytes != app.SizeBytes) Fail();
        string directory = relative[..(relative.LastIndexOf('/') + 1)];
        foreach (var file in manifest.FileInventory.Where(e => e.RelativePath.StartsWith(directory, StringComparison.OrdinalIgnoreCase)))
            await VerifyAsync(file, root, token).ConfigureAwait(false);
        var actual = Directory.EnumerateFiles(PathValidator.ResolveUnderRoot(root, directory.TrimEnd('/'), true), "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase);
        var expected = manifest.FileInventory.Where(e => e.RelativePath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.RelativePath).Order(StringComparer.OrdinalIgnoreCase);
        if (!actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase)) Fail();
    }

    private static async Task VerifyAsync(ManifestFileEntry entry, string root, CancellationToken token)
    {
        string file = PathValidator.ResolveUnderRoot(root, entry.RelativePath, true);
        if (!(await new Sha256HashService().VerifyFileAsync(file, entry.Sha256, entry.LengthBytes, token).ConfigureAwait(false)).IsValid) Fail();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail() => throw new InvalidDataException(DeploymentStrings.Get("PackageManifestMissing"));
}
