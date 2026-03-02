#nullable enable
using System;
using System.Buffers;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Services;

public static class CanonicalJson
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static string Normalize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        using var doc = JsonDocument.Parse(json, DocOptions);

        var buffer = new ArrayBufferWriter<byte>(Math.Max(256, json.Length));

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        }))
        {
            WriteCanonical(writer, doc.RootElement);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                writer.WriteStartObject();

                foreach (var prop in element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonical(writer, prop.Value);
                }

                writer.WriteEndObject();
                break;
            }

            case JsonValueKind.Array:
            {
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            }

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l))
                    writer.WriteNumberValue(l);
                else if (element.TryGetDecimal(out var d))
                    writer.WriteNumberValue(d);
                else
                    writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }
}