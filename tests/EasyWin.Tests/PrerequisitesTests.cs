using EasyWin.Deployment.WinPe;

namespace EasyWin.Tests;

public sealed class PrerequisitesTests
{
    [Fact]
    public void EmptyAdkDirectoryIsNotSufficient()
    {
        string adk = TestData.NewDirectory(), payload = TestData.NewDirectory();
        var result = WinPePrerequisites.Inspect(adk, payload);
        Assert.All(result, file => Assert.False(file.Found));
        Assert.Throws<FileNotFoundException>(() => WinPePrerequisites.Validate(adk, payload));
    }

    [Fact]
    public void EveryRequiredComponentIsCheckedAndRefreshDetectsRemoval()
    {
        string adk = TestData.NewDirectory(), payload = TestData.NewDirectory();
        var result = WinPePrerequisites.Inspect(adk, payload);
        foreach (var file in result)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            File.WriteAllText(file.Path, "test fixture, not a real Windows component");
        }
        WinPePrerequisites.Validate(adk, payload);
        foreach (var file in result)
        {
            File.Delete(file.Path);
            Assert.Throws<FileNotFoundException>(() => WinPePrerequisites.Validate(adk, payload));
            Assert.False(WinPePrerequisites.Inspect(adk, payload).Single(item => item.Path == file.Path).Found);
            File.WriteAllText(file.Path, "fixture");
        }
    }
}
