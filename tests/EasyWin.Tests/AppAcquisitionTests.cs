using System.Net;
using EasyWin.Core.Models;
using EasyWin.Deployment.PostInstall;

namespace EasyWin.Tests;

public sealed class AppAcquisitionTests
{
    private sealed class Signature(bool valid) : IAuthenticodeVerifier
    {
        public int Calls { get; private set; }
        public Task VerifyAsync(string file, string publisher, CancellationToken token)
        {
            Calls++;
            Assert.True(File.Exists(file));
            Assert.False(string.IsNullOrWhiteSpace(publisher));
            if (!valid) throw new InvalidDataException("signature rejected by test verifier");
            return Task.CompletedTask;
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(response(request)); }
    }
    private static ApplicationPackage App => new() { Id = "steam", Name = "Steam", Installer = "Apps/Steam/SteamSetup.exe" };

    [Fact]
    public async Task Apps_SelectedPackageDownloadedBeforeConfirmation()
    {
        var signature = new Signature(true);
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var client = new HttpClient(handler);
        string root = TestData.NewDirectory();
        var package = await new VerifiedAppAcquisition(client, signature).AcquireAsync(App, root, default);
        Assert.Equal(3, package.SizeBytes);
        Assert.Equal(64, package.Sha256.Length);
        Assert.Equal(1, signature.Calls);
        Assert.True(File.Exists(Path.Combine(root, App.Installer)));
        Assert.Empty(Directory.EnumerateFiles(root, "*.download.exe", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task Apps_InvalidSignature_FailsPreflight()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        using var client = new HttpClient(handler); string root = TestData.NewDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedAppAcquisition(client, new Signature(false)).AcquireAsync(App, root, default));
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task Apps_DownloadFailure_FailsBeforeConfirmation()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler); string root = TestData.NewDirectory();
        await Assert.ThrowsAsync<HttpRequestException>(() => new VerifiedAppAcquisition(client, new Signature(true)).AcquireAsync(App, root, default));
        Assert.Equal(3, handler.Calls);
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task Apps_NoSelection_DoesNotRequireInternet()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Network must not be used"));
        using var client = new HttpClient(handler); var signature = new Signature(true);
        var prepared = await new PreparedApplications(new(client, signature), signature).PrepareAsync([], TestData.FindConfigRoot(), TestData.NewDirectory(), TestData.NewDirectory(), default);
        Assert.Empty(prepared.Applications); Assert.Equal(0, handler.Calls); Assert.Equal(0, signature.Calls);
    }
    [Theory]
    [InlineData("change")]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task Apps_PreparedConfigurationTampering_FailsBeforeConfirmation(string mutation)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Network must not be used"));
        using var client = new HttpClient(handler);
        var signature = new Signature(true);
        var prepared = await new PreparedApplications(new(client, signature), signature).PrepareAsync(
            [], TestData.FindConfigRoot(), TestData.NewDirectory(), TestData.NewDirectory(), default);
        await prepared.ValidateAsync([], default);
        string profile = Path.Combine(prepared.ConfigurationRoot, "profiles", "standard.json");
        if (mutation == "change") await File.AppendAllTextAsync(profile, " ");
        else if (mutation == "remove") File.Delete(profile);
        else await File.WriteAllTextAsync(Path.Combine(prepared.PayloadRoot, "unexpected.bin"), "unexpected");
        await Assert.ThrowsAsync<InvalidDataException>(() => prepared.ValidateAsync([], default));
    }

    [Theory]
    [InlineData("http://cdn.fastly.steamstatic.com/installer.exe")]
    [InlineData("https://untrusted.example/installer.exe")]
    public async Task Apps_RedirectOutsideTrustChain_Rejected(string redirect)
    {
        using var handler = new Handler(_ => { var result = new HttpResponseMessage(HttpStatusCode.Redirect); result.Headers.Location = new(redirect); return result; });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedAppAcquisition(client, new Signature(true)).AcquireAsync(App, TestData.NewDirectory(), default));
        Assert.Equal(1, handler.Calls);
    }
}
