using EasyWin.Core.Models;

namespace EasyWin.Core.Manifests;

/// <summary>
/// Persists deployment progress after every important boundary. Manifest writes are
/// atomic through IJsonSerializer, so a power loss leaves either the old or new file.
/// </summary>
public sealed class DeploymentCheckpointService(IManifestService manifests)
{
    public async Task<DeploymentManifest> BeginAttemptAsync(
        DeploymentManifest manifest,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var updated = manifest with
        {
            State = manifest.State with
            {
                AttemptNumber = checked(manifest.State.AttemptNumber + 1),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastError = null,
            },
        };
        await manifests.SaveAsync(updated, path, cancellationToken).ConfigureAwait(false);
        return manifests.Seal(updated);
    }

    public async Task<DeploymentManifest> StartAsync(
        DeploymentManifest manifest,
        string path,
        DeploymentStage stage,
        bool recoveryRequired,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var updated = manifest with
        {
            State = manifest.State with
            {
                CurrentStage = stage,
                StartedStages = manifest.State.StartedStages.Append(stage).Distinct().ToArray(),
                RecoveryRequired = recoveryRequired,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastError = null,
            },
        };
        await manifests.SaveAsync(updated, path, cancellationToken).ConfigureAwait(false);
        return manifests.Seal(updated);
    }

    public async Task<DeploymentManifest> CompleteAsync(
        DeploymentManifest manifest,
        string path,
        DeploymentStage stage,
        bool recoveryRequired,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var completed = manifest.State.CompletedStages
            .Append(stage)
            .Distinct()
            .ToArray();
        var checkpoints = new Dictionary<string, DateTimeOffset>(manifest.State.Checkpoints, StringComparer.OrdinalIgnoreCase)
        {
            [stage.ToString()] = now,
        };
        var updated = manifest with
        {
            State = manifest.State with
            {
                CurrentStage = stage,
                CompletedStages = completed,
                Checkpoints = checkpoints,
                LastSuccessfulStage = stage,
                RecoveryRequired = recoveryRequired,
                UpdatedAtUtc = now,
                LastError = null,
            },
        };
        await manifests.SaveAsync(updated, path, cancellationToken).ConfigureAwait(false);
        return manifests.Seal(updated);
    }

    public async Task<DeploymentManifest> FailAsync(
        DeploymentManifest manifest,
        string path,
        string code,
        string userMessage,
        string? technicalDetails,
        bool recoverable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        DeploymentStage failedStage = manifest.State.CurrentStage;
        var error = new DeploymentError
        {
            Stage = failedStage,
            Code = string.IsNullOrWhiteSpace(code) ? "deployment.failed" : code,
            UserMessage = string.IsNullOrWhiteSpace(userMessage) ? "Deployment failed." : userMessage,
            TechnicalDetails = technicalDetails,
            Severity = DeploymentErrorSeverity.Critical,
            IsRecoverable = recoverable,
        };
        var updated = manifest with
        {
            State = manifest.State with
            {
                CurrentStage = DeploymentStage.Failed,
                RecoveryRequired = recoverable || manifest.State.RecoveryRequired,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastError = error,
            },
        };
        await manifests.SaveAsync(updated, path, cancellationToken).ConfigureAwait(false);
        return manifests.Seal(updated);
    }
}
