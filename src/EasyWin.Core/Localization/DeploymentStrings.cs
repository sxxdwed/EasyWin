using System.Globalization;
using System.Resources;

namespace EasyWin.Core.Localization;

public static class DeploymentStrings
{
    private static readonly ResourceManager Resources = new("EasyWin.Core.Resources.Strings", typeof(DeploymentStrings).Assembly);
    private static readonly Lazy<Dictionary<string, string>> TextKeys = new(() =>
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string locale in new[] { "en-US", "ru-RU" })
            foreach (System.Collections.DictionaryEntry entry in Resources.GetResourceSet(CultureInfo.GetCultureInfo(locale), true, true)!)
                if (entry.Value is string text) result.TryAdd(text, (string)entry.Key);
        return result;
    });
    public static string Translate(string text) => TextKeys.Value.TryGetValue(text, out var key) ? Get(key) : text;
    public static string Format(string key, params object?[] values) => string.Format(CultureInfo.CurrentCulture, Get(key), values);
    public static string Get(string key, string? language = null)
    {
        language ??= CultureInfo.CurrentUICulture.Name;
        var culture = CultureInfo.GetCultureInfo(language == "ru-RU" ? "ru-RU" : "en-US");
        string? text = Resources.GetString(key, culture);
        return string.IsNullOrWhiteSpace(text) ? Resources.GetString("GenericFailure", CultureInfo.InvariantCulture)! : text;
    }

    public static void SetLanguage(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language == "ru-RU" ? "ru-RU" : "en-US");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
