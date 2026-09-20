using System.Text;
using System.Text.Json;
using EasyWin.Core.Localization;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Core.Security;

namespace EasyWin.Deployment.PostInstall;

public sealed record AppDownloadSource(Uri Url, string Publisher, IReadOnlySet<string> Hosts, long MaxBytes, string? ExpectedSha256 = null);

public interface IAuthenticodeVerifier
{
    Task VerifyAsync(string file, string publisher, CancellationToken token);
}

public sealed class AuthenticodeVerifier(IProcessRunner runner) : IAuthenticodeVerifier
{
    public async Task VerifyAsync(string file, string publisher, CancellationToken token)
    {
        string path64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(file)));
        string script = "$ErrorActionPreference='Stop'; Import-Module (Join-Path $PSHOME 'Modules\\Microsoft.PowerShell.Security\\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop; $p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + path64 + "')); " +
            "$s=Get-AuthenticodeSignature -LiteralPath $p; [pscustomobject]@{Status=[int]$s.Status; Publisher=if($s.SignerCertificate){$s.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false)}else{''}} | ConvertTo-Json -Compress";
        var result = await runner.RunAsync(new CommandSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], timeout: TimeSpan.FromMinutes(2)), token).ConfigureAwait(false);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
            throw new InvalidDataException(DeploymentStrings.Get("PackageSignatureInvalid") + "\n" + result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        if (!result.Succeeded || json.RootElement.GetProperty("Status").GetInt32() != 0 ||
            !string.Equals(json.RootElement.GetProperty("Publisher").GetString(), publisher, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(DeploymentStrings.Get("PackageSignatureInvalid"));
    }
}

public sealed class VerifiedAppAcquisition(HttpClient http, IAuthenticodeVerifier signatures)
{
    // Exact official download endpoints/redirect hosts, not arbitrary catalog-supplied mirrors.
    public static AppDownloadSource? Source(string id) => id switch
    {
        "steam" => new(new("https://cdn.fastly.steamstatic.com/client/installer/SteamSetup.exe"), "Valve Corp.", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cdn.fastly.steamstatic.com" }, 64L << 20),
        "discord" => new(new("https://discord.com/api/downloads/distributions/app/installers/latest?arch=x64&channel=stable&platform=win"), "Discord Inc.", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "discord.com", "stable.dl2.discordapp.net", "dl.discordapp.net" }, 256L << 20),
        _ => null,
    };

    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<ApplicationPackage> AcquireAsync(ApplicationPackage app, string root, CancellationToken token)
    {
        var source = Source(app.Id) ?? throw new InvalidDataException(DeploymentStrings.Get("PackageSourceMissing"));
        string destination = PathValidator.ResolveUnderRoot(root, app.Installer);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        for (int attempt = 0; ; attempt++)
        {
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download.exe";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                Uri address = source.Url;
                for (int redirects = 0; ; redirects++)
                {
                    ValidateAddress(address, source);
                    using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    // Also reject handlers that silently followed an untrusted redirect.
                    if (response.RequestMessage?.RequestUri is Uri actual) ValidateAddress(actual, source);
                    if ((int)response.StatusCode is >= 300 and < 400)
                    {
                        if (redirects >= 5 || response.Headers.Location is null) throw new InvalidDataException(DeploymentStrings.Get("PackageDownloadInvalid"));
                        address = new Uri(address, response.Headers.Location);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > source.MaxBytes) throw new InvalidDataException(DeploymentStrings.Get("PackageDownloadInvalid"));
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                    {
                        byte[] buffer = new byte[81920]; long bytes = 0; int count;
                        while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                        {
                            bytes = checked(bytes + count);
                            if (bytes > source.MaxBytes) throw new InvalidDataException(DeploymentStrings.Get("PackageDownloadInvalid"));
                            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                        }
                        if (bytes == 0) throw new InvalidDataException(DeploymentStrings.Get("PackageDownloadInvalid"));
                    }
                    break;
                }
                await signatures.VerifyAsync(temporary, source.Publisher, timeout.Token).ConfigureAwait(false);
                string hash = await new Sha256HashService().ComputeSha256Async(temporary, timeout.Token).ConfigureAwait(false);
                if (source.ExpectedSha256 is not null && !hash.Equals(source.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(DeploymentStrings.Get("PackageDownloadInvalid"));
                long length = new FileInfo(temporary).Length;
                File.Move(temporary, destination, true);
                return app with { Sha256 = hash, SizeBytes = length };
            }
            catch (HttpRequestException) when (attempt < 2)
            { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt < 2)
            { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token).ConfigureAwait(false); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static void ValidateAddress(Uri address, AppDownloadSource source)
    {
        if (address.Scheme != Uri.UriSchemeHttps || address.Port != 443 || address.UserInfo.Length != 0 || !source.Hosts.Contains(address.IdnHost))
            throw new InvalidDataException(DeploymentStrings.Get("PackageDownloadInvalid"));
    }
}
