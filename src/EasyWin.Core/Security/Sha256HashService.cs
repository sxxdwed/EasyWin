using System.Security.Cryptography;

namespace EasyWin.Core.Security;

public sealed class Sha256HashService : IHashService
{
    public async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ComputeSha256Async(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<FileHashVerificationResult> VerifyFileAsync(
        string path,
        string expectedSha256,
        long? expectedLengthBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedExpected = NormalizeSha256(expectedSha256);
        string fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            return new FileHashVerificationResult(
                false,
                normalizedExpected,
                null,
                expectedLengthBytes,
                null,
                "File does not exist.");
        }

        var fileInfo = new FileInfo(fullPath);
        if (expectedLengthBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedLengthBytes),
                "Expected file length cannot be negative.");
        }

        if (expectedLengthBytes.HasValue && fileInfo.Length != expectedLengthBytes.Value)
        {
            return new FileHashVerificationResult(
                false,
                normalizedExpected,
                null,
                expectedLengthBytes,
                fileInfo.Length,
                "File length does not match the manifest.");
        }

        string actual = await ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);
        bool valid = CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(normalizedExpected),
            Convert.FromHexString(actual));

        return new FileHashVerificationResult(
            valid,
            normalizedExpected,
            actual,
            expectedLengthBytes,
            fileInfo.Length,
            valid ? null : "SHA-256 does not match the manifest.");
    }

    public static bool IsValidSha256(string? value) =>
        value is not null &&
        value.Length == 64 &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    public static string NormalizeSha256(string value)
    {
        if (!IsValidSha256(value))
        {
            throw new ArgumentException("A SHA-256 value must contain exactly 64 hexadecimal characters.", nameof(value));
        }

        return value.ToLowerInvariant();
    }
}
