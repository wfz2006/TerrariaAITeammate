using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string CrownAnvilCase = "p4_crown_anvil_native";
    private const int CrownAnvilRadiusX = 192, CrownAnvilRadiusY = 96;
    private static readonly string CrownAnvilItem = ItemID.GoldCrown.ToString();
    private static string CrownAnvilResultPath => Path.Combine(Main.SavePath, "AITeammate", "p4-crown-anvil-native-result.json");
    private enum CrownAnvilPhase { Index, Graph, Explore, Craft }
    private readonly List<object> crownAnvilEvents = new();
    private bool crownAnvilRunning, crownAnvilEnteredRange;
    private DateTime crownAnvilStarted;
    private CrownAnvilPhase crownAnvilPhase;
    private ShelterWorldAudit? crownAnvilAudit;
    private Recipe? crownAnvilRecipe;
    private int crownAnvilActor, crownAnvilGoldBars;
    private long crownAnvilBoundary;
    private Vector2 crownAnvilStart;
    private Point[] crownAnvilSites = Array.Empty<Point>();
    private Point crownAnvilSelected, crownAnvilStand;
    private TilePathSearch? crownAnvilSearch;
    private Dictionary<int, int> crownAnvilSuppliedStock = new();

    private void BeginCrownAnvilAcceptance()
    {
        if (!HouseMode || File.Exists(CrownAnvilResultPath)) return;
        crownAnvilStarted = DateTime.UtcNow;
        try
        {
            crownAnvilAudit = ShelterWorldAudit.Capture(3220, 390);
            var before = CompanionCrafting.InventoryCounts(Loadout);
            int[] inputsAndCrowns = { ItemID.Ruby, ItemID.GoldBar, ItemID.GoldCrown, ItemID.PlatinumCrown, ItemID.SlimeCrown };
            if (!RubyNativeGatesClosed() || CompanionNpc is not { active: true } npc ||
                inputsAndCrowns.Any(before.ContainsKey) || Main.item.Any(item => item.active && !item.IsAir && inputsAndCrowns.Contains(item.type)) ||
                !CrownAnvilPreserved(crownAnvilAudit.CheckPreservation()))
            { FinishCrownAnvil("failed_setup", "Expected fresh original-house preservation, empty test ingredients and crowns, and closed API and teleport gates."); return; }

            var recipes = CompanionCrafting.FindRecipes(CrownAnvilItem)
                .Select(recipe => (Recipe: recipe, GoldBars: GoldBarsForCrownAnvilRecipe(recipe)))
                .Where(candidate => candidate.GoldBars > 0).ToArray();
            if (recipes.Length != 1)
            { FinishCrownAnvil("recipe_uncovered", "Expected exactly one enabled Gold Crown recipe for one Ruby and Gold Bars at a native anvil."); return; }
            (crownAnvilRecipe, crownAnvilGoldBars) = recipes[0];
            bool rubyAdded = Loadout.AddItem(ItemID.Ruby, 1);
            bool goldAdded = Loadout.AddItem(ItemID.GoldBar, crownAnvilGoldBars);
            crownAnvilSuppliedStock = CompanionCrafting.InventoryCounts(Loadout);
            crownAnvilEvents.Add(new { kind = "controlled_recipe_inputs", tick = Main.GameUpdateCount,
                recipeId = crownAnvilRecipe.RecipeIndex, itemId = ItemID.GoldCrown, stationType = TileID.Anvils,
                suppliedRuby = 1, suppliedGoldBars = crownAnvilGoldBars, rubyAdded, goldAdded, before, after = crownAnvilSuppliedStock });
            if (!rubyAdded || !goldAdded || crownAnvilSuppliedStock.GetValueOrDefault(ItemID.Ruby) != 1 ||
                crownAnvilSuppliedStock.GetValueOrDefault(ItemID.GoldBar) != crownAnvilGoldBars)
            { FinishCrownAnvil("failed_setup", "Could not supply the disclosed exact recipe inputs to the companion."); return; }
            Executor.ResetNavigationTelemetry();
            crownAnvilActor = npc.whoAmI;
            crownAnvilPhase = CrownAnvilPhase.Index;
            crownAnvilRunning = true;
            WriteCrownAnvilResult("running", "");
        }
        catch (Exception exception) { FinishCrownAnvil("failed_setup", exception.Message); }
    }

    private void TickCrownAnvilAcceptance()
    {
        if (!crownAnvilRunning) return;
        try
        {
            Player player = Main.LocalPlayer;
            if (!RubyNativeGatesClosed() || Main.gameMenu || Main.worldID != houseWorldId ||
                crownAnvilAudit?.BaselineReport.epoch != WorldKnowledgeObserver.LoadEpoch || !player.active || player.dead ||
                CompanionNpc is not { active: true } npc || npc.whoAmI != crownAnvilActor || Executor.NavigationTeleportCount != 0)
            { FinishCrownAnvil("failed_runtime", "The fixture lost its original world, actor or closed API and teleport gates."); return; }
            if (DateTime.UtcNow - crownAnvilStarted > TimeSpan.FromMinutes(3))
            { FinishCrownAnvil("deadline", "The bounded three-minute native test elapsed. Untested anvils and routes remain unknown."); return; }
            player.immune = true;
            player.immuneTime = 600;
            foreach (NPC hostile in Main.npc) if (hostile.active && !hostile.friendly) hostile.active = false;

            if (crownAnvilPhase == CrownAnvilPhase.Index) { ObserveCrownAnvilIndex(npc, player); return; }
            if (crownAnvilPhase == CrownAnvilPhase.Graph) { AdvanceCrownAnvilGraph(npc); return; }

            bool stationAvailable = AtCrownAnvil(npc, crownAnvilSelected);
            if (crownAnvilPhase == CrownAnvilPhase.Explore && stationAvailable && !crownAnvilEnteredRange)
            {
                crownAnvilEnteredRange = true;
                crownAnvilEvents.Add(new { kind = "entered_native_anvil_range", tick = Main.GameUpdateCount,
                    anvil = new { crownAnvilSelected.X, crownAnvilSelected.Y }, pose = CrownAnvilPose(npc),
                    distanceFromStartPixels = Vector2.Distance(npc.Center, crownAnvilStart) });
            }
            AIActionType expected = crownAnvilPhase == CrownAnvilPhase.Explore ? AIActionType.Explore : AIActionType.Craft;
            AgentOutcome? outcome = agentJournal.Outcomes.FirstOrDefault(entry => entry.Sequence > crownAnvilBoundary && entry.Task.Action == expected);
            if (outcome == null) return;
            var stock = CompanionCrafting.InventoryCounts(Loadout);
            crownAnvilEvents.Add(new { kind = expected == AIActionType.Explore ? "native_explore_outcome" : "native_craft_outcome",
                tick = Main.GameUpdateCount, outcome, pose = CrownAnvilPose(npc), stationAvailable, stock });
            if (outcome.Status != "succeeded" || !stationAvailable || !CrownAnvilGrounded(npc))
            { FinishCrownAnvil("native_step_failed", "The actual action did not succeed at a grounded pose in range of the indexed native anvil."); return; }

            if (expected == AIActionType.Explore)
            {
                if (!crownAnvilEnteredRange || outcome.Task.X != crownAnvilStand.X || outcome.Task.Y != crownAnvilStand.Y ||
                    Vector2.DistanceSquared(npc.Center, crownAnvilStart) < 16f * 16f || !SameCrownAnvilStock(stock, crownAnvilSuppliedStock))
                { FinishCrownAnvil("incomplete_arrival", "The measured Explore did not preserve exact starting ingredients and show real movement into station range."); return; }
                crownAnvilBoundary = agentJournal.LastSequence;
                crownAnvilPhase = CrownAnvilPhase.Craft;
                if (!ApplyPlan(new AIPlan { Action = AIActionType.Craft, Item = CrownAnvilItem, Count = 1,
                    RecipeId = crownAnvilRecipe!.RecipeIndex }, null, "direct"))
                { FinishCrownAnvil("dispatch_rejected", "The production Craft with the exact Gold Crown recipe was rejected."); return; }
                WriteCrownAnvilResult("running", "");
                return;
            }

            var expectedStock = new Dictionary<int, int>(crownAnvilSuppliedStock);
            expectedStock.Remove(ItemID.Ruby);
            expectedStock.Remove(ItemID.GoldBar);
            expectedStock.Add(ItemID.GoldCrown, 1);
            bool exactCraft = outcome.Task.RecipeId == crownAnvilRecipe!.RecipeIndex && outcome.Task.Item == CrownAnvilItem &&
                outcome.Task.Count == 1 && SameCrownAnvilStock(stock, expectedStock);
            crownAnvilEvents.Add(new { kind = "exact_inventory_delta", tick = Main.GameUpdateCount, exactCraft,
                consumedRuby = crownAnvilSuppliedStock.GetValueOrDefault(ItemID.Ruby) - stock.GetValueOrDefault(ItemID.Ruby),
                consumedGoldBars = crownAnvilSuppliedStock.GetValueOrDefault(ItemID.GoldBar) - stock.GetValueOrDefault(ItemID.GoldBar),
                goldCrown = stock.GetValueOrDefault(ItemID.GoldCrown), slimeCrown = stock.GetValueOrDefault(ItemID.SlimeCrown) });
            FinishCrownAnvil(exactCraft ? "passed" : "incomplete_craft", exactCraft ? "" : "Exact recipe and inventory deltas were not proven.");
        }
        catch (Exception exception) { FinishCrownAnvil("failed_runtime", exception.Message); }
    }

    private void ObserveCrownAnvilIndex(NPC npc, Player player)
    {
        var index = JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Status());
        if (!index.GetProperty("ready").GetBoolean() || !CrownAnvilGrounded(npc)) return;
        crownAnvilStart = npc.Center;
        bool stationAvailable = CompanionCrafting.HasStationNear(npc, crownAnvilRecipe!);
        crownAnvilEvents.Add(new { kind = "original_house_start", tick = Main.GameUpdateCount, pose = CrownAnvilPose(npc),
            playerX = player.Center.X, playerFeet = player.Bottom.Y, stationAvailable, index });
        if (stationAvailable || !TilePathfinder.TryResolveStableStand(npc.Center, out Point start))
        { FinishCrownAnvil("start_uncovered", "The ordinary house start must resolve to a grounded stand outside native recipe range."); return; }

        string query = $"tile:{TileID.Anvils};limit=8;maxDistance={CrownAnvilRadiusX}";
        JsonElement observed = JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Query(query, npc));
        crownAnvilEvents.Add(new { kind = "live_anvil_index", tick = Main.GameUpdateCount, query, observed });
        if (!observed.TryGetProperty("data", out var data) || !data.GetProperty("coverageComplete").GetBoolean())
        { FinishCrownAnvil("index_uncovered", "The current native tile:16 index page did not have complete index coverage."); return; }
        crownAnvilSites = data.GetProperty("entries").EnumerateArray()
            .Where(entry => entry.GetProperty("tileType").GetInt32() == TileID.Anvils)
            .SelectMany(entry => entry.GetProperty("samples").EnumerateArray())
            .Select(sample => new Point(sample.GetProperty("x").GetInt32(), sample.GetProperty("y").GetInt32()))
            .Where(IsCrownAnvil).Distinct().ToArray();
        if (crownAnvilSites.Length == 0)
        { FinishCrownAnvil("index_uncovered", "The bounded index page yielded no current native anvil sample."); return; }

        crownAnvilSearch = TilePathfinder.BeginReachableStandSurvey(start, CrownAnvilRadiusX, CrownAnvilRadiusY,
            new TilePathSearchLimits(6000, 12000), IsCrownAnvilApproach);
        crownAnvilPhase = CrownAnvilPhase.Graph;
        WriteCrownAnvilResult("running", "");
    }

    private bool IsCrownAnvilApproach(Point node)
    {
        if (!crownAnvilSites.Any(site => Math.Abs(node.X - site.X) <= 6 && Math.Abs(node.Y - site.Y) <= 6) ||
            !TilePathfinder.TryGetStandPose(node, out TileStandPose pose) ||
            Vector2.DistanceSquared(pose.Center, crownAnvilStart) < 16f * 16f ||
            !crownAnvilSites.Any(site => CrownAnvilInRange(site, pose.Center, margin: 1))) return false;
        var exploreTarget = new Vector2(node.X * 16f + 8f, node.Y * 16f + 8f);
        return TilePathfinder.TryResolveStableStand(exploreTarget, out Point resolved) && resolved == node;
    }

    private void AdvanceCrownAnvilGraph(NPC npc)
    {
        if (Vector2.DistanceSquared(npc.Center, crownAnvilStart) > 16f * 16f || CompanionCrafting.HasStationNear(npc, crownAnvilRecipe!))
        { FinishCrownAnvil("start_changed", "The companion's starting pose changed before production Explore."); return; }
        TilePathSearchSnapshot route = crownAnvilSearch!.Advance(64, 2);
        if (route.Status == "searching") return;
        crownAnvilEvents.Add(new { kind = "bounded_native_approach", tick = Main.GameUpdateCount, route.Status,
            route.Diagnostic, route.UniqueExpanded, route.Discovered, route.MinX, route.MaxX, route.MinY, route.MaxY,
            pathNodes = route.Path?.Count });
        if (route.Status != "found" || route.Path is not { Count: > 1 } || route.GoalPose is not { } pose ||
            !TilePathfinder.MatchesStandPose(pose) || !IsCrownAnvilApproach(pose.Node))
        { FinishCrownAnvil("route_uncovered", "The bounded production graph did not prove an exact usable anvil approach. Untested routes remain unknown."); return; }
        crownAnvilStand = pose.Node;
        crownAnvilSelected = crownAnvilSites.First(site => CrownAnvilInRange(site, pose.Center, margin: 1));
        crownAnvilEvents.Add(new { kind = "native_explore_dispatch", tick = Main.GameUpdateCount,
            anvil = new { crownAnvilSelected.X, crownAnvilSelected.Y }, stand = new { pose.Node.X, pose.Node.Y },
            center = new { pose.Center.X, pose.Center.Y }, feet = pose.Bottom });
        crownAnvilSearch = null;
        crownAnvilBoundary = agentJournal.LastSequence;
        crownAnvilPhase = CrownAnvilPhase.Explore;
        if (!ApplyPlan(new AIPlan { Action = AIActionType.Explore, X = pose.Node.X, Y = pose.Node.Y }, null, "direct"))
        { FinishCrownAnvil("dispatch_rejected", "The production Explore to the measured native station was rejected."); return; }
        WriteCrownAnvilResult("running", "");
    }

    private static int GoldBarsForCrownAnvilRecipe(Recipe recipe)
    {
        if (recipe.createItem.type != ItemID.GoldCrown || recipe.createItem.stack != 1 ||
            recipe.requiredTile.Count != 1 || recipe.requiredTile[0] != TileID.Anvils) return 0;
        IReadOnlyList<MaterialRequirement> requirements = CompanionCrafting.Requirements(recipe);
        if (requirements.Count != 2 || !requirements.Any(item => item.Id == ItemID.Ruby && item.Count == 1 &&
            item.AcceptedItems is [ItemID.Ruby])) return 0;
        return requirements.FirstOrDefault(item => item.Id == ItemID.GoldBar && item.AcceptedItems is [ItemID.GoldBar])?.Count ?? 0;
    }

    private bool AtCrownAnvil(NPC npc, Point site) => CrownAnvilInRange(site, npc.Center, margin: 0) &&
        CompanionCrafting.HasStationNear(npc, crownAnvilRecipe!);

    private static bool CrownAnvilInRange(Point site, Vector2 center, int margin) => IsCrownAnvil(site) &&
        Math.Abs((int)(center.X / 16f) - site.X) <= 5 - margin && Math.Abs((int)(center.Y / 16f) - site.Y) <= 4 - margin;

    private static bool IsCrownAnvil(Point site) => WorldGen.InWorld(site.X, site.Y, 2) &&
        Main.tile[site.X, site.Y].HasTile && !Main.tile[site.X, site.Y].IsActuated && Main.tile[site.X, site.Y].TileType == TileID.Anvils;

    private static bool CrownAnvilGrounded(NPC npc) => TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
    private static object CrownAnvilPose(NPC npc) => new { x = npc.Center.X, y = npc.Center.Y, feet = npc.Bottom.Y, grounded = CrownAnvilGrounded(npc) };
    private static bool SameCrownAnvilStock(Dictionary<int, int> first, Dictionary<int, int> second) =>
        first.Count == second.Count && first.All(pair => second.GetValueOrDefault(pair.Key) == pair.Value);
    private static bool CrownAnvilPreserved(ShelterPreservationReport? report) =>
        report is { protectedCells: 5803, preserved: true, coverageComplete: true, diffCount: 0 };

    private void FinishCrownAnvil(string status, string error)
    {
        crownAnvilRunning = false;
        ShelterPreservationReport? protection = crownAnvilAudit?.CheckPreservation();
        if (status == "passed" && (!CrownAnvilPreserved(protection) || !RubyNativeGatesClosed() || Executor.NavigationTeleportCount != 0))
        { status = "incomplete_proof"; error = "The final fixed protection, model or teleport witness failed."; }
        WriteCrownAnvilResult(status, error, protection);
        Stop();
    }

    private void WriteCrownAnvilResult(string status, string error, ShelterPreservationReport? protection = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CrownAnvilResultPath)!);
        File.WriteAllText(CrownAnvilResultPath, JsonSerializer.Serialize(new
        {
            status, error, @case = CrownAnvilCase, phase = crownAnvilPhase.ToString(), worldId = Main.worldID,
            epoch = WorldKnowledgeObserver.LoadEpoch, elapsedSeconds = (DateTime.UtcNow - crownAnvilStarted).TotalSeconds,
            requests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount, warps = Executor.NavigationTeleportCount,
            recipeId = crownAnvilRecipe?.RecipeIndex, finalStock = CompanionCrafting.InventoryCounts(Loadout),
            navigation = Executor.NavigationState, events = crownAnvilEvents, protection,
            scope = "Controlled zero-API native Gold Crown step on a fresh original-house copy. Actual travel starts at the ordinary house spawn. Setup supplies one Ruby and the exact Gold Bars and suppresses hostile NPCs. The index and bounded graph locate an existing anvil. Grounded Explore and Craft establish physical arrival and exact material use."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
