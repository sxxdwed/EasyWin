using System.Xml.Linq;

namespace EasyWin.Deployment.PostInstall;

public static class PostInstallBootstrap
{
    public const string TaskName = "EasyWin-PostInstall";
    public const string RegisterCommand = "cmd.exe /c C:\\ProgramData\\EasyWin\\RegisterPostInstall.cmd";
    public static string TaskXml()
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object value) => new(ns + name, value);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("Triggers", new[] { E("BootTrigger", new[] { E("Enabled", "true"), E("Delay", "PT1M") }), E("LogonTrigger", E("Enabled", "true")) }),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "System"), E("UserId", "S-1-5-18"), E("LogonType", "ServiceAccount"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"), E("StartWhenAvailable", "true"), E("ExecutionTimeLimit", "PT0S"),
                E("RestartOnFailure", new[] { E("Interval", "PT1M"), E("Count", "30") }) }),
            new XElement(ns + "Actions", new XAttribute("Context", "System"), E("Exec", new[] {
                E("Command", "C:\\ProgramData\\EasyWin\\EasyWin.PostInstall.exe"),
                E("Arguments", "--manifest C:\\ProgramData\\EasyWin\\manifest.json --defer-until-setup-complete") })))).ToString();
    }

    public static string RegistrationScript => "@echo off\r\nschtasks.exe /Create /TN \"" + TaskName + "\" /XML \"C:\\ProgramData\\EasyWin\\PostInstallTask.xml\" /F\r\nexit /b %errorlevel%\r\n";

    public static FileStream AcquireLock(string root)
    {
        Directory.CreateDirectory(root);
        return new FileStream(Path.Combine(root, "postinstall.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
