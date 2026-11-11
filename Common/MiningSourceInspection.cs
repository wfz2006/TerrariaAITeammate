using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

public sealed record MiningSourceFrame(int WorldId, string Epoch, ulong Tick, object Owner,
    double CenterX, double CenterY, double Bottom, string GoalIdentity)
{
    public string InventoryToolIdentity { get; init; } = "";
}
public sealed record MiningSourcePage(JsonObject Header, JsonArray Samples);

/// <summary>Stored-sample compatibility followed by bounded live refinement; native work uses read-only delegates.</summary>
public sealed class MiningSourceInspection
{
    public const int MaximumCandidates = 24, MaximumTicks = 600;
    private readonly JsonObject header;
    private readonly JsonArray samples;
    private readonly MiningSourceFrame initial;
    private MiningSourceFrame latest;
    private readonly Func<JsonObject, JsonObject> normalize;
    private readonly Func<int, int, JsonObject> inspect;
    private readonly Func<ulong, bool>? tryBeginSample;
    private readonly MiningSourceRefinement? refinement;
    private readonly Dictionary<(int X, int Y), JsonObject> candidates = new();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private ulong? advancedTick;
    private string status, invalidationReason = "";
    private int processed, fullChecks, duplicates, frames, liveAliasSamplesRetained, liveAliasSamplesOmitted;
    private double observationMilliseconds;
    private bool tickWorkDeferred;
    public bool IsComplete => status != "checking";

    public MiningSourceInspection(JsonObject header, JsonArray samples, MiningSourceFrame initial,
        Func<JsonObject, JsonObject> normalize, Func<int, int, JsonObject> inspect)
        : this(header, samples, initial, normalize, inspect, null) { }

    public MiningSourceInspection(JsonObject header, JsonArray samples, MiningSourceFrame initial,
        Func<JsonObject, JsonObject> normalize, Func<int, int, JsonObject> inspect, Func<ulong, bool>? tryBeginSample)
        : this(header, samples, initial, normalize, inspect, tryBeginSample, null, null) { }

    public MiningSourceInspection(JsonObject header, JsonArray samples, MiningSourceFrame initial,
        Func<JsonObject, JsonObject> normalize, Func<int, int, JsonObject> inspect, Func<ulong, bool>? tryBeginSample,
        Func<int, int, JsonObject>? readLiveCell, Func<int, int, JsonObject>? observeGeometry)
    {
        if (samples.Count > MaximumCandidates || samples.Any(sample => sample is not JsonObject))
            throw new ArgumentException("Sources page must contain at most24 stored sample objects.");
        this.header = header.DeepClone().AsObject(); this.samples = samples.DeepClone().AsArray();
        this.initial = latest = initial; this.normalize = normalize; this.inspect = inspect; this.tryBeginSample = tryBeginSample;
        bool available = Flag(header, "available") == true && Flag(header, "inputValid") == true;
        if (available && readLiveCell != null && observeGeometry != null)
        {
            refinement = new MiningSourceRefinement(this.header, readLiveCell, observeGeometry);
            this.header["query"]!["refineOffset"] ??= 0;
            this.header["query"]!["refineScope"] = MiningSourceRefinementRequest.ComputeScope(this.header);
        }
        status = !available ? "error" : samples.Count == 0 && (refinement == null || refinement.IsComplete) ? "complete" : "checking";
        if (IsComplete) elapsed.Stop();
    }

    public static WorldKnowledgeRequest ParseRequest(string query, int? x = null, int? y = null)
    {
        return MiningSourceRefinementRequest.Parse(query, x, y).Request;
    }

    public void Advance(MiningSourceFrame current)
    {
        latest = current;
        if (status is "cancelled" or "error") return;
        string reason = current.WorldId != initial.WorldId || current.Epoch.Length == 0 || current.Epoch != initial.Epoch ? "world_epoch_changed" :
            !ReferenceEquals(current.Owner, initial.Owner) ? "action_replaced" : current.GoalIdentity != initial.GoalIdentity ? "goal_changed" :
            current.InventoryToolIdentity != initial.InventoryToolIdentity ? "inventory_or_tool_changed" :
            current.Tick < initial.Tick ? "world_tick_rewound" :
            Math.Pow(current.CenterX - initial.CenterX, 2) + Math.Pow(current.CenterY - initial.CenterY, 2) > 16d * 16d ||
                Math.Abs(current.Bottom - initial.Bottom) > 5d ? "observer_pose_changed" : "";
        if (reason.Length > 0) { Invalidate("invalidated", reason); return; }
        if (IsComplete) return;
        if (current.Tick - initial.Tick >= MaximumTicks) { Invalidate("time_budget_exhausted", "600_game_tick_limit_not_no_source_proof"); return; }
        if (advancedTick == current.Tick) return;
        tickWorkDeferred = tryBeginSample?.Invoke(current.Tick) == false;
        if (tickWorkDeferred) return;
        advancedTick = current.Tick; frames++;
        bool live = processed == samples.Count;
        JsonObject? sample = live ? refinement?.Advance(current.Tick, (x, y) => candidates.ContainsKey((x, y))) : samples[processed]!.DeepClone().AsObject();
        if (sample == null)
        {
            if (refinement == null || refinement.IsComplete) { status = "complete"; elapsed.Stop(); }
            return;
        }
        int sx = Integer(sample["x"]), sy = Integer(sample["y"]);
        var timer = Stopwatch.StartNew();
        JsonObject normalized;
        try { normalized = normalize(sample.DeepClone().AsObject()); }
        catch (Exception exception)
        {
            normalized = new JsonObject { ["x"] = sx, ["y"] = sy, ["normalization"] = "native_identity_unavailable",
                ["treeProduct"] = new JsonObject { ["status"] = "unknown", ["itemId"] = null, ["reason"] = exception.GetType().Name } };
        }
        int x = Integer(normalized["x"]), y = Integer(normalized["y"]);
        sample["normalizationObservedAtTick"] = current.Tick;
        sample["sampleProduct"] = Pick(normalized["treeProduct"] as JsonObject, "status", "reason", "itemId", "observedAtTick");
        if (candidates.TryGetValue((x, y), out JsonObject? prior))
        {
            if (!live || liveAliasSamplesRetained < MiningSourceRefinement.MaximumLivePreflights)
            { prior["samples"]!.AsArray().Add(sample); if (live) liveAliasSamplesRetained++; }
            else liveAliasSamplesOmitted++;
            if (live)
            {
                refinement!.RecordAlias();
                prior["additionalLiveAliasCount"] = (prior["additionalLiveAliasCount"]?.GetValue<int>() ?? 0) + 1;
                prior["lastLiveAliasNormalizationObservedAtTick"] = current.Tick;
                prior["aliasPreflightNotRepeated"] = true;
            }
            duplicates++;
        }
        else
        {
            JsonObject full;
            fullChecks++;
            if (live) refinement!.RecordPreflight();
            try { full = inspect(x, y); }
            catch (Exception exception)
            {
                full = new JsonObject { ["available"] = false, ["inputValid"] = true, ["canAttempt"] = null,
                    ["error"] = "native_preflight_unavailable", ["reason"] = exception.GetType().Name };
            }
            JsonObject preflight = SummarizePreflight(full);
            string resource = header["resource"]?.GetValue<string>() ?? "";
            double? distance = Distance(header["origin"] as JsonObject, x, y);
            candidates.Add((x, y), new JsonObject
            {
                ["candidateId"] = resource + ":" + x + ":" + y,
                ["seed"] = new JsonObject { ["x"] = x, ["y"] = y, ["distanceFromQueryOriginTiles"] = distance },
                ["normalization"] = normalized["normalization"]?.DeepClone(),
                ["discovery"] = live ? "bounded_live_refinement" : "stored_index_sample",
                ["naturalSourceIdentity"] = "unknown",
                ["geometry"] = sample["geometry"]?.DeepClone(),
                ["samples"] = new JsonArray(sample), ["sampleTreeProduct"] = CompactTree(normalized["treeProduct"] as JsonObject),
                ["preflight"] = preflight, ["observedAtTick"] = current.Tick,
                ["observer"] = new JsonObject { ["centerTileX"] = current.CenterX / 16, ["centerTileY"] = current.CenterY / 16, ["feetTileY"] = current.Bottom / 16 },
                ["elapsedMilliseconds"] = timer.Elapsed.TotalMilliseconds,
                ["mineArguments"] = new JsonObject { ["type"] = "mine", ["target"] = resource, ["x"] = x, ["y"] = y }
            });
        }
        timer.Stop(); observationMilliseconds += timer.Elapsed.TotalMilliseconds;
        if (!live) processed++;
        if (processed == samples.Count && (refinement == null || refinement.IsComplete)) { status = "complete"; elapsed.Stop(); }
    }

    public void Cancel() => Invalidate("cancelled", "executor_cancelled");

    private void Invalidate(string state, string reason)
    { status = state; invalidationReason = reason; candidates.Clear(); elapsed.Stop(); }

    public JsonObject Progress => new()
    {
        ["status"] = status, ["processedSamples"] = processed, ["totalSamples"] = samples.Count,
        ["fullPreflightChecks"] = fullChecks, ["candidateCount"] = candidates.Count,
        ["normalizationDuplicates"] = duplicates, ["framesAdvanced"] = frames, ["tickWorkDeferred"] = tickWorkDeferred,
        ["liveAliasSamplesRetained"] = liveAliasSamplesRetained, ["liveAliasSamplesOmitted"] = liveAliasSamplesOmitted
    };

    public JsonObject Describe(bool continuationRetained)
    {
        bool valid = status is "checking" or "complete";
        JsonObject result = valid || status == "error" ? header.DeepClone().AsObject() : new JsonObject
        {
            ["inputValid"] = header["inputValid"]?.DeepClone(), ["query"] = header["query"]?.DeepClone(),
            ["groups"] = new JsonArray(), ["error"] = invalidationReason
        };
        result["topic"] = "sources"; result["source"] = "world_index_and_native_mine_preflight";
        result["available"] = valid && Flag(header, "available") == true;
        result["status"] = status; result["complete"] = IsComplete; result["continuationRetained"] = continuationRetained && !IsComplete;
        result["currentWorldId"] = latest.WorldId; result["currentEpoch"] = latest.Epoch;
        foreach (var field in Progress) result[field.Key] = field.Value?.DeepClone();
        result["candidates"] = new JsonArray(candidates.Values.Select(candidate => (JsonNode?)candidate.DeepClone()).ToArray());
        result["scanStartedAtTick"] = initial.Tick; result["scanCompletedAtTick"] = IsComplete ? JsonValue.Create(latest.Tick) : null;
        result["mixedTickObservation"] = initial.Tick != latest.Tick;
        result["elapsedMilliseconds"] = elapsed.Elapsed.TotalMilliseconds;
        result["candidateWorkMilliseconds"] = observationMilliseconds;
        if (valid && refinement != null) result["refinement"] = refinement.Describe();
        if (status == "complete" && result["paging"] is JsonObject paging &&
            paging["nextOffset"] is JsonValue next && next.TryGetValue(out int nextOffset) &&
            header["query"]?["offset"] is JsonValue current && current.TryGetValue(out int currentOffset) &&
            nextOffset > currentOffset && nextOffset <= 50000 &&
            MiningSourceRefinementRequest.TryGetResolvedOrigin(header, out int originX, out int originY))
        {
            paging["nextTask"] = new JsonObject { ["type"] = "inspect", ["target"] = "sources",
                ["item"] = MiningSourceRefinementRequest.PageQuery(header, nextOffset), ["x"] = originX, ["y"] = originY };
        }
        if (status == "complete" && header["query"]?["maxDistance"] is JsonValue max &&
            max.TryGetValue(out int distance) && distance < MiningSourceRefinementRequest.NearbyDiscoveryDistance &&
            !candidates.Values.Any(candidate => candidate["preflight"] is JsonObject preflight && Flag(preflight, "canAttempt") == true) &&
            MiningSourceRefinementRequest.TryGetResolvedOrigin(header, out int widerX, out int widerY))
        {
            result["widerSearchTask"] = new JsonObject
            {
                ["type"] = "inspect", ["target"] = "sources", ["x"] = widerX, ["y"] = widerY,
                ["item"] = $"{header["resource"]!.GetValue<string>()};limit=8;minDistance={distance};maxDistance={MiningSourceRefinementRequest.NearbyDiscoveryDistance}"
            };
        }
        result["limits"] = new JsonObject { ["maximumIndexedGroups"] = 8, ["maximumStoredSamplesPerGroup"] = 3,
            ["maximumSamples"] = MaximumCandidates, ["maximumFullPreflightsPerGameTick"] = 1, ["maximumGameTicks"] = MaximumTicks,
            ["maximumLivePrimaryCellReadsPerGameTick"] = MiningSourceRefinement.MaximumPrimaryReadsPerTick,
            ["maximumLivePrimaryCellReads"] = MiningSourceRefinement.MaximumPrimaryReads,
            ["maximumCandidateAccessChecksPerGameTick"] = MiningSourceRefinement.MaximumCandidateAccessChecksPerTick,
            ["maximumCandidateAccessChecks"] = MiningSourceRefinement.MaximumCandidateAccessChecks,
            ["maximumExposureNeighbourReadsPerPrimaryCell"] = 4,
            ["maximumExposureNeighbourReadsPerGameTick"] = MiningSourceRefinement.MaximumPrimaryReadsPerTick * 4,
            ["maximumExposureNeighbourReads"] = MiningSourceRefinement.MaximumPrimaryReads * 4,
            ["maximumCoarseOpenSpaceProbeReadsPerPrimaryCell"] = 4,
            ["maximumCoarseOpenSpaceProbeReadsPerGameTick"] = MiningSourceRefinement.MaximumPrimaryReadsPerTick * 4,
            ["maximumCoarseOpenSpaceProbeReads"] = MiningSourceRefinement.MaximumPrimaryReads * 4,
            ["coarseOpenSpaceProbeOffsetTiles"] = MiningSourceRefinement.CoarseOpenSpaceProbeOffset,
            ["maximumLiveGeometryEnumerationsPerGameTick"] = 1, ["maximumLiveGeometryEnumerations"] = MiningSourceRefinement.MaximumGeometryChecks,
            ["maximumSafeStandPredicatesPerGeometryEnumeration"] = 195,
            ["maximumLiveSafeStandPredicates"] = MiningSourceRefinement.MaximumGeometryChecks * 195,
            ["maximumLivePreflights"] = MiningSourceRefinement.MaximumLivePreflights,
            ["maximumAdditionalLiveAliasSamples"] = MiningSourceRefinement.MaximumLivePreflights,
            ["maximumTotalFullPreflights"] = MaximumCandidates + MiningSourceRefinement.MaximumLivePreflights,
            ["maximumRouteProbesPerFullPreflight"] = 16,
            ["maximumReversePreviewProbesPerFullPreflight"] = MiningSourceReturnPreview.MaximumReverseRouteProbes,
            ["reversePreviewSharesOriginalRouteProbeLimit"] = true,
            ["reversePreviewFiltersForwardAdmission"] = false,
            ["maximumTotalRouteProbes"] = (MaximumCandidates + MiningSourceRefinement.MaximumLivePreflights) * 16,
            ["primaryCellBudgetBoundsAllEngineReads"] = false,
            ["extraNativeReadScope"] = "Per occupied primary cell: four adjacent exposure reads plus four axial open-space probes at offset5 tiles, only an observation-order hint, not body/stand/path/tool/return proof. At most8 native legality accesses/tick,1536/query read unchanged protection±6/CanKill/actuation. First nonblocked geometry attempt ends the tick, even negative/unavailable:1/tick,256/query,195 safe-stand predicates/enumeration with footprint/body/support/lava helpers; at most3 stand coordinates, routes untested. Full preflight stays1/tick with native tool/tree hooks. Its unchanged16 graph searches (3000 expanded/6000 discovered each) now share at most1 reverse preview to the query's actual observer-start anchor: after16 forward probes reverse is unknown, never a17th search. Reverse preview does not filter forward admission or prove return after mining/to player. Extra helper reads include origin contact/launch checks; no total raw engine-cell-read or2ms guarantee.",
            ["hardTwoMillisecondGuarantee"] = false };
        result["worldModified"] = false; result["inventoryModified"] = false; result["actorsMoved"] = false; result["miningExecuted"] = false;
        result["note"] = "Stored samples keep their first-sample behavior, then optional bounded live refinement inspects only returned group bounds. At most one full same-source explicit mine preflight per tick; one candidate can exceed2ms. Index distance filtering concerns original samples; live refinement also filters each freshly read cell, not normalized roots: compare coordinates/distances. Base aliases share the first preflight's observed tick, not a new check. Natural-source identity is unknown even when native canAttempt=true; do not dismantle unidentified structures. Partial index, stale or unsampled cells remain unknown; empty/negative bounded results are not global exhaustion. Budget exhaustion and untested stands are unknown, not proof of no route. Explicit seed return/cascade facts keep their native scope; source cell counts are not recoverable item quantities and no travel or yield is proven. Wood IDs remain distinct and family capacity does not guarantee recipe substitution. mineArguments are parameters only; the model must choose any action and count. Total elapsed includes waiting frames; candidateWorkMilliseconds sums normalization/preflight work, excluding live scan/geometry work.";
        return result;
    }

    public static JsonObject NormalizeWoodSeed(int x, int y, JsonObject product, Func<int, int, bool> baseStillValid)
    {
        bool known = product["status"]?.GetValue<string>() == "known_vanilla_root_soil_identity" &&
            product["itemId"] is JsonValue id && id.TryGetValue(out int item) && item > 0 &&
            product["baseTrunk"] is JsonObject point && point["x"] is JsonValue bx && bx.TryGetValue(out int _) &&
            point["y"] is JsonValue by && by.TryGetValue(out int _) && baseStillValid(Integer(point["x"]), Integer(point["y"]));
        return new JsonObject { ["x"] = known ? product["baseTrunk"]!["x"]!.DeepClone() : JsonValue.Create(x),
            ["y"] = known ? product["baseTrunk"]!["y"]!.DeepClone() : JsonValue.Create(y),
            ["normalization"] = known ? "confirmed_native_base_trunk" : "unchanged_sample_identity_unconfirmed",
            ["treeProduct"] = product.DeepClone() };
    }

    public static JsonObject SummarizePreflight(JsonObject full)
    {
        var result = full.DeepClone().AsObject(); var omitted = new List<string>();
        if (result.ContainsKey("detailOmitted")) return result;
        void Omit(JsonObject? obj, string field, string path) { if (obj?.Remove(field) == true) omitted.Add(path + field); }
        Omit(result, "note", ""); Omit(result["tool"] as JsonObject, "scope", "tool.");
        foreach (string field in new[] { "familyItems", "note" }) Omit(result["product"] as JsonObject, field, "product.");
        Omit(result["returnSafety"] as JsonObject, "note", "returnSafety.");
        if (result["approach"] is JsonObject approach)
        {
            string[] generated = { "failedStandProbeCount", "failureReasons", "failureGraphStatuses", "failureDiagnostics", "failureDiagnosticsOmitted" };
            string[] knownFailureFields = { "x", "standRow", "centerTileX", "centerTileY", "reason", "diagnostic" };
            if (!generated.Any(approach.ContainsKey) && approach["failedStandProbes"] is JsonArray failures && failures.All(failure =>
                failure is JsonObject obj && obj.All(field => knownFailureFields.Contains(field.Key)) &&
                obj["reason"] is JsonValue reason && reason.TryGetValue(out string? _) &&
                obj["diagnostic"] is JsonValue diagnostic && diagnostic.TryGetValue(out string? _)))
            {
                approach["failedStandProbeCount"] = failures.Count;
                approach["failureReasons"] = new JsonArray(failures.Select(failure => failure!["reason"]!.GetValue<string>()).Distinct().Select(reason => (JsonNode?)JsonValue.Create(reason)).ToArray());
                string[] diagnostics = failures.Select(failure => failure!["diagnostic"]).OfType<JsonValue>()
                    .Select(value => value.TryGetValue(out string? diagnostic) ? diagnostic : null).Where(value => value != null).Cast<string>().Distinct().ToArray();
                // These are exact native diagnostic prefixes, not an inference from the
                // outer probe status. Inspect every failure before bounding raw examples.
                string[] knownStatuses = { "searching", "found", "frontier_exhausted", "budget_exhausted", "start_unresolved", "goal_unresolved" };
                string GraphStatus(string diagnostic) => knownStatuses.FirstOrDefault(status => diagnostic.StartsWith(status + " ", StringComparison.Ordinal)) ?? "";
                string[] statuses = diagnostics.Select(GraphStatus).Where(value => value.Length > 0).Distinct().ToArray();
                approach["failureGraphStatuses"] = new JsonArray(statuses.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                string[] examples = statuses.Select(status => diagnostics.First(value => GraphStatus(value) == status))
                    .Concat(diagnostics).Distinct().Take(3).ToArray();
                approach["failureDiagnostics"] = new JsonArray(examples.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                approach["failureDiagnosticsOmitted"] = Math.Max(0, diagnostics.Length - examples.Length);
                Omit(approach, "failedStandProbes", "approach.");
            }
            Omit(approach, "probeElapsedMs", "approach.");
        }
        Omit(result["treeProduct"] as JsonObject, "nativeReadMilliseconds", "treeProduct.");
        result["detailOmitted"] = new JsonArray(omitted.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
        return result;
    }

    private static JsonObject? CompactTree(JsonObject? product)
    {
        if (product == null) return null;
        var copy = product.DeepClone().AsObject(); copy.Remove("nativeReadMilliseconds"); return copy;
    }
    private static JsonObject? Pick(JsonObject? source, params string[] fields)
    {
        if (source == null) return null;
        var result = new JsonObject(); foreach (string field in fields) if (source.ContainsKey(field)) result[field] = source[field]?.DeepClone(); return result;
    }
    private static double? Distance(JsonObject? origin, int x, int y)
        => origin?["x"] is JsonValue ox && ox.TryGetValue(out int ix) && origin["y"] is JsonValue oy && oy.TryGetValue(out int iy)
            ? Math.Sqrt(Math.Pow(x - ix, 2) + Math.Pow(y - iy, 2)) : null;
    private static bool? Flag(JsonObject source, string field) => source[field] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;
    private static int Integer(JsonNode? value) => value is JsonValue number && number.TryGetValue(out int result) && result >= 0 ? result : throw new ArgumentException("Source coordinates must be nonnegative integers.");
}
