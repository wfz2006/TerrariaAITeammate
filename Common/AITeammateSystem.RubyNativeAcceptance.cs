using System;
using System.Collections.Generic;
using System.IO;
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
    private const string RubyNativeCase = "p4_ruby_native";
    private const int RubyNativeMaxGroups = 32, RubyNativeMaxGeometry = 256;
    private sealed record RubyNativeSeed(Point Tile, MiningApproachStand Stand, string GroupId, bool IndexedSample);
    private bool rubyNativeRunning;
    private ulong rubyNativeStarted, rubyNativeMineTick;
    private int rubyNativeOffset, rubyNativeCells, rubyNativeGeometry;
    private long rubyNativeBoundary;
    private ShelterWorldAudit? rubyNativeAudit;
    private RubyNativeSeed? rubyNativeSeed;
    private JsonElement rubyNativeWorldQuery, rubyNativePreflight;
    private object? rubyNativeSetup;
    private readonly HashSet<string> rubyNativeScanned = new();
    private readonly List<NaturalRubyDrop> rubyNativeDrops = new();
    private static string RubyNativeResultPath => Path.Combine(Main.SavePath, "AITeammate", "p4-ruby-native-result.json");

    private void BeginRubyNativeAcceptance()
    {
        if (!HouseMode || File.Exists(RubyNativeResultPath)) return;
        rubyNativeAudit = ShelterWorldAudit.Capture(3220, 390);
        if (!RubyNativeGatesClosed() || CompanionNpc is not { active: true } ||
            Loadout.CountItem(ItemID.Ruby) != 0 || HasLooseRuby() ||
            rubyNativeAudit.CheckPreservation() is not { protectedCells: 5803, preserved: true, coverageComplete: true, diffCount: 0 })
        { FinishRubyNativeAcceptance("failed_setup", "A fresh original-house copy, zero Ruby, and closed model/teleport gates are required."); return; }
        Executor.ResetNavigationTelemetry();
        rubyNativeStarted = Main.GameUpdateCount;
        rubyNativeBoundary = agentJournal.LastSequence;
        rubyNativeRunning = true;
    }

    private bool RubyNativeGatesClosed()
    {
        AIConfig config = ModContent.GetInstance<AIConfig>();
        return HouseMode && !config.UseLlm && !config.AllowTeleportRecovery && !HousePlanningAllowed && !CombatPlannerEnabled &&
            PrimaryDecisionRequestCount == 0 && CombatDecisionRequestCount == 0;
    }

    private static bool HasLooseRuby() => Main.item.Any(item => item.active && !item.IsAir && item.type == ItemID.Ruby);

    private void TickRubyNativeAcceptance()
    {
        try
        {
            if (!RubyNativeGatesClosed() || CompanionNpc is not { active: true } npc || Main.LocalPlayer.dead)
            { FinishRubyNativeAcceptance("failed_runtime", "Test isolation or a live actor was lost."); return; }
            ulong elapsed = Main.GameUpdateCount - rubyNativeStarted;
            if (rubyNativeSeed == null)
            {
                if (elapsed > 3600 || rubyNativeScanned.Count >= RubyNativeMaxGroups || rubyNativeGeometry >= RubyNativeMaxGeometry)
                { FinishRubyNativeAcceptance("uncovered", "No legal in-range Ruby stand was found within the measured indexed bounds."); return; }
                if (elapsed % 90 != 0) return;
                rubyNativeWorldQuery = JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Query($"ruby;offset={rubyNativeOffset};limit=8", npc));
                if (!rubyNativeWorldQuery.TryGetProperty("data", out JsonElement data) || !data.TryGetProperty("entries", out JsonElement entries)) return;
                RubyNativeSeed? seed = SelectRubyNativeSeed(npc, entries);
                if (seed == null)
                {
                    int nextOffset = data.GetProperty("nextOffset").GetInt32();
                    if (nextOffset < 0 && data.GetProperty("coverageComplete").GetBoolean())
                    { FinishRubyNativeAcceptance("uncovered", "Current index pages did not contain a legal measured Ruby stand."); return; }
                    rubyNativeOffset = Math.Max(0, nextOffset);
                    return;
                }
                StartRubyNativeMine(npc, seed);
                return;
            }

            AgentOutcome? outcome = agentJournal.Outcomes.FirstOrDefault(entry => entry.Sequence > rubyNativeBoundary &&
                entry.Task is { Action: AIActionType.Mine, Target: "ruby" });
            if (outcome == null && Main.GameUpdateCount - rubyNativeMineTick <= 600) return;
            if (outcome == null || outcome.Status != "succeeded")
            { FinishRubyNativeAcceptance("blocked", outcome == null ? "Native Mine did not finish within 600 ticks." : "Native Mine reported blocked.", outcome); return; }
            bool verified = VerifyRubyNativeMine(rubyNativeSeed, outcome) && Executor.NavigationTeleportCount == 0 &&
                rubyNativeAudit!.CheckPreservation() is { protectedCells: 5803, preserved: true, coverageComplete: true, diffCount: 0 };
            FinishRubyNativeAcceptance(verified ? "passed" : "incomplete_proof",
                verified ? "" : "Exact native removal, Ruby drop and inventory gain, or preservation was not verified.", outcome);
        }
        catch (Exception exception)
        {
            FinishRubyNativeAcceptance("failed_runtime", exception.Message);
        }
    }

    private RubyNativeSeed? SelectRubyNativeSeed(NPC npc, JsonElement entries)
    {
        foreach (JsonElement group in entries.EnumerateArray())
        {
            if (rubyNativeScanned.Count >= RubyNativeMaxGroups || rubyNativeGeometry >= RubyNativeMaxGeometry) return null;
            string id = group.GetProperty("id").GetString()!;
            if (group.GetProperty("tileType").GetInt32() != TileID.Ruby || !rubyNativeScanned.Add(id)) continue;
            JsonElement bounds = group.GetProperty("bounds");
            int left = bounds.GetProperty("x").GetInt32(), top = bounds.GetProperty("y").GetInt32();
            int width = bounds.GetProperty("width").GetInt32(), height = bounds.GetProperty("height").GetInt32();
            var samples = group.GetProperty("samples").EnumerateArray()
                .Select(sample => new Point(sample.GetProperty("x").GetInt32(), sample.GetProperty("y").GetInt32())).ToHashSet();
            for (int x = left; x < left + width; x++)
            for (int y = top; y < top + height; y++)
            {
                rubyNativeCells++;
                if (!WorldGen.InWorld(x, y, 3)) continue;
                Tile tile = Main.tile[x, y];
                if (!tile.HasTile || tile.IsActuated || tile.TileType != TileID.Ruby || !RubyNativeOutsideMask(x, y) ||
                    AIExecutor.IsPartOfHouseOrStructure(x, y) || !WorldGen.CanKillTile(x, y)) continue;
                if (rubyNativeGeometry >= RubyNativeMaxGeometry) return null;
                rubyNativeGeometry++;
                Vector2 target = new(x * 16f + 8, y * 16f + 8);
                MiningApproachGeometry geometry = MiningApproachProbe.EnumerateGeometry(npc.Center, npc.height, target,
                    TilePathfinder.IsStableStand, (node, bodyHeight) => new(node, TilePathfinder.StandCenter(node, bodyHeight), TilePathfinder.StandBottom(node)));
                foreach (MiningApproachStand stand in geometry.Candidates.OrderBy(candidate => Vector2.DistanceSquared(candidate.Center, target)))
                {
                    if (!RubyNativeBodyOutsideMask(stand, npc, x, y)) continue;
                    var seed = new Point(x, y);
                    return new(seed, stand, id, samples.Contains(seed));
                }
            }
        }
        return null;
    }

    private bool RubyNativeOutsideMask(int x, int y) =>
        !rubyNativeAudit!.BaselineReport.sampleBounds.Contains(x, y) ||
        rubyNativeAudit.ReadConstructionProtection(x, y, 1, 1).Allowed == true;

    private bool RubyNativeBodyOutsideMask(MiningApproachStand stand, NPC npc, int seedX, int seedY)
    {
        int left = CompanionMovement.FootprintLeft(stand.Center.X, npc.width), right = CompanionMovement.FootprintRight(stand.Center.X, npc.width);
        int bottom = CompanionMovement.StandRow(stand.FeetY), top = (int)((stand.FeetY - npc.height) / 16f);
        if (seedY == stand.Node.Y + 1 && seedX >= left && seedX <= right) return false;
        for (int x = left; x <= right; x++)
        for (int y = top; y <= bottom; y++)
            if (!RubyNativeOutsideMask(x, y)) return false;
        return true;
    }

    private void StartRubyNativeMine(NPC npc, RubyNativeSeed seed)
    {
        if (HasLooseRuby() || Loadout.CountItem(ItemID.Ruby) != 0)
        { FinishRubyNativeAcceptance("failed_setup", "Unexpected Ruby appeared before the test action."); return; }
        Vector2 prior = npc.Center;
        npc.Center = seed.Stand.Center;
        npc.velocity = Vector2.Zero;
        rubyNativeSeed = seed;
        rubyNativeMineTick = Main.GameUpdateCount;
        int x = seed.Tile.X, y = seed.Tile.Y;
        bool grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, 0, npc.width);
        rubyNativePreflight = JsonSerializer.SerializeToElement(Executor.InspectMiningInteraction(npc, "ruby", x, y));
        rubyNativeSetup = new { beforeX = prior.X, beforeY = prior.Y, actorX = npc.Center.X, actorFeet = npc.Bottom.Y,
            grounded, seedX = x, seedY = y, standX = seed.Stand.Node.X, standY = seed.Stand.Node.Y,
            seed.GroupId, seed.IndexedSample, beforeType = Main.tile[x, y].TileType, rubyBefore = 0, looseRubyBefore = false };
        if (!grounded || !rubyNativePreflight.GetProperty("canAttempt").GetBoolean() ||
            !rubyNativePreflight.GetProperty("actualWithinToolRange").GetBoolean() ||
            rubyNativePreflight.GetProperty("product").GetProperty("itemId").GetInt32() != ItemID.Ruby)
        { FinishRubyNativeAcceptance("failed_setup", "Native directed mining preflight did not validate the chosen start."); return; }
        AIPlan plan = AIPlanParser.Parse($$"""{"tasks":[{"type":"mine","target":"ruby","count":1,"x":{{x}},"y":{{y}}}]}""");
        if (!ApplyPlan(plan, null, "direct")) FinishRubyNativeAcceptance("dispatch_rejected", "Native Ruby Mine could not be queued.");
    }

    internal void RecordRubyNativeDrop(Item item, IEntitySource source)
    {
        if (!rubyNativeRunning || rubyNativeSeed == null || !HouseMode || item.type != ItemID.Ruby) return;
        Point? point = source is EntitySource_TileBreak tile ? tile.TileCoords : null;
        rubyNativeDrops.Add(new(Main.GameUpdateCount, item.whoAmI, item.stack, point?.X, point?.Y,
            source.GetType().FullName ?? "", Executor.CurrentAction == AIActionType.Mine));
    }

    private bool VerifyRubyNativeMine(RubyNativeSeed seed, AgentOutcome outcome)
    {
        using JsonDocument document = JsonDocument.Parse(outcome.Result);
        JsonElement result = document.RootElement;
        int x = seed.Tile.X, y = seed.Tile.Y;
        JsonElement[] changes = result.GetProperty("changedTiles").EnumerateArray().ToArray();
        return outcome.Task.X == x && outcome.Task.Y == y && !Main.tile[x, y].HasTile &&
            !result.GetProperty("changedTilesTruncated").GetBoolean() && changes.Length == 1 &&
            changes[0].GetProperty("x").GetInt32() == x && changes[0].GetProperty("y").GetInt32() == y &&
            changes[0].GetProperty("beforeType").GetInt32() == TileID.Ruby && changes[0].GetProperty("removed").GetBoolean() &&
            result.GetProperty("actualYield").GetProperty("itemType").GetInt32() == ItemID.Ruby &&
            result.GetProperty("actualGained").GetInt32() == 1 && Loadout.CountItem(ItemID.Ruby) == 1 && !HasLooseRuby() &&
            rubyNativeDrops.Count == 1 && rubyNativeDrops[0] is { DuringMine: true, Stack: 1 } drop &&
            drop.Source == typeof(EntitySource_TileBreak).FullName && drop.X == x && drop.Y == y;
    }

    private void FinishRubyNativeAcceptance(string status, string error, AgentOutcome? outcome = null)
    {
        rubyNativeRunning = false;
        Directory.CreateDirectory(Path.GetDirectoryName(RubyNativeResultPath)!);
        File.WriteAllText(RubyNativeResultPath, JsonSerializer.Serialize(new
        {
            status, error, worldId = Main.worldID, epoch = WorldKnowledgeObserver.LoadEpoch,
            groupsScanned = rubyNativeScanned.Count, indexedCellsRead = rubyNativeCells, geometryProbes = rubyNativeGeometry,
            worldQuery = rubyNativeWorldQuery.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : rubyNativeWorldQuery,
            setup = rubyNativeSetup, preflight = rubyNativePreflight.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : rubyNativePreflight,
            outcome, drops = rubyNativeDrops, rubyAfter = Loadout.CountItem(ItemID.Ruby),
            requests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount, warps = Executor.NavigationTeleportCount,
            protection = rubyNativeAudit?.CheckPreservation(),
            scope = "One initial measured actor placement in a controlled isolated replay. No route from the house or autonomous planning is asserted."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Stop();
    }
}

public sealed class RubyNativeDropWitness : GlobalItem
{
    public override void OnSpawn(Item item, IEntitySource source)
    {
        if (item.type == ItemID.Ruby) AITeammateSystem.Instance?.RecordRubyNativeDrop(item, source);
    }
}
