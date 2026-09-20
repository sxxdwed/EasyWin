using EasyWin.Core.Localization;
using EasyWin.Core.Processes;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Environment;

public static class DeploymentPrerequisiteChecks
{
    private static readonly string[] ReservedLetters = ["S", "W", "R", "T"];
    public static void ValidateDriveLetters(IEnumerable<string> occupied)
    {
        if (occupied.Any(letter => ReservedLetters.Contains(letter.TrimEnd(':', '\\').ToUpperInvariant())))
            throw new DeploymentSafetyException("preflight.letters", DeploymentStrings.Get("DriveLetterCollision"));
    }

    public static void ValidateVersions(Version image, Version winPe, Version dism)
    {
        if (image.Major != 10 || winPe.Major != 10 || dism.Major != 10 ||
            winPe.Build < 22000 || winPe.Build < image.Build || dism.Build < image.Build || dism.Build < winPe.Build)
            throw new DeploymentSafetyException("preflight.versions", DeploymentStrings.Get("ToolVersionsInvalid"));
    }

    // Probe capability on an exported copy. Never add entries or bootsequence to the live store during preflight.
    public static async Task ProbeBcdAsync(IProcessRunner runner, string work, CancellationToken token)
    {
        string directory = Path.Combine(Path.GetFullPath(work), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string store = Path.Combine(directory, "probe.bcd");
        async Task Run(params string[] args)
        {
            var result = await runner.RunAsync(new CommandSpec("bcdedit.exe", args, requiresElevation: true, timeout: TimeSpan.FromMinutes(1)), token).ConfigureAwait(false);
            if (!result.Succeeded) throw new DeploymentSafetyException("preflight.bcd", DeploymentStrings.Get("BcdProbeFailed"));
        }
        await Run("/enum", "{bootmgr}").ConfigureAwait(false);
        await Run("/export", store).ConfigureAwait(false);
        if (!File.Exists(store)) throw new DeploymentSafetyException("preflight.bcd", DeploymentStrings.Get("BcdProbeFailed"));
        var options = await runner.RunAsync(new CommandSpec("bcdedit.exe", ["/store", store, "/enum", "{ramdiskoptions}"], requiresElevation: true), token).ConfigureAwait(false);
        if (!options.Succeeded) await Run("/store", store, "/create", "{ramdiskoptions}", "/d", "EasyWin probe").ConfigureAwait(false);
        await Run("/store", store, "/enum", "{ramdiskoptions}").ConfigureAwait(false);
        string id = "{" + Guid.NewGuid().ToString() + "}";
        await Run("/store", store, "/create", id, "/d", "EasyWin probe", "/application", "osloader").ConfigureAwait(false);
        await Run("/store", store, "/bootsequence", id).ConfigureAwait(false);
        await Run("/store", store, "/enum", "{bootmgr}").ConfigureAwait(false);
    }

    public static async Task ProbeWritableAsync(string root, CancellationToken token)
    {
        string file = Path.Combine(Path.GetFullPath(root), ".easywin-write-probe-" + Guid.NewGuid().ToString("N"));
        byte[] data = Guid.NewGuid().ToByteArray();
        try
        {
            await using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await stream.WriteAsync(data, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
            if (!(await File.ReadAllBytesAsync(file, token).ConfigureAwait(false)).SequenceEqual(data))
                throw new IOException(DeploymentStrings.Get("StorageChanged"));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
