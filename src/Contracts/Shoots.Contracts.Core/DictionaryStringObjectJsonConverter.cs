using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shoots.Contracts.Core;

public sealed class DictionaryStringObjectJsonConverter : JsonConverter<IReadOnlyDictionary<string, object?>>
{
    public override IReadOnlyDictionary<string, object?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ref reader, options)
            ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var converted = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, element) in values)
            converted[key] = ConvertElement(element);

        return converted;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, item) in value)
        {
            writer.WritePropertyName(key);
            JsonSerializer.Serialize(writer, item, item?.GetType() ?? typeof(object), options);
        }

        writer.WriteEndObject();
    }

    private static object? ConvertElement(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var int64) => int64,
            JsonValueKind.Number when element.TryGetDecimal(out var dec) => dec,
            JsonValueKind.Number => element.GetDouble(),
            _ => element.Clone()
        };
}
