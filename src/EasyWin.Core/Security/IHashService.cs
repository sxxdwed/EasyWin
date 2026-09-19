namespace EasyWin.Core.Security;

public sealed record FileHashVerificationResult(
    bool IsValid,
    string ExpectedSha256,
    string? ActualSha256,
    long? ExpectedLengthBytes,
    long? ActualLengthBytes,
    string? Error = null);

public interface IHashService
{
    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default);

    Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken = default);

    Task<FileHashVerificationResult> VerifyFileAsync(
        string path,
        string expectedSha256,
        long? expectedLengthBytes = null,
        CancellationToken cancellationToken = default);
}
