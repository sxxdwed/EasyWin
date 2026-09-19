using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EasyWin.Core.Serialization;

public static class DeterministicJson
{
    public static byte[] SerializeCanonicalUtf8<T>(T value, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        options ??= JsonDefaults.CreateOptions();

        JsonNode node = JsonSerializer.SerializeToNode(value, options)
            ?? throw new JsonException("The value serialized to an empty JSON document.");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        }))
        {
            WriteCanonicalNode(writer, node, options);
        }

        return stream.ToArray();
    }

    public static string SerializeCanonical<T>(T value, JsonSerializerOptions? options = null) =>
        Encoding.UTF8.GetString(SerializeCanonicalUtf8(value, options));

    private static void WriteCanonicalNode(
        Utf8JsonWriter writer,
        JsonNode? node,
        JsonSerializerOptions options)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;

            case JsonObject jsonObject:
                writer.WriteStartObject();
                foreach ((string name, JsonNode? child) in jsonObject.OrderBy(
                             property => property.Key,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(name);
                    WriteCanonicalNode(writer, child, options);
                }

                writer.WriteEndObject();
                break;

            case JsonArray jsonArray:
                writer.WriteStartArray();
                foreach (JsonNode? child in jsonArray)
                {
                    WriteCanonicalNode(writer, child, options);
                }

                writer.WriteEndArray();
                break;

            case JsonValue jsonValue:
                jsonValue.WriteTo(writer, options);
                break;

            default:
                throw new JsonException($"Unsupported JSON node type: {node.GetType().Name}.");
        }
    }
}
