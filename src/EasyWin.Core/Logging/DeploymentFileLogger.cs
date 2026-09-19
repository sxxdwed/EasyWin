using System.Text.Json;
using EasyWin.Core.Models;

namespace EasyWin.Core.Logging;

public interface IDeploymentLogger
{
    Task WriteAsync(string logName, DeploymentStage stage, string message, int? exitCode = null, CancellationToken cancellationToken = default);
}

public sealed class DeploymentFileLogger(string logRoot) : IDeploymentLogger, IDisposable
{
    private readonly string _root = Path.GetFullPath(logRoot);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly HashSet<string> AllowedLogs = new(StringComparer.OrdinalIgnoreCase)
    {
        "installer.log", "boot.log", "disk.log", "dism.log", "postinstall.log"
    };

    public async Task WriteAsync(string logName, DeploymentStage stage, string message, int? exitCode = null, CancellationToken cancellationToken = default)
    {
        if (!AllowedLogs.Contains(logName))
        {
            throw new ArgumentException("Unsupported deployment log name.", nameof(logName));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, logName);
        string line = JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow, stage, exitCode, message }) + Environment.NewLine;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
