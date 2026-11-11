using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

/// <summary>Optional structural view that explicitly omits native frame fields; never edits a native observation.</summary>
public static class StructurePhysicsView
{
    public const string Encoding = "physics_defaults_and_named_overrides_v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonObject Project(JsonObject grid)
    {
        // Work on detached data only. An unfamiliar/already encoded contract is kept
        // verbatim rather than guessing how its keys or optional fields should decode.
        if (grid.ContainsKey("legendEncoding") || grid.ContainsKey("legendDefaults") ||
            grid.ContainsKey("renderFrameDetailsOmitted") || grid.ContainsKey("omittedFields") ||
            grid.ContainsKey("shapeRows") || grid.ContainsKey("glyphLegend") ||
            !Int(grid["x"], out _) || !Int(grid["y"], out _) ||
            !Int(grid["width"], out int width) || !Int(grid["height"], out int height) ||
            width < 1 || width > 97 || height < 1 || height > 81 ||
            grid["legend"] is not JsonArray legend || legend.Count == 0 || legend.Count > 97 * 81 ||
            grid["rows"] is not JsonArray rows || rows.Count != height)
            return grid.DeepClone().AsObject();

        var oldKeys = new Dictionary<int, string>();
        var classes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (JsonNode? node in legend)
        {
            if (node is not JsonObject entry || !Int(entry["key"], out int key) || key < 0 ||
                !Int(entry["frameX"], out _) || !Int(entry["frameY"], out _) || oldKeys.ContainsKey(key))
                return grid.DeepClone().AsObject();
            var physical = entry.DeepClone().AsObject();
            physical.Remove("key"); physical.Remove("frameX"); physical.Remove("frameY");
            physical = Canonical(physical)!.AsObject();
            string identity = physical.ToJsonString(JsonOptions);
            oldKeys.Add(key, identity);
            classes.TryAdd(identity, physical);
        }

        string[] identities = classes.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var newKeys = identities.Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index, StringComparer.Ordinal);
        var encodedRows = new JsonArray();
        foreach (JsonNode? rowNode in rows)
        {
            if (rowNode is not JsonValue rowValue || !rowValue.TryGetValue(out string? row) || row == null)
                return grid.DeepClone().AsObject();
            var runs = new List<string>();
            int used = 0, previous = -1, previousCount = 0;
            foreach (string run in row.Split(','))
            {
                string[] parts = run.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int oldKey) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ||
                    count < 1 || count > width - used || !oldKeys.TryGetValue(oldKey, out string? identity))
                    return grid.DeepClone().AsObject();
                used += count;
                int key = newKeys[identity];
                if (key == previous) previousCount += count;
                else
                {
                    if (previousCount > 0) runs.Add(Run(previous, previousCount));
                    previous = key; previousCount = count;
                }
            }
            if (used != width) return grid.DeepClone().AsObject();
            runs.Add(Run(previous, previousCount));
            encodedRows.Add(string.Join(',', runs));
        }

        var named = new JsonArray();
        foreach (string identity in identities)
        {
            var entry = new JsonObject { ["key"] = newKeys[identity] };
            foreach (var property in classes[identity]) entry[property.Key] = property.Value?.DeepClone();
            named.Add(entry);
        }
        var defaults = new JsonObject();
        foreach (string field in named[0]!.AsObject().Select(property => property.Key).Where(field => field != "key").OrderBy(field => field, StringComparer.Ordinal).ToArray())
        {
            // Never give a default to a field absent in any entry. Explicit null is
            // retained as a real value and must not be confused with a missing field.
            if (named.OfType<JsonObject>().Any(entry => !entry.ContainsKey(field))) continue;
            var mode = named.OfType<JsonObject>().GroupBy(entry => ValueKey(entry[field]), StringComparer.Ordinal)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).First();
            if (mode.Count() < 2) continue;
            JsonNode? value = mode.First()[field];
            defaults[field] = value?.DeepClone();
            foreach (JsonObject entry in named.OfType<JsonObject>())
                if (JsonNode.DeepEquals(entry[field], value)) entry.Remove(field);
        }

        var projected = grid.DeepClone().AsObject();
        projected["rows"] = encodedRows;
        projected["legend"] = named;
        projected["legendDefaults"] = defaults;
        projected["legendEncoding"] = Encoding;
        projected["legendDecoding"] = "Each named legend entry: copy every legendDefaults field, then overwrite with every entry field. Missing fields stay missing; null stays null. Rows use the resulting key:cellCount, left to right at the original x/y. All supplied non-frame entry fields are retained.";
        projected["renderFrameDetailsOmitted"] = true;
        projected["omittedFields"] = new JsonArray("frameX", "frameY");
        projected["omissionNote"] = "frameX/frameY may encode local multi-tile frames, style or orientation. This view omits both fields; it cannot validate precise native object placement or mechanism state. Other supplied fields remain, and missing/null shape facts are not inferred.";
        var glyphRows = new JsonArray();
        char[] glyphs = identities.Select(identity => Glyph(classes[identity])).ToArray();
        var usedGlyphs = new HashSet<char>();
        foreach (JsonNode? row in encodedRows)
        {
            var text = new StringBuilder(width);
            foreach (string run in row!.GetValue<string>().Split(','))
            {
                string[] parts = run.Split(':');
                char glyph = glyphs[int.Parse(parts[0], CultureInfo.InvariantCulture)];
                text.Append(glyph, int.Parse(parts[1], CultureInfo.InvariantCulture));
                usedGlyphs.Add(glyph);
            }
            glyphRows.Add(text.ToString());
        }
        var glyphLegend = new JsonObject();
        foreach (char glyph in usedGlyphs.Order()) glyphLegend[glyph.ToString()] = GlyphMeaning(glyph);
        projected["shapeRows"] = glyphRows;
        projected["glyphLegend"] = glyphLegend;
        projected["shapeNote"] = "Overview only: shapeRows[y-grid.y] has exactly grid.width ASCII cells from grid.x. Glyphs summarize reported categories, not material counts, path/standing proof or native placement validity. Missing/null shape fields remain unknown; use the exact named legend/catalog and terrain facts. Hazard remains in the exact legend and does not replace a shape glyph.";
        return projected;
    }

    private static bool Int(JsonNode? node, out int value)
    { value = 0; return node is JsonValue number && number.TryGetValue(out value); }
    private static string Run(int key, int count) => key.ToString(CultureInfo.InvariantCulture) + ":" + count.ToString(CultureInfo.InvariantCulture);
    private static string ValueKey(JsonNode? value) => Canonical(value)?.ToJsonString(JsonOptions) ?? "null";
    private static bool True(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool present) && present;
    private static char Glyph(JsonObject entry)
    {
        if (entry["hasTile"] is not JsonValue hasTile || !hasTile.TryGetValue(out bool occupied)) return '+';
        if (!occupied) return Int(entry["wallType"], out int wall) ? wall == 0 ? '.' : wall > 0 ? ':' : '+' : '+';
        if (True(entry["actuated"])) return 'a';
        if (True(entry["halfBlock"])) return 'h';
        if (Int(entry["slope"], out int slope) && slope is >= 1 and <= 4) return (char)('0' + slope);
        if (True(entry["door"])) return 'D';
        if (True(entry["platform"])) return '=';
        if (True(entry["solid"])) return '#';
        if (True(entry["furniture"]) && entry["solid"] is JsonValue solid && solid.TryGetValue(out bool isSolid) && !isSolid) return 'f';
        return '+';
    }
    private static string GlyphMeaning(char glyph) => glyph switch
    {
        '.' => "Reported air without a background wall.",
        ':' => "Reported air with a background wall; wall material/safety is in the exact legend.",
        '#' => "Reported solid-typed tile. Missing/null activation, half-block or slope facts remain unknown; this does not certify a full blocking square.",
        'h' => "Known halfBlock=true.",
        >= '1' and <= '4' => "Reported native slope code " + glyph + "; this is the native numeric code, not a guessed direction.",
        '=' => "Reported platform category; consult exact shape/activation facts in the legend.",
        'D' => "Reported door category; no openable/path guarantee.",
        'f' => "Reported non-solid furniture category.",
        'a' => "Known actuated=true foreground tile.",
        _ => "Other or unknown foreground/category facts."
    };
    private static JsonNode? Canonical(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var field in obj.OrderBy(field => field.Key, StringComparer.Ordinal)) result[field.Key] = Canonical(field.Value);
            return result;
        }
        if (node is JsonArray array) return new JsonArray(array.Select(Canonical).ToArray());
        return node?.DeepClone();
    }
}
