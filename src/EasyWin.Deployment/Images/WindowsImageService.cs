using EasyWin.Core.Processes;
using EasyWin.Core.Models;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Images;

public interface IWindowsImageService
{
    Task<IReadOnlyList<WindowsImageInfo>> GetEditionsAsync(string imagePath, CancellationToken cancellationToken = default);

    Task ApplyAsync(ApplyImageRequest request, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    Task AddDriversAsync(DriverInjectionRequest request, CancellationToken cancellationToken = default);
}

public sealed class WindowsImageService(IProcessRunner processRunner) : IWindowsImageService
{
    public async Task<IReadOnlyList<WindowsImageInfo>> GetEditionsAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        var normalized = ValidateImagePath(imagePath, true);
        var command = DismCommandFactory.GetImageInfo(normalized);
        var result = await processRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed("Read Windows image metadata", result);
        var editions = DismOutputParser.ParseImageInfo(result.StandardOutput, normalized).ToArray();
        for (var i = 0; i < editions.Length; i++)
        {
            if (editions[i].Architecture != ProcessorArchitecture.Unknown && !string.IsNullOrWhiteSpace(editions[i].Version))
            {
                continue;
            }

            var detailResult = await processRunner.RunAsync(
                DismCommandFactory.GetImageInfo(normalized, editions[i].ImageIndex),
                cancellationToken).ConfigureAwait(false);
            CommandFailureException.ThrowIfFailed($"Read Windows image index {editions[i].ImageIndex} metadata", detailResult);
            WindowsImageInfo detail = DismOutputParser.ParseImageInfo(detailResult.StandardOutput, normalized).Single();
            editions[i] = editions[i] with
            {
                Architecture = detail.Architecture,
                Version = string.IsNullOrWhiteSpace(detail.Version) ? editions[i].Version : detail.Version,
                SizeBytes = detail.SizeBytes > 0 ? detail.SizeBytes : editions[i].SizeBytes
            };
        }

        return editions;
    }

    public async Task ApplyAsync(
        ApplyImageRequest request,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = ValidateImagePath(request.ImagePath, true);
        if (request.ImageIndex < 1)
        {
            throw new DeploymentSafetyException("image.index.invalid", "The Windows image index must be positive.");
        }

        progress?.Report(0);
        var result = await processRunner.RunAsync(DismCommandFactory.ApplyImage(request), cancellationToken).ConfigureAwait(false);
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var percent = DismOutputParser.ParseProgressPercent(line);
            if (percent.HasValue)
            {
                progress?.Report(percent.Value);
            }
        }

        CommandFailureException.ThrowIfFailed("Apply Windows image", result);
        progress?.Report(100);
    }

    public async Task AddDriversAsync(
        DriverInjectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ForceUnsigned)
        {
            throw new DeploymentSafetyException("driver.unsigned_forbidden", "EasyWin never installs unsigned drivers automatically.");
        }

        foreach (var inf in request.InfPaths)
        {
            _ = DeploymentGuard.AbsolutePath(inf, "Driver INF", true);
            if (!Path.GetExtension(inf).Equals(".inf", StringComparison.OrdinalIgnoreCase))
            {
                throw new DeploymentSafetyException("driver.not_inf", "Only INF driver packages are supported.");
            }

            var result = await processRunner.RunAsync(DismCommandFactory.AddDriver(request.TargetImagePath, inf), cancellationToken).ConfigureAwait(false);
            CommandFailureException.ThrowIfFailed($"Inject driver {Path.GetFileName(inf)}", result);
        }
    }

    private static string ValidateImagePath(string imagePath, bool mustExist)
    {
        var normalized = DeploymentGuard.AbsolutePath(imagePath, "Windows image", mustExist);
        var extension = Path.GetExtension(normalized);
        if (!extension.Equals(".wim", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".esd", StringComparison.OrdinalIgnoreCase))
        {
            throw new DeploymentSafetyException("image.extension.invalid", "Windows image must be install.wim or install.esd.");
        }

        return normalized;
    }
}

public static class DismCommandFactory
{
    public static CommandSpec GetImageInfo(string imagePath) =>
        new(
            "dism.exe",
            ["/English", "/Get-WimInfo", $"/WimFile:{imagePath}"],
            timeout: TimeSpan.FromMinutes(5));

    public static CommandSpec GetImageInfo(string imagePath, int imageIndex) =>
        new(
            "dism.exe",
            ["/English", "/Get-WimInfo", $"/WimFile:{imagePath}", $"/Index:{imageIndex}"],
            timeout: TimeSpan.FromMinutes(5));

    public static CommandSpec ApplyImage(ApplyImageRequest request)
    {
        var arguments = new List<string>
        {
            "/English",
            "/Apply-Image",
            $"/ImageFile:{request.ImagePath}",
            $"/Index:{request.ImageIndex}",
            $"/ApplyDir:{request.ApplyDirectory}"
        };
        if (request.Verify)
        {
            arguments.Add("/Verify");
        }

        if (request.CheckIntegrity)
        {
            arguments.Add("/CheckIntegrity");
        }

        if (request.ScratchSpaceMiB.HasValue)
        {
            arguments.Add($"/ScratchDir:{Path.Combine(request.ApplyDirectory, "EasyWinScratch")}");
        }

        return new CommandSpec("dism.exe", arguments, timeout: TimeSpan.FromHours(4));
    }

    public static CommandSpec AddDriver(string targetImagePath, string infPath) =>
        new(
            "dism.exe",
            ["/English", $"/Image:{targetImagePath}", "/Add-Driver", $"/Driver:{infPath}"],
            timeout: TimeSpan.FromMinutes(30));
}
