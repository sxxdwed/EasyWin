namespace EasyWin.Core.Models;

public sealed record DeploymentProgress
{
    public DeploymentProgress(
        DeploymentStage stage,
        string message,
        double? percentage = null,
        DateTimeOffset? timestampUtc = null,
        bool isIndeterminate = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A progress message is required.", nameof(message));
        }

        if (percentage is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentage), "Progress must be between 0 and 100.");
        }

        Stage = stage;
        Message = message.Trim();
        Percentage = percentage;
        TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow;
        IsIndeterminate = isIndeterminate;
    }

    public DeploymentStage Stage { get; }

    public string Message { get; }

    public double? Percentage { get; }

    public DateTimeOffset TimestampUtc { get; }

    public bool IsIndeterminate { get; }
}

public sealed record DeploymentError
{
    public DeploymentStage Stage { get; init; } = DeploymentStage.NotStarted;

    public string Code { get; init; } = string.Empty;

    public string UserMessage { get; init; } = string.Empty;

    public string? TechnicalDetails { get; init; }

    public int? ExitCode { get; init; }

    public DeploymentErrorSeverity Severity { get; init; } = DeploymentErrorSeverity.Error;

    public bool IsRecoverable { get; init; }

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

public record OperationResult
{
    public bool IsSuccess { get; init; }

    public IReadOnlyList<DeploymentError> Errors { get; init; } = Array.Empty<DeploymentError>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static OperationResult Success(params string[] warnings) => new()
    {
        IsSuccess = true,
        Warnings = warnings,
    };

    public static OperationResult Failure(params DeploymentError[] errors) => new()
    {
        IsSuccess = false,
        Errors = errors,
    };
}

public sealed record OperationResult<T> : OperationResult
{
    public T? Value { get; init; }

    public static OperationResult<T> Success(T value, params string[] warnings) => new()
    {
        IsSuccess = true,
        Value = value,
        Warnings = warnings,
    };

    public new static OperationResult<T> Failure(params DeploymentError[] errors) => new()
    {
        IsSuccess = false,
        Errors = errors,
    };
}
