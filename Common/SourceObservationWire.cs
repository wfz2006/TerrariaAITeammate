using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

/// <summary>Historical view of a complete, already read sources page. Native results remain immutable.</summary>
public static class SourceObservationWire
{
    public const string View = "acknowledged_sources_summary_v1";
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] StandSampleFields = { "x", "standRow", "centerTileX", "centerTileY", "feetTileY" };

    internal static bool IsCompletePage(JsonElement body, int worldId, string epoch, ulong completedTick)
    {
        if (body.ValueKind != JsonValueKind.Object || !Text(body, "topic", "sources") ||
            !Text(body, "source", "world_index_and_native_mine_preflight") || !Text(body, "status", "complete") ||
            !True(body, "complete") || !True(body, "available") || !True(body, "inputValid") ||
            body.TryGetProperty("worldId", out _) && !Integer(body, "worldId", worldId) ||
            body.TryGetProperty("epoch", out _) && !Text(body, "epoch", epoch) ||
            !Integer(body, "currentWorldId", worldId) || !Text(body, "currentEpoch", epoch) ||
            !body.TryGetProperty("world", out JsonElement world) || world.ValueKind != JsonValueKind.Object ||
            !Integer(world, "worldId", worldId) || !Text(world, "epoch", epoch) ||
            !Tick(body, "scanStartedAtTick", out ulong started) || !Tick(body, "scanCompletedAtTick", out ulong completed) ||
            completed != completedTick || completed < started || !Tick(body, "pageObservedAtTick", out ulong pageTick) || pageTick != started ||
            !Tick(world, "observedAtTick", out ulong worldTick) || worldTick != started ||
            !body.TryGetProperty("origin", out JsonElement origin) || origin.ValueKind != JsonValueKind.Object ||
            !Coordinate(origin, "x") || !Coordinate(origin, "y") ||
            !body.TryGetProperty("query", out JsonElement query) || query.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("paging", out JsonElement paging) || paging.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("groups", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array ||
            !body.TryGetProperty("candidates", out JsonElement candidates) || candidates.ValueKind != JsonValueKind.Array ||
            !NativeBody(body, 0)) return false;

        // A page is assembled over multiple ticks. The descriptor records completion;
        // the index/world header records start, and each candidate retains its own tick.
        foreach (JsonElement candidate in candidates.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object || !Tick(candidate, "observedAtTick", out ulong observed) || observed < started || observed > completed ||
                !candidate.TryGetProperty("preflight", out JsonElement preflight) || preflight.ValueKind != JsonValueKind.Object ||
                preflight.TryGetProperty("worldId", out JsonElement candidateWorld) && (candidateWorld.ValueKind != JsonValueKind.Number || !candidateWorld.TryGetInt32(out int actualWorld) || actualWorld != worldId) ||
                preflight.TryGetProperty("epoch", out JsonElement candidateEpoch) && (candidateEpoch.ValueKind != JsonValueKind.String || candidateEpoch.GetString() != epoch) ||
                preflight.TryGetProperty("observedAtTick", out JsonElement preflightTick) && (preflightTick.ValueKind != JsonValueKind.Number || !preflightTick.TryGetUInt64(out ulong checkedTick) || checkedTick != observed)) return false;
        }
        return groups.EnumerateArray().All(group => group.ValueKind == JsonValueKind.Object);
    }

    private static bool NativeBody(JsonElement node, int depth)
    {
        if (depth > 96) return false;
        if (node.ValueKind == JsonValueKind.Array) return node.EnumerateArray().All(child => NativeBody(child, depth + 1));
        if (node.ValueKind != JsonValueKind.Object) return true;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty field in node.EnumerateObject())
            if (!names.Add(field.Name) || field.Name is "wireView" or "$ref" ||
                field.Name == "detailOmittedFromWire" && field.Value.ValueKind == JsonValueKind.True || !NativeBody(field.Value, depth + 1)) return false;
        // preflight.detailOmitted is native-domain scope, not a wire omission marker.
        return true;
    }

    public static JsonObject? Project(JsonObject original, string id) => Project(original, id, false);

    internal static JsonObject? Project(JsonObject original, string id, bool olderPage)
    {
        if (original["scanCompletedAtTick"] is not JsonValue completed || !completed.TryGetValue(out ulong tick) ||
            original["currentWorldId"] is not JsonValue current || !current.TryGetValue(out int worldId) ||
            original["currentEpoch"] is not JsonValue epochValue || !epochValue.TryGetValue(out string? epoch) || string.IsNullOrEmpty(epoch) ||
            new[] { "historical", "currentWorldRechecked", "summaryDefaults", "summaryDecoding", "summaryScope", "omittedFields", "retrieval" }.Any(original.ContainsKey) ||
            !IsCompletePage(JsonSerializer.SerializeToElement(original, Options), worldId, epoch, tick) ||
            original["candidates"]!.AsArray().OfType<JsonObject>().Any(candidate => candidate["samples"] is JsonArray samples &&
                samples.OfType<JsonObject>().Any(sample => sample.ContainsKey("geometryFromCandidate")))) return null;

        olderPage &= !original.ContainsKey("archiveDetail");
        var result = original.DeepClone().AsObject();
        var omitted = new SortedSet<string>(StringComparer.Ordinal);
        bool archivedDetail = false;
        void Remove(JsonObject? owner, string field, string path)
        { if (owner != null && owner.Remove(field)) omitted.Add(path + "." + field); }
        void OmitExplanation(JsonObject? owner, string field, string path)
        {
            // Null, false and unfamiliar value shapes remain observations. Only
            // the known explanatory string is omitted after its full-read ACK.
            if (owner?[field] is JsonValue value && value.TryGetValue(out string? _)) Remove(owner, field, path);
        }
        void Archive(JsonObject? owner, string field, string path)
        {
            Remove(owner, field, path);
            archivedDetail = true;
        }
        void ArchiveNumbers(JsonObject? owner, string path, params string[] fields)
        {
            foreach (string field in fields)
                if (owner?[field] is JsonValue value && value.TryGetValue(out int _)) Archive(owner, field, path);
        }
        void ArchiveStandExamples(JsonObject? geometry, string path)
        {
            if (geometry?["standSamples"] is JsonArray { Count: > 0 } stands && stands.All(node =>
                node is JsonObject stand && stand.Count == StandSampleFields.Length && StandSampleFields.All(field =>
                    stand[field] is JsonValue value && value.GetValueKind() == JsonValueKind.Number)))
                Archive(geometry, "standSamples", path);
        }
        foreach (string field in new[] { "elapsedMilliseconds", "candidateWorkMilliseconds" }) Remove(result, field, "sources");
        Remove(result, "note", "sources");
        foreach (string field in new[] { "distanceFilter", "countMeaning", "preflightContract", "indexPageValidationReadScope" })
            OmitExplanation(result, field, "sources");
        OmitExplanation(result["limits"] as JsonObject, "extraNativeReadScope", "limits");
        OmitExplanation(result["refinement"] as JsonObject, "note", "refinement");
        OmitExplanation(result["world"] as JsonObject, "freshness", "world");
        if (result["world"]?["scan"] is JsonObject scan)
            foreach (string field in new[] { "lastTileReads", "maximumTileReadsPerTick", "totalTileReads", "lastMilliseconds", "maximumMilliseconds", "targetMillisecondsPerTick", "timeLimit" })
                Remove(scan, field, "world.scan");
        JsonObject[] candidates = result["candidates"]!.AsArray().OfType<JsonObject>().ToArray();
        foreach (JsonObject candidate in candidates)
        {
            Remove(candidate, "elapsedMilliseconds", "candidates[]");
            if (candidate["seed"] is not JsonObject seed || candidate["preflight"] is not JsonObject preflight) continue;
            if (olderPage)
            {
                const string approachPath = "candidates[].preflight.approach";
                var approach = preflight["approach"] as JsonObject;
                if (approach?["diagnostic"] is JsonValue diagnostic && diagnostic.TryGetValue(out string? _))
                    Archive(approach, "diagnostic", approachPath);
                if (approach?["failureDiagnostics"] is JsonArray { Count: > 0 } diagnostics &&
                    diagnostics.All(node => node is JsonValue value && value.TryGetValue(out string? _)))
                    Archive(approach, "failureDiagnostics", approachPath);
                ArchiveNumbers(approach, approachPath, "newRouteProbeCount");
                ArchiveNumbers((preflight["returnToObserverStart"] as JsonObject)?["budget"] as JsonObject,
                    "candidates[].preflight.returnToObserverStart.budget", "forwardRouteProbeCount", "reverseRouteProbeCount", "totalRouteProbeCount", "reverseInvocationCount");
                ArchiveStandExamples(candidate["geometry"] as JsonObject, "candidates[].geometry");
            }
            bool SamePoint(JsonObject point) => point.ContainsKey("x") && point.ContainsKey("y") && seed.ContainsKey("x") && seed.ContainsKey("y") &&
                JsonNode.DeepEquals(point["x"], seed["x"]) && JsonNode.DeepEquals(point["y"], seed["y"]);
            if (preflight["requestedSeed"] is JsonObject requested && requested.Count == 2 && SamePoint(requested))
                Remove(preflight, "requestedSeed", "candidates[].preflight");
            if (candidate["mineArguments"] is JsonObject arguments && arguments.Count == 4 && SamePoint(arguments) &&
                arguments["type"]?.GetValue<string>() == "mine" && JsonNode.DeepEquals(arguments["target"], result["resource"]))
                Remove(candidate, "mineArguments", "candidates[]");
            if (preflight["seed"] is JsonObject checkedSeed && SamePoint(checkedSeed))
                foreach (string field in new[] { "x", "y" }) Remove(checkedSeed, field, "candidates[].preflight.seed");
            if (candidate["samples"] is JsonArray samples)
                foreach (JsonObject sample in samples.OfType<JsonObject>())
                {
                    if (olderPage)
                    {
                        ArchiveNumbers(sample, "candidates[].samples[]", "primaryCellCursor", "exposedFaceCount", "nearbyOpenSpaceProbeCount");
                        ArchiveStandExamples(sample["geometry"] as JsonObject, "candidates[].samples[].geometry");
                    }
                    if (!sample.ContainsKey("geometryFromCandidate") && candidate["geometry"] is JsonObject geometry &&
                        sample["geometry"] is JsonObject sampleGeometry && JsonNode.DeepEquals(geometry, sampleGeometry))
                    {
                        Remove(sample, "geometry", "candidates[].samples[]");
                        sample["geometryFromCandidate"] = true;
                    }
                    if (SamePoint(sample)) foreach (string field in new[] { "x", "y" }) Remove(sample, field, "candidates[].samples[]");
                    if (sample.ContainsKey("distanceFromOriginTiles") && seed.ContainsKey("distanceFromQueryOriginTiles") && JsonNode.DeepEquals(sample["distanceFromOriginTiles"], seed["distanceFromQueryOriginTiles"]))
                        Remove(sample, "distanceFromOriginTiles", "candidates[].samples[]");
                    if (sample.ContainsKey("tileType") && preflight["seed"] is JsonObject sourceSeed && sourceSeed.ContainsKey("actualTileType") && JsonNode.DeepEquals(sample["tileType"], sourceSeed["actualTileType"]))
                        Remove(sample, "tileType", "candidates[].samples[]");
                }
        }

        // Factor only known, exactly equal fields. Unknown future metadata and all
        // per-candidate differences stay in place, including explicit false and null.
        var defaults = new JsonObject();
        Factor(defaults, "candidate", candidates, new[] { "normalization", "sampleTreeProduct", "observer", "discovery", "naturalSourceIdentity" });
        Factor(defaults, "sample", candidates.Where(candidate => candidate["samples"] is JsonArray)
            .SelectMany(candidate => candidate["samples"]!.AsArray().OfType<JsonObject>()).ToArray(),
            olderPage ? new[] { "sampleVerifiedAtTick", "sampleProduct", "groupId", "discovery", "nearbyOpenSpaceHint", "nearbySettledStandHint" } :
                new[] { "sampleVerifiedAtTick", "sampleProduct" });
        JsonObject[] preflights = candidates.Select(candidate => candidate["preflight"]!.AsObject()).ToArray();
        Factor(defaults, "preflight", preflights, new[] { "topic", "source", "available", "callSucceeded", "inputValid", "worldId", "resource",
            "returnSafety", "tool", "product", "treeProduct", "sourceSafety", "maximumToolRangePixels", "worldModified", "inventoryModified", "actorsMoved", "miningExecuted", "detailOmitted",
            "canAttempt", "reasons", "actualWithinToolRange", "returnToObserverStart" });
        Factor(defaults, "geometry", candidates.Select(candidate => candidate["geometry"]).OfType<JsonObject>().ToArray(), new[] {
            "available", "geometryStatus", "geometricWorkStandCount", "gridChecks", "standStabilityHorizontalMarginPixels",
            "standSamplesOmitted", "structureProtected", "nativeCanKill", "actualWithinToolRangeByDistance", "maximumToolRangePixels",
            "bodyHeightPixels", "route", "tool", "returnToOrigin", "normalizationNotApplied" });
        if (preflights.All(preflight => preflight["seed"] is JsonObject))
            Factor(defaults, "seed", preflights.Select(preflight => preflight["seed"]!.AsObject()).ToArray(), new[] {
                "hasTile", "actualTileType", "resourceTarget", "targetMatches", "actuated", "structureProtected", "nativeCanKill" });
        if (preflights.All(preflight => preflight["approach"] is JsonObject))
            Factor(defaults, "approach", preflights.Select(preflight => preflight["approach"]!.AsObject()).ToArray(), new[] {
                "status", "selectedStand", "geometricCandidateCount", "routeProbeCount", "newRouteProbeCount", "maximumRouteProbes", "selectedDistanceToTargetPixels",
                "pathWaypointCount", "diagnostic", "routeEndpoint", "routeSearchPerformed", "currentActualRangeNeedsNoRoute", "travelExecuted", "scope",
                "failedStandProbeCount", "failureReasons", "failureGraphStatuses", "failureDiagnostics", "failureDiagnosticsOmitted" });
        result["wireView"] = View; result["detailOmittedFromWire"] = true;
        result["historical"] = true; result["currentWorldRechecked"] = false;
        result["omittedFields"] = new JsonArray(omitted.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
        result["summaryDefaults"] = defaults;
        result["summaryDecoding"] = "Merge summaryDefaults.candidate/sample/preflight/seed/approach/geometry into the corresponding existing objects; explicit properties, including false/null, win. Only for samples marked geometryFromCandidate=true, restore the parent candidate.geometry after merging its defaults, then remove that marker. It was exactly equal, including unknown fields and timestamps. Named duplicate coordinates/distances/type/arguments restore from candidate.seed/resource or preflight.seed. Historical summary, never a full-read receipt.";
        result["summaryScope"] = "ACKed historical bounded page, ID-retrievable original. Page/world time=scan start; archive time=completion; candidate/sample ticks stay distinct. Unknown fields stay. Named explanation/timing omissions do not erase constraints: distance is inclusive straight-line from paired query origin before paging, not route/root distance; indexed chunk counts are not in-band counts or recoverable yield. Wood identities/substitution remain unproven. No current route/tool/protection/work-stand/return/yield/global-absence approval. Coverage, paging, remaining unknown and native preflight.detailOmitted retain original scope; refresh sources for a new native check.";
        if (archivedDetail)
        {
            result["archiveDetail"] = "older_page";
            result["summaryScope"] = result["summaryScope"]!.GetValue<string>() +
                " Older pages keep route and return findings; inspect the same retrieval ID for named diagnostic, probe and geometric sample details.";
        }
        result["retrieval"] = new JsonObject { ["type"] = "inspect", ["target"] = "observation", ["item"] = id, ["historical"] = true, ["currentWorldRechecked"] = false };
        return result.ToJsonString(Options).Length < original.ToJsonString(Options).Length ? result : null;
    }

    private static void Factor(JsonObject defaults, string group, IReadOnlyList<JsonObject> rows, IReadOnlyList<string> fields)
    {
        if (rows.Count < 2) return;
        var common = new JsonObject();
        foreach (string field in fields)
        {
            if (!rows.All(row => row.ContainsKey(field))) continue;
            JsonNode? value = rows.Select(row => row[field]).OrderByDescending(candidate => rows.Count(row => JsonNode.DeepEquals(row[field], candidate))).First();
            // Keep varying objects present in every row so the following lossless
            // table can share their nested facts. Removing only modal whole objects
            // first hides those facts from the recursive defaults pass.
            if ((field == "returnToObserverStart" || rows.All(row => row[field] is JsonObject)) &&
                rows.Any(row => !JsonNode.DeepEquals(row[field], value))) continue;
            if (rows.Count(row => JsonNode.DeepEquals(row[field], value)) < 2) continue;
            common[field] = value?.DeepClone();
            foreach (JsonObject row in rows) if (JsonNode.DeepEquals(row[field], value)) row.Remove(field);
        }
        if (common.Count > 0) defaults[group] = common;
    }

    private static bool Text(JsonElement body, string field, string expected) => body.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() == expected;
    private static bool True(JsonElement body, string field) => body.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    private static bool Integer(JsonElement body, string field, int expected) => body.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int actual) && actual == expected;
    private static bool Tick(JsonElement body, string field, out ulong tick)
    { tick = 0; return body.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out tick); }
    private static bool Coordinate(JsonElement body, string field) => body.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double coordinate) && double.IsFinite(coordinate);
}
