using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AlbumArtTool.Core
{
    // Small, reflection-free JSON reading helpers (NativeAOT friendly).
    internal static class Json
    {
        internal static JsonDocument Parse(string text, int maxDepth = 40)
        {
            try { return JsonDocument.Parse(text ?? "", new JsonDocumentOptions { MaxDepth = maxDepth, AllowTrailingCommas = true }); }
            catch (JsonException e) { throw new InvalidDataException("The response was not valid JSON.", e); }
        }

        internal static JsonElement? Get(JsonElement? item, string key)
        {
            if (item is JsonElement element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null)
                return value;
            return null;
        }

        internal static bool Has(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out _);

        internal static string String(JsonElement? item, string key)
        {
            var value = Get(item, key);
            if (value == null) return "";
            switch (value.Value.ValueKind)
            {
                case JsonValueKind.String: return value.Value.GetString() ?? "";
                case JsonValueKind.Number: return value.Value.GetRawText();
                case JsonValueKind.True: return "True";
                case JsonValueKind.False: return "False";
                default: return value.Value.GetRawText();
            }
        }

        internal static IEnumerable<JsonElement> Array(JsonElement? item, string key)
        {
            var value = Get(item, key);
            if (value == null || value.Value.ValueKind != JsonValueKind.Array) yield break;
            foreach (var entry in value.Value.EnumerateArray()) yield return entry;
        }
    }
}
