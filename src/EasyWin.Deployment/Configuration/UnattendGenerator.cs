using System.Text;
using System.Xml;
using System.Xml.Linq;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Configuration;

public sealed class UnattendGenerator
{
    private static readonly XNamespace Unattend = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    public string Generate(UnattendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string language = ValidateToken(options.Language, "language");
        string locale = ValidateToken(options.Locale, "locale");
        string timeZone = ValidateText(options.TimeZone, 128, "time zone");
        string computer = ValidateComputerName(options.ComputerName);
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Unattend + "unattend",
                new XAttribute(XNamespace.Xmlns + "wcm", Wcm),
                Settings("specialize",
                    Component("Microsoft-Windows-Deployment", new XElement(Unattend + "RunSynchronous",
                        new XElement(Unattend + "RunSynchronousCommand", new XAttribute(Wcm + "action", "add"),
                            Element("Order", "1"), Element("Description", "Register EasyWin recovery bootstrap"),
                            Element("Path", PostInstall.PostInstallBootstrap.RegisterCommand), Element("WillReboot", "Never")))),
                    Component("Microsoft-Windows-International-Core",
                        Element("InputLocale", locale), Element("SystemLocale", locale), Element("UILanguage", language), Element("UserLocale", locale)),
                    Component("Microsoft-Windows-Shell-Setup", Element("ComputerName", computer), Element("TimeZone", timeZone))),
                Settings("oobeSystem",
                    Component("Microsoft-Windows-International-Core",
                        Element("InputLocale", locale), Element("SystemLocale", locale), Element("UILanguage", language), Element("UserLocale", locale)),
                    Component("Microsoft-Windows-Shell-Setup",
                        new XElement(Unattend + "OOBE",
                            Element("HideEULAPage", options.HideEulaPage ? "true" : "false"),
                            Element("HideWirelessSetupInOOBE", options.SkipWirelessSetup ? "true" : "false"),
                            Element("ProtectYourPC", "1"))))));
        using var writer = new Utf8StringWriter();
        document.Save(writer, SaveOptions.None);
        return writer.ToString();
    }

    public async Task WriteAsync(string path, UnattendOptions options, CancellationToken cancellationToken = default)
    {
        string full = DeploymentGuard.AbsolutePath(path, "unattend output");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, Generate(options), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static XElement Settings(string pass, params XElement[] components) => new(Unattend + "settings", new XAttribute("pass", pass), components);
    private static XElement Component(string name, params XElement[] children) => new(Unattend + "component", new XAttribute("name", name), new XAttribute("processorArchitecture", "amd64"), new XAttribute("publicKeyToken", "31bf3856ad364e35"), new XAttribute("language", "neutral"), new XAttribute("versionScope", "nonSxS"), children);
    private static XElement Element(string name, string value) => new(Unattend + name, value);
    private static string ValidateToken(string value, string name) => System.Text.RegularExpressions.Regex.IsMatch(value ?? string.Empty, "^[A-Za-z0-9-]{2,20}$") ? value! : throw new DeploymentSafetyException("unattend.token.invalid", $"Invalid {name}.");
    private static string ValidateText(string value, int max, string name) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl) ? value : throw new DeploymentSafetyException("unattend.text.invalid", $"Invalid {name}.");
    private static string ValidateComputerName(string value) => System.Text.RegularExpressions.Regex.IsMatch(value ?? string.Empty, "^[A-Za-z0-9][A-Za-z0-9-]{0,14}$") ? value! : throw new DeploymentSafetyException("unattend.computer.invalid", "Computer name must be a valid NetBIOS name.");

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
