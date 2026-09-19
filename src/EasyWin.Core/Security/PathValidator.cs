namespace EasyWin.Core.Security;

public static class PathValidator
{
    private static readonly HashSet<string> ReservedWindowsNames = new(
        new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        },
        StringComparer.OrdinalIgnoreCase);

    public static string ValidateRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (!string.Equals(relativePath, relativePath.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Relative paths cannot start or end with whitespace.", nameof(relativePath));
        }

        if (relativePath.Contains('\0') || relativePath.Any(char.IsControl))
        {
            throw new ArgumentException("Relative paths cannot contain control characters.", nameof(relativePath));
        }

        string portablePath = relativePath.Replace('\\', '/');
        if (portablePath.StartsWith('/') ||
            portablePath.StartsWith("//", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath) ||
            portablePath.Contains(':'))
        {
            throw new ArgumentException("An absolute, UNC, device, or alternate-stream path is not allowed.", nameof(relativePath));
        }

        string[] segments = portablePath.Split('/');
        if (segments.Length == 0 || segments.Any(static segment => segment.Length == 0))
        {
            throw new ArgumentException("Relative paths cannot contain empty segments.", nameof(relativePath));
        }

        foreach (string segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException("Path traversal segments are not allowed.", nameof(relativePath));
            }

            if (!string.Equals(segment, segment.TrimEnd(' ', '.'), StringComparison.Ordinal))
            {
                throw new ArgumentException("Path segments cannot end with a space or period.", nameof(relativePath));
            }

            if (segment.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
            {
                throw new ArgumentException("Wildcard and reserved path characters are not allowed.", nameof(relativePath));
            }

            string baseName = segment.Split('.', 2)[0];
            if (ReservedWindowsNames.Contains(baseName))
            {
                throw new ArgumentException("A reserved Windows device name is not allowed.", nameof(relativePath));
            }
        }

        return string.Join('/', segments);
    }

    public static string ResolveUnderRoot(
        string rootDirectory,
        string relativePath,
        bool mustExist = false,
        bool rejectReparsePoints = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string normalizedRelativePath = ValidateRelativePath(relativePath);
        string root = Path.GetFullPath(rootDirectory);
        string candidate = Path.GetFullPath(
            Path.Combine(root, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        EnsureAbsolutePathUnderRoot(root, candidate);

        if (mustExist && !File.Exists(candidate) && !Directory.Exists(candidate))
        {
            throw new FileNotFoundException("The path does not exist beneath the trusted root.", candidate);
        }

        if (rejectReparsePoints)
        {
            RejectExistingReparsePoints(root, normalizedRelativePath);
        }

        return candidate;
    }

    public static void EnsureAbsolutePathUnderRoot(string rootDirectory, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        string root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(candidatePath);
        string rootPrefix = root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidate.StartsWith(rootPrefix, comparison))
        {
            throw new UnauthorizedAccessException("The resolved path escapes the trusted root directory.");
        }
    }

    private static void RejectExistingReparsePoints(string root, string normalizedRelativePath)
    {
        string current = Path.GetFullPath(root);
        foreach (string segment in normalizedRelativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    $"Reparse points are not allowed in trusted deployment paths: '{segment}'.");
            }
        }
    }
}
