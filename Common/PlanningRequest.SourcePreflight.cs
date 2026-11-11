using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

/// <summary>Lossless object defaults for source candidates; no source omission or archive/ACK policy.</summary>
internal static class SourcePreflightWire
{
    public sealed record Replacement(JsonNode Original, JsonNode Shared);
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public const string ObjectDefaultsEncoding = "objects_with_defaults_v1";
    public const string ObjectDefaultsDecoding = "Reconstruct each original object by recursively adding missing properties from defaults to each row. Explicit row properties, including false and null, win. Recurse only when both values are objects; arrays are complete values and are never merged. Every default property existed in every corresponding original object. Preserve row order and every unknown field. If the surrounding page has summaryDefaults, expand this table first, then apply summaryDefaults; historical omission markers still apply.";

    public static IReadOnlyList<Replacement> Compact(JsonObject state)
    {
        var replacements = new List<Replacement>();
        if (state["planning"]?["outcomes"] is not JsonArray outcomes) return replacements;
        for (int index = 0; index < outcomes.Count; index++)
        {
            if (outcomes[index]?["result"] is not JsonObject result) continue;
            JsonObject? page = Text(result["topic"]) == "sources" ? result :
                Text(result["topic"]) == "observation" ? result["observation"] as JsonObject : null;
            if (page == null || Text(page["topic"]) != "sources" ||
                page["candidates"] is not JsonArray candidates || candidates.Count < 2 ||
                candidates.Any(candidate => candidate is not JsonObject)) continue;
            bool historicalSummary = Text(page["wireView"]) == SourceObservationWire.View &&
                Boolean(page["detailOmittedFromWire"], true) && Boolean(page["historical"], true) &&
                Boolean(page["currentWorldRechecked"], false);
            if (HasWireForms(page, historicalSummary, 0)) continue;

            var rows = candidates.DeepClone().AsArray();
            JsonObject defaults = ExtractDefaults(rows.Cast<JsonObject>().ToArray());
            if (defaults.Count == 0) continue;
            var table = new JsonObject
            {
                ["encoding"] = ObjectDefaultsEncoding, ["defaults"] = defaults,
                ["rows"] = rows, ["decoding"] = ObjectDefaultsDecoding
            };
            if (table.ToJsonString(Options).Length >= candidates.ToJsonString(Options).Length) continue;
            JsonNode original = result.DeepClone();
            page["candidates"] = table;
            replacements.Add(new(original, result.DeepClone()));
        }
        return replacements;
    }

    private static JsonObject ExtractDefaults(IReadOnlyList<JsonObject> rows)
    {
        var defaults = new JsonObject();
        foreach (string field in rows[0].Select(pair => pair.Key).ToArray())
        {
            // Absence is a fact: adding a default must never fill a field absent
            // in even one corresponding original object.
            if (!rows.All(row => row.ContainsKey(field))) continue;
            JsonNode?[] values = rows.Select(row => row[field]).ToArray();
            if (values.All(value => value is JsonObject))
            {
                JsonObject nested = ExtractDefaults(values.Cast<JsonObject>().ToArray());
                if (nested.Count > 0) defaults[field] = nested;
                continue;
            }
            // A differing object may have intentionally missing child fields.
            // Do not supply another object's shape to it through recursion.
            if (values.Any(value => value is JsonObject)) continue;
            string[] exactValues = values.Select(value => value?.ToJsonString(Options) ?? "null").ToArray();
            var common = exactValues.GroupBy(value => value, StringComparer.Ordinal).OrderByDescending(group => group.Count()).First();
            if (common.Count() < 2) continue;
            int first = Array.IndexOf(exactValues, common.Key);
            defaults[field] = values[first]?.DeepClone();
            for (int index = 0; index < rows.Count; index++)
                if (exactValues[index] == common.Key) rows[index].Remove(field);
        }
        return defaults;
    }

    private static bool HasWireForms(JsonNode? node, bool historicalSummary, int depth)
    {
        if (depth > 96) return true;
        if (node is JsonArray array) return array.Any(value => HasWireForms(value, false, depth + 1));
        if (node is not JsonObject obj) return false;
        foreach (var property in obj)
        {
            bool summaryMarker = depth == 0 && historicalSummary && property.Key is "wireView" or "detailOmittedFromWire";
            if (!summaryMarker && property.Key is "$ref" or "encoding" or "wireView" or "detailOmittedFromWire" ||
                HasWireForms(property.Value, false, depth + 1)) return true;
        }
        return false;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    private static bool Boolean(JsonNode? node, bool expected) => node is JsonValue value && value.TryGetValue(out bool actual) && actual == expected;
}
