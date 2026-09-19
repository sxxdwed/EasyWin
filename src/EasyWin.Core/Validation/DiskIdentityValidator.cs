using EasyWin.Core.Models;

namespace EasyWin.Core.Validation;

public sealed record DiskIdentityValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public sealed class DiskIdentityValidator
{
    public DiskIdentityValidationResult Validate(DiskIdentity expected, DiskIdentity actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var errors = new List<string>();
        CompareRequired("Device ID", expected.DeviceId, actual.DeviceId, errors);
        CompareRequired("Serial", expected.SerialNumber, actual.SerialNumber, errors);
        CompareRequired("Model", expected.Model, actual.Model, errors);
        if (expected.SizeBytes <= 0 || actual.SizeBytes <= 0 || expected.SizeBytes != actual.SizeBytes)
        {
            errors.Add($"Size changed: expected {expected.SizeBytes}, actual {actual.SizeBytes}.");
        }

        if (expected.BusType == DiskBusType.Unknown || actual.BusType == DiskBusType.Unknown || expected.BusType != actual.BusType)
        {
            errors.Add($"Bus type changed: expected {expected.BusType}, actual {actual.BusType}.");
        }

        if (!string.IsNullOrWhiteSpace(expected.UniqueId) &&
            !string.Equals(Normalize(expected.UniqueId), Normalize(actual.UniqueId), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Unique disk identifier changed.");
        }

        return new DiskIdentityValidationResult(errors.Count == 0, errors);
    }

    private static void CompareRequired(string name, string expected, string actual, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual) ||
            !string.Equals(Normalize(expected), Normalize(actual), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{name} changed or is unavailable.");
        }
    }

    private static string Normalize(string? value) => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
