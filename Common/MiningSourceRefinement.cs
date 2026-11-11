using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

/// <summary>Bounded live observations inside indexed groups. Chooses observations, never an execution goal.</summary>
public sealed class MiningSourceRefinement
{
    public const int MaximumPrimaryReadsPerTick = 256, MaximumPrimaryReads = 32768;
    // Cheap legality rejects may share a tick; geometry/full preflight remain
    // at most once each. With1536 accesses and256 geometry checks, worst-case
    // native selection needs256 + (1536-256)/8 =416 ticks. Add24 stored and128
    // discovery ticks:568, leaving room within the unchanged600-tick session.
    public const int MaximumGeometryChecks = 256, MaximumLivePreflights = 8;
    public const int MaximumCandidateAccessChecks = 1536, MaximumCandidateAccessChecksPerTick = 8;
    public const int CoarseOpenSpaceProbeOffset = 5;
    private sealed record Region(string Id, int Type, int X, int Y, int Width, int Height, int Start)
    { public int Count => Width * Height; }
    private sealed record Pending(string GroupId, int X, int Y, int Type, int Cursor, ulong ObservedTick, double Distance,
        int ExposedFaces, bool? NearbyOpenSpace, bool NearbySettledStand, int ReachProbeReads);
    private readonly JsonObject header;
    private readonly List<Region> regions = new();
    private readonly MiningSourceObservationQueue<Pending> pending;
    private readonly Func<int, int, JsonObject> readCell, geometry;
    private readonly string scope;
    private readonly bool continuation;
    private readonly int initialOffset, totalCells;
    private int cursor, reads, covered, unavailableReads, auxiliaryReads, miningReachProbeReads, matches, represented;
    private int geometryChecks, geometryRejected, geometryUnavailable, fullChecks, candidateAccessChecks, legalityRejected;
    private readonly List<JsonObject> legalityRejectedSamples = new();
    private int? firstUnavailable;
    private ulong? firstReadTick, lastReadTick, lastGeometryTick;
    private string stopped = "";

    public MiningSourceRefinement(JsonObject header, Func<int, int, JsonObject> readCell, Func<int, int, JsonObject> geometry)
    {
        this.header = header.DeepClone().AsObject(); this.readCell = readCell; this.geometry = geometry;
        if (header["groups"] is JsonArray groups)
            foreach (JsonObject group in groups.OfType<JsonObject>())
            {
                if (regions.Count >= 8) throw new ArgumentException("Source refinement allows at most8 indexed groups.");
                JsonObject bounds = group["bounds"]?.AsObject() ?? throw new ArgumentException("Source refinement needs actual group bounds.");
                int x = Number(bounds["x"]), y = Number(bounds["y"]), width = Number(bounds["width"]), height = Number(bounds["height"]);
                if (width is < 1 or > 64 || height is < 1 or > 64 || x > int.MaxValue - width || y > int.MaxValue - height)
                    throw new ArgumentException("Source refinement bounds must be finite world-clipped index chunks.");
                regions.Add(new(group["id"]?.GetValue<string>() ?? "", Number(group["tileType"]), x, y, width, height, totalCells));
                totalCells += width * height;
            }
        scope = MiningSourceRefinementRequest.ComputeScope(header);
        initialOffset = Number(header["query"]?["refineOffset"], 0); cursor = initialOffset;
        string requestedScope = header["query"]?["refineScope"]?.GetValue<string>() ?? "";
        continuation = (header["query"] is JsonObject query ? Flag(query, "refinementContinuation") : null)
            ?? (initialOffset > 0 || requestedScope.Length > 0);
        pending = new MiningSourceObservationQueue<Pending>(diversifyGeometry: !continuation);
        if (initialOffset > totalCells || initialOffset > 0 && requestedScope.Length == 0 || requestedScope.Length > 0 && requestedScope != scope)
            throw new ArgumentException("refinement_cursor_scope_changed: use a fresh sources page; previous coverage was not reused.");
    }

    public bool IsComplete => stopped.Length > 0 || cursor >= totalCells && pending.Count == 0;
    public void RecordPreflight() => fullChecks++;
    public void RecordAlias() => represented++;

    /// <summary>At most256 primary reads and one native geometry enumeration; caller owns the single preflight allowance.</summary>
    public JsonObject? Advance(ulong tick, Func<int, int, bool> alreadyInspected)
    {
        if (fullChecks >= MaximumLivePreflights) stopped = "candidate_budget_exhausted";
        if (geometryChecks >= MaximumGeometryChecks) stopped = "geometry_budget_exhausted";
        if (candidateAccessChecks >= MaximumCandidateAccessChecks) stopped = "candidate_access_budget_exhausted";
        if (IsComplete) return null;
        if (cursor < totalCells)
        {
            int remaining = Math.Min(MaximumPrimaryReadsPerTick, MaximumPrimaryReads - reads);
            while (remaining-- > 0 && cursor < totalCells)
            {
                int position = cursor++; Region region = regions.First(group => position < group.Start + group.Count);
                int local = position - region.Start, x = region.X + local % region.Width, y = region.Y + local / region.Width;
                reads++; firstReadTick ??= tick; lastReadTick = tick;
                JsonObject cell;
                try { cell = readCell(x, y); }
                catch { cell = new JsonObject { ["available"] = false }; }
                int extra = Number(cell["auxiliaryCellReads"], 0);
                int reachReads = Number(cell["miningReachProbeReads"], 0);
                if (reachReads > 4 || extra < reachReads || extra - reachReads > 4)
                    throw new ArgumentException("Live refinement primary reader exceeded four adjacent plus four coarse open-space probes.");
                auxiliaryReads += extra - reachReads; miningReachProbeReads += reachReads;
                if (Flag(cell, "available") != true) { unavailableReads++; firstUnavailable ??= position; continue; }
                covered++;
                if (Flag(cell, "hasTile") != true || Flag(cell, "actuated") != false || Number(cell["tileType"], -1) != region.Type || !InDistance(x, y)) continue;
                matches++;
                if (alreadyInspected(x, y)) { represented++; continue; }
                double distance = Distance(x, y);
                int exposed = Number(cell["exposedFaceCount"], 0); bool? nearby = Flag(cell, "nearbyOpenSpaceHint");
                bool nearStand = Flag(cell, "nearbySettledStandHint") == true;
                int hint = nearStand ? -2 : exposed > 0 || nearby == true ? -1 : 0;
                pending.Enqueue(new(region.Id, x, y, region.Type, position, tick, distance, exposed, nearby, nearStand, reachReads),
                    x, y, position, continuation ? (0, 0d, position) : (hint, distance, position));
            }
            if (reads >= MaximumPrimaryReads && cursor < totalCells) stopped = "primary_read_budget_exhausted";
        }
        // Discover the entire bounded page before spending scarce native slots.
        // Previously, a buried first batch could consume all 256 geometry checks
        // while exposed resources in a later group were never even read.
        if (cursor < totalCells || pending.Count == 0) return null;
        // First-page nearby-open-space hints/distance rank observations only.
        // Continuations consume row-major cursors so a low-cursor buried seed
        // cannot repeatedly rewind the same higher-priority batch. No candidate
        // observation from a prior query is reused as current evidence.
        for (int accessThisTick = 0; accessThisTick < MaximumCandidateAccessChecksPerTick && pending.Count > 0 &&
            candidateAccessChecks < MaximumCandidateAccessChecks; accessThisTick++)
        {
            Pending next = pending.Dequeue();
            int sx = next.X, sy = next.Y;
            if (alreadyInspected(sx, sy)) { represented++; continue; }
            candidateAccessChecks++;
            JsonObject observed;
            try { observed = geometry(sx, sy); }
            catch (Exception error) { observed = new JsonObject { ["available"] = false, ["reason"] = error.GetType().Name }; }
            if (Flag(observed, "available") == true && Flag(observed, "sourceEligibilityBlocked") == true)
            {
                // Same native protection/CanKill result as the actual skill;
                // not a geometry search or a complete mine preflight.
                legalityRejected++;
                if (legalityRejectedSamples.Count < 8)
                    legalityRejectedSamples.Add(new JsonObject { ["x"] = sx, ["y"] = sy, ["observedAtTick"] = tick,
                        ["structureProtected"] = observed["structureProtected"]?.DeepClone(),
                        ["nativeCanKill"] = observed["nativeCanKill"]?.DeepClone(),
                        ["actuated"] = observed["actuated"]?.DeepClone(), ["hasTile"] = observed["hasTile"]?.DeepClone(),
                        ["reasons"] = observed["reasons"]?.DeepClone() });
                continue;
            }
            pending.MarkGeometryObserved(sx, sy); // Scheduling only, including an unavailable attempt; no route/cell proof.
            geometryChecks++; lastGeometryTick = tick;
            if (Flag(observed, "available") != true || observed["geometricWorkStandCount"] == null)
            { geometryUnavailable++; firstUnavailable = Math.Min(firstUnavailable ?? next.Cursor, next.Cursor); return null; }
            bool eligible = Flag(observed, "actualWithinToolRangeByDistance") == true || Number(observed["geometricWorkStandCount"]) > 0;
            if (!eligible) { geometryRejected++; return null; }
            JsonObject measured = observed.DeepClone().AsObject();
            measured["measurementSeed"] = new JsonObject { ["x"] = sx, ["y"] = sy };
            measured["normalizationNotApplied"] = true;
            return new JsonObject { ["groupId"] = next.GroupId, ["x"] = sx, ["y"] = sy, ["tileType"] = next.Type,
                ["distanceFromOriginTiles"] = next.Distance, ["sampleVerifiedAtTick"] = next.ObservedTick,
                ["exposedFaceCount"] = next.ExposedFaces, ["nearbyOpenSpaceHint"] = next.NearbyOpenSpace,
                ["nearbySettledStandHint"] = next.NearbySettledStand,
                ["nearbyOpenSpaceProbeCount"] = next.ReachProbeReads,
                ["discovery"] = "bounded_live_refinement", ["primaryCellCursor"] = next.Cursor,
                ["geometry"] = measured, ["geometryObservedAtTick"] = tick };
        }
        return null;
    }

    public JsonObject Describe()
    {
        bool budget = stopped.Length > 0 || fullChecks >= MaximumLivePreflights || geometryChecks >= MaximumGeometryChecks || candidateAccessChecks >= MaximumCandidateAccessChecks;
        int? next = pending.Count > 0 ? pending.EarliestCursor : cursor < totalCells ? cursor : null;
        if (firstUnavailable.HasValue) next = Math.Min(next ?? firstUnavailable.Value, firstUnavailable.Value);
        string state = stopped.Length > 0 ? stopped : fullChecks >= MaximumLivePreflights && (pending.Count > 0 || cursor < totalCells) ? "candidate_budget_exhausted" :
            geometryChecks >= MaximumGeometryChecks && (pending.Count > 0 || cursor < totalCells) ? "geometry_budget_exhausted" : IsComplete ? "complete" : "checking";
        string? nextQuery = next.HasValue ? MiningSourceRefinementRequest.ContinueQuery(header, next.Value, scope) : null;
        bool anchored = MiningSourceRefinementRequest.TryGetResolvedOrigin(header, out int originX, out int originY);
        return new JsonObject
        {
            ["status"] = state, ["scope"] = scope, ["scopeMeaning"] = "enumeration_identity_only_not_same_world_snapshot_or_tile_version",
            ["refineOffset"] = initialOffset, ["cursor"] = cursor, ["totalGroupCells"] = totalCells,
            ["discoveryComplete"] = cursor == totalCells,
            ["selectionOrder"] = continuation ? "bounded_page_discovery_then_row_major_cursor_asc" :
                "bounded_page_discovery_then_8x8_geometry_diversity_settled_stand_hint_open_hint_distance_cursor",
            ["geometryDiversityEnabled"] = !continuation, ["geometryDiversityBucketSizeTiles"] = 8,
            ["geometryAttemptedBucketCount"] = pending.GeometryObservedBucketCount,
            ["geometryDeferredMatchingCells"] = pending.DeferredCandidateCount,
            ["refinementContinuation"] = continuation,
            ["maximumPendingSourceCells"] = MaximumPrimaryReads,
            ["primaryCellReads"] = reads, ["coveredCellCount"] = covered, ["unavailableCellReads"] = unavailableReads,
            ["auxiliaryExposureCellReads"] = auxiliaryReads, ["observedMatchingCells"] = matches, ["alreadyRepresentedCells"] = represented,
            ["auxiliaryMiningReachProbeReads"] = miningReachProbeReads, ["coarseOpenSpaceProbeOffsetTiles"] = CoarseOpenSpaceProbeOffset,
            ["coarseOpenSpaceMeaning"] = "Only four axial probes at the named offset; an open cell is not a body-sized stand, mining approval, reachable route or return proof. False does not describe the entire neighbourhood; unread cells remain unknown.",
            ["geometryChecks"] = geometryChecks, ["geometryRejectedCells"] = geometryRejected, ["geometryUnavailableCells"] = geometryUnavailable,
            ["candidateAccessChecks"] = candidateAccessChecks, ["maximumCandidateAccessChecks"] = MaximumCandidateAccessChecks,
            ["maximumCandidateAccessChecksPerTick"] = MaximumCandidateAccessChecksPerTick,
            ["nativeLegalityRejectedCells"] = legalityRejected,
            ["nativeLegalityRejectedSamples"] = new JsonArray(legalityRejectedSamples.Select(sample => (JsonNode?)sample.DeepClone()).ToArray()),
            ["nativeLegalityRejectedSamplesOmitted"] = Math.Max(0, legalityRejected - legalityRejectedSamples.Count),
            ["fullPreflightChecks"] = fullChecks, ["reportedLiveCandidates"] = fullChecks,
            ["untestedMatchingCells"] = pending.Count, ["unreadCellCount"] = totalCells - cursor,
            ["notFullyPreflightedMatchingCells"] = Math.Max(0, matches - represented - fullChecks),
            ["unreportedMatchingCells"] = Math.Max(0, matches - represented - fullChecks),
            ["allNativePreflightsComplete"] = initialOffset == 0 && cursor == totalCells && unavailableReads == 0 && pending.Count == 0 && geometryUnavailable == 0 && matches == represented + fullChecks,
            ["unknownRemaining"] = initialOffset > 0 || cursor < totalCells || pending.Count > 0 || unavailableReads > 0 || geometryUnavailable > 0 || geometryRejected > 0 || legalityRejected > 0,
            ["prefixNotRechecked"] = initialOffset > 0, ["prefixCellCountNotRechecked"] = initialOffset,
            ["coverageComplete"] = initialOffset == 0 && cursor == totalCells && unavailableReads == 0 && pending.Count == 0 && geometryUnavailable == 0 && legalityRejected == 0,
            ["primaryCellCoverageComplete"] = initialOffset == 0 && cursor == totalCells && unavailableReads == 0,
            ["budgetExhausted"] = budget && next.HasValue, ["nextOffset"] = next.HasValue ? JsonValue.Create(next.Value) : null,
            ["nextQuery"] = nextQuery,
            ["nextTask"] = nextQuery == null ? null : new JsonObject
            {
                ["type"] = "inspect", ["target"] = "sources", ["item"] = nextQuery,
                ["x"] = anchored ? JsonValue.Create(originX) : header["query"]?["x"]?.DeepClone(),
                ["y"] = anchored ? JsonValue.Create(originY) : header["query"]?["y"]?.DeepClone()
            },
            ["firstCellReadAtTick"] = firstReadTick.HasValue ? JsonValue.Create(firstReadTick.Value) : null,
            ["lastCellReadAtTick"] = lastReadTick.HasValue ? JsonValue.Create(lastReadTick.Value) : null,
            ["lastGeometryObservedAtTick"] = lastGeometryTick.HasValue ? JsonValue.Create(lastGeometryTick.Value) : null,
            ["groups"] = new JsonArray(regions.Select(group => (JsonNode?)new JsonObject
            {
                ["groupId"] = group.Id, ["tileType"] = group.Type,
                ["bounds"] = new JsonObject { ["x"] = group.X, ["y"] = group.Y, ["width"] = group.Width, ["height"] = group.Height },
                ["cursorStart"] = group.Start, ["cursorEndExclusive"] = group.Start + group.Count,
                ["currentQueryReadCells"] = Math.Max(0, Math.Min(cursor, group.Start + group.Count) - Math.Max(initialOffset, group.Start)),
                ["notReadThisQuery"] = group.Count - Math.Max(0, Math.Min(cursor, group.Start + group.Count) - Math.Max(initialOffset, group.Start))
            }).ToArray()),
            ["note"] = "Row-major discovery over the bounded indexed page precedes native candidate selection. At most32768 primitive pending records, not cached terrain or current route proof. First-page exposure/distance only rank observations; continuations consume cursors in row-major order to avoid repeating a high-priority batch. Buried seeds remain unknown until checked. Current query reads only; skipped prefixes are historical unknown. Unavailable cells may keep the next cursor from advancing; never skip them as if observed. coverageComplete concerns bounded cell/geometry observation, not all native preflights or routes. Geometry rejection is this window, not global unreachability. Unavailable, untested, dirty/unindexed cells and unreturned groups remain unknown. Native legality does not prove natural origin or dismantling permission. Continuation can reread tested cells after the first remaining unknown; it carries no prior candidate proof."
        };
    }

    private double Distance(int x, int y) => Math.Sqrt(Math.Pow(x - Number(header["origin"]?["x"]), 2) + Math.Pow(y - Number(header["origin"]?["y"]), 2));
    private bool InDistance(int x, int y)
    {
        double distance = Distance(x, y); JsonNode? min = header["query"]?["minDistance"], max = header["query"]?["maxDistance"];
        return distance >= Number(min, 0) && (max == null || distance <= Number(max));
    }
    private static bool? Flag(JsonObject source, string field) => source[field] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;
    private static int Number(JsonNode? node, int? fallback = null) => node is JsonValue value && value.TryGetValue(out int number) && number >= 0 ? number :
        fallback ?? throw new ArgumentException("Refinement coordinates/counts require nonnegative integers.");
}
