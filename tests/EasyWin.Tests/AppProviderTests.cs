using System.IO.Compression;
using System.Net;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Core.Security;
using EasyWin.Deployment.PostInstall;

namespace EasyWin.Tests;

public sealed class AppProviderTests
{
    private sealed class Signature : IAuthenticodeVerifier
    {
        public int Calls { get; private set; }
        public bool Reject { get; set; }
        public Task VerifyAsync(string path, string publisher, CancellationToken token)
        { Calls++; if (Reject) throw new InvalidDataException("Invalid signature"); return Task.CompletedTask; }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(respond(request)); }
    }
    private static ApplicationPackage App(string id) => new() { Id = id, Name = id, Installer = $"Apps/{id}/setup.exe" };
    private static HttpResponseMessage Success() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(InstallerTestData.Executable()) };

    private static async Task Official(string id)
    {
        var source = AppProviders.Find(id)!;
        var signature = new Signature();
        using var handler = new Handler(request =>
        {
            Assert.Equal(source.Url, request.RequestUri);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Contains(request.RequestUri.IdnHost, source.Hosts);
            if (source.Container != "zip") return Success();
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            using (var stream = zip.CreateEntry("MSI Center.exe").Open()) stream.Write(InstallerTestData.Executable());
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(memory.ToArray()) };
        });
        using var client = new HttpClient(handler);
        var package = await new VerifiedAppAcquisition(client, signature).AcquireAsync(App(id), TestData.NewDirectory(), default);
        Assert.Equal(source.Publisher, package.ExpectedPublisher);
        Assert.Equal(1, signature.Calls);
        Assert.Equal(64, package.Sha256.Length);
    }
    [Fact] public Task AppProvider_Steam_DownloadsOfficialInstaller() => Official("steam");
    [Fact] public Task AppProvider_Discord_DownloadsOfficialInstaller() => Official("discord");
    [Fact] public Task AppProvider_Chrome_DownloadsOfficialInstaller() => Official("chrome");
    [Fact] public Task AppProvider_Firefox_DownloadsOfficialInstaller() => Official("firefox");
    [Fact] public Task AppProvider_Nvidia_DownloadsOfficialInstaller() => Official("nvidia-app");
    [Fact] public Task AppProvider_Amd_UsesVerifiedOfficialSource() => Official("amd-software");
    [Fact] public Task AppProvider_MsiCenter_UsesVerifiedOfficialSource() => Official("msi-center");
    [Fact] public Task AppProvider_DirectX_DownloadsOfficialRedistributable() => Official("directx");
    [Fact] public Task AppProvider_VcRuntime_DownloadsOfficialInstaller() => Official("vcredist");
    [Fact] public void AppProvider_7Zip_UnsignedSourceUnavailable() => Assert.Null(AppProviders.Find("7zip"));
    [Fact] public Task PostInstall_AppFailure_DoesNotBreakWindows() => new TwoDiskTests().PostInstall_FullDryRun_PersistsAppResultsAndSecondLaunchDoesNothing(true, false);

    [Fact]
    public async Task AppProvider_UntrustedDomain_Rejected()
    {
        using var handler = new Handler(_ => { var response = Success(); response.RequestMessage = new(HttpMethod.Get, "https://untrusted.example/setup.exe"); return response; });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedAppAcquisition(client, new Signature()).AcquireAsync(App("chrome"), TestData.NewDirectory(), default));
    }

    [Fact]
    public async Task AppProvider_RedirectToUntrustedDomain_Rejected()
    {
        using var handler = new Handler(_ => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://untrusted.example/setup.exe"); return response; });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedAppAcquisition(client, new Signature()).AcquireAsync(App("chrome"), TestData.NewDirectory(), default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Preflight_NoAppsSelected_DoesNotRequireInternet()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("No network expected"));
        using var client = new HttpClient(handler); var signature = new Signature();
        var result = await new PreparedApplications(new(client, signature), signature, TestData.NewDirectory())
            .PrepareAsync([], TestData.FindConfigRoot(), TestData.NewDirectory(), TestData.NewDirectory(), default);
        await result.ValidateAsync([], default);
        Assert.Equal(0, handler.Calls); Assert.Equal(0, signature.Calls);
    }

    [Fact]
    public async Task PostInstall_CoreHashChanged_RemainsCritical()
    {
        string root = TestData.NewDirectory();
        string file = Path.Combine(root, "core.bin"); await File.WriteAllTextAsync(file, "original");
        var manifest = new DeploymentManifest { FileInventory = [new() { RelativePath = "core.bin", Required = true,
            Sha256 = await new Sha256HashService().ComputeSha256Async(file), LengthBytes = new FileInfo(file).Length }] };
        await File.WriteAllTextAsync(file, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => StagedApplicationGuard.ValidateCoreFilesAsync(manifest, root, default));
    }

    [Fact]
    public async Task AppProvider_CachedFileHashMismatch_Redownloads()
    {
        using var handler = new Handler(_ => Success()); using var client = new HttpClient(handler);
        var signatures = new Signature(); var cache = new VerifiedAppCache(new(client, signatures), signatures);
        string root = TestData.NewDirectory();
        await cache.GetAsync(App("chrome"), root, TestData.NewDirectory(), default);
        await cache.GetAsync(App("chrome"), root, TestData.NewDirectory(), default);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(2, signatures.Calls);
        string installer = Directory.GetFiles(root, "installer.exe", SearchOption.AllDirectories).Single();
        await File.AppendAllTextAsync(installer, "changed");
        await cache.GetAsync(App("chrome"), root, TestData.NewDirectory(), default);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AppProvider_SizeLimitExceeded_Rejected()
    {
        using var handler = new Handler(_ => { var response = Success(); response.Content.Headers.ContentLength = long.MaxValue; return response; });
        using var client = new HttpClient(handler); var signatures = new Signature();
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedAppAcquisition(client, signatures).AcquireAsync(App("steam"), TestData.NewDirectory(), default));
        Assert.Equal(0, signatures.Calls);
    }

    [Fact]
    public async Task AppProvider_InvalidPe_Rejected()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>not an installer</html>") });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedAppAcquisition(client, new Signature()).AcquireAsync(App("steam"), TestData.NewDirectory(), default));
    }

    [Fact]
    public async Task AppProvider_WrongPublisher_Rejected()
    {
        var runner = new RecordingProcessRunner(command => new(command, 0, "{\"Status\":0,\"Publisher\":\"Untrusted publisher\"}", "", TimeSpan.Zero));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AuthenticodeVerifier(runner).VerifyAsync("test.exe", "Google LLC", default));
    }

    [Fact]
    public async Task AppProvider_InvalidAuthenticode_Rejected()
    {
        var runner = new RecordingProcessRunner(command => new(command, 0, "{\"Status\":2,\"Publisher\":\"Google LLC\"}", "", TimeSpan.Zero));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AuthenticodeVerifier(runner).VerifyAsync("test.exe", "Google LLC", default));
    }

    [Fact]
    public async Task Preflight_SelectedAppMissing_FailsBeforeConfirmation()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("No network expected"));
        using var client = new HttpClient(handler); var signature = new Signature();
        await Assert.ThrowsAsync<InvalidDataException>(() => new PreparedApplications(new(client, signature), signature, TestData.NewDirectory())
            .PrepareAsync(["7zip"], TestData.FindConfigRoot(), TestData.NewDirectory(), TestData.NewDirectory(), default));
    }

    [Fact]
    public async Task Preflight_AllSelectedAppsPrepared_Passes()
    {
        using var handler = new Handler(_ => Success()); using var client = new HttpClient(handler); var signature = new Signature();
        var prepared = await new PreparedApplications(new(client, signature), signature, TestData.NewDirectory())
            .PrepareAsync(["chrome", "firefox"], TestData.FindConfigRoot(), TestData.NewDirectory(), TestData.NewDirectory(), default);
        await prepared.ValidateAsync(["chrome", "firefox"], default);
        Assert.Equal(2, prepared.Applications.Count);
    }

    [Fact]
    public async Task PostInstall_UsesStagedInstallerOnly()
    {
        string root = TestData.NewDirectory(); var signature = new Signature();
        string path = Path.Combine(root, "Apps", "chrome", "setup.exe"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, InstallerTestData.Executable());
        var app = App("chrome") with { ExpectedPublisher = "Google LLC", SizeBytes = new FileInfo(path).Length, Sha256 = await new Sha256HashService().ComputeSha256Async(path) };
        var runner = new RecordingProcessRunner();
        await new ApplicationInstaller(runner, new Sha256HashService(), signature).InstallAsync([app], root, ExecutionMode.Live);
        Assert.Single(runner.Commands); Assert.Equal(path, runner.Commands[0].FileName); Assert.Equal(1, signature.Calls);
    }

    [Fact]
    public async Task PostInstall_InstallerHashChanged_FailsAppOnly()
    {
        string root = TestData.NewDirectory(); string directory = Path.Combine(root, "Apps", "chrome"); Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "setup.exe"); await File.WriteAllBytesAsync(file, InstallerTestData.Executable());
        var app = App("chrome") with { Sha256 = await new Sha256HashService().ComputeSha256Async(file), SizeBytes = new FileInfo(file).Length };
        var manifest = new DeploymentManifest { FileInventory = [new() { RelativePath = app.Installer, Sha256 = app.Sha256, LengthBytes = app.SizeBytes.Value }] };
        await StagedApplicationGuard.ValidateAppAsync(manifest, app, root, default);
        await File.AppendAllTextAsync(file, "changed");
        await StagedApplicationGuard.ValidateCoreFilesAsync(manifest, root, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => StagedApplicationGuard.ValidateAppAsync(manifest, app, root, default));
    }
}
