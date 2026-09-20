using System.Reflection;
using System.Text.RegularExpressions;
using EasyWin.Core.Localization;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;

namespace EasyWin.Tests;

public sealed class SourcePolicyTests
{
    private static IEnumerable<string> Sources()
    {
        string root = Path.GetDirectoryName(TestData.FindConfigRoot())!;
        foreach (string project in new[] { "EasyWin.Desktop", "EasyWin.WinPE", "EasyWin.PostInstall" })
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", project), "*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal)) && !f.Contains("\\obj\\", StringComparison.Ordinal) && !f.Contains("\\bin\\", StringComparison.Ordinal)))
                yield return file;
    }
    [Fact]
    public void Localization_NoHardcodedRussianUserFacingStrings()
    {
        foreach (string file in Sources()) Assert.False(Regex.IsMatch(File.ReadAllText(file), "[А-Яа-яЁё]"), file);
    }
    [Fact]
    public void Localization_NoHardcodedEnglishUserFacingStrings()
    {
        // Entry-point exception sinks; this is not a proof of whole-program data-flow localization.
        foreach (string file in Sources()) Assert.False(Regex.IsMatch(File.ReadAllText(file), "(?:throw\\s+new\\s+\\w+Exception|ThrowIfFailed)\\(\\s*\\$?\"[A-Za-z][^\"\\r\\n]*\\s[A-Za-z]"), file);
    }
    [Fact]
    public void Localization_AppCategories_RuEn()
    {
        foreach (string key in new[] { "CategoryGeneral", "CategoryEssentials", "CategoryGaming", "CategoryComponents", "CategoryVendor" })
        {
            Assert.NotEqual(DeploymentStrings.Get(key, "ru-RU"), DeploymentStrings.Get(key, "en-US"));
            Assert.NotEqual(key, DeploymentStrings.Get(key, "en-US"));
        }
    }
    [Fact]
    public async Task Release_CommitShaMatchesBuildSource()
    {
        var assembly = typeof(DeploymentManifest).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        string root = Path.GetDirectoryName(TestData.FindConfigRoot())!;
        var git = await new ProcessRunner().RunAsync(new("git", ["rev-parse", "HEAD"], workingDirectory: root));
        Assert.True(git.Succeeded);
        Assert.Equal(git.StandardOutput.Trim(), metadata.Single(m => m.Key == "GitCommit").Value);
        Assert.True(DateTimeOffset.TryParse(metadata.Single(m => m.Key == "BuildTimestampUtc").Value, out _));
    }
}
