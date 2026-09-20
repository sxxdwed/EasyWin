using System.IO.Compression;
using EasyWin.Core.Localization;

namespace EasyWin.Deployment.PostInstall;

public static class AppContainerPreparation
{
    public static async Task ExtractSingleExecutableAsync(string archive, long limit, CancellationToken token)
    {
        string extracted = archive + ".unpacked.exe";
        try
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToArray();
                // MSI's official ZIP currently contains exactly one self-contained signed EXE.
                // Do not silently drop extra payloads or extract arbitrary paths if packaging changes.
                if (files.Length != 1 || !files[0].Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    files[0].Length <= 0 || files[0].Length > limit)
                    throw new InvalidDataException(DeploymentStrings.Get("PackageDownloadInvalid"));
                await using var input = files[0].Open();
                await using var output = new FileStream(extracted, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920]; long length = 0; int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    length = checked(length + read);
                    if (length > limit) throw new InvalidDataException(DeploymentStrings.Get("PackageDownloadInvalid"));
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (length != files[0].Length) throw new InvalidDataException(DeploymentStrings.Get("PackageDownloadInvalid"));
            }
            File.Move(extracted, archive, true);
        }
        finally { if (File.Exists(extracted)) File.Delete(extracted); }
    }
}
