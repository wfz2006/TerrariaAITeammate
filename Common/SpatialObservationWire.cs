using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

/// <summary>Historical wire view only. Exact original detail stays in the goal-owned archive.</summary>
public static class SpatialObservationWire
{
    public const string View = "acknowledged_spatial_summary_v1";
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonNode? Origin(JsonObject body, string topic) => topic switch
    {
        "terrain" or "site" => body["measurement"] is JsonObject measurement ? measurement["bounds"] : null,
        "route" => body["measurement"] is JsonObject route ? route["actualStart"] : null,
        _ => null
    };

    public static JsonObject? Project(JsonObject original, string id)
    {
        string? topic = Text(original["topic"]);
        if (topic is not ("terrain" or "site" or "route") || Text(original["source"]) != "live_world" ||
            original["available"] is not JsonValue available || !available.TryGetValue(out bool valid) || !valid ||
            original["measurement"] is not JsonObject measurement ||
            new[] { "wireView", "detailOmittedFromWire", "omittedFields", "summaryScope", "retrieval" }.Any(original.ContainsKey)) return null;
        if (topic == "route")
        {
            if (Text(measurement["kind"]) != "navigation_route_observation" ||
                Text(measurement["searchStatus"]) is not ("found" or "searching" or "budget_exhausted" or "frontier_exhausted" or
                    "start_unresolved" or "goal_unresolved" or "invalidated" or "cancelled" or "time_budget_exhausted" or "unknown") ||
                !Columns(measurement["waypointColumns"], "standX", "standRow", "centerTileX", "centerTileY", "feetTileY") ||
                !measurement.ContainsKey("waypoints") || measurement["waypoints"] is not null && !NumericRows(measurement["waypoints"], 5)) return null;
        }
        else
        {
            if (Text(measurement["kind"]) != "construction_site_observation" || measurement["bounds"] is not JsonObject ||
                !Grid(measurement, "tile", new[] { "x", "count", "actualTileType(-1=air)", "solid", "platform", "actuated", "slope", "halfBlock" }) ||
                !Grid(measurement, "wall", new[] { "x", "count", "actualWallType(0=none)" }) ||
                !Grid(measurement, "liquid", new[] { "x", "count", "liquidType", "amount", "lava" }) ||
                !Grid(measurement, "object", new[] { "x", "count", "actualTileType", "door", "furniture" }) ||
                measurement["supportBelow"] is not JsonObject support ||
                !Columns(support["columns"], "x", "count", "tileType(-1=air)", "solid", "platform", "actuated", "slope", "halfBlock") ||
                !NumericRows(support["runs"], 8)) return null;
            if (original.ContainsKey("standingCandidates") &&
                (original["standingCandidates"] is not JsonObject stands || stands["listed"] is not JsonArray)) return null;
            if (original.ContainsKey("miningFacts") &&
                (original["miningFacts"] is not JsonObject mining ||
                    !Columns(mining["miningProtectionColumns"], "x", "count", "structureProtected", "nativeCanKill", "actuated") ||
                    !RunRows(mining["miningProtectionRuns"], 5) ||
                    !Columns(mining["resourceColumns"], "x", "count", "resourceClassIndex") || !RunRows(mining["resourceTargetRuns"], 3))) return null;
        }

        var result = original.DeepClone().AsObject();
        var omitted = new List<string>();
        void Omit(JsonObject owner, string field, string path)
        { if (owner.Remove(field)) omitted.Add(path + "." + field); }
        var observed = result["measurement"]!.AsObject();
        if (topic == "route")
        {
            Omit(observed, "waypoints", "measurement"); Omit(observed, "waypointColumns", "measurement");
        }
        else
        {
            // These are material/placement catalogs of an already read window,
            // not current capability or route evidence. Keep the original in
            // the same ID archive instead of repeating it after every action.
            foreach (string field in new[] { "tileTypes", "wallTypes" })
                if (result[field] is JsonArray) Omit(result, field, topic);
            foreach (string field in new[] { "placementCatalogSource", "placementCatalogNote" })
                if (result[field] is JsonValue text && text.TryGetValue(out string? _)) Omit(result, field, topic);
            foreach (string prefix in new[] { "tile", "wall", "liquid", "object" })
            { Omit(observed, prefix + "Runs", "measurement"); Omit(observed, prefix + "RunColumns", "measurement"); }
            var support = observed["supportBelow"]!.AsObject();
            Omit(support, "runs", "measurement.supportBelow"); Omit(support, "columns", "measurement.supportBelow");
            if (result["standingCandidates"] is JsonObject stands) Omit(stands, "listed", "standingCandidates");
            if (result["miningFacts"] is JsonObject mining)
                foreach (string field in new[] { "miningProtectionRuns", "miningProtectionColumns", "resourceTargetRuns", "resourceColumns" })
                    Omit(mining, field, "miningFacts");
        }
        result["wireView"] = View;
        result["detailOmittedFromWire"] = true;
        result["omittedFields"] = new JsonArray(omitted.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
        result["summaryScope"] = "Previously acknowledged historical observation. Named omissions are detail, not air/zero/false or new absence evidence. Original run/list decoding notes apply only to retrieved detail. All retained counts, bounds, query/target, partial/truncated/unknown flags, route status/limits/projections/errors and read timestamps keep their original meaning; this is not a fresh world check, movement, mining, placement or housing approval.";
        result["retrieval"] = new JsonObject { ["type"] = "inspect", ["target"] = "observation", ["item"] = id,
            ["historical"] = true, ["currentWorldRechecked"] = false };
        return result.ToJsonString(Options).Length < original.ToJsonString(Options).Length ? result : null;
    }

    private static bool Grid(JsonObject measurement, string prefix, string[] columns)
        => Columns(measurement[prefix + "RunColumns"], columns) && RunRows(measurement[prefix + "Runs"], columns.Length);
    private static bool Columns(JsonNode? node, params string[] expected)
        => node is JsonArray columns && columns.Count == expected.Length && columns.Select(Text).SequenceEqual(expected);
    private static bool RunRows(JsonNode? node, int columns)
        => node is JsonArray rows && rows.All(row => row is JsonObject obj && obj.Count == 2 &&
            obj["y"] is JsonValue y && y.TryGetValue(out int _) && NumericRows(obj["runs"], columns));
    private static bool NumericRows(JsonNode? node, int columns)
        => node is JsonArray rows && rows.All(row => row is JsonArray cells && cells.Count == columns &&
            cells.All(cell => cell is JsonValue number && number.TryGetValue(out double value) && double.IsFinite(value)));
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
