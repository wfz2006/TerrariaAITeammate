using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string NaturalBossPreparationRequest = "你没有现成王冠或红玉。请根据背包和现场确认史莱姆王的真实召唤条件，自行采集红玉，利用现有工作站和自己的材料先做金冠或铂金王冠，再做史莱姆王冠。合法召唤并独自击败刚召唤的这只，捡起它实际掉落的战利品，步行回到我身边后汇报。我在原屋旁等待，不攻击。受阻时自行调整。若取红玉受阻，只可在原屋和现有设施外、距可采红玉横纵合计12格内，指定坐标每次采挖1格天然石、土或草开路，总共最多8格。不要改动原屋或现有设施，不开箱、不建造、不传送。";
    private const string NaturalBossAltarReplayRequest = "受控零 API 验收。实际走到原有祭坛，消耗已提供的金冠和凝胶制作史莱姆王冠。";
    private static string NaturalBossPreparationMode => NaturalFlashProfile == "natural_boss_flash"
        ? Environment.GetEnvironmentVariable("AITEAMMATE_NATURAL_BOSS_PREP") ?? "" : "";
    private static bool NaturalBossFromScratch => NaturalBossPreparationMode == "from_scratch";
    private static bool NaturalBossAltarReplay => NaturalBossPreparationMode == "altar_replay";
    private static bool NaturalBossPreparation => NaturalBossFromScratch || NaturalBossAltarReplay;
    private sealed record NaturalRubyDrop(ulong Tick, int Slot, int Stack, int? X, int? Y, string Source, bool DuringMine);
    private sealed record NaturalAltarRoute(Point Altar, Point Start, Point End, int Nodes, int Probes);
    private readonly List<NaturalRubyDrop> naturalRubyDrops = new();
    private readonly NaturalBossExcavationLimit naturalBossExcavation = new();
    private long naturalRubySequence, naturalMetalCrownSequence, naturalSlimeCrownSequence, naturalPreparedSummonSequence;
    private int naturalMetalCrownType;
    private Vector2? naturalBeforeAltar;
    private JsonElement naturalAltarSurvey;
    private bool naturalBossPreparationSurveyReady;
    private NaturalAltarRoute? naturalAltarRoute;
    private int? naturalAltarRecipeId;

    private static bool NaturalBossPreparationConfig()
    {
        AIConfig config = ModContent.GetInstance<AIConfig>();
        return NaturalBossPreparationMode switch
        {
            "" => true,
            "from_scratch" => !NaturalFlashReplay && !config.AllowTeleportRecovery && config.DebugLog,
            "altar_replay" => NaturalFlashReplay && !config.UseLlm && !config.AllowTeleportRecovery,
            _ => false
        };
    }

    private static bool NaturalMetalCrown(int item) => item is ItemID.GoldCrown or ItemID.PlatinumCrown;

    private static bool NaturalBossPreparationGroundClean() => !Main.item.Any(item => item.active &&
        !item.IsAir && (item.type is ItemID.Ruby or ItemID.SlimeCrown || NaturalMetalCrown(item.type)));

    private bool PrepareNaturalBossCraftStock(Dictionary<int, int> supplies)
    {
        Recipe? metal = CompanionCrafting.FindRecipes(ItemID.GoldCrown.ToString()).FirstOrDefault(recipe =>
            recipe.requiredTile.Contains(TileID.Anvils) &&
            CompanionCrafting.Requirements(recipe).Any(row => row.AcceptedItems.Contains(ItemID.Ruby)));
        Recipe? slime = CompanionCrafting.FindRecipes(ItemID.SlimeCrown.ToString()).FirstOrDefault(recipe =>
            recipe.requiredTile.Contains(TileID.DemonAltar) &&
            CompanionCrafting.Requirements(recipe).Any(row => row.AcceptedItems.Contains(ItemID.GoldCrown)));
        if (metal == null || slime == null || !NaturalBossPreparationGroundClean()) return false;
        int gold = CompanionCrafting.Requirements(metal).Where(row => row.AcceptedItems.Contains(ItemID.GoldBar)).Sum(row => row.Count);
        int gel = CompanionCrafting.Requirements(slime).Where(row => row.AcceptedItems.Contains(ItemID.Gel)).Sum(row => row.Count);
        if (gold <= 0 || gel <= 0) return false;
        if (NaturalBossFromScratch) supplies[ItemID.GoldBar] = gold;
        else
        {
            supplies[ItemID.GoldCrown] = 1;
            naturalMetalCrownType = ItemID.GoldCrown;
            naturalAltarRecipeId = slime.RecipeIndex;
        }
        supplies[ItemID.Gel] = gel;
        Loadout.Reset();
        Loadout.EnsureStarterTools();
        return true;
    }

    private bool SurveyNaturalBossPreparation(NPC npc)
    {
        if (!NaturalBossPreparationGroundClean()) return false;
        JsonElement ruby = JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Query("ruby;limit=8", npc));
        JsonElement anvils = JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Query($"tile:{TileID.Anvils};limit=8", npc));
        naturalAltarSurvey = JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Query($"tile:{TileID.DemonAltar};limit=8", npc));
        naturalEvents.Add(new { kind = "boss_preparation_survey", tick = Main.GameUpdateCount,
            actorX = npc.Center.X, actorFeet = npc.Bottom.Y, ruby, anvils, altars = naturalAltarSurvey,
            scope = "Current indexed sample cells from original-house start. No planner injection or whole-world route proof." });
        return NaturalIndexedSamples(naturalAltarSurvey).Any() &&
            (!NaturalBossFromScratch || NaturalIndexedSamples(ruby).Any() && NaturalIndexedSamples(anvils).Any());
    }

    private static IEnumerable<JsonElement> NaturalIndexedSamples(JsonElement report) =>
        report.TryGetProperty("data", out var data) && data.TryGetProperty("entries", out var entries)
            ? entries.EnumerateArray().SelectMany(entry => entry.GetProperty("samples").EnumerateArray())
            : Enumerable.Empty<JsonElement>();

    private bool NaturalBossPreparationStockAtStart() => NaturalBossPreparationGroundClean() &&
        Loadout.CountItem(ItemID.Ruby) == 0 && Loadout.CountItem(ItemID.SlimeCrown) == 0 &&
        Loadout.CountItem(ItemID.PlatinumCrown) == 0 && Loadout.CountItem(ItemID.GoldCrown) == (NaturalBossAltarReplay ? 1 : 0);

    private bool AllowNaturalBossPreparationAction(AIPlan task)
    {
        if (task.Action == AIActionType.Mine)
        {
            if (!NaturalBossFromScratch) return false;
            if (task.Target == "ruby") return true;
            return task is { X: int x, Y: int y } && naturalBossExcavation.Allows(task, x, y) &&
                Loadout.CountItem(ItemID.Ruby) == 0 && NaturalBossOriginalHouseAllows(x, y) &&
                NaturalBossAuxiliarySeedMatches(task.Target, x, y) && NearbyNaturalBossRuby(x, y);
        }
        if (task.Action != AIActionType.Craft) return false;
        Recipe[] recipes = CompanionCrafting.FindRecipes(task.Item).Where(recipe =>
            !task.RecipeId.HasValue || recipe.RecipeIndex == task.RecipeId.Value).ToArray();
        return recipes.Length > 0 && recipes.All(recipe => recipe.createItem.type == ItemID.SlimeCrown ||
            NaturalBossFromScratch && NaturalMetalCrown(recipe.createItem.type));
    }

    private bool NaturalBossOriginalHouseAllows(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 3) || naturalHouseAudit?.BaselineReport is not
            { available: true, primaryCoverageComplete: true } baseline || baseline.worldId != Main.worldID ||
            baseline.epoch != WorldKnowledgeObserver.LoadEpoch) return false;
        if (baseline.sampleBounds.Contains(x, y) && naturalHouseAudit.ReadConstructionProtection(x, y, 1, 1).Allowed != true)
            return false;
        return !AIExecutor.IsPartOfHouseOrStructure(x, y);
    }

    private static bool NaturalBossAuxiliarySeedMatches(string target, int x, int y)
    {
        int expected = target switch { "stone" => TileID.Stone, "dirt" => TileID.Dirt, "grass" => TileID.Grass, _ => -1 };
        Tile tile = Main.tile[x, y];
        return tile.HasTile && !tile.IsActuated && tile.TileType == expected && WorldGen.CanKillTile(x, y);
    }

    private bool NearbyNaturalBossRuby(int x, int y)
    {
        const int radius = 12;
        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = Math.Abs(dx) - radius; dy <= radius - Math.Abs(dx); dy++)
        {
            int sx = x + dx, sy = y + dy;
            if (!WorldGen.InWorld(sx, sy, 3)) continue;
            Tile tile = Main.tile[sx, sy];
            if (tile.HasTile && !tile.IsActuated && tile.TileType == TileID.Ruby &&
                NaturalBossOriginalHouseAllows(sx, sy) && WorldGen.CanKillTile(sx, sy)) return true;
        }
        return false;
    }

    internal bool AllowsNaturalBossExcavation(AIPlan? task, int x, int y)
    {
        if (!naturalFlashRunning || naturalFlashFinished || !HouseMode || !NaturalBossFromScratch ||
            task is not { Action: AIActionType.Mine }) return true;
        bool auxiliary = NaturalBossExcavationLimit.IsAuxiliary(task);
        if (auxiliary && (!naturalBossExcavation.Allows(task, x, y) || Loadout.CountItem(ItemID.Ruby) > 0)) return false;
        if (!NaturalBossOriginalHouseAllows(x, y)) return false;
        if (RequiresStructurePreservation)
        {
            GoalConstructionProtection protection = InspectConstructionProtection(x, y, 1, 1);
            if (!protection.Available || protection.Covered && protection.Allowed != true) return false;
        }
        if (!auxiliary) return task.Target == "ruby";
        int previousAttempts = naturalBossExcavation.AttemptedCount;
        if (!naturalBossExcavation.TryReserve(task, x, y)) return false;
        if (previousAttempts < naturalBossExcavation.AttemptedCount)
            naturalEvents.Add(new { kind = "boss_preparation_auxiliary_excavation_admitted", tick = Main.GameUpdateCount,
                x, y, target = task.Target, distinctCells = naturalBossExcavation.AttemptedCount, maximumCells = NaturalBossExcavationLimit.MaximumCells });
        return true;
    }

    internal void RecordNaturalBossRubyDrop(Item item, IEntitySource source)
    {
        if (!HouseMode || !NaturalBossFromScratch || !naturalFlashRunning || naturalFlashFinished || item.type != ItemID.Ruby) return;
        Point? point = source is EntitySource_TileBreak tile ? tile.TileCoords : null;
        naturalRubyDrops.Add(new(Main.GameUpdateCount, item.whoAmI, item.stack, point?.X, point?.Y,
            source.GetType().FullName ?? "", Executor.CurrentAction == AIActionType.Mine));
    }

    private void TrackNaturalBossPreparation(NPC npc, IReadOnlyList<AgentOutcome> fresh, Dictionary<int, int> stock)
    {
        var stations = CompanionCrafting.NearbyStations(npc);
        if (naturalSlimeCrownSequence == 0 && !stations.Any(station => IsNaturalAltar(station.X, station.Y)))
            naturalBeforeAltar ??= npc.Center;
        foreach (AgentOutcome outcome in fresh)
        {
            if (outcome.Task is { Action: AIActionType.Mine, Target: "ruby" })
                TrackNaturalRubyMine(outcome, stock);
            if (outcome.Status != "succeeded") continue;
            if (outcome.Task.Action == AIActionType.Craft && fresh.Count(row => row.Task.Action == AIActionType.Craft) == 1)
            {
                int metal = new[] { ItemID.GoldCrown, ItemID.PlatinumCrown }.FirstOrDefault(item =>
                    naturalStock.GetValueOrDefault(item) == 0 && stock.GetValueOrDefault(item) > 0);
                if (metal > 0 && naturalRubySequence > 0 && naturalRubySequence < outcome.Sequence &&
                    VerifyNaturalCrownCraft(npc, outcome, metal, TileID.Anvils, ItemID.Ruby,
                        metal == ItemID.GoldCrown ? ItemID.GoldBar : ItemID.PlatinumBar, stock))
                {
                    naturalMetalCrownSequence = outcome.Sequence;
                    naturalMetalCrownType = metal;
                }
                if ((NaturalBossAltarReplay || naturalMetalCrownSequence > 0 && naturalMetalCrownSequence < outcome.Sequence) &&
                    naturalStock.GetValueOrDefault(ItemID.SlimeCrown) == 0 && naturalBeforeAltar is { } prior &&
                    Vector2.DistanceSquared(prior, npc.Center) >= 16f * 16f &&
                    VerifyNaturalCrownCraft(npc, outcome, ItemID.SlimeCrown, TileID.DemonAltar, ItemID.Gel, naturalMetalCrownType, stock))
                    naturalSlimeCrownSequence = outcome.Sequence;
            }
            if (outcome.Task.Action == AIActionType.Use && naturalSlimeCrownSequence > 0 && naturalSlimeCrownSequence < outcome.Sequence &&
                naturalStock.GetValueOrDefault(ItemID.SlimeCrown) - stock.GetValueOrDefault(ItemID.SlimeCrown) == 1)
                naturalPreparedSummonSequence = outcome.Sequence;
        }
    }

    private void TrackNaturalRubyMine(AgentOutcome outcome, Dictionary<int, int> stock)
    {
        try
        {
            using JsonDocument result = JsonDocument.Parse(outcome.Result);
            JsonElement body = result.RootElement;
            if (!body.TryGetProperty("changedTilesTruncated", out var truncated) || truncated.ValueKind != JsonValueKind.False ||
                !body.TryGetProperty("changedTiles", out var changes) || !body.TryGetProperty("actualYields", out var yields)) return;
            var removed = changes.EnumerateArray().Where(cell => cell.GetProperty("beforeType").GetInt32() == TileID.Ruby &&
                cell.GetProperty("removed").GetBoolean() && cell.GetProperty("afterType").ValueKind == JsonValueKind.Null)
                .Select(cell => (X: cell.GetProperty("x").GetInt32(), Y: cell.GetProperty("y").GetInt32())).ToHashSet();
            int gained = yields.EnumerateArray().Where(row => row.GetProperty("itemType").GetInt32() == ItemID.Ruby)
                .Sum(row => row.GetProperty("amount").GetInt32());
            int drops = naturalRubyDrops.Where(drop => drop.DuringMine && drop.Source == typeof(EntitySource_TileBreak).FullName &&
                drop.X.HasValue && drop.Y.HasValue && removed.Contains((drop.X.Value, drop.Y.Value))).Sum(drop => drop.Stack);
            bool proven = gained > 0 && drops >= gained && stock.GetValueOrDefault(ItemID.Ruby) >= gained;
            naturalEvents.Add(new { kind = "boss_preparation_ruby_mine", outcome.Sequence, gained, drops, removed = removed.Select(p => new { p.X, p.Y }), proven });
            if (proven && naturalRubySequence == 0) naturalRubySequence = outcome.Sequence;
        }
        catch (JsonException) { /* An unstructured mining result cannot prove a native Ruby yield. */ }
    }

    private bool VerifyNaturalCrownCraft(NPC npc, AgentOutcome outcome, int output, int stationType, int firstInput, int secondInput,
        Dictionary<int, int> stock)
    {
        int produced = stock.GetValueOrDefault(output) - naturalStock.GetValueOrDefault(output);
        if (produced <= 0 || firstInput <= 0 || secondInput <= 0) return false;
        var stations = CompanionCrafting.NearbyStations(npc).Where(station =>
            stationType == TileID.DemonAltar ? IsNaturalAltar(station.X, station.Y) : CompanionCrafting.StationMatches(station.Type, stationType)).ToArray();
        if (stations.Length == 0) return false;
        var consumed = naturalStock.Where(pair => pair.Value > stock.GetValueOrDefault(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value - stock.GetValueOrDefault(pair.Key));
        if (consumed.GetValueOrDefault(firstInput) <= 0 || consumed.GetValueOrDefault(secondInput) <= 0) return false;
        foreach (Recipe recipe in CompanionCrafting.FindRecipes(outcome.Task.Item).Where(recipe =>
            recipe.createItem.type == output && (!outcome.Task.RecipeId.HasValue || recipe.RecipeIndex == outcome.Task.RecipeId) &&
            recipe.requiredTile.Contains(stationType) && CompanionCrafting.HasStationNear(npc, recipe)))
        {
            int stack = recipe.createItem.stack;
            if (stack <= 0 || produced % stack != 0 ||
                !MaterialAllocator.TryAllocate(CompanionCrafting.Requirements(recipe, produced / stack), naturalStock, out var expected) ||
                consumed.Count != expected.Count || !expected.All(pair => consumed.GetValueOrDefault(pair.Key) == pair.Value)) continue;
            naturalEvents.Add(new { kind = "boss_preparation_craft", outcome.Sequence, output, produced,
                matchedRecipeId = recipe.RecipeIndex, requestedRecipeId = outcome.Task.RecipeId, consumed,
                stations = stations.Select(station => new { station.Type, station.X, station.Y }),
                actorX = npc.Center.X, actorFeet = npc.Bottom.Y,
                beforeAltar = naturalBeforeAltar is { } prior ? new { x = prior.X, y = prior.Y } : null });
            return true;
        }
        return false;
    }

    private static bool IsNaturalAltar(int x, int y) => WorldGen.InWorld(x, y, 2) &&
        Main.tile[x, y].HasTile && !Main.tile[x, y].IsActuated && Main.tile[x, y].TileType == TileID.DemonAltar;

    private static bool NativeAltarNear(Vector2 center)
    {
        int cx = (int)(center.X / 16f), cy = (int)(center.Y / 16f);
        for (int x = cx - 5; x <= cx + 5; x++)
            for (int y = cy - 4; y <= cy + 4; y++)
                if (IsNaturalAltar(x, y)) return true;
        return false;
    }

    private NaturalAltarRoute? FindNaturalAltarRoute()
    {
        int probes = 0;
        var samples = NaturalIndexedSamples(naturalAltarSurvey)
            .OrderBy(sample => sample.GetProperty("distanceFromOriginTiles").GetDouble());
        foreach (JsonElement sample in samples)
        {
            var altar = new Point(sample.GetProperty("x").GetInt32(), sample.GetProperty("y").GetInt32());
            if (!IsNaturalAltar(altar.X, altar.Y)) continue;
            var stands = (from x in Enumerable.Range(altar.X - 12, 25)
                          from y in Enumerable.Range(altar.Y - 8, 17)
                          where TilePathfinder.IsStableStand(x, y)
                          let point = new Point(x, y)
                          let center = TilePathfinder.StandCenter(point)
                          orderby Math.Abs(altar.X - point.X) + Math.Abs(altar.Y - point.Y)
                          select (Point: point, Center: center)).ToArray();
            var ends = stands.Where(stand => NativeAltarNear(stand.Center) && TilePathfinder.TryResolveStableStand(
                new(stand.Point.X * 16f + 8, stand.Point.Y * 16f + 8), out Point resolved) && resolved == stand.Point).Take(3).ToArray();
            foreach (var start in stands.Where(stand => !NativeAltarNear(stand.Center)).Take(5))
                foreach (var end in ends)
                {
                    if (probes++ >= 24) return null;
                    List<Point>? path = TilePathfinder.FindPathBetweenStands(start.Point, end.Point, 16, 12);
                    if (path is { Count: > 1 }) return new(altar, start.Point, end.Point, path.Count, probes);
                }
        }
        return null;
    }

    private void StartNaturalAltarReplay()
    {
        if (CompanionNpc is not { } npc || naturalAltarRecipeId == null ||
            naturalHouseAudit?.CheckPreservation() is not { preserved: true, coverageComplete: true, diffCount: 0 } ||
            FindNaturalAltarRoute() is not { } route)
        { FinishNaturalFlash("uncovered_altar", "No bounded native altar route with a legal outside starting stand was found."); return; }
        naturalAltarRoute = route;
        npc.Center = TilePathfinder.StandCenter(route.Start, npc.height);
        npc.velocity = Vector2.Zero;
        if (NativeAltarNear(npc.Center) || !TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, 0, npc.width))
        { FinishNaturalFlash("uncovered_altar", "Measured altar setup does not begin grounded outside native station range."); return; }
        naturalBeforeAltar = npc.Center;
        naturalEvents.Add(new { kind = "altar_control_setup", altar = new { route.Altar.X, route.Altar.Y },
            start = new { route.Start.X, route.Start.Y }, end = new { route.End.X, route.End.Y },
            route.Nodes, route.Probes, actorX = npc.Center.X, actorFeet = npc.Bottom.Y, nearAltar = false });
        manualControlActive = false;
        if (!ApplyPlan(new AIPlan { Goal = "Walk to the measured original altar and craft", Action = AIActionType.Explore,
            X = route.End.X, Y = route.End.Y, CompletionConditions = new() { new() { Type = "structure_preserved" } } },
            null, "chat", NaturalRequest))
        { FinishNaturalFlash("replay_dispatch_failed", "Measured altar Explore was rejected."); return; }
        naturalReplayStage = 1;
    }

    private void AdvanceNaturalAltarReplay(IReadOnlyList<AgentOutcome> fresh)
    {
        if (fresh.Any(outcome => outcome.Status == "blocked"))
        { FinishNaturalFlash("replay_native_blocked", "Measured altar navigation or native crafting was blocked."); return; }
        if (naturalReplayStage == 1 && fresh.Any(outcome => outcome is { Status: "succeeded", Task.Action: AIActionType.Explore }))
        {
            NPC? npc = CompanionNpc;
            if (npc == null || naturalAltarRoute == null || !NativeAltarNear(npc.Center) ||
                !TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width))
            { FinishNaturalFlash("uncovered_altar", "Explore did not arrive grounded in native altar range."); return; }
            naturalEvents.Add(new { kind = "altar_control_arrival", tick = Main.GameUpdateCount,
                actorX = npc.Center.X, actorFeet = npc.Bottom.Y, grounded = true, nearAltar = true });
            naturalReplayStage = 2;
            if (!ApplyPlan(new AIPlan { Action = AIActionType.Craft, Item = ItemID.SlimeCrown.ToString(), Count = 1,
                RecipeId = naturalAltarRecipeId }, null, "goal"))
                FinishNaturalFlash("replay_dispatch_failed", "Native Slime Crown craft was rejected.");
            return;
        }
        if (naturalReplayStage == 2 && fresh.Any(outcome => outcome.Task.Action == AIActionType.Craft))
        {
            bool verified = naturalSlimeCrownSequence > 0 && Loadout.CountItem(ItemID.SlimeCrown) == 1 &&
                naturalBossSpawnId == 0 && naturalHouseAudit?.CheckPreservation() is { preserved: true, coverageComplete: true, diffCount: 0 };
            FinishNaturalFlash(verified ? "passed_altar_prep" : "incomplete_proof",
                verified ? "" : "Actual altar arrival, exact ingredients and one new Slime Crown were not all verified.");
        }
    }
}

public sealed class NaturalBossRubyDropWitness : GlobalItem
{
    public override void OnSpawn(Item item, IEntitySource source) =>
        AITeammateSystem.Instance?.RecordNaturalBossRubyDrop(item, source);
}
