using EasyWin.Deployment.Models;

namespace EasyWin.Deployment.Environment;

public sealed record PreflightRequest(
    string SystemVolumePath,
    string ImagePath,
    long RequiredFreeBytes,
    bool RequireAcPower = true,
    int MinimumBatteryPercent = 40,
    bool RequireBitLockerProtectionOff = true);

public interface IPreflightService
{
    Task<PreflightReport> CheckAsync(PreflightRequest request, CancellationToken cancellationToken = default);
}

public sealed class PreflightService(
    IWindowsEnvironmentProbe environmentProbe,
    IBitLockerService bitLockerService) : IPreflightService
{
    public async Task<PreflightReport> CheckAsync(
        PreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var environment = environmentProbe.Capture(request.SystemVolumePath);
        var checks = new List<PreflightCheck>
        {
            Critical("platform.windows", "Windows environment", environment.IsWindows, "EasyWin must run on Windows."),
            Critical("privilege.admin", "Administrator", environment.IsAdministrator, "Administrator privileges are required."),
            Critical("firmware.uefi", "UEFI firmware", environment.IsUefi, "UEFI firmware is required for the GPT deployment layout."),
            Critical(
                "power.ac",
                "External power",
                !request.RequireAcPower || environment.IsAcPowerConnected || environment.BatteryPercent >= request.MinimumBatteryPercent,
                $"Connect external power or charge the battery above {request.MinimumBatteryPercent}%."),
            Critical(
                "storage.free",
                "Free space",
                environment.AvailableBytes >= request.RequiredFreeBytes,
                $"At least {request.RequiredFreeBytes} bytes of free space are required."),
            Critical(
                "image.exists",
                "Windows image",
                File.Exists(request.ImagePath),
                "The selected Windows image does not exist.")
        };

        if (environment.IsWindows && environment.IsAdministrator)
        {
            try
            {
                var status = await bitLockerService.GetStatusAsync(request.SystemVolumePath, cancellationToken).ConfigureAwait(false);
                checks.Add(Critical(
                    "bitlocker.protection",
                    "BitLocker",
                    !request.RequireBitLockerProtectionOff || !status.IsProtectionEnabled,
                    status.IsProtectionEnabled
                        ? "Suspend BitLocker protection before arming the one-time WinPE boot. EasyWin never bypasses BitLocker."
                        : status.ConversionStatus));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                checks.Add(Critical("bitlocker.query", "BitLocker status", false, exception.Message));
            }
        }

        return PreflightReport.From(checks);
    }

    private static PreflightCheck Critical(string code, string name, bool passed, string failureMessage) =>
        new(code, name, CheckSeverity.Critical, passed, passed ? "Passed" : failureMessage);
}
