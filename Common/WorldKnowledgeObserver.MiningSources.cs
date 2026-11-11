using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public static partial class WorldKnowledgeObserver
{
    /// <summary>Only enumerates the same bounded indexed page; no mining geometry or paths are tested here.</summary>
    public static MiningSourcePage ReadMiningSourcesPage(string query, NPC observer, int? x = null, int? y = null)
    {
        WorldKnowledgeRequest request; MiningSourceRefinementRequest refinement;
        try { refinement = MiningSourceRefinementRequest.Parse(query, x, y); request = refinement.Request; }
        catch (ArgumentException error) { return MiningSourcesPageError("invalid_resource_query", error.Message, false); }
        if (Index.Epoch.Length == 0 || Index.WorldId != Main.worldID)
            return MiningSourcesPageError("world_not_ready", "Current world index is not initialized; no previous world was read.", true);
        int ox = x ?? (int)(observer.Center.X / 16), oy = y ?? (int)(observer.Center.Y / 16);
        if (ox < 0 || oy < 0 || ox >= Main.maxTilesX || oy >= Main.maxTilesY)
            return MiningSourcesPageError("origin_out_of_world", "The paired query origin must be in the current world.", !x.HasValue);

        WorldTileSearch search = Index.FindTiles(ResourceTypes(request.Value), ox, oy, request.Offset, request.Limit, request.MinDistance, request.MaxDistance);
        bool StoredSampleMatches(WorldTileSample point, int type)
        { Tile tile = Main.tile[point.X, point.Y]; return tile.HasTile && tile.TileType == type; }
        var changed = search.Candidates.Where(candidate => candidate.Group.Samples.Any(point => !StoredSampleMatches(point, candidate.Group.TileType)))
            .Select(candidate => (candidate.Chunk.ChunkX, candidate.Chunk.ChunkY)).ToHashSet();
        foreach (var chunk in changed) Invalidate(chunk.ChunkX * WorldKnowledgeIndex.ChunkSize, chunk.ChunkY * WorldKnowledgeIndex.ChunkSize);
        var groups = new JsonArray(); var samples = new JsonArray(); int staleGroups = 0;
        foreach (WorldTileCandidate candidate in search.Candidates)
        {
            if (changed.Contains((candidate.Chunk.ChunkX, candidate.Chunk.ChunkY))) { staleGroups++; continue; }
            WorldTileSample[] matching = candidate.MatchingSamples.Where(point => StoredSampleMatches(point, candidate.Group.TileType))
                .Take(WorldKnowledgeIndex.SampleLimit).ToArray();
            if (matching.Length == 0) { staleGroups++; continue; }
            WorldTileChunk chunk = candidate.Chunk; int type = candidate.Group.TileType;
            string groupId = $"{Index.Epoch}:{chunk.ChunkX}:{chunk.ChunkY}:{type}";
            groups.Add(new JsonObject
            {
                ["id"] = groupId, ["tileType"] = type, ["tileName"] = TileID.Search.GetName(type) ?? type.ToString(),
                ["bounds"] = new JsonObject { ["x"] = chunk.Left, ["y"] = chunk.Top, ["width"] = chunk.Width, ["height"] = chunk.Height },
                ["indexedTileCellCount"] = candidate.Group.CellCount, ["returnedStoredSamples"] = matching.Length,
                ["scanStartedAtTick"] = chunk.ScanStartedAtTick, ["scanCompletedAtTick"] = chunk.ObservedAtTick,
                ["sampleVerifiedAtTick"] = Main.GameUpdateCount
            });
            foreach (WorldTileSample point in matching)
                samples.Add(new JsonObject { ["groupId"] = groupId, ["x"] = point.X, ["y"] = point.Y,
                    ["tileType"] = point.TileType, ["distanceFromOriginTiles"] = Distance(point.X, point.Y, ox, oy),
                    ["sampleVerifiedAtTick"] = Main.GameUpdateCount });
        }
        var header = new JsonObject
        {
            ["available"] = true, ["inputValid"] = true, ["callSucceeded"] = true, ["resource"] = request.Value,
            ["query"] = new JsonObject { ["resource"] = request.Value, ["offset"] = request.Offset, ["limit"] = request.Limit,
                ["minDistance"] = request.MinDistance, ["maxDistance"] = request.MaxDistance, ["x"] = request.X, ["y"] = request.Y,
                ["refineOffset"] = refinement.RefineOffset,
                ["refinementContinuation"] = refinement.RefineScope.Length > 0 },
            ["world"] = JsonSerializer.SerializeToNode(Status()), ["origin"] = new JsonObject { ["x"] = ox, ["y"] = oy },
            ["groups"] = groups, ["pageObservedAtTick"] = Main.GameUpdateCount,
            ["paging"] = new JsonObject { ["offset"] = request.Offset, ["limit"] = request.Limit,
                ["totalIndexedGroups"] = search.TotalMatches, ["returnedGroups"] = groups.Count, ["truncated"] = search.Truncated,
                ["nextOffset"] = search.NextOffset, ["staleGroupsDropped"] = staleGroups, ["changedChunksInvalidated"] = changed.Count,
                ["coverageComplete"] = Index.Ready,
                ["absenceMeaning"] = "only_valid_indexed_groups_and_at_most3_stored_samples_per_group; uncovered_dirty_changed_or_unsampled_cells_remain_unknown; not_global_absence_or_unreachability" },
            ["distanceFilter"] = "inclusive straight-line tile distance from paired query origin (or current observer tile) to original stored samples, before paging; not normalized root distance or route length",
            ["countMeaning"] = "indexedTileCellCount is the whole chunk/type count at scan ticks, not in-band count, connected source size, recoverable item count or yield",
            ["preflightContract"] = "Unchanged explicit-seed InspectMiningInteraction tool/protection/88px/work-stand predicates: at most16 stand probes per unique seed; each synchronous graph search retains3000 unique expanded/6000 discovered limits. Budgets are not enlarged. Budget exhaustion/untested is unknown, not global no-route proof.",
            ["indexPageValidationReadScope"] = "At most24 stored samples are read for stale-group validation, then at most24 matching samples are revalidated when constructing this page. These native index-page checks are additional to the live refinement primary-cell budget."
        };
        string scope = MiningSourceRefinementRequest.ComputeScope(header);
        if (refinement.RefineScope.Length > 0 && refinement.RefineScope != scope ||
            refinement.RefineOffset > groups.OfType<JsonObject>().Sum(group => group["bounds"]!["width"]!.GetValue<int>() * group["bounds"]!["height"]!.GetValue<int>()))
            return MiningSourcesPageError("refinement_cursor_scope_changed", "Current world, origin, filters or indexed group page differs from the continuation. Start a fresh sources query; skipped cells were not rechecked.", false);
        header["query"]!["refineScope"] = scope;
        return new MiningSourcePage(header, samples);
    }

    private static MiningSourcePage MiningSourcesPageError(string code, string message, bool inputValid)
        => new(new JsonObject { ["available"] = false, ["inputValid"] = inputValid, ["callSucceeded"] = true,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }, ["groups"] = new JsonArray() }, new JsonArray());
}
