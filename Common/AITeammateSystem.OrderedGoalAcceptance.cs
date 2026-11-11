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
    private const string OrderedGoalRequest = "带着至少10木材和5石块走回我这里，然后先做2个木平台，再做3个普通火把，留至少9木、5石、2平台、3火把在背包里。保留原屋。";
    private const string EarlyPlatform = """{"tasks":[{"type":"craft","item":"WoodPlatform","count":2},{"type":"come"}]}""";
    private bool orderedGoalRunning, orderedGoalThroughDoor;
    private int orderedGoalPhase, orderedGoalFrames, orderedGoalRequests, orderedGoalCombatRequests, orderedGoalEarlyAttempts;
    private DateTime orderedGoalStarted;
    private ShelterWorldAudit? orderedGoalAudit;
    private readonly List<object> orderedGoalEvents = new();

    private void BeginOrderedGoalAcceptance(Vector2 start, bool throughDoor)
    {
        NPC npc = CompanionNpc!;
        Player player = Main.LocalPlayer;
        Stop();
        orderedGoalPhase = orderedGoalFrames = orderedGoalEarlyAttempts = 0;
        orderedGoalEvents.Clear();
        orderedGoalThroughDoor = throughDoor;
        npc.Center = throughDoor ? Stand(3206, 393) - new Vector2(4, 0) : start;
        if (throughDoor) player.Center = Stand(3208, 393) + new Vector2(4, 0);
        npc.velocity = player.velocity = Vector2.Zero;
        Executor.ResetNavigationTelemetry();
        // Seed carried materials and already-owned products. Only the later native craft can prove new production.
        Loadout.AddItem(ItemID.Wood, 11);
        Loadout.AddItem(ItemID.StoneBlock, 5);
        Loadout.AddItem(ItemID.Gel, 1);
        Loadout.AddItem(ItemID.WoodPlatform, 2);
        Loadout.AddItem(ItemID.Torch, 3);
        orderedGoalAudit = ShelterWorldAudit.Capture(3220, 390);
        orderedGoalStarted = DateTime.UtcNow;
        orderedGoalRequests = PrimaryDecisionRequestCount;
        orderedGoalCombatRequests = CombatDecisionRequestCount;
        orderedGoalRunning = true;
        manualControlActive = false;
        if (throughDoor && (!Enumerable.Range(390, 3).All(y => Main.tile[3207, y].HasTile && Main.tile[3207, y].TileType == TileID.ClosedDoor) ||
            Math.Abs(npc.Center.X - player.Center.X) > 48f || Math.Abs(npc.Bottom.Y - player.Bottom.Y) > 24f ||
            !TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width) || OrderedGoalPassage(npc, player)))
        { FinishOrderedGoal("uncovered", "Expected grounded poses inside the old Come radius with a closed door between them."); return; }

        if (!ApplyPlan(AIPlanParser.Parse("""{"goal":"更早的制作任务","tasks":[{"type":"craft","item":94,"count":2}]}"""), null, "chat", "做两个平台") ||
            !Executor.HasCurrentPlan)
        { FinishOrderedGoal("failed", "Could not prepare an earlier active task to replace."); return; }
        RecordOrderedGoalEvent("old_goal_active");
        const string initial = """
            {"goal":"携材返回后依次制作","keep_tasks":false,"completion_conditions":[
              {"type":"ordered_actions","actions":[{"type":"come","carrying":[{"item_id":9,"count":10},{"item_id":3,"count":5}]},
                {"type":"craft","item_id":94,"count":2},{"type":"craft","item_id":8,"count":3}]},
              {"type":"inventory_at_least","item_id":9,"count":9},{"type":"inventory_at_least","item_id":3,"count":5},
              {"type":"inventory_at_least","item_id":94,"count":2},{"type":"inventory_at_least","item_id":8,"count":3},{"type":"near_player"}],
              "working_plan":{"summary":"返程、制作平台、制作火把","steps":[
                {"id":"carry","objective":"携料返回","success_criteria":"带着10木5石走到玩家身边"},
                {"id":"platform","objective":"制作木平台","success_criteria":"实际新做2个平台","depends_on":["carry"]},
                {"id":"torch","objective":"制作普通火把","success_criteria":"实际新做3火把","depends_on":["platform"]}]}}
            """;
        if (!ApplyPlan(AIPlanParser.Parse(initial), null, "chat", OrderedGoalRequest) || Executor.SnapshotTasks().Count != 0)
        { FinishOrderedGoal("failed", "New taskless goal rejected or inherited the previous queue."); return; }
        RecordOrderedGoalEvent("initial");
        bool reversed = DispatchOrderedGoal("""{"tasks":[{"type":"craft","item":94,"count":2,"plan_step":"platform"},{"type":"come","plan_step":"carry"}]}""");
        RecordOrderedGoalEvent("reversed_batch");
        if (reversed || decisionReceipts.Last?.code != "working_plan_rejected" || Executor.SnapshotTasks().Count != 0)
        { FinishOrderedGoal("failed", "Contradictory bound queue reached the executor."); return; }
        if (!DispatchOrderedGoal(EarlyPlatform))
            FinishOrderedGoal("failed", "Unbound early craft did not reach native admission.");
    }

    private bool DispatchOrderedGoal(string json) => ApplyPlan(AIPlanParser.Parse(json), null, "goal");

    private void TickOrderedGoalAcceptance()
    {
        Player player = Main.LocalPlayer;
        player.immune = true;
        player.immuneTime = 600;
        foreach (NPC hostile in Main.npc) if (hostile.active && !hostile.friendly) hostile.active = false;
        orderedGoalFrames++;
        if (CompanionNpc == null || orderedGoalFrames > 3600 || DateTime.UtcNow - orderedGoalStarted > TimeSpan.FromSeconds(90) ||
            PrimaryDecisionRequestCount != orderedGoalRequests || CombatDecisionRequestCount != orderedGoalCombatRequests || Executor.NavigationTeleportCount != 0)
        { FinishOrderedGoal("failed", "Missing actor, deadline, API call or warp."); return; }

        if (orderedGoalPhase is 0 or 2 && decisionReceipts.Last?.code == "ordered_action_pending")
        {
            RecordOrderedGoalEvent(orderedGoalPhase == 0 ? "early_craft_rejected" : "early_torch_rejected");
            if (Loadout.CountItem(ItemID.Wood) != 11 || Loadout.CountItem(ItemID.Gel) != 1 || Loadout.CountItem(ItemID.WoodPlatform) != 2 ||
                Loadout.CountItem(ItemID.Torch) != 3 || agentJournal.Outcomes.Any(o => o.Task.Action == AIActionType.Craft) ||
                orderedGoalPhase == 0 && agentJournal.Outcomes.Count != 0 || Executor.SnapshotTasks().Count != 0)
            { FinishOrderedGoal("failed", "A rejected craft mutated inventory, executed or left a runnable tail."); return; }
            int expectedRejections = orderedGoalPhase == 0 ? ++orderedGoalEarlyAttempts : 1;
            if (dispatchRepairs.Rejections != expectedRejections)
            { FinishOrderedGoal("failed", "Native order refusal did not retain/reset its repair budget correctly."); return; }
            if (orderedGoalPhase == 0 && orderedGoalEarlyAttempts == 1)
            {
                if (!DispatchOrderedGoal(EarlyPlatform)) FinishOrderedGoal("failed", "Could not replay identical rejected proposal.");
                return;
            }
            string json = orderedGoalPhase == 0
                ? """{"tasks":[{"type":"come","plan_step":"carry"},{"type":"inspect","target":"recipe","item":94}]}"""
                : """{"tasks":[{"type":"craft","item":94,"count":2,"plan_step":"platform"},{"type":"craft","item":8,"count":3,"plan_step":"torch"}]}""";
            orderedGoalPhase++;
            if (!DispatchOrderedGoal(json)) FinishOrderedGoal("failed", "Corrected segment rejected.");
        }
        else if (orderedGoalPhase == 1 && agentJournal.Outcomes.Any(o => o.Task.Action == AIActionType.Inspect))
        {
            RecordOrderedGoalEvent("carrying_return_then_inspect");
            var evidence = CurrentGoalCompletion();
            bool liveFactsMet = evidence.Conditions.Where(c => c.Type != "ordered_actions").All(c => c.Status == "met");
            if (!liveFactsMet || dispatchRepairs.Rejections != 0 ||
                orderedGoalThroughDoor && !OrderedGoalPassage(CompanionNpc, player) ||
                !agentJournal.Outcomes.Any(o => o.Task.Action == AIActionType.Come && o.Status == "succeeded") ||
                DispatchOrderedGoal("""{"goal_done":true,"tasks":[]}""") || decisionReceipts.Last?.code != "completion_evidence_missing")
            { FinishOrderedGoal("failed", "Return failed or existing items bypassed ordered evidence."); return; }
            RecordOrderedGoalEvent("premature_completion_rejected");
            orderedGoalPhase = 2;
            if (!DispatchOrderedGoal("""{"tasks":[{"type":"craft","item":8,"count":3}]}"""))
                FinishOrderedGoal("failed", "Early second craft did not reach native admission.");
        }
        else if (orderedGoalPhase == 3 && agentJournal.Outcomes.Count(o => o.Task.Action == AIActionType.Craft) == 2)
        {
            RecordOrderedGoalEvent("both_crafts_finished");
            var crafts = agentJournal.Outcomes.Where(o => o.Task.Action == AIActionType.Craft).ToArray();
            bool crafted = crafts.All(o => o.Status == "succeeded") && crafts.Select(o => o.Task.Item).SequenceEqual(new[] { "94", "8" }) &&
                Loadout.CountItem(ItemID.Wood) == 9 && Loadout.CountItem(ItemID.Gel) == 0 &&
                Loadout.CountItem(ItemID.WoodPlatform) == 4 && Loadout.CountItem(ItemID.Torch) == 6;
            bool preserved = orderedGoalAudit?.CheckPreservation() is { preserved: true, coverageComplete: true, diffCount: 0 };
            bool passed = crafted && preserved && DispatchOrderedGoal("""{"goal_done":true,"tasks":[]}""") &&
                decisionReceipts.Last?.code == "completion_evidence_verified";
            FinishOrderedGoal(passed ? "passed" : "failed", passed ? "" : "Final craft, original-house audit or completion proof failed.");
        }
    }

    private static bool OrderedGoalPassage(NPC npc, Player player)
        => Collision.CanHitLine(npc.position, npc.width, npc.height, player.position, player.width, player.height);

    private void RecordOrderedGoalEvent(string phase)
    {
        NPC npc = CompanionNpc!;
        Player player = Main.LocalPlayer;
        orderedGoalEvents.Add(new { phase, frame = orderedGoalFrames, tick = Main.GameUpdateCount,
            actor = new { x = npc.Center.X, feet = npc.Bottom.Y, grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width) },
            player = new { x = player.Center.X, feet = player.Bottom.Y },
            wood = Loadout.CountItem(ItemID.Wood), stone = Loadout.CountItem(ItemID.StoneBlock), gel = Loadout.CountItem(ItemID.Gel),
            platforms = Loadout.CountItem(ItemID.WoodPlatform), torches = Loadout.CountItem(ItemID.Torch),
            directPassage = OrderedGoalPassage(npc, player), rejections = dispatchRepairs.Rejections,
            receipt = decisionReceipts.Last, completion = CurrentGoalCompletion() });
    }

    private void FinishOrderedGoal(string status, string error)
    {
        orderedGoalRunning = false;
        var protection = orderedGoalAudit?.CheckPreservation();
        File.WriteAllText(Path.Combine(Main.SavePath, "AITeammate", "ordered-goal-native-result.json"), JsonSerializer.Serialize(new
        {
            status, error, scope = "Original-house copy, seeded materials and finished items, controlled JSON decisions; no model request.",
            profile = orderedGoalThroughDoor ? "original_closed_door" : "original_platform_floors",
            request = OrderedGoalRequest, frames = orderedGoalFrames, apiCalls = PrimaryDecisionRequestCount - orderedGoalRequests,
            combatCalls = CombatDecisionRequestCount - orderedGoalCombatRequests, warps = Executor.NavigationTeleportCount,
            events = orderedGoalEvents, outcomes = agentJournal.Outcomes, lastCompletedGoal,
            protection = protection == null ? null : new { protection.protectedCells, protection.coverageComplete, protection.preserved, protection.diffCount }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Stop();
    }
}
