using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public static partial class WorldKnowledgeObserver
{
    public static object Query(string query, NPC observer, int? x = null, int? y = null)
    {
        WorldKnowledgeRequest request;
        try { request = WorldKnowledgeRequest.Parse(query, x, y); }
        catch (ArgumentException error) { return QueryError(error.Message); }
        if (Index.Epoch.Length == 0 || Index.WorldId != Main.worldID)
            return QueryError("当前世界索引尚未初始化；没有读取上个世界。", "world_not_ready", true);
        if (x.HasValue && (x < 0 || x >= Main.maxTilesX || y < 0 || y >= Main.maxTilesY))
            return QueryError("查询坐标不在当前世界内。");
        int ox = x ?? (int)(observer.Center.X / 16), oy = y ?? (int)(observer.Center.Y / 16);
        object body;
        switch (request.Kind)
        {
            case "overview":
                body = new { supported = new[] { "wood/stone/ore/ruby resource names", "tile:ID", "item:ID", "chests", "chest+x/y", "stations", "items", "npc:ID", "boss:name" },
                    usage = "append ;offset=N;limit=N (default4,max8); location lists also allow ;minDistance=0..100000;maxDistance=0..100000 inclusive, filtered before paging. Tile filters match actual stored samples, not chunk near edges. Paired x/y selects chest or query-distance origin; query existence separately from routes" };
                break;
            case "resource":
                body = TileResults(ResourceTypes(request.Value), request, ox, oy, observer); break;
            case "tile":
                int tile = int.Parse(request.Value);
                if (tile >= Main.tileSolid.Length) return QueryError("tile ID不是当前加载的方块类型。");
                body = TileResults(new HashSet<int> { tile }, request, ox, oy); break;
            case "stations":
                body = TileResults(stationTypes, request, ox, oy); break;
            case "chest":
                var matches = Main.chest.Select((chest, index) => (chest, index))
                    .Where(p => p.chest != null && CompanionChests.IsChestBuilt(p.chest) &&
                        ox >= p.chest.x && ox < p.chest.x + 2 && oy >= p.chest.y && oy < p.chest.y + 2).ToArray();
                body = new { found = matches.Length == 1, matches = matches.Length,
                    chest = matches.Length == 1 ? ChestDescription(matches[0].chest!, matches[0].index, ox, oy, true) : null,
                    selection = "exact_world_2x2_footprint; no_range_limit_for_observation; no_fallback_to_another_chest" };
                break;
            case "chests":
                body = LivePage(ChestEntries(ox, oy), request, "all_current_native_chests"); break;
            case "items":
                body = LivePage(GroundEntries(ox, oy), request, "all_current_native_ground_items"); break;
            case "item":
                int item = int.Parse(request.Value);
                if (!ContentSamples.ItemsByType.ContainsKey(item)) return QueryError("item ID不是当前加载的物品类型。");
                var sources = ChestEntries(ox, oy, item).Concat(GroundEntries(ox, oy, item)).ToList();
                var types = ItemSourceTypes(item);
                body = new { itemId = item, itemName = Lang.GetItemNameValue(item),
                    storedOrDropped = LivePage(sources, request, "current_chests_and_ground; companion/player_inventory_not_transferred"),
                    possibleNaturalSources = types.Count > 0 ? TileResults(types, request, ox, oy) : null,
                    paginationScope = "offset_and_limit_apply_independently_to_each_source_section; maximum8_entries_per_section",
                    sourceCoverage = "known_native_resource_mappings_only; tile_presence_not_guaranteed_item_drop_or_yield; recipes_use_inspect_recipe" };
                break;
            case "npc":
                int npcType = int.Parse(request.Value);
                if (!ContentSamples.NpcsByNetId.Values.Any(n => n.type == npcType)) return QueryError("npc ID不是当前加载的NPC类型。");
                body = LivePage(NpcEntries(npcType, ox, oy), request, "active_native_npcs_now; absent_does_not_predict_future_spawn"); break;
            case "boss":
                int boss = BossRules.ResolveBoss(request.Value);
                body = new { npcType = boss, active = boss != 0 && NPC.AnyNPCs(boss),
                    locations = LivePage(NpcEntries(boss, ox, oy), request, "only_currently_active_matching_npcs"),
                    requirements = BossKnowledge.Query(request.Value, observer, Main.LocalPlayer),
                    note = "未生成Boss没有当前位置；召唤条件和生成后的位置是不同事实。" };
                break;
            default: return QueryError("不支持的world查询。");
        }
        return new
        {
            topic = "world", source = "omniscient_native_world", available = true, inputValid = true,
            query = new { request.Kind, request.Value, request.Offset, request.Limit, request.MinDistance, request.MaxDistance },
            world = Status(), origin = new { x = ox, y = oy }, data = body,
            access = new { reachability = "not_tested", interactionPermission = "not_tested", movementPerformed = false,
                note = "全图位置知识不批准取物/采掘/施工。已知目标可由你直接选择显式explore(x,y)，route仅在需要证明路线时按需查询。正常inspect route动作自动逐帧续算64唯一节点/软限2ms，总限30000唯一/60000发现，最多600游戏ticks后返回一次；动作期间无需LLM轮询。兼容即时查询可能只返回searching，它表示计算未完成，不能当无路。长程行走仍须真实执行，局部受阻不证明全局不可达。" },
            worldModified = false, inventoryModified = false, actorsMoved = false, miningExecuted = false
        };
    }

    private static object QueryError(string message, string code = "invalid_query", bool inputValid = false) => new
    {
        topic = "world", source = "omniscient_native_world", available = false, inputValid,
        error = new { code, message }, worldModified = false, inventoryModified = false, actorsMoved = false
    };

    private static object TileResults(IReadOnlySet<int> types, WorldKnowledgeRequest request, int ox, int oy, NPC? observer = null)
    {
        WorldTileSearch search = Index.FindTiles(types, ox, oy, request.Offset, request.Limit, request.MinDistance, request.MaxDistance);
        var changedChunks = search.Candidates.Where(candidate => candidate.Group.Samples.Any(p =>
            !Main.tile[p.X, p.Y].HasTile || Main.tile[p.X, p.Y].TileType != candidate.Group.TileType))
            .Select(candidate => (candidate.Chunk.ChunkX, candidate.Chunk.ChunkY)).ToHashSet();
        foreach (var changed in changedChunks)
            Invalidate(changed.ChunkX * WorldKnowledgeIndex.ChunkSize, changed.ChunkY * WorldKnowledgeIndex.ChunkSize);
        int staleGroups = 0;
        int accessSamples = 0;
        double accessMilliseconds = 0;
        object Sample(WorldTileSample point)
        {
            var reading = new Dictionary<string, object?>
            {
                ["x"] = point.X, ["y"] = point.Y,
                ["distanceFromOriginTiles"] = Distance(point.X, point.Y, ox, oy), ["standingEvidence"] = "not_tested",
                ["treeProduct"] = request.Kind == "resource" && request.Value == "wood" ? TreeProductWorldObserver.Observe(point.X, point.Y) : null
            };
            if (request.Kind == "resource" && observer != null)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                reading["miningAccess"] = MiningAccessWorldObserver.Observe(observer, point.X, point.Y);
                accessMilliseconds += timer.Elapsed.TotalMilliseconds;
                accessSamples++;
            }
            return reading;
        }
        var entries = new List<object>();
        foreach (WorldTileCandidate candidate in search.Candidates)
        {
            WorldTileGroup group = candidate.Group; WorldTileChunk chunk = candidate.Chunk;
            if (changedChunks.Contains((chunk.ChunkX, chunk.ChunkY))) { staleGroups++; continue; }
            WorldTileSample[] samples = candidate.MatchingSamples.Where(p => Main.tile[p.X, p.Y].HasTile &&
                Main.tile[p.X, p.Y].TileType == group.TileType).ToArray();
            if (samples.Length == 0) { staleGroups++; continue; }
            entries.Add(new
            {
                id = $"{Index.Epoch}:{chunk.ChunkX}:{chunk.ChunkY}:{group.TileType}", kind = "indexed_tile_region",
                tileType = group.TileType, tileName = TileID.Search.GetName(group.TileType) ?? group.TileType.ToString(),
                bounds = new { x = chunk.Left, y = chunk.Top, width = chunk.Width, height = chunk.Height },
                indexedTileCellCount = group.CellCount, countMeaning = "mixed_tick_chunk_scan_tile_cells_not_atomic_snapshot_or_objects_or_harvest_yield",
                scanStartedAtTick = chunk.ScanStartedAtTick, scanCompletedAtTick = chunk.ObservedAtTick,
                sampleVerifiedAtTick = Main.GameUpdateCount,
                samples = samples.Select(Sample).ToArray(),
                sampleMeaning = "actual_matching_cells_now; not_connected_vein_or_safe_mining_seed_or_work_stand",
                estimatedDistanceTiles = Math.Round(candidate.DistanceTiles, 2), distanceMeaning = "straight_line_to_chunk_not_route_length"
            });
        }
        var result = new
        {
            entries, totalIndexedGroups = search.TotalMatches, returned = entries.Count,
            offset = request.Offset, limit = request.Limit, truncated = search.Truncated, nextOffset = search.NextOffset,
            staleGroupsDropped = staleGroups, changedChunksInvalidated = changedChunks.Count,
            distanceFilter = new { minDistance = request.MinDistance, maxDistance = request.MaxDistance,
                appliedBeforePagination = true, inclusive = true, units = "tiles",
                meaning = "straight_line_from_query_origin_tile_coordinate_to_each_returned_actual_sample_tile_coordinate; not_region_edge_or_route_length",
                countsMeaning = "indexedTileCellCount_is_whole_chunk_type_count_not_count_inside_distance_band",
                sampleCoverage = "at_most_three_stored_samples_per_chunk_type; unsampled_matching_cells_can_exist_inside_band",
                ordering = "chunk_near_edge_distance_then_chunk_y_x_and_tile_type; not_nearest_sample_globally" },
            treeProductScope = request.Kind == "resource" && request.Value == "wood"
                ? "Only ordinary native tree identity is confirmed from current root soil; supportSoil is not a mining seed. Other variants/ModTree overrides/unknown soil stay unknown, never default to Wood9. Quantity, actual drops, complete cascade safety, tools and reachability remain unproven. Protection/CanKill flags concern only the named sample/base cells now. Native GetTreeBottom may read downward outside this chunk to maxTilesY-50; span and elapsed time are disclosed, not a 2ms query guarantee. No filtering, ordering or source choice changes."
                : null,
            coverageComplete = Index.Ready,
            absenceMeaning = request.HasDistanceFilter ? "no_recorded_sample_in_band_in_valid_indexed_groups; unsampled_cells_uncovered_dirty_or_changed_regions_unknown; not_absence_or_unreachability"
                : "no_match_only_in_valid_indexed_chunks_at_their_scan_ticks; uncovered_dirty_or_changed_regions_unknown",
            scope = request.Kind == "stations" ? "native_recipe_station_tile_types; tile_cells_not_machine_count" : "native_tile_types_across_world_index"
        };
        if (request.Kind != "resource") return result;
        var withAccess = JsonSerializer.SerializeToNode(result)!.AsObject();
        withAccess["miningAccessScope"] = JsonSerializer.SerializeToNode(new
        {
            observedSamples = accessSamples, maximumSamples = 8 * 3,
            maximumGridChecksPerSample = MiningApproachProbe.MaximumGeometryChecks,
            maximumGridChecksPerPage = 8 * 3 * MiningApproachProbe.MaximumGeometryChecks,
            radiusX = MiningApproachProbe.RadiusX, radiusY = MiningApproachProbe.RadiusY,
            elapsedMilliseconds = Math.Round(accessMilliseconds, 3),
            meaning = "Complete13x15 current geometry enumeration per measured sample, shared with mine; first3 stands are x/y enumeration samples, not selected routes. Uses actual observer pose separately from query distance origin. Only that sample is described, not its whole chunk/vein. Zero geometry does not forbid an already-in-distance interaction. Route, tool, return, cascade and excavation aftermath are not tested. No graph search, sample filtering or task changes; bounded cell work is not a2ms query guarantee."
        });
        return withAccess;
    }

    private static HashSet<int> ResourceTypes(string resource) => resource switch
    {
        "wood" => new() { TileID.Trees, TileID.PalmTree, TileID.VanityTreeSakura, TileID.VanityTreeYellowWillow, TileID.TreeAsh, TileID.LivingWood },
        "stone" => new() { TileID.Stone }, "iron" => new() { TileID.Iron }, "copper" => new() { TileID.Copper },
        "gold" => new() { TileID.Gold }, "silver" => new() { TileID.Silver }, "lead" => new() { TileID.Lead },
        "tin" => new() { TileID.Tin }, "tungsten" => new() { TileID.Tungsten }, "platinum" => new() { TileID.Platinum },
        "meteorite" => new() { TileID.Meteorite }, "ruby" => new() { TileID.Ruby }, "dirt" => new() { TileID.Dirt }, "grass" => new() { TileID.Grass },
        _ => new()
    };

    private static HashSet<int> ItemSourceTypes(int item) => item switch
    {
        ItemID.Wood => ResourceTypes("wood"), ItemID.StoneBlock => ResourceTypes("stone"),
        ItemID.IronOre => ResourceTypes("iron"), ItemID.CopperOre => ResourceTypes("copper"),
        ItemID.GoldOre => ResourceTypes("gold"), ItemID.SilverOre => ResourceTypes("silver"),
        ItemID.LeadOre => ResourceTypes("lead"), ItemID.TinOre => ResourceTypes("tin"),
        ItemID.TungstenOre => ResourceTypes("tungsten"), ItemID.PlatinumOre => ResourceTypes("platinum"),
        ItemID.Meteorite => ResourceTypes("meteorite"), ItemID.Ruby => ResourceTypes("ruby"),
        ItemID.DirtBlock => ResourceTypes("dirt"), _ => new()
    };
}
