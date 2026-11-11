using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string RubyTravelCase = "p4_ruby_travel_native";
    private const int RubyTravelNearestPages = 4, RubyTravelMaxRefinements = 4, RubyTravelShallowGroups = 8;
    private const int RubyTravelMaxSurveyPages = 20, RubyTravelMaxProbesPerPage = 4, RubyTravelMaxSearchTicks = 600;
    private const int RubyTravelReachableRadiusX = 1600, RubyTravelReachableRadiusY = 720;
    private const int RubyTravelReachableTicks = 1800, RubyTravelReachableCellLimit = 120000;
    private enum RubyTravelPhase { Index, Sources, Graph, SurveyShallowGroups, SurveyReachableStands, Walk }
    private sealed record RubyTravelCandidate(Point Seed, TileStandPose Stand, string Discovery);
    private sealed record RubyTravelGroup(string Id, int Offset, int X, int Y, int Width, int Height, int Score);
    private static string RubyTravelResultPath => Path.Combine(Main.SavePath, "AITeammate", "p4-ruby-travel-native-result.json");
    private readonly List<object> rubyTravelEvents = new();
    private readonly HashSet<Point> rubyTravelProbedStands = new();
    private readonly Queue<RubyTravelCandidate> rubyTravelStands = new();
    private readonly HashSet<string> rubyTravelObservedGroupIds = new();
    private readonly HashSet<(int Offset, int Limit)> rubyTravelRefinedPages = new();
    private readonly HashSet<Point> rubyTravelRubyChunks = new();
    private readonly List<RubyTravelGroup> rubyTravelShallowSurvey = new();
    private readonly Queue<RubyTravelGroup> rubyTravelShallowQueue = new();
    private bool rubyTravelRunning, rubyTravelWideSearch, rubyTravelRubyChunksComplete;
    private DateTime rubyTravelStarted;
    private ShelterWorldAudit? rubyTravelAudit;
    private RubyTravelPhase rubyTravelPhase;
    private Vector2 rubyTravelStart;
    private int rubyTravelPages, rubyTravelPrimaryPages, rubyTravelSurveyPages, rubyTravelSurveyOffset;
    private int rubyTravelReachableCellReads, rubyTravelReachableStableStands;
    private long rubyTravelBoundary;
    private ulong rubyTravelProbeAt;
    private string? rubyTravelNextQuery, rubyTravelNextRefinement;
    private RubyTravelGroup? rubyTravelExpectedGroup;
    private TilePathSearch? rubyTravelSearch;
    private RubyTravelCandidate? rubyTravelCandidate;

    private void BeginRubyTravelAcceptance()
    {
        if (!HouseMode || File.Exists(RubyTravelResultPath)) return;
        rubyTravelStarted = DateTime.UtcNow;
        rubyTravelAudit = ShelterWorldAudit.Capture(3220, 390);
        if (!RubyNativeGatesClosed() || CompanionNpc is not { active: true } || HasLooseRuby() ||
            Loadout.CountItem(ItemID.Ruby) != 0 || rubyTravelAudit.CheckPreservation() is not
                { protectedCells: 5803, coverageComplete: true, preserved: true, diffCount: 0 })
        { FinishRubyTravel("failed_setup", "Expected the fresh original-house copy, zero Ruby, and closed API and teleport gates."); return; }
        Executor.ResetNavigationTelemetry();
        rubyTravelRunning = true;
        WriteRubyTravelResult("running", "");
    }

    private void TickRubyTravelAcceptance()
    {
        try
        {
            Player player = Main.LocalPlayer;
            if (!RubyNativeGatesClosed() || Main.worldID != houseWorldId || Main.gameMenu || player.dead ||
                rubyTravelAudit?.BaselineReport.epoch != WorldKnowledgeObserver.LoadEpoch ||
                CompanionNpc is not { active: true } npc || Executor.NavigationTeleportCount != 0)
            { FinishRubyTravel("failed_runtime", "Isolation, live actors or zero-teleport gate was lost."); return; }
            if (DateTime.UtcNow - rubyTravelStarted > TimeSpan.FromMinutes(6))
            { FinishRubyTravel("deadline", "Six-minute bounded native test elapsed; untested sources and routes remain unknown."); return; }
            player.immune = true;
            player.immuneTime = 600;
            foreach (NPC hostile in Main.npc) if (hostile.active && !hostile.friendly) hostile.active = false;

            if (rubyTravelPhase == RubyTravelPhase.Index)
            {
                JsonElement index = JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Status());
                if (!index.GetProperty("ready").GetBoolean() || !RubyTravelGrounded(npc)) return;
                rubyTravelStart = npc.Center;
                rubyTravelEvents.Add(new { kind = "original_house_start", tick = Main.GameUpdateCount,
                    actorX = npc.Center.X, actorFeet = npc.Bottom.Y, playerX = player.Center.X, playerFeet = player.Bottom.Y, index });
                StartRubyTravelPage("ruby;limit=8", "nearest_index_page");
                return;
            }
            if (rubyTravelPhase != RubyTravelPhase.Walk && Vector2.DistanceSquared(npc.Center, rubyTravelStart) > 16f * 16f)
            { FinishRubyTravel("start_pose_changed", "The companion moved before the outbound Explore dispatch."); return; }
            if (rubyTravelPhase == RubyTravelPhase.Graph)
            { AdvanceRubyTravelGraph(npc); return; }
            if (rubyTravelPhase == RubyTravelPhase.SurveyShallowGroups)
            { SurveyRubyTravelShallowGroups(npc); return; }
            if (rubyTravelPhase == RubyTravelPhase.SurveyReachableStands)
            { AdvanceRubyTravelReachableSurvey(npc); return; }

            AIActionType expected = rubyTravelPhase == RubyTravelPhase.Sources ? AIActionType.Inspect : AIActionType.Explore;
            AgentOutcome? outcome = agentJournal.Outcomes.LastOrDefault(entry => entry.Sequence > rubyTravelBoundary && entry.Task.Action == expected);
            if (outcome == null) return;
            if (rubyTravelPhase == RubyTravelPhase.Sources)
            { ReadRubyTravelPage(npc, outcome); return; }

            RubyTravelCandidate candidate = rubyTravelCandidate!;
            JsonElement preflight = JsonSerializer.SerializeToElement(Executor.InspectMiningInteraction(npc, "ruby", candidate.Seed.X, candidate.Seed.Y));
            bool grounded = RubyTravelGrounded(npc);
            bool traveled = Vector2.DistanceSquared(rubyTravelStart, npc.Center) >= 32f * 32f;
            rubyTravelEvents.Add(new { kind = "native_explore_outcome", tick = Main.GameUpdateCount, outcome,
                actorX = npc.Center.X, actorFeet = npc.Bottom.Y, grounded, traveled, preflight });
            if (outcome.Status != "succeeded")
            { FinishRubyTravel("explore_blocked", "Production Explore failed after a bounded graph found a candidate route."); return; }
            bool verified = grounded && traveled && RubyTravelSeedLegal(candidate.Seed) &&
                FlagRubyTravel(preflight, "canAttempt") && FlagRubyTravel(preflight, "actualWithinToolRange") &&
                preflight.GetProperty("product").GetProperty("itemId").GetInt32() == ItemID.Ruby &&
                Loadout.CountItem(ItemID.Ruby) == 0 && !HasLooseRuby() && rubyTravelAudit!.CheckPreservation() is
                    { protectedCells: 5803, coverageComplete: true, preserved: true, diffCount: 0 };
            FinishRubyTravel(verified ? "passed" : "arrival_not_mineable", verified ? "" :
                "Explore completion lacked actual grounded Ruby tool reach, fresh native legality, zero starting Ruby or intact house proof.");
        }
        catch (Exception exception) { FinishRubyTravel("failed_runtime", exception.Message); }
    }

    private Point RubyTravelOrigin => new((int)(rubyTravelStart.X / 16f), (int)(rubyTravelStart.Y / 16f));

    private void StartRubyTravelPage(string query, string reason)
    {
        rubyTravelPages++;
        if (reason == "nearest_index_page") rubyTravelPrimaryPages++;
        rubyTravelBoundary = agentJournal.LastSequence;
        rubyTravelPhase = RubyTravelPhase.Sources;
        Point origin = RubyTravelOrigin;
        Executor.Apply(new AIPlan { Action = AIActionType.Inspect, Target = "sources", Item = query,
            X = origin.X, Y = origin.Y });
        rubyTravelEvents.Add(new { kind = "native_sources_dispatch", page = rubyTravelPages, reason, query,
            x = origin.X, y = origin.Y, expectedGroup = rubyTravelExpectedGroup?.Id });
        WriteRubyTravelResult("running", "");
    }

    private void ReadRubyTravelPage(NPC npc, AgentOutcome outcome)
    {
        rubyTravelEvents.Add(new { kind = "native_sources_page", page = rubyTravelPages, outcome });
        if (outcome.Status != "succeeded")
        { FinishRubyTravel("sources_incomplete", "The retained native sources action reported a blocked page."); return; }
        using JsonDocument document = JsonDocument.Parse(outcome.Result);
        JsonElement result = document.RootElement;
        if (result.GetProperty("status").GetString() != "complete" ||
            !FlagRubyTravel(result.GetProperty("world"), "ready"))
        { FinishRubyTravel("sources_incomplete", "The retained native sources page did not complete on the full current index."); return; }
        string[] groups = result.GetProperty("groups").EnumerateArray().Select(group => group.GetProperty("id").GetString()!).ToArray();
        if (rubyTravelExpectedGroup is { } expected && (groups.Length != 1 || groups[0] != expected.Id))
        { FinishRubyTravel("sources_scope_changed", "The live index no longer matches the focused shallow group's identity."); return; }
        rubyTravelObservedGroupIds.UnionWith(groups);
        JsonElement query = result.GetProperty("query");
        var key = (Offset: query.GetProperty("offset").GetInt32(), Limit: query.GetProperty("limit").GetInt32());
        rubyTravelNextQuery = rubyTravelExpectedGroup == null ? ReadRubyTravelNextTask(result, "paging", query) : null;
        rubyTravelNextRefinement = rubyTravelRefinedPages.Count < RubyTravelMaxRefinements && !rubyTravelRefinedPages.Contains(key) &&
            ReadRubyTravelNextTask(result, "refinement", query) is { } refine && rubyTravelRefinedPages.Add(key) ? refine : null;
        var stands = new List<RubyTravelCandidate>();
        foreach (JsonElement row in result.GetProperty("candidates").EnumerateArray())
        {
            JsonElement source = row.GetProperty("seed"), preflight = row.GetProperty("preflight");
            Point seed = new(source.GetProperty("x").GetInt32(), source.GetProperty("y").GetInt32());
            if (!RubyTravelSeedLegal(seed) || !preflight.TryGetProperty("tool", out JsonElement tool) || !FlagRubyTravel(tool, "canBreak")) continue;
            Vector2 center = RubyTravelTileCenter(seed);
            MiningApproachGeometry geometry = MiningApproachProbe.EnumerateGeometry(npc.Center, npc.height, center,
                TilePathfinder.IsStableStand, (node, height) => new(node, TilePathfinder.StandCenter(node, height), TilePathfinder.StandBottom(node)));
            foreach (MiningApproachStand candidate in geometry.Candidates.OrderBy(stand => Vector2.DistanceSquared(stand.Center, center)).Take(3))
            {
                if (rubyTravelProbedStands.Contains(candidate.Node) || !TilePathfinder.TryResolveStableStand(RubyTravelTileCenter(candidate.Node), out Point resolved) ||
                    resolved != candidate.Node || !TilePathfinder.TryGetStandPose(resolved, out TileStandPose pose) ||
                    Vector2.Distance(pose.Center, center) > MiningApproachProbe.ToolReachPixels) continue;
                stands.Add(new(seed, pose, row.GetProperty("discovery").GetString() ?? ""));
            }
        }
        foreach (RubyTravelCandidate candidate in stands.OrderBy(row => Vector2.DistanceSquared(rubyTravelStart, RubyTravelTileCenter(row.Seed)))
            .ThenBy(row => Vector2.DistanceSquared(row.Stand.Center, RubyTravelTileCenter(row.Seed)))
            .DistinctBy(row => row.Stand.Node).Take(RubyTravelMaxProbesPerPage))
            rubyTravelStands.Enqueue(candidate);
        rubyTravelPhase = RubyTravelPhase.Graph;
        WriteRubyTravelResult("running", "");
    }

    private string? ReadRubyTravelNextTask(JsonElement result, string field, JsonElement current)
    {
        if (!result.TryGetProperty(field, out JsonElement section) || !section.TryGetProperty("nextTask", out JsonElement next) ||
            next.ValueKind != JsonValueKind.Object) return null;
        Point origin = RubyTravelOrigin;
        if (next.GetProperty("x").GetInt32() != origin.X || next.GetProperty("y").GetInt32() != origin.Y) return null;
        string query = next.GetProperty("item").GetString()!;
        MiningSourceRefinementRequest request = MiningSourceRefinementRequest.Parse(query, origin.X, origin.Y);
        int offset = current.GetProperty("offset").GetInt32();
        if (request.Request.Value != "ruby" || request.Request.Limit != current.GetProperty("limit").GetInt32()) return null;
        return field == "refinement"
            ? request.Request.Offset == offset && request.RefineOffset > current.GetProperty("refineOffset").GetInt32() &&
                request.RefineScope == current.GetProperty("refineScope").GetString() ? query : null
            : request.Request.Offset > offset && request.RefineOffset == 0 ? query : null;
    }

    private void SurveyRubyTravelShallowGroups(NPC npc)
    {
        Point origin = RubyTravelOrigin;
        MiningSourcePage survey = WorldKnowledgeObserver.ReadMiningSourcesPage($"ruby;offset={rubyTravelSurveyOffset};limit=8", npc, origin.X, origin.Y);
        JsonObject header = survey.Header;
        if (header["world"]?["ready"]?.GetValue<bool>() != true || header["paging"] is not JsonObject paging ||
            paging["staleGroupsDropped"]?.GetValue<int>() != 0 || header["groups"] is not JsonArray groups)
        { FinishRubyTravel("shallow_index_changed", "The full current index changed while surveying bounded shallow groups."); return; }
        for (int i = 0; i < groups.Count; i++)
        {
            JsonNode group = groups[i]!;
            string id = group["id"]!.GetValue<string>();
            JsonNode bounds = group["bounds"]!;
            int x = bounds["x"]!.GetValue<int>(), y = bounds["y"]!.GetValue<int>();
            int w = bounds["width"]!.GetValue<int>(), h = bounds["height"]!.GetValue<int>();
            if (group["tileType"]?.GetValue<int>() == TileID.Ruby) rubyTravelRubyChunks.Add(RubyTravelChunk(x, y));
            if (rubyTravelObservedGroupIds.Contains(id)) continue;
            int horizontal = Math.Abs(origin.X - Math.Clamp(origin.X, x, x + w - 1));
            int vertical = Math.Abs(origin.Y - Math.Clamp(origin.Y, y, y + h - 1));
            if (horizontal > 1800 || y > origin.Y + 224 || y + h - 1 < origin.Y - 48) continue;
            rubyTravelShallowSurvey.Add(new(id, rubyTravelSurveyOffset + i, x, y, w, h, horizontal + vertical * 2));
        }
        rubyTravelSurveyPages++;
        int next = paging["nextOffset"]!.GetValue<int>();
        if (next > rubyTravelSurveyOffset && rubyTravelSurveyPages < RubyTravelMaxSurveyPages)
        { rubyTravelSurveyOffset = next; return; }

        rubyTravelRubyChunksComplete = next < 0 && rubyTravelRubyChunks.Count == paging["totalIndexedGroups"]!.GetValue<int>();

        RubyTravelGroup[] selected = rubyTravelShallowSurvey.OrderBy(group => group.Score)
            .ThenBy(group => group.Offset).DistinctBy(group => (group.X / 192, group.Y / 128))
            .Take(RubyTravelShallowGroups).ToArray();
        foreach (RubyTravelGroup group in selected) rubyTravelShallowQueue.Enqueue(group);
        rubyTravelEvents.Add(new { kind = "shallow_index_survey", rubyTravelSurveyPages, totalMatchingGroups = rubyTravelShallowSurvey.Count,
            exhaustedIndex = next < 0, rubyChunks = rubyTravelRubyChunks.Count, rubyChunksComplete = rubyTravelRubyChunksComplete,
            maximumHorizontalTiles = 1800, maximumDepthBelowOriginTiles = 224, selected });
        StartNextRubyTravelQuery(npc);
    }

    private void StartNextRubyTravelQuery(NPC npc)
    {
        if (rubyTravelNextRefinement is { } refine)
        { rubyTravelNextRefinement = null; StartRubyTravelPage(refine, "live_refinement_cursor"); return; }
        if (rubyTravelPrimaryPages < RubyTravelNearestPages && rubyTravelNextQuery is { } page)
        { StartRubyTravelPage(page, "nearest_index_page"); return; }
        if (rubyTravelSurveyPages == 0)
        { rubyTravelPhase = RubyTravelPhase.SurveyShallowGroups; return; }
        if (rubyTravelShallowQueue.TryDequeue(out RubyTravelGroup? group))
        { rubyTravelExpectedGroup = group; StartRubyTravelPage($"ruby;offset={group.Offset};limit=1", "shallow_index_group"); return; }
        BeginRubyTravelReachableSurvey(npc);
    }

    private void AdvanceRubyTravelGraph(NPC npc)
    {
        if (rubyTravelSearch == null)
        {
            if (!rubyTravelStands.TryDequeue(out RubyTravelCandidate? next))
            {
                StartNextRubyTravelQuery(npc);
                return;
            }
            rubyTravelCandidate = next;
            rubyTravelProbedStands.Add(next.Stand.Node);
            rubyTravelWideSearch = false;
            rubyTravelProbeAt = Main.GameUpdateCount;
            rubyTravelSearch = TilePathfinder.BeginSearch(npc.Center, next.Stand.Center);
        }
        TilePathSearchSnapshot snapshot = rubyTravelSearch.Advance(64, 2);
        if (snapshot.Status == "searching" && Main.GameUpdateCount - rubyTravelProbeAt < RubyTravelMaxSearchTicks) return;
        RubyTravelCandidate chosen = rubyTravelCandidate!;
        bool exactRoute = snapshot.Status == "found" && snapshot.Goal == chosen.Stand.Node && snapshot.Path is { Count: > 1 };
        bool widenEligible = !rubyTravelWideSearch && rubyTravelSearch.ShouldWidenHorizontalEnvelope;
        bool scheduleWideRetry = widenEligible && snapshot.Goal == chosen.Stand.Node && RubyTravelGrounded(npc) &&
            Vector2.DistanceSquared(npc.Center, snapshot.RequestedStart) <= 4f * 4f &&
            RubyTravelSeedLegal(chosen.Seed) && TilePathfinder.MatchesStandPose(chosen.Stand) &&
            TilePathfinder.IsStableStand(chosen.Stand.Node.X, chosen.Stand.Node.Y);
        string? widenTrigger = !widenEligible ? null : scheduleWideRetry
            ? "long_frontier_exhausted_at_extendable_horizontal_boundary" : "recheck_actor_seed_or_exact_stand_failed";
        var boundary = RubyTravelGraphBoundary(rubyTravelSearch, snapshot);
        rubyTravelEvents.Add(new { kind = "bounded_route_probe", tick = Main.GameUpdateCount, chosen.Discovery,
            seed = new { chosen.Seed.X, chosen.Seed.Y }, stand = new { chosen.Stand.Node.X, chosen.Stand.Node.Y, chosen.Stand.Bottom },
            status = snapshot.Status == "searching" ? "search_time_budget" : snapshot.Status, snapshot.Diagnostic,
            attempt = rubyTravelWideSearch ? 2 : 1, radiusX = rubyTravelWideSearch ? TilePathfinder.WideRoutePaddingX : 64, radiusY = 48,
            requestedStart = new { snapshot.RequestedStart.X, snapshot.RequestedStart.Y },
            actor = new { x = npc.Center.X, feet = npc.Bottom.Y }, widenEligible, widenTrigger, widenedRetryScheduled = scheduleWideRetry,
            searchTicks = Main.GameUpdateCount - rubyTravelProbeAt, limits = snapshot.Limits,
            snapshot.UniqueExpanded, snapshot.Discovered, snapshot.MinX, snapshot.MinY, snapshot.MaxX, snapshot.MaxY,
            boundary, routeNodes = snapshot.Path?.Count, exactRoute });
        if (scheduleWideRetry)
        {
            rubyTravelWideSearch = true;
            rubyTravelProbeAt = Main.GameUpdateCount;
            rubyTravelSearch = TilePathfinder.BeginSearch(snapshot.RequestedStart, chosen.Stand.Center,
                radiusX: TilePathfinder.WideRoutePaddingX, radiusY: 48);
            WriteRubyTravelResult("running", "");
            return;
        }
        rubyTravelSearch = null;
        if (exactRoute) BeginRubyTravelWalk(chosen);
        WriteRubyTravelResult("running", "");
    }

    private void BeginRubyTravelReachableSurvey(NPC npc)
    {
        if (!TilePathfinder.TryResolveGroundedLaunch(npc.Center, npc.Bottom.Y, npc.velocity.Y, out Point launch))
        { FinishRubyTravel("reachable_launch_unresolved", "The original-house actor did not have a measured grounded local launch connection."); return; }
        Item? pick = Loadout.GetBestPickaxe();
        if (pick == null || Loadout.CountFreeCapacity(ItemID.Ruby) == 0)
        { FinishRubyTravel("reachable_tool_unavailable", "The current companion lacked a real pickaxe or Ruby inventory capacity."); return; }
        rubyTravelCandidate = null;
        rubyTravelPhase = RubyTravelPhase.SurveyReachableStands;
        rubyTravelProbeAt = Main.GameUpdateCount;
        int pickPower = pick.pick;
        rubyTravelSearch = TilePathfinder.BeginReachableStandSurvey(launch, RubyTravelReachableRadiusX, RubyTravelReachableRadiusY,
            new TilePathSearchLimits(6000, 12000), stand => ProbeRubyTravelReachableStand(stand, pickPower));
        rubyTravelEvents.Add(new { kind = "reachable_ruby_survey_started", tick = rubyTravelProbeAt,
            actorX = npc.Center.X, actorFeet = npc.Bottom.Y, launch = new { launch.X, launch.Y },
            pickaxeId = pick.type, pickPower, toolReachPixels = MiningApproachProbe.ToolReachPixels,
            rubyChunks = rubyTravelRubyChunks.Count, useCompleteIndexFilter = rubyTravelRubyChunksComplete,
            radiusX = RubyTravelReachableRadiusX, radiusY = RubyTravelReachableRadiusY,
            maximumTicks = RubyTravelReachableTicks, maximumRubyCellReads = RubyTravelReachableCellLimit });
        WriteRubyTravelResult("running", "");
    }

    private bool ProbeRubyTravelReachableStand(Point node, int pickPower)
    {
        if (rubyTravelReachableCellReads >= RubyTravelReachableCellLimit ||
            rubyTravelRubyChunksComplete && !RubyTravelChunkNear(node) ||
            !TilePathfinder.TryGetStandPose(node, out TileStandPose pose) || !TilePathfinder.IsStableStand(node.X, node.Y) ||
            Vector2.DistanceSquared(rubyTravelStart, pose.Center) < 32f * 32f) return false;
        rubyTravelReachableStableStands++;
        Point? bestSeed = null;
        float bestDistance = MiningApproachProbe.ToolReachPixels * MiningApproachProbe.ToolReachPixels;
        int leftFoot = CompanionMovement.FootprintLeft(pose.Center.X), rightFoot = CompanionMovement.FootprintRight(pose.Center.X);
        for (int x = node.X - 7; x <= node.X + 7 && rubyTravelReachableCellReads < RubyTravelReachableCellLimit; x++)
        for (int y = node.Y - 8; y <= node.Y + 7 && rubyTravelReachableCellReads < RubyTravelReachableCellLimit; y++)
        {
            Point seed = new(x, y);
            float distance = Vector2.DistanceSquared(pose.Center, RubyTravelTileCenter(seed));
            if (distance > bestDistance || y == node.Y + 1 && x >= leftFoot && x <= rightFoot ||
                !WorldGen.InWorld(x, y, 3) || rubyTravelRubyChunksComplete && !rubyTravelRubyChunks.Contains(RubyTravelChunk(x, y))) continue;
            rubyTravelReachableCellReads++;
            Tile tile = Main.tile[x, y];
            if (!tile.HasTile || tile.IsActuated || tile.TileType != TileID.Ruby || !RubyTravelSeedLegal(seed)) continue;
            int damage = Math.Max(1, pickPower);
            TileLoader.PickPowerCheck(tile, pickPower, ref damage);
            if (damage <= 0) continue;
            bestSeed = seed;
            bestDistance = distance;
        }
        if (bestSeed is not { } selected || !TilePathfinder.TryResolveStableStand(RubyTravelTileCenter(node), out Point projected) || projected != node)
            return false;
        rubyTravelCandidate = new(selected, pose, "settled_stand_live_ruby");
        return true;
    }

    private static Point RubyTravelChunk(int x, int y) => new(x / WorldKnowledgeIndex.ChunkSize, y / WorldKnowledgeIndex.ChunkSize);

    private bool RubyTravelChunkNear(Point stand)
    {
        Point first = RubyTravelChunk(stand.X - 7, stand.Y - 8), last = RubyTravelChunk(stand.X + 7, stand.Y + 7);
        for (int x = first.X; x <= last.X; x++)
        for (int y = first.Y; y <= last.Y; y++)
            if (rubyTravelRubyChunks.Contains(new Point(x, y))) return true;
        return false;
    }

    private void AdvanceRubyTravelReachableSurvey(NPC npc)
    {
        TilePathSearch search = rubyTravelSearch!;
        TilePathSearchSnapshot snapshot = search.Advance(64, 2);
        ulong searchTicks = Main.GameUpdateCount - rubyTravelProbeAt;
        bool cellLimit = rubyTravelReachableCellReads >= RubyTravelReachableCellLimit;
        if (snapshot.Status == "searching" && !cellLimit && searchTicks < RubyTravelReachableTicks) return;
        string status = snapshot.Status != "searching" ? snapshot.Status : cellLimit ? "ruby_cell_budget" : "search_time_budget";
        IReadOnlyList<Point>? path = snapshot.Path;
        var pathSpan = path is { Count: > 1 } ? new { nodes = path.Count, minX = path.Min(node => node.X), maxX = path.Max(node => node.X),
            minY = path.Min(node => node.Y), maxY = path.Max(node => node.Y), first = new { path[0].X, path[0].Y },
            last = new { path[^1].X, path[^1].Y } } : null;
        RubyTravelCandidate? candidate = rubyTravelCandidate;
        bool exactRoute = snapshot.Status == "found" && pathSpan != null && candidate != null && snapshot.Goal == candidate.Stand.Node &&
            TilePathfinder.TryResolveGroundedLaunch(npc.Center, npc.Bottom.Y, npc.velocity.Y, out Point launch) && launch == snapshot.Start &&
            RubyTravelSeedLegal(candidate.Seed) && TilePathfinder.MatchesStandPose(candidate.Stand);
        rubyTravelEvents.Add(new { kind = "reachable_ruby_survey_result", tick = Main.GameUpdateCount, status, snapshot.Diagnostic,
            searchTicks, snapshot.UniqueExpanded, snapshot.Discovered, snapshot.Limits, snapshot.MinX, snapshot.MinY, snapshot.MaxX, snapshot.MaxY,
            deepestSettledRow = search.DeepestSettledRow, boundary = RubyTravelGraphBoundary(search, snapshot),
            stableStandsScanned = rubyTravelReachableStableStands, rubyCellReads = rubyTravelReachableCellReads,
            seed = candidate == null ? null : new { candidate.Seed.X, candidate.Seed.Y },
            stand = candidate == null ? null : new { candidate.Stand.Node.X, candidate.Stand.Node.Y, candidate.Stand.Bottom },
            pathSpan, exactRoute });
        rubyTravelSearch = null;
        if (!exactRoute)
        {
            if (search.SettledExtentStands is { } extent)
            {
                object ObserveExtent(string kind, Point stand)
                {
                    int left = Math.Clamp(stand.X - 10, 2, Main.maxTilesX - 23);
                    int top = Math.Clamp(stand.Y - 12, 2, Main.maxTilesY - 44);
                    return new { kind, stand = new { stand.X, stand.Y }, terrain = NavigationWorldObserver.Terrain($"{left},{top},21,42", npc) };
                }
                rubyTravelEvents.Add(new { kind = "reachable_ruby_extent_geometry", samples = new[]
                { ObserveExtent("west", extent.West), ObserveExtent("east", extent.East), ObserveExtent("deepest", extent.Deepest) } });
            }
            FinishRubyTravel("no_tested_route", "Bounded source and original-house reachable-stand surveys did not prove a current exact Ruby approach. Untested areas remain unknown.");
            return;
        }
        BeginRubyTravelWalk(candidate!);
        WriteRubyTravelResult("running", "");
    }

    private void BeginRubyTravelWalk(RubyTravelCandidate candidate)
    {
        rubyTravelPhase = RubyTravelPhase.Walk;
        rubyTravelBoundary = agentJournal.LastSequence;
        Executor.Apply(new AIPlan { Action = AIActionType.Explore, X = candidate.Stand.Node.X, Y = candidate.Stand.Node.Y });
    }

    private static object RubyTravelGraphBoundary(TilePathSearch search, TilePathSearchSnapshot snapshot)
    {
        bool NearX(int edge, int direction)
        {
            for (int i = 0; i <= CompanionMovement.ExtendedGapJumpSpanTiles; i++)
                for (int y = snapshot.MinY; y <= snapshot.MaxY; y++)
                    if (search.HasSettledStand(new(edge + i * direction, y))) return true;
            return false;
        }
        bool NearY(int edge, int direction, int width)
        {
            for (int i = 0; i < width; i++)
                for (int x = snapshot.MinX; x <= snapshot.MaxX; x++)
                    if (search.HasSettledStand(new(x, edge + i * direction))) return true;
            return false;
        }
        return new { west = NearX(snapshot.MinX, 1), east = NearX(snapshot.MaxX, -1),
            upper = NearY(snapshot.MinY, 1, CompanionMovement.MaximumJumpRiseTiles), lower = NearY(snapshot.MaxY, -1, 18) };
    }

    private bool RubyTravelSeedLegal(Point seed)
    {
        if (!WorldGen.InWorld(seed.X, seed.Y, 3) || AIExecutor.IsPartOfHouseOrStructure(seed.X, seed.Y) ||
            !WorldGen.CanKillTile(seed.X, seed.Y)) return false;
        Tile tile = Main.tile[seed.X, seed.Y];
        return tile.HasTile && !tile.IsActuated && tile.TileType == TileID.Ruby &&
            (!rubyTravelAudit!.BaselineReport.sampleBounds.Contains(seed.X, seed.Y) ||
                rubyTravelAudit.ReadConstructionProtection(seed.X, seed.Y, 1, 1).Allowed == true);
    }

    private static Vector2 RubyTravelTileCenter(Point cell) => new(cell.X * 16f + 8, cell.Y * 16f + 8);
    private static bool RubyTravelGrounded(NPC npc) => TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
    private static bool FlagRubyTravel(JsonElement row, string key) => row.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private void FinishRubyTravel(string status, string error)
    {
        rubyTravelRunning = false;
        WriteRubyTravelResult(status, error);
        Stop();
    }

    private void WriteRubyTravelResult(string status, string error)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RubyTravelResultPath)!);
        File.WriteAllText(RubyTravelResultPath, JsonSerializer.Serialize(new
        {
            status, error, phase = rubyTravelPhase.ToString(), worldId = Main.worldID, epoch = WorldKnowledgeObserver.LoadEpoch,
            elapsedSeconds = (DateTime.UtcNow - rubyTravelStarted).TotalSeconds, pages = rubyTravelPages,
            probedStands = rubyTravelProbedStands.Count, requests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount,
            warps = Executor.NavigationTeleportCount, navigation = Executor.NavigationState, events = rubyTravelEvents,
            protection = status == "running" ? null : rubyTravelAudit?.CheckPreservation(),
            scope = "Controlled native approach from ordinary original-house spawn. Candidates come from retained live sources or Ruby within tool reach of settled production-graph stands. No target or actor relocation, excavation, synthetic Ruby, or model calls. Hostile NPCs are suppressed. Graph presence is distinct from grounded actual arrival in current tool reach."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
