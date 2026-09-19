using System.Text;
using System.Text.Json;

namespace EasyWin.Core.Serialization;

public sealed class SystemTextJsonSerializer : IJsonSerializer
{
    public string Serialize<T>(T value, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, JsonDefaults.CreateOptions(indented));
    }

    public T Deserialize<T>(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<T>(json, JsonDefaults.CreateOptions())
            ?? throw new JsonException($"The JSON document did not contain a {typeof(T).Name} value.");
    }

    public async Task SerializeToFileAsync<T>(
        T value,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The JSON file must have a parent directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    JsonDefaults.CreateOptions(writeIndented: true),
                    cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<T> DeserializeFileAsync<T>(
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
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await JsonSerializer.DeserializeAsync<T>(
                   stream,
                   JsonDefaults.CreateOptions(),
                   cancellationToken).ConfigureAwait(false)
               ?? throw new JsonException($"The JSON document did not contain a {typeof(T).Name} value.");
    }
}
