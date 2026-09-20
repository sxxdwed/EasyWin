using System.Xml.Linq;
using EasyWin.Core.Localization;

namespace EasyWin.Tests;

public sealed class LocalizationTests
{
    private static Dictionary<string, string> Read(string locale)
    {
        string path = Path.Combine(Path.GetDirectoryName(TestData.FindConfigRoot())!, "src", "EasyWin.Core", "Resources", locale == "en-US" ? "Strings.resx" : "Strings.ru-RU.resx");
        return XDocument.Load(path).Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")!.Value);
    }
    [Fact]
    public void Localization_Russian_AllRequiredKeysExist() => Assert.Equal(Read("en-US").Keys.Order(), Read("ru-RU").Keys.Order());
    [Fact]
    public void Localization_English_AllRequiredKeysExist() => Assert.All(Read("en-US"), e => Assert.Equal(e.Value, DeploymentStrings.Get(e.Key, "en-US")));
    [Fact]
    public void Localization_NoEmptyUserFacingStrings()
    {
        Assert.All(Read("en-US").Concat(Read("ru-RU")), e => Assert.False(string.IsNullOrWhiteSpace(e.Value)));
    }
    [Fact]
    public void Localization_NoRawResourceKeysInUI()
    {
        foreach (var pair in Read("en-US"))
        {
            Assert.NotEqual(pair.Key, DeploymentStrings.Get(pair.Key, "en-US"));
            Assert.NotEqual(pair.Key, DeploymentStrings.Get(pair.Key, "ru-RU"));
        }
        Assert.False(string.IsNullOrWhiteSpace(DeploymentStrings.Get("missing-key", "ru-RU")));
    }
    [Theory]
    [InlineData("TargetErased")]
    [InlineData("StoragePreserved")]
    [InlineData("StorageEncrypted")]
    [InlineData("AutoReboot")]
    [InlineData("StaleDeployment")]
    public void SafetyWarnings_ExistInRussianAndEnglish(string key)
    {
        Assert.True(Read("en-US").ContainsKey(key)); Assert.True(Read("ru-RU").ContainsKey(key));
        Assert.NotEqual(DeploymentStrings.Get(key, "en-US"), DeploymentStrings.Get(key, "ru-RU"));
    }
}
