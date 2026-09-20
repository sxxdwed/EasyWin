using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using EasyWin.Core.Localization;

namespace EasyWin.Desktop.Localization;

public sealed class LocalizedText : INotifyPropertyChanged
{
    public static LocalizedText Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => DeploymentStrings.Get(key);
    public void SetLanguage(string language)
    {
        DeploymentStrings.SetLanguage(language);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding("[" + key + "]") { Source = LocalizedText.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
