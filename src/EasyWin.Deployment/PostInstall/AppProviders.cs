using EasyWin.Core.Models;

namespace EasyWin.Deployment.PostInstall;

public static class AppProviders
{
    // Versioned vendor endpoints are deliberately reviewed, not scraped at deployment time.
    // Unsigned 7-Zip is unavailable: do not relax Authenticode for an unpinned download.
    public static AppDownloadSource? Find(string id) => id.ToLowerInvariant() switch
    {
        "steam" => Make("steam", "https://cdn.fastly.steamstatic.com/client/installer/SteamSetup.exe", "Valve Corp.", 64, ["/S"], true),
        "discord" => Make("discord", "https://discord.com/api/downloads/distributions/app/installers/latest?arch=x64&channel=stable&platform=win", "Discord Inc.", 256, ["-s"], true, ["stable.dl2.discordapp.net", "dl.discordapp.net"]),
        "chrome" => Make("chrome", "https://dl.google.com/chrome/install/ChromeStandaloneSetup64.exe", "Google LLC", 512, ["/silent", "/install"]),
        "firefox" => Make("firefox", "https://download.mozilla.org/?product=firefox-latest-ssl&os=win64&lang=ru", "Mozilla Corporation", 256, ["-ms"], false, ["download-installer.cdn.mozilla.net"]),
        "vcredist" => Make("vcredist", "https://aka.ms/vs/17/release/vc_redist.x64.exe", "Microsoft Corporation", 128, ["/install", "/quiet", "/norestart"], false, ["download.visualstudio.microsoft.com"]),
        "nvidia-app" => Make("nvidia-app", "https://us.download.nvidia.com/nvapp/client/11.0.9.251/NVIDIA_app_v11.0.9.251.exe", "NVIDIA Corporation", 512, ["-s"], true),
        "amd-software" => Make("amd-software", "https://drivers.amd.com/drivers/installer/26.10/whql/amd-software-adrenalin-edition-26.8.1-minimalsetup-260818_web.exe", "Advanced Micro Devices", 128, ["-install"], true) with { Referrer = new("https://www.amd.com/") },
        "msi-center" => Make("msi-center", "https://download.msi.com/uti_exe/desktop/MSI-Center.zip", "MICRO-STAR INTERNATIONAL CO., LTD.", 1024, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], true, ["download-2.msi.com"]) with { Container = "zip" },
        "directx" => Make("directx", "https://download.microsoft.com/download/8/4/a/84a35bf1-dafe-4ae8-82af-ad2ae20b6b14/directx_Jun2010_redist.exe", "Microsoft Corporation", 128, ["/silent"]) with { Container = "directx-sfx" },
        _ => null,
    };

    private static AppDownloadSource Make(string id, string url, string publisher, long mib, string[] arguments,
        bool internet = false, string[]? redirects = null)
    {
        var uri = new Uri(url);
        return new(uri, publisher, new HashSet<string>(new[] { uri.IdnHost }.Concat(redirects ?? []), StringComparer.OrdinalIgnoreCase), mib << 20)
        { AppId = id, Arguments = arguments, RequiresInternet = internet, Architecture = ProcessorArchitecture.X64 };
    }
}
