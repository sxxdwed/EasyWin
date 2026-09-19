namespace EasyWin.Core.Serialization;

public interface IJsonSerializer
{
    string Serialize<T>(T value, bool indented = true);

    T Deserialize<T>(string json);

    Task SerializeToFileAsync<T>(T value, string path, CancellationToken cancellationToken = default);

    Task<T> DeserializeFileAsync<T>(string path, CancellationToken cancellationToken = default);
}
