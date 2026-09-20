namespace EasyWin.Deployment.WinPe;

public sealed record PrerequisiteFile(string Name, string Path, bool Found);

public static class WinPePrerequisites
{
    public const string DownloadUrl = "https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install";
    public static IReadOnlyList<string> Components { get; } = Array.AsReadOnly(new[]
        { "WinPE-WMI", "WinPE-NetFX", "WinPE-Scripting", "WinPE-PowerShell", "WinPE-StorageWMI" });

    public static string DefaultRoot => System.Environment.GetEnvironmentVariable("EASYWIN_ADK_WINPE_ROOT")
        ?? System.Environment.GetEnvironmentVariable("EASYWIN_ADK_WINPE_ROOT", EnvironmentVariableTarget.User)
        ?? System.Environment.GetEnvironmentVariable("EASYWIN_ADK_WINPE_ROOT", EnvironmentVariableTarget.Machine)
        ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits", "10", "Assessment and Deployment Kit", "Windows Preinstallation Environment");

    public static IReadOnlyList<PrerequisiteFile> Inspect(string root, string payloadRoot)
    {
        string arch = Path.Combine(Path.GetFullPath(root), "amd64");
        var files = new List<(string Name, string Path)>
        {
            ("WinPE amd64", Path.Combine(arch, "en-us", "winpe.wim")),
            ("WinPE boot.sdi", Path.Combine(arch, "Media", "Boot", "boot.sdi")),
            ("EasyWin.WinPE", Path.Combine(payloadRoot, "EasyWin.WinPE", "EasyWin.WinPE.exe")),
            ("EasyWin.PostInstall", Path.Combine(payloadRoot, "EasyWin.PostInstall", "EasyWin.PostInstall.exe")),
        };
        files.AddRange(Components.Select(component => (component, Path.Combine(arch, "WinPE_OCs", component + ".cab"))));
        return files.Select(item => new PrerequisiteFile(item.Name, item.Path, File.Exists(item.Path))).ToArray();
    }

    public static void Validate(string root, string payloadRoot)
    {
        var missing = Inspect(root, payloadRoot).Where(item => !item.Found).ToArray();
        if (missing.Length != 0)
            throw new FileNotFoundException("Required deployment components missing:\n" + string.Join("\n", missing.Select(item => item.Path)));
    }
}
