using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Relay.Infrastructure.Persistence;

public static class CanonicalJson
{
    // v1: ordinal property order, array order preserved, duplicate properties rejected.
    // Validated DTOs must normalize semantic values (numbers, UTC times, defaults) before calling.
    public static string Hash(JsonElement input)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, input);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    internal static string HashText(string input) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!names.Add(property.Name)) throw new ArgumentException("Duplicate JSON property.");
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var element in value.EnumerateArray()) Write(writer, element);
                writer.WriteEndArray();
                break;
            default: value.WriteTo(writer); break;
        }
    }
}
