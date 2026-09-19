using System.Collections.ObjectModel;
using System.Diagnostics;
using EasyWin.Core.Logging;
using EasyWin.Core.Models;

namespace EasyWin.Core.Processes;

public sealed class CommandSpec
{
    public CommandSpec(
        string fileName,
        IEnumerable<string>? arguments = null,
        string? workingDirectory = null,
        bool requiresElevation = false,
        TimeSpan? timeout = null,
        IReadOnlySet<int>? acceptableExitCodes = null,
        string? logName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Executable name contains invalid characters.", nameof(fileName));
        }

        FileName = fileName;
        Arguments = new ReadOnlyCollection<string>((arguments ?? []).Select(ValidateArgument).ToArray());
        WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : Path.GetFullPath(workingDirectory);
        RequiresElevation = requiresElevation;
        Timeout = timeout ?? TimeSpan.FromMinutes(15);
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        AcceptableExitCodes = acceptableExitCodes ?? new HashSet<int> { 0 };
        LogName = logName;
    }

    public string FileName { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string? WorkingDirectory { get; }
    public bool RequiresElevation { get; }
    public TimeSpan Timeout { get; }
    public IReadOnlySet<int> AcceptableExitCodes { get; }
    public string? LogName { get; }

    public string ToDisplayString() => string.Join(' ', new[] { FileName }.Concat(Arguments.Select(QuoteForDisplay)));

    private static string ValidateArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0'))
        {
            throw new ArgumentException("Process arguments cannot contain NUL characters.", nameof(value));
        }

        return value;
    }

    private static string QuoteForDisplay(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;
}

public sealed record ProcessResult(
    CommandSpec Command,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool WasRecorded = false)
{
    public bool Succeeded => Command.AcceptableExitCodes.Contains(ExitCode);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = command.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{command.FileName}'.");
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(command.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"'{command.FileName}' exceeded {command.Timeout}.");
            }

            throw;
        }

        return new ProcessResult(
            command,
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false),
            stopwatch.Elapsed);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed class LoggingProcessRunner(IProcessRunner inner, IDeploymentLogger logger) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        string logName = ResolveLogName(command);
        DeploymentStage stage = ResolveStage(command);
        await logger.WriteAsync(logName, stage, $"Starting {Path.GetFileName(command.FileName)}.", cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            ProcessResult result = await inner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            await logger.WriteAsync(logName, stage, $"Finished {Path.GetFileName(command.FileName)} in {result.Duration.TotalSeconds:0.###} seconds.", result.ExitCode, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await logger.WriteAsync(logName, stage, $"{Path.GetFileName(command.FileName)} failed: {exception.Message}", cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static string ResolveLogName(CommandSpec command)
    {
        if (!string.IsNullOrWhiteSpace(command.LogName))
        {
            return command.LogName;
        }

        return Path.GetFileName(command.FileName).ToLowerInvariant() switch
        {
            "diskpart.exe" => "disk.log",
            "dism.exe" => "dism.log",
            "bcdedit.exe" or "bcdboot.exe" or "shutdown.exe" or "wpeutil.exe" => "boot.log",
            "pnputil.exe" or "reg.exe" or "robocopy.exe" or "reagentc.exe" => "postinstall.log",
            _ => "installer.log",
        };
    }

    private static DeploymentStage ResolveStage(CommandSpec command) => Path.GetFileName(command.FileName).ToLowerInvariant() switch
    {
        "diskpart.exe" => DeploymentStage.PrepareDisk,
        "dism.exe" => command.Arguments.Any(argument => argument.Equals("/Apply-Image", StringComparison.OrdinalIgnoreCase))
            ? DeploymentStage.ApplyImage
            : DeploymentStage.ValidateImage,
        "bcdedit.exe" => DeploymentStage.PrepareBoot,
        "bcdboot.exe" => DeploymentStage.ConfigureBoot,
        "pnputil.exe" => DeploymentStage.InstallDrivers,
        "reg.exe" => DeploymentStage.ApplyProfile,
        "robocopy.exe" or "reagentc.exe" => DeploymentStage.CleanupStaging,
        _ => DeploymentStage.ValidateEnvironment,
    };
}

public sealed class RecordingProcessRunner : IProcessRunner
{
    private readonly List<CommandSpec> _commands = [];
    private readonly Func<CommandSpec, ProcessResult>? _resultFactory;

    public RecordingProcessRunner(Func<CommandSpec, ProcessResult>? resultFactory = null) => _resultFactory = resultFactory;

    public IReadOnlyList<CommandSpec> Commands => _commands.AsReadOnly();

    public Task<ProcessResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(command);
        _commands.Add(command);
        return Task.FromResult(_resultFactory?.Invoke(command) ?? new ProcessResult(command, 0, string.Empty, string.Empty, TimeSpan.Zero, true));
    }
}
