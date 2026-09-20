namespace EasyWin.Tests;

internal static class InstallerTestData
{
    // Real PE structure from the test host; no vendor installer is executed by a unit test.
    public static byte[] Executable() => File.ReadAllBytes(Environment.ProcessPath!);
}
