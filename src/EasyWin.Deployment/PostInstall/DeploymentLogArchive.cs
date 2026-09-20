using EasyWin.Core.Security;

namespace EasyWin.Deployment.PostInstall;

public static class DeploymentLogArchive
{
    public static string Preserve(string stagingRoot, string workDirectory, Guid planId)
    {
        if (planId == Guid.Empty) throw new ArgumentException("A deployment plan id is required.", nameof(planId));
        string destination = PathValidator.ResolveUnderRoot(workDirectory, $"Logs/{planId:N}/Deployment");
        string source = PathValidator.ResolveUnderRoot(stagingRoot, "Logs");
        string sourcePrefix = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Archived logs must be outside deployment storage.");
        if (!Directory.Exists(source)) return destination;
        CopyDirectory(source);
        return destination;

        void CopyDirectory(string directory)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                string relative = Path.GetRelativePath(source, entry);
                _ = PathValidator.ResolveUnderRoot(source, relative, true);
                if (Directory.Exists(entry)) { CopyDirectory(entry); continue; }
                string saved = PathValidator.ResolveUnderRoot(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.Copy(entry, saved, true);
            }
        }
    }
}
