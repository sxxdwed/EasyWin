using EasyWin.Desktop.Models;

namespace EasyWin.Desktop.Services;

public interface IDesktopUiWorkflow
{
    Task<DesktopDiscovery> DiscoverAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<EditionChoice>> InspectImageAsync(string isoPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<UiPreflightCheck>> PreflightAsync(UiPreparationRequest request, CancellationToken cancellationToken);
    Task<UiWorkflowResult> StageAndArmAsync(
        UiPreparationRequest request,
        IProgress<UiDeploymentProgress> progress,
        CancellationToken cancellationToken);
}
