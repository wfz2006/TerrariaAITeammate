using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

/// <summary>A measured first-look view. Exact native cell facts remain available through structure_detail.</summary>
public static class StructureOverviewView
{
    public const string View = "structure_overview_v1";
    private const string NativeEncoding = "Each rows[y-grid.y] covers every x left-to-right. Runs are legendKey:cellCount. Expand runs to recover the exact tile and wall at each coordinate.";
    private static readonly HashSet<string> KnownLegendFields = new(StringComparer.Ordinal)
    {
        "key", "hasTile", "tileType", "tileName", "wallType", "wallName", "solid", "platform", "door", "furniture",
        "safeWall", "hazard", "frameX", "frameY", "tileItemId", "tileItemName", "wallItemId", "wallItemName",
        "actuated", "halfBlock", "slope"
    };

    public static JsonObject Project(JsonObject nativeReading)
    {
        var result = nativeReading.DeepClone().AsObject();
        if (Text(nativeReading["topic"]) != "structure" || nativeReading["observation"] is not JsonObject observation ||
            observation.ContainsKey("materialCellCounts") || observation["grid"] is not JsonObject grid ||
            grid.ContainsKey("view") || grid.ContainsKey("legendEncoding") || grid.ContainsKey("shapeRows") ||
            grid.ContainsKey("omittedFields") || grid.ContainsKey("additionalLegendFacts") ||
            Text(grid["encoding"]) != NativeEncoding || !Bounds.TryRead(grid, out Bounds sample) ||
            sample.Width > 97 || sample.Height > 81 ||
            grid["legend"] is not JsonArray legend || legend.Count == 0 || legend.Count > 97 * 81 ||
            grid["rows"] is not JsonArray rows || rows.Count != sample.Height)
            return result;

        var keys = new Dictionary<int, JsonObject>();
        foreach (JsonNode? node in legend)
        {
            if (node is not JsonObject entry || !Int(entry["key"], out int key) || key < 0 || !keys.TryAdd(key, entry))
                return result;
        }
        var cells = new List<JsonObject>(sample.Width * sample.Height);
        var usedCounts = keys.Keys.ToDictionary(key => key, _ => 0);
        var shapes = new JsonArray();
        var glyphs = new HashSet<char>();
        foreach (JsonNode? rowNode in rows)
        {
            string? row = Text(rowNode);
            if (row == null) return result;
            int used = 0;
            var shape = new StringBuilder(sample.Width);
            foreach (string run in row.Split(','))
            {
                string[] parts = run.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int key) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ||
                    count < 1 || count > sample.Width - used || !keys.TryGetValue(key, out JsonObject? entry)) return result;
                used += count;
                usedCounts[key] += count;
                char glyph = Glyph(entry);
                glyphs.Add(glyph); shape.Append(glyph, count);
                for (int index = 0; index < count; index++) cells.Add(entry);
            }
            if (used != sample.Width) return result;
            shapes.Add(shape.ToString());
        }

        var projected = grid.DeepClone().AsObject();
        projected.Remove("encoding"); projected.Remove("rows"); projected.Remove("legend");
        projected["view"] = View;
        projected["completeCellGridProvided"] = false;
        projected["omittedFields"] = new JsonArray("encoding", "rows", "legend");
        projected["detailTarget"] = "structure_detail";
        projected["omissionNote"] = "Exact legend entries and their per-cell RLE associations, including frameX/frameY, material, collision, activation, safety and hazard detail, are omitted. Frame fields may encode multi-tile local frames, styles or orientation. Request inspect target=structure_detail for precise facts; this overview does not validate placement, mechanisms, standing or routes.";
        projected["shapeRows"] = shapes;
        var meanings = new JsonObject();
        foreach (char glyph in glyphs.Order()) meanings[glyph.ToString()] = Meaning(glyph);
        projected["glyphLegend"] = meanings;
        projected["shapeNote"] = "Each shapeRows[row] has width ASCII cells. Absolute tile coordinates are x+column,y+row. Glyphs summarize observed categories only; missing/null shape flags are unknown. These are not material counts or a full collision grid, and do not identify a preferred building or plan.";
        var extra = new JsonArray();
        foreach (var pair in keys.OrderBy(pair => pair.Key))
        {
            var fields = new JsonObject();
            foreach (var field in pair.Value)
                if (!KnownLegendFields.Contains(field.Key)) fields[field.Key] = field.Value?.DeepClone();
            if (fields.Count > 0) extra.Add(new JsonObject { ["key"] = pair.Key, ["observedCellCount"] = usedCounts[pair.Key], ["fields"] = fields });
        }
        if (extra.Count > 0) projected["additionalLegendFacts"] = extra;

        var counts = new JsonObject
        {
            ["unit"] = "observed_cells",
            ["notInventoryYieldOrConstructionBill"] = true,
            ["mappingNote"] = "placementItemIds are positive IDs reported in observed legend entries, not proven unique placement choices. Missing, null or nonpositive IDs count as unmappedCells; use placementCatalog for style, candidate and ambiguity facts. A multi-tile object contributes multiple tile cells, not that many items. Counts do not imply recoverable drops, required construction items or whole-building coverage.",
            ["sampleGrid"] = Count(sample, sample, cells),
            ["reportedBounds"] = Bounds.TryRead(observation["bounds"], out Bounds reported) ? Count(reported, sample, cells) : null
        };
        JsonObject output = result["observation"]!.AsObject();
        output["grid"] = projected;
        output["materialCellCounts"] = counts;
        return result;
    }

    private static JsonObject Count(Bounds scope, Bounds sample, List<JsonObject> cells)
    {
        int observed = 0, emptyTiles = 0, unknownTiles = 0, noWalls = 0, unknownWalls = 0;
        var tiles = new SortedDictionary<int, Material>();
        var walls = new SortedDictionary<int, Material>();
        for (int index = 0; index < cells.Count; index++)
        {
            long x = (long)sample.X + index % sample.Width, y = (long)sample.Y + index / sample.Width;
            if (!scope.Contains(x, y)) continue;
            observed++;
            JsonObject cell = cells[index];
            if (!Bool(cell["hasTile"], out bool hasTile)) unknownTiles++;
            else if (!hasTile) emptyTiles++;
            else if (Int(cell["tileType"], out int tile) && tile >= 0) Add(tiles, tile, cell, "tile");
            else unknownTiles++;
            if (!Int(cell["wallType"], out int wall) || wall < 0) unknownWalls++;
            else if (wall == 0) noWalls++;
            else Add(walls, wall, cell, "wall");
        }
        return new JsonObject
        {
            ["countBounds"] = scope.Json(), ["observedCells"] = observed,
            ["unobservedCells"] = (long)scope.Width * scope.Height - observed,
            ["emptyTileCells"] = emptyTiles, ["unknownTileCells"] = unknownTiles,
            ["noWallCells"] = noWalls, ["unknownWallCells"] = unknownWalls,
            ["tileCells"] = new JsonArray(tiles.Select(pair => (JsonNode)pair.Value.Json("tile", pair.Key)).ToArray()),
            ["wallCells"] = new JsonArray(walls.Select(pair => (JsonNode)pair.Value.Json("wall", pair.Key)).ToArray())
        };
    }

    private static void Add(SortedDictionary<int, Material> groups, int type, JsonObject cell, string kind)
    {
        if (!groups.TryGetValue(type, out Material? material)) groups[type] = material = new Material();
        material.Cells++;
        if (cell.ContainsKey(kind + "Name") && !material.Names.Any(name => JsonNode.DeepEquals(name, cell[kind + "Name"])))
            material.Names.Add(cell[kind + "Name"]?.DeepClone());
        if (Int(cell[kind + "ItemId"], out int id) && id > 0) material.ItemIds.Add(id);
        else material.Unmapped++;
    }

    private sealed class Material
    {
        public int Cells, Unmapped;
        public readonly List<JsonNode?> Names = new();
        public readonly SortedSet<int> ItemIds = new();
        public JsonObject Json(string kind, int type) => new()
        {
            [kind + "Type"] = type, ["observedCells"] = Cells,
            ["names"] = new JsonArray(Names.Select(name => name?.DeepClone()).ToArray()),
            ["placementItemIds"] = new JsonArray(ItemIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()), ["unmappedCells"] = Unmapped
        };
    }

    private readonly record struct Bounds(int X, int Y, int Width, int Height)
    {
        public static bool TryRead(JsonNode? node, out Bounds bounds)
        {
            bounds = default;
            if (node is not JsonObject obj || !Int(obj["x"], out int x) || !Int(obj["y"], out int y) ||
                !Int(obj["width"], out int width) || !Int(obj["height"], out int height) || width < 1 || height < 1) return false;
            bounds = new Bounds(x, y, width, height); return true;
        }
        public bool Contains(long x, long y) => x >= X && x < (long)X + Width && y >= Y && y < (long)Y + Height;
        public JsonObject Json() => new() { ["x"] = X, ["y"] = Y, ["width"] = Width, ["height"] = Height };
    }

    private static bool Int(JsonNode? node, out int value)
    { value = 0; return node is JsonValue number && number.TryGetValue(out value); }
    private static bool Bool(JsonNode? node, out bool value)
    { value = false; return node is JsonValue boolean && boolean.TryGetValue(out value); }
    private static bool True(JsonNode? node) => Bool(node, out bool value) && value;
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static char Glyph(JsonObject entry)
    {
        if (!Bool(entry["hasTile"], out bool occupied)) return '+';
        if (!occupied) return Int(entry["wallType"], out int wall) ? wall == 0 ? '.' : wall > 0 ? ':' : '+' : '+';
        if (True(entry["actuated"])) return 'a';
        if (True(entry["halfBlock"])) return 'h';
        if (Int(entry["slope"], out int slope) && slope is >= 1 and <= 4) return (char)('0' + slope);
        if (True(entry["door"])) return 'D';
        if (True(entry["platform"])) return '=';
        if (True(entry["solid"])) return '#';
        if (True(entry["furniture"]) && Bool(entry["solid"], out bool solid) && !solid) return 'f';
        return '+';
    }
    private static string Meaning(char glyph) => glyph switch
    {
        '.' => "Reported air without a background wall.", ':' => "Reported air with a background wall; material and safety require detail.",
        '#' => "Reported solid-typed tile; missing/null activation, slope or half-block facts remain unknown, not proof of a full blocking square.",
        'h' => "Known halfBlock=true.", >= '1' and <= '4' => "Reported native slope code " + glyph + "; direction is not inferred.",
        '=' => "Reported platform category; exact shape/activation and traversability require detail.",
        'D' => "Reported door category, not an openable/path guarantee.", 'f' => "Reported non-solid furniture category.",
        'a' => "Known actuated=true foreground tile.", _ => "Other or unknown category/shape facts."
    };
}
