#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Shoots.UI.Services;

public static class JobSpecDigestBuilder
{
    /// <summary>
    /// Hash a JSON string after canonicalizing it (minified, stable).
    /// Output: lowercase hex SHA256.
    /// </summary>
    public static string HashCanonical(string json)
    {
        var canonical = CanonicalizeJson(json);
        return Sha256Hex(canonical);
    }

    /// <summary>
    /// Hash an object payload after serializing + canonicalizing.
    /// Tests pass anonymous objects like: new { toolId = "...", bindings = ... }.
    /// Output: lowercase hex SHA256.
    /// </summary>
    public static string HashCanonical(object? payload)
    {
        if (payload is null)
            return Sha256Hex("{}");

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        var canonical = CanonicalizeJson(json);
        return Sha256Hex(canonical);
    }

    /// <summary>
    /// Deterministic digest for the chat intake fields.
    /// Output: lowercase hex SHA256.
    /// </summary>
	public static string Build(string? intent, string? target, string? attachments, string? stack)
	{
		static string Normalize(string? value)
			=> (value ?? string.Empty).Trim();

		static string NormalizeAttachments(string? attachments)
		{
			if (string.IsNullOrWhiteSpace(attachments))
				return string.Empty;

			var lines = attachments
				.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(l => l.Trim())
				.Where(l => l.Length > 0)
				.OrderBy(l => l, StringComparer.Ordinal);

			return string.Join("\n", lines);
		}

		var normalized =
			$"{Normalize(intent)}|" +
			$"{Normalize(target)}|" +
			$"{NormalizeAttachments(attachments)}|" +
			$"{Normalize(stack)}";

		return Sha256Hex(normalized);
	}

	private static string CanonicalizeJson(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
			return "{}";

		using var doc = JsonDocument.Parse(json);

		using var ms = new MemoryStream();
		using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
		{
			Indented = false,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			SkipValidation = false
		}))
		{
			WriteElement(writer, doc.RootElement);
		}

		return Encoding.UTF8.GetString(ms.ToArray());
	}

	private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				writer.WriteStartObject();
				foreach (var prop in element.EnumerateObject()) // preserves input order
				{
					writer.WritePropertyName(prop.Name);
					WriteElement(writer, prop.Value);
				}
				writer.WriteEndObject();
				break;

			case JsonValueKind.Array:
				writer.WriteStartArray();
				foreach (var item in element.EnumerateArray())
					WriteElement(writer, item);
				writer.WriteEndArray();
				break;

			case JsonValueKind.String:
				writer.WriteStringValue(element.GetString());
				break;

			case JsonValueKind.Number:
				// Keep number text stable and minified (no added spaces, preserves 1 vs 1.0 as provided)
				writer.WriteRawValue(element.GetRawText());
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
				// Fallback: raw text
				writer.WriteRawValue(element.GetRawText());
				break;
		}
	}

    private static string Sha256Hex(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}