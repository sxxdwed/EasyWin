using EasyWin.Core.Processes;

namespace EasyWin.Deployment.Commands;

public sealed class CommandFailureException : InvalidOperationException
{
    public CommandFailureException(string stage, ProcessResult result)
        : base(CreateMessage(stage, result))
    {
        Stage = stage;
        Result = result;
    }

    public string Stage { get; }

    public ProcessResult Result { get; }

    public static void ThrowIfFailed(string stage, ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            throw new CommandFailureException(stage, result);
        }
    }

    private static string CreateMessage(string stage, ProcessResult result)
    {
        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        details = details.Trim();
        if (details.Length > 600)
        {
            details = details[..600];
        }

        return $"{stage} failed with exit code {result.ExitCode}: {details}";
    }
}
