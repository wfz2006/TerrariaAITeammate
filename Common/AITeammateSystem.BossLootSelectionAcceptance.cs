using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string BossLootSelectionNativeCase = "boss_loot_selection_native";
    private const int BossLootDecoyType = ItemID.Silk;
    private const float BossLootSelectionRadius = 80f * 16f;
    private const float BossLootBatchRadius = 5f * 16f;
    private const string BossLootNegativeJson = """{"resume":true,"tasks":[{"type":"pickup","item":"boss_loot","count":1}]}""";
    private const string BossLootBatchJson = """{"tasks":[{"type":"pickup","item":"boss_loot","count":0,"plan_step":"s4"},{"type":"come","plan_step":"s5"}]}""";
    private static bool BossLootSelectionBatchSelected => NaturalFlashReplay && NaturalFlashProfile == "natural_boss_flash" &&
        Environment.GetEnvironmentVariable("AITEAMMATE_BOSS_LOOT_SELECTION") == "1";

    private sealed record BossLootGround(int Slot, bool Active, int ItemType, int Stack, long SpawnId, float X, float Y, float Distance);
    private sealed record BossLootTransfers(int ActualCount, Dictionary<int, int> Items);
    private sealed record BossLootTransferBefore(uint Tick, float ActorX, float ActorY, BossLootGround Decoy,
        bool SameDecoy, BossLootGround[] TaggedInReach, int LedgerLoot, Dictionary<int, int> Stock);
    private sealed record BossLootTransferFrame(BossLootTransferBefore Before, BossLootGround DecoyAfter,
        bool SameDecoyAfter, int LedgerAfter, Dictionary<int, int> StockAfter);

    private bool bossLootSelectionRunning, bossLootNumericControl, bossLootBatchActive, bossLootBatchDecoyAttempted;
    private uint bossLootDispatchTick;
    private long bossLootOutcomeBoundary;
    private int bossLootDecoySlot = -1;
    private Item? bossLootDecoyInstance;
    private Dictionary<int, int> bossLootSeedStock = new();
    private Dictionary<string, string>? bossLootInputHashes;
    private ShelterWorldAudit? bossLootSelectionAudit;
    private object? bossLootPreflight, bossLootSeed;
    private AIPlan? bossLootAliasPlan, bossLootNumericPlan;
    private AgentOutcome? bossLootAliasOutcome, bossLootNumericOutcome;
    private BossLootTransferBefore? bossLootBeforePickup;
    private BossLootTransferFrame? bossLootFirstTransfer;
    private readonly List<object> bossLootStages = new();

    private static BossLootGround ReadBossLootGround(int slot, Vector2 center)
    {
        Item item = Main.item[slot];
        bool active = item.active && !item.IsAir && item.stack > 0;
        return new(slot, active, item.type, item.stack, active ? item.GetGlobalItem<BossLootTag>().BossSpawnId : 0,
            item.Center.X, item.Center.Y, Vector2.Distance(center, item.Center));
    }

    private static BossLootGround[] BossLootGroundWithin(Vector2 center, float pixels) => Enumerable.Range(0, Main.maxItems)
        .Where(slot => Main.item[slot].active && !Main.item[slot].IsAir && Vector2.Distance(center, Main.item[slot].Center) <= pixels)
        .Select(slot => ReadBossLootGround(slot, center)).ToArray();

    private bool SameBossLootDecoy(BossLootGround? item) => item is { Active: true, ItemType: BossLootDecoyType, Stack: 1, SpawnId: 0 } &&
        ReferenceEquals(Main.item[item.Slot], bossLootDecoyInstance);

    private bool SeedBossLootDecoy(NPC actor, Vector2 position, BossLootGround? anchor)
    {
        bossLootSeedStock = CompanionCrafting.InventoryCounts(Loadout);
        if (bossLootSeedStock.GetValueOrDefault(BossLootDecoyType) != 0 ||
            Main.item.Take(Main.maxItems).Any(item => item.active && !item.IsAir && item.type == BossLootDecoyType)) return false;
        bool[] occupied = Main.item.Take(Main.maxItems).Select(item => item.active).ToArray();
        if (occupied.All(active => active)) return false;
        int slot = Item.NewItem(new EntitySource_Misc("BossLootSelectionAcceptanceOrdinaryDecoy"), position, BossLootDecoyType, 1);
        if (slot < 0 || slot >= Main.maxItems || occupied[slot]) return false;
        bossLootDecoySlot = slot;
        bossLootDecoyInstance = Main.item[slot];
        BossLootGround decoy = ReadBossLootGround(slot, actor.Center);
        bossLootSeed = new { tick = Main.GameUpdateCount, anchor, decoy, actorX = actor.Center.X, actorY = actor.Center.Y, stock = bossLootSeedStock };
        return SameBossLootDecoy(decoy);
    }

    private static Dictionary<int, int> BossLootStockDelta(Dictionary<int, int> before, Dictionary<int, int> after) =>
        before.Keys.Union(after.Keys).Select(type => (Type: type, Count: after.GetValueOrDefault(type) - before.GetValueOrDefault(type)))
            .Where(row => row.Count != 0).ToDictionary(row => row.Type, row => row.Count);

    private static bool BossLootStockMatchesTransfers(Dictionary<int, int> delta, Dictionary<int, int> items)
    {
        static long CoinValue(int type) => type switch
        {
            ItemID.CopperCoin => 1, ItemID.SilverCoin => 100, ItemID.GoldCoin => 10_000, ItemID.PlatinumCoin => 1_000_000, _ => 0
        };
        return delta.Keys.Union(items.Keys).Where(type => CoinValue(type) == 0)
            .All(type => delta.GetValueOrDefault(type) == items.GetValueOrDefault(type)) &&
            delta.Sum(row => CoinValue(row.Key) * row.Value) == items.Sum(row => CoinValue(row.Key) * row.Value);
    }

    private static BossLootTransfers? ReadBossLootTransfers(AgentOutcome? outcome)
    {
        if (outcome == null) return null;
        try
        {
            using JsonDocument result = JsonDocument.Parse(outcome.Result);
            JsonElement root = result.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("actualCount", out JsonElement actual) || !actual.TryGetInt32(out int count) ||
                !root.TryGetProperty("actualItems", out JsonElement items) || items.ValueKind != JsonValueKind.Array) return null;
            return new(count, items.EnumerateArray().ToDictionary(item => item.GetProperty("itemType").GetInt32(), item => item.GetProperty("amount").GetInt32()));
        }
        catch (JsonException) { return null; }
    }

    private void BeginBossLootSelectionNative()
    {
        bossLootInputHashes = ReadBossReviewHashes();
        bossLootSelectionAudit = ShelterWorldAudit.Capture(3220, 390);
        Executor.ResetNavigationTelemetry();
        Player player = Main.LocalPlayer;
        if (GetLlmSettings() != null || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 || !player.active || player.dead)
        { FinishBossLootSelectionNative("uncovered", "An isolated world with disabled LLM and a live observer is required."); return; }
        bool spawned = CompanionNpc == null;
        if (spawned) SpawnCompanion(player);
        if (CompanionNpc is not { active: true } actor)
        { FinishBossLootSelectionNative("uncovered", "Native companion was absent."); return; }
        BossLootGround[] nearby = BossLootGroundWithin(actor.Center, BossLootSelectionRadius);
        bossLootPreflight = new { tick = Main.GameUpdateCount, spawned, actorX = actor.Center.X, actorY = actor.Center.Y,
            playerX = player.Center.X, playerY = player.Center.Y, nearby, encounters = BossEvidence.Snapshot() };
        if (nearby.Any(item => item.SpawnId != 0) || Main.npc.Any(npc => npc.active && npc.boss) ||
            !SeedBossLootDecoy(actor, actor.Center, null))
        { FinishBossLootSelectionNative("uncovered", "No clean no-Boss baseline or independent ordinary item was available."); return; }
        BossLootGround decoy = ReadBossLootGround(bossLootDecoySlot, actor.Center);
        if (decoy.Distance > MiningApproachProbe.ToolReachPixels || nearby.Any(item => item.Distance <= decoy.Distance))
        { FinishBossLootSelectionNative("uncovered", "Ordinary decoy was not the uniquely closest reachable drop."); return; }
        bossLootSelectionRunning = true;
        bossLootAliasPlan = AIPlanParser.Parse(BossLootNegativeJson);
        DispatchBossLootSelectionNative(bossLootAliasPlan);
    }

    private void DispatchBossLootSelectionNative(AIPlan plan)
    {
        bossLootOutcomeBoundary = agentJournal.LastSequence;
        bossLootDispatchTick = Main.GameUpdateCount;
        if (!ApplyPlan(plan, null, "chat"))
            FinishBossLootSelectionNative("failed", "Parsed pickup was rejected before the native executor ran.");
    }

    private void TickBossLootSelectionNative()
    {
        if (CompanionNpc is not { active: true } actor || Main.GameUpdateCount - bossLootDispatchTick > 90)
        { FinishBossLootSelectionNative("uncovered", "Native pickup did not produce an outcome within the bounded window."); return; }
        AgentOutcome? outcome = agentJournal.Outcomes.FirstOrDefault(entry => entry.Sequence > bossLootOutcomeBoundary && entry.Task.Action == AIActionType.Pickup);
        if (outcome == null) return;
        if (bossLootNumericControl) bossLootNumericOutcome = outcome;
        else bossLootAliasOutcome = outcome;
        BossLootTransfers? transfer = ReadBossLootTransfers(outcome);
        BossLootGround decoy = ReadBossLootGround(bossLootDecoySlot, actor.Center);
        var delta = BossLootStockDelta(bossLootSeedStock, CompanionCrafting.InventoryCounts(Loadout));
        bossLootStages.Add(new { tick = Main.GameUpdateCount, bossLootNumericControl, outcome, transfer, decoy, delta,
            sameDecoy = SameBossLootDecoy(decoy), encounters = BossEvidence.Snapshot() });
        if (outcome.Task.Item != (bossLootNumericControl ? bossLootNumericPlan?.Item : "boss_loot") || outcome.Task.Count != 1)
        { FinishBossLootSelectionNative("uncovered", "The recorded native outcome did not belong to the exact pickup request."); return; }
        bool exactDecoyTransfer = outcome.Status == "succeeded" && transfer is { ActualCount: 1 } &&
            transfer.Items.Count == 1 && transfer.Items.GetValueOrDefault(BossLootDecoyType) == 1 &&
            delta.Count == 1 && delta.GetValueOrDefault(BossLootDecoyType) == 1 && !decoy.Active;
        if (bossLootNumericControl)
        {
            FinishBossLootSelectionNative(exactDecoyTransfer ? "passed" : "uncovered",
                exactDecoyTransfer ? "" : "Numeric item ID did not actually pick up the same ordinary decoy.");
            return;
        }
        if (exactDecoyTransfer)
        { FinishBossLootSelectionNative("failed", "Boss-only alias transferred the ordinary decoy without a Boss source."); return; }
        if (outcome.Status != "blocked" || transfer is not { ActualCount: 0, Items.Count: 0 } || delta.Count != 0 ||
            !SameBossLootDecoy(decoy) || decoy.Distance > MiningApproachProbe.ToolReachPixels ||
            BossLootGroundWithin(actor.Center, BossLootSelectionRadius).Any(item => item.SpawnId != 0))
        { FinishBossLootSelectionNative("uncovered", "The exact ordinary-decoy rejection baseline was not retained."); return; }
        bossLootNumericControl = true;
        bossLootNumericPlan = AIPlanParser.Parse(JsonSerializer.Serialize(new
        { resume = true, tasks = new[] { new { type = "pickup", item = BossLootDecoyType, count = 1 } } }));
        DispatchBossLootSelectionNative(bossLootNumericPlan);
    }

    private void FinishBossLootSelectionNative(string status, string detail)
    {
        bossLootSelectionRunning = false;
        SaveBossLootSelectionResult(status, detail, false);
        Stop();
    }

    private AIPlan? BeginBossLootSelectionBatch()
    {
        bossLootInputHashes = ReadBossReviewHashes();
        bossLootSelectionAudit = naturalHouseAudit;
        BossEncounter? encounter = BossEvidence.ReadEncounter(naturalBossSpawnId);
        if (CompanionNpc is not { active: true } actor || encounter is not { NpcType: NPCID.KingSlime, DeathSequence: > 0, CompanionDamage: > 0, LootPickedUp: 0 })
        { FinishNaturalFlash("replay_boss_loot_uncovered", "No real same-SpawnId Boss kill with untouched loot was witnessed."); return null; }
        BossLootGround[] tagged = BossLootGroundWithin(actor.Center, BossLootSelectionRadius).Where(item => item.SpawnId > 0).ToArray();
        BossLootGround? anchor = tagged.Where(item => item.SpawnId == encounter.SpawnId).MinBy(item => item.Distance);
        bossLootPreflight = new { tick = Main.GameUpdateCount, encounter, tagged, anchor };
        if (anchor == null || tagged.Any(item => item.SpawnId != encounter.SpawnId))
        { FinishNaturalFlash("replay_boss_loot_uncovered", "No clean native drop from the killed Boss was in selection range."); return null; }

        bossLootAliasPlan = AIPlanParser.Parse(BossLootBatchJson);
        bossLootOutcomeBoundary = agentJournal.LastSequence;
        bossLootBatchActive = true;
        return bossLootAliasPlan;
    }

    internal void ObserveBossLootBeforeNpcAI(NPC npc)
    {
        if (!bossLootBatchActive || naturalFlashFinished || bossLootFirstTransfer != null ||
            Executor.CurrentAction != AIActionType.Pickup || Executor.HoldPosition || !ReferenceEquals(npc, CompanionNpc) ||
            ContinuousField("currentPlan") is not AIPlan { Action: AIActionType.Pickup, Item: "boss_loot", Count: 0 } ||
            ContinuousField("completeStepRequested") is true) return;
        BossLootGround[] taggedInReach = BossLootGroundWithin(npc.Center, MiningApproachProbe.ToolReachPixels)
            .Where(item => item.SpawnId == naturalBossSpawnId).ToArray();
        if (!bossLootBatchDecoyAttempted)
        {
            // A previously selected drop can still be out of reach while another tagged stack is closer.
            int selectedSlot = (int)(ContinuousField("pickupItemIndex") ?? -1);
            BossLootGround? reachable = selectedSlot < 0 ? taggedInReach.MinBy(item => item.Distance) :
                taggedInReach.FirstOrDefault(item => item.Slot == selectedSlot);
            if (reachable == null) return;
            bossLootBatchDecoyAttempted = true;
            bool seeded = SeedBossLootDecoy(npc, npc.Center, reachable);
            bossLootStages.Add(new { kind = "batch_decoy_seed_attempt", tick = Main.GameUpdateCount, selectedSlot, reachable, seeded });
        }
        if (bossLootDecoySlot < 0) return;
        BossLootGround decoy = ReadBossLootGround(bossLootDecoySlot, npc.Center);
        bossLootBeforePickup = new(Main.GameUpdateCount, npc.Center.X, npc.Center.Y, decoy, SameBossLootDecoy(decoy),
            taggedInReach, BossEvidence.ReadEncounter(naturalBossSpawnId)?.LootPickedUp ?? 0, CompanionCrafting.InventoryCounts(Loadout));
    }

    internal void ObserveBossLootAfterNpcAI(NPC npc)
    {
        if (!bossLootBatchActive || naturalFlashFinished || bossLootFirstTransfer != null || !ReferenceEquals(npc, CompanionNpc) ||
            bossLootBeforePickup is not { } before || before.Tick != Main.GameUpdateCount) return;
        int loot = BossEvidence.ReadEncounter(naturalBossSpawnId)?.LootPickedUp ?? 0;
        Dictionary<int, int> stock = CompanionCrafting.InventoryCounts(Loadout);
        if (loot == before.LedgerLoot && BossLootStockDelta(before.Stock, stock).Count == 0) return;
        BossLootGround decoy = ReadBossLootGround(bossLootDecoySlot, npc.Center);
        bossLootFirstTransfer = new(before, decoy, SameBossLootDecoy(decoy), loot, stock);
    }

    private void SaveBossLootSelectionBatch(string replayStatus, string replayError)
    {
        if (!BossLootSelectionBatchSelected) return;
        AgentOutcome? alias = bossLootAliasOutcome = naturalOutcomes.FirstOrDefault(outcome => outcome.Sequence > bossLootOutcomeBoundary &&
            outcome.Task.Action == AIActionType.Pickup && outcome.Task.Item == "boss_loot" && outcome.Task.Count == 0);
        BossLootTransfers? transfer = ReadBossLootTransfers(alias);
        BossLootTransferFrame? first = bossLootFirstTransfer;
        bool exposed = first is { Before: { SameDecoy: true, Decoy.Distance: <= BossLootBatchRadius, TaggedInReach.Length: > 0 } };
        int ledgerDelta = first == null ? 0 : first.LedgerAfter - first.Before.LedgerLoot;
        var stockDelta = first == null ? new Dictionary<int, int>() : BossLootStockDelta(first.Before.Stock, first.StockAfter);
        bool mixed = ledgerDelta > 0 && transfer?.Items.GetValueOrDefault(BossLootDecoyType) == 1 && stockDelta.GetValueOrDefault(BossLootDecoyType) == 1 &&
            first is { DecoyAfter.Active: false };
        bool decoyRetained = first is { SameDecoyAfter: true } && CompanionNpc is { } actor &&
            SameBossLootDecoy(ReadBossLootGround(bossLootDecoySlot, actor.Center));
        bool actualLootOnly = first != null && transfer is { ActualCount: > 0 } && ledgerDelta == transfer.ActualCount &&
            transfer.Items.Values.Sum() == transfer.ActualCount && decoyRetained && stockDelta.GetValueOrDefault(BossLootDecoyType) == 0 &&
            BossLootStockMatchesTransfers(stockDelta, transfer.Items) &&
            transfer.Items.All(row => first.Before.TaggedInReach.Where(item => item.ItemType == row.Key).Sum(item => item.Stack) >= row.Value);
        bool completed = replayStatus == "passed_replay" && goalStatus == "completed" && alias is { Status: "succeeded" } &&
            naturalOutcomes.Any(outcome => outcome.Sequence > alias.Sequence && outcome.Task.Action == AIActionType.Come && outcome.Status == "succeeded");
        bossLootStages.Add(new { replayStatus, replayError, exposed, ledgerDelta, mixed, decoyRetained, actualLootOnly, completed, goalStatus, lastCompletedGoal, stockDelta,
            encounter = BossEvidence.ReadEncounter(naturalBossSpawnId), transfer, first });
        string status = !exposed || transfer == null ? "uncovered" : mixed ? "failed" : actualLootOnly && completed ? "passed" : "inconclusive";
        SaveBossLootSelectionResult(status, status switch
        {
            "uncovered" => "No same-tick proof of the ordinary decoy inside the native 5-tile batch radius alongside reachable tagged loot.",
            "failed" => "Boss-only batch transferred an ordinary decoy within the same native pickup.",
            "inconclusive" => "Exposure was witnessed but a Boss-only transfer and full return/completion were not both verified.",
            _ => ""
        }, true);
    }

    private void SaveBossLootSelectionResult(string status, string detail, bool batch)
    {
        var preservation = (bossLootSelectionAudit ?? naturalHouseAudit)?.CheckPreservation();
        int primary = PrimaryDecisionRequestCount, combat = CombatDecisionRequestCount, warps = Executor.NavigationTeleportCount;
        if (primary != 0 || combat != 0 || warps != 0 || preservation is { preserved: false } || preservation is { diffCount: > 0 })
        { status = "failed"; detail = "Zero-request, zero-warp or original-house preservation boundary failed. " + detail; }
        if (status == "passed" && preservation is not { preserved: true, coverageComplete: true, diffCount: 0 })
        { status = "uncovered"; detail = "Complete original-house preservation coverage was unavailable."; }
        string dir = Path.Combine(Main.SavePath, "AITeammate");
        Directory.CreateDirectory(dir);
        NPC? actor = CompanionNpc;
        File.WriteAllText(Path.Combine(dir, "boss-loot-selection-native-result.json"), JsonSerializer.Serialize(new
        {
            status, detail, variant = batch ? "same_spawn_batch" : "no_boss_source", controlled = true,
            llmDisabled = GetLlmSettings() == null, primaryRequests = primary, combatRequests = combat,
            warps, preservation, copiedInputHashes = bossLootInputHashes, isolatedFileHashesAtFinish = ReadBossReviewHashes(),
            scope = batch ? "Controlled replay of the opening public decision and native same-Boss kill. A single ordinary Item.NewItem decoy is attempted at the live companion center immediately before pickup AI once the native tagged drop is in reach. The existing isolated observer guard remains active." :
                "Native companion at its live start or summoned beside the loaded player, with one ordinary Item.NewItem decoy. The isolated observer cannot pick up items or take damage. No player relocation or route injection.",
            preflight = bossLootPreflight, seed = bossLootSeed, aliasJson = batch ? BossLootBatchJson : BossLootNegativeJson,
            aliasPlan = bossLootAliasPlan, aliasOutcome = bossLootAliasOutcome, numericPlan = bossLootNumericPlan, numericOutcome = bossLootNumericOutcome,
            decoyAtFinish = actor == null || bossLootDecoySlot < 0 ? null : ReadBossLootGround(bossLootDecoySlot, actor.Center), stages = bossLootStages
        }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        TestLog($"{BossLootSelectionNativeCase} {(batch ? "batch" : "no-source")} {status}: {detail}");
    }
}

public sealed class BossLootSelectionPickupWitness : GlobalNPC
{
    public override bool PreAI(NPC npc)
    {
        AITeammateSystem.Instance?.ObserveBossLootBeforeNpcAI(npc);
        return true;
    }

    public override void PostAI(NPC npc) => AITeammateSystem.Instance?.ObserveBossLootAfterNpcAI(npc);
}
