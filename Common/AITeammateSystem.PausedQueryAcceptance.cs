using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string PausedQueryNativeCase = "paused_query_native";
    private const string PausedQueryGoal = "受控原任务暂停查询";
    private const string PausedQueryNewGoal = "受控显式恢复后换装备";
    private const int PausedQueryObserveTicks = 15, PausedQueryResumeDeadline = 240;
    private static readonly string PausedRecipeInput = $$"""{"tasks":[{"type":"inspect","target":"recipe","item":{{ItemID.WoodPlatform}}}]}""";

    private enum PausedQueryPhase { HoldQuery, HoldResume, ExternalWaitQuery, ExternalWaitResume, StopQuery, Mixed, NewGoalResume }
    private sealed record PausedQueryStock(string Container, int Slot, int Item, int Count, int Prefix);
    private sealed record PausedQueryState(ulong Tick, string Goal, string GoalId, string GoalStatus, int ContractRevision,
        string Contract, GoalWorkingPlanView? WorkingPlan, GoalExecutionPlanView? ExecutionPlan,
        AIActionType Action, bool HasPlan, AIPlan[] Queue, bool Hold, bool WaitingForPlayer, bool ReviewPending,
        long Sequence, float ActorX, float ActorFeet, float PlayerX, PausedQueryStock[] Stock);
    private sealed record PausedQueryDecision(JsonElement Input, string Source, AIPlan? Parsed, bool Accepted, string Error,
        PausedQueryState Before, PausedQueryState After, PlanDecisionReceipt? Receipt);
    private sealed record PausedQueryCheck(string Name, bool Passed, object Evidence);

    private bool pausedQueryRunning, pausedQueryStateStable, pausedQueryStockStable;
    private PausedQueryPhase pausedQueryPhase;
    private PausedQueryDecision? pausedQueryDecision;
    private AIPlan[] pausedQueryOldQueue = Array.Empty<AIPlan>();
    private float pausedQueryMaxTravel;
    private ShelterWorldAudit? pausedQueryAudit;
    private Dictionary<string, string> pausedQueryInputHashes = new();
    private readonly List<PausedQueryCheck> pausedQueryChecks = new();

    private void BeginPausedQueryNative()
    {
        Stop();
        Executor.ResetNavigationTelemetry();
        if (GetLlmSettings() != null || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 ||
            CompanionNpc is not { active: true } npc || Main.LocalPlayer.dead)
        { FinishPausedQueryNative("failed_setup", "Requires disabled LLM and a live companion and observer."); return; }

        pausedQueryInputHashes = ReadBossReviewHashes();
        Player player = Main.LocalPlayer;
        npc.Center = Stand(3170, 400);
        player.Center = Stand(3162, 400);
        npc.velocity = player.velocity = Vector2.Zero;
        if (!TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, 0, npc.width) ||
            !TilePathfinder.IsGroundedAt(player.Center.X, player.Bottom.Y, 0, player.width))
        { FinishPausedQueryNative("failed_setup", "The measured original-house exterior positions were unavailable."); return; }

        int[] supplied = { ItemID.MoltenHelmet, ItemID.MoltenBreastplate, ItemID.MoltenGreaves, ItemID.CopperHelmet, ItemID.GoldHelmet };
        if (supplied.Any(item => !Loadout.AddItem(item, 1)))
        { FinishPausedQueryNative("failed_setup", "The controlled equipment did not fit."); return; }
        pausedQueryAudit = ShelterWorldAudit.Capture(3220, 390);
        ShelterPreservationReport preservation = pausedQueryAudit.CheckPreservation();
        bool protectedHouse = preservation is { preserved: true, coverageComplete: true, diffCount: 0 };
        pausedQueryChecks.Add(new("original_house_baseline", protectedHouse, new { preservation, supplied, setup = CapturePausedQuery() }));
        if (!protectedHouse)
        { FinishPausedQueryNative("failed_setup", "Original-house protection baseline was incomplete."); return; }

        pausedQueryRunning = true;
        StartPausedRecipeCase(external: false);
    }

    private void SeedPausedQueryQueue(int equip, bool external, string label)
    {
        Stop();
        manualControlActive = false;
        string seed = $$"""
            {"goal":"{{PausedQueryGoal}}","resume":true,"completion_conditions":[{"type":"structure_preserved"}],
             "working_plan":{"summary":"先返回再装备","steps":[
               {"id":"return_home","objective":"走回观察者附近","success_criteria":"原生 Come 到达"},
               {"id":"equip_gear","objective":"戴上本案指定装备","success_criteria":"原生装备槽中可见","depends_on":["return_home"]}]},
             "tasks":[{"type":"come","plan_step":"return_home"},{"type":"equip","item":{{equip}},"plan_step":"equip_gear"}]}
            """;
        PausedQueryDecision started = DispatchPausedQuery(seed, "chat");
        PausedQueryDecision pause = DispatchPausedQuery(external ? """{"wait_for_player":true}""" : """{"hold":true}""", "goal");
        bool armed = started.Accepted && started.After.Goal == PausedQueryGoal && started.After.HasPlan &&
            started.After.Action == AIActionType.Come && started.After.Queue.Length == 2 && !started.After.Hold &&
            pause.Accepted && pause.After.Hold && pause.After.WaitingForPlayer == external &&
            pause.After.GoalStatus == (external ? "waiting_for_player" : "paused") && SamePausedQueryQueue(started.After, pause.After) &&
            pause.After.WorkingPlan?.steps.Count == 2 && pause.After.ExecutionPlan is { paused: true, tasks.Count: 2 };
        pausedQueryChecks.Add(new(label + "_seed_paused_queue", armed, new { started, pause }));
    }

    private void StartPausedRecipeCase(bool external)
    {
        SeedPausedQueryQueue(external ? ItemID.MoltenBreastplate : ItemID.MoltenHelmet, external, external ? "external_wait" : "hold");
        WatchPausedQuery(external ? PausedQueryPhase.ExternalWaitQuery : PausedQueryPhase.HoldQuery,
            DispatchPausedQuery(PausedRecipeInput, "chat"));
    }

    private void WatchPausedQuery(PausedQueryPhase phase, PausedQueryDecision decision)
    {
        pausedQueryPhase = phase;
        pausedQueryDecision = decision;
        pausedQueryMaxTravel = Math.Abs(decision.After.ActorX - decision.Before.ActorX);
        pausedQueryStateStable = SamePausedQueryState(decision.Before, decision.After);
        pausedQueryStockStable = decision.Before.Stock.SequenceEqual(decision.After.Stock);
    }

    private void TickPausedQueryNative()
    {
        if (CompanionNpc is not { active: true } || Main.LocalPlayer.dead || GetLlmSettings() != null ||
            PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 || Executor.NavigationTeleportCount != 0)
        { FinishPausedQueryNative("failed", "Missing actor, enabled LLM, API call or warp."); return; }

        Main.LocalPlayer.immune = true;
        Main.LocalPlayer.immuneTime = 600;
        foreach (NPC hostile in Main.npc)
            if (hostile.active && !hostile.friendly) hostile.active = false;

        PausedQueryDecision decision = pausedQueryDecision!;
        PausedQueryState after = CapturePausedQuery();
        ulong ticks = after.Tick - decision.After.Tick;
        AgentOutcome[] outcomes = agentJournal.Outcomes.Where(outcome => outcome.Sequence > decision.Before.Sequence).ToArray();
        if (pausedQueryPhase is PausedQueryPhase.HoldResume or PausedQueryPhase.ExternalWaitResume or PausedQueryPhase.NewGoalResume)
        {
            VerifyPausedQueryResume(decision, after, outcomes, ticks);
            return;
        }

        pausedQueryMaxTravel = Math.Max(pausedQueryMaxTravel, Math.Abs(after.ActorX - decision.Before.ActorX));
        pausedQueryStateStable &= SamePausedQueryState(decision.Before, after);
        pausedQueryStockStable &= decision.Before.Stock.SequenceEqual(after.Stock);
        if (ticks < PausedQueryObserveTicks) return;

        bool mixed = pausedQueryPhase == PausedQueryPhase.Mixed;
        bool physicalStatePreserved = decision.Before.Hold && pausedQueryStateStable && pausedQueryStockStable && pausedQueryMaxTravel <= 0.25f;
        bool observedRecipe = outcomes.Length == 1 && IsNativePausedRecipe(outcomes[0]);
        bool passed = mixed
            ? decision.Parsed?.Steps.Count == 2 && !decision.Accepted && decision.Receipt?.status == "rejected" && outcomes.Length == 0 && physicalStatePreserved
            : decision.Parsed?.Resume == false && decision.Accepted && observedRecipe && physicalStatePreserved;
        pausedQueryChecks.Add(new(pausedQueryPhase + "_native_ticks", passed,
            new { decision, after, ticks, outcomes, observedRecipe, stateStable = pausedQueryStateStable, stockStable = pausedQueryStockStable,
                maximumHorizontalPixels = pausedQueryMaxTravel }));

        switch (pausedQueryPhase)
        {
            case PausedQueryPhase.HoldQuery:
            case PausedQueryPhase.ExternalWaitQuery:
                pausedQueryOldQueue = decision.Before.Queue;
                WatchPausedQuery(pausedQueryPhase == PausedQueryPhase.HoldQuery ? PausedQueryPhase.HoldResume : PausedQueryPhase.ExternalWaitResume,
                    DispatchPausedQuery("""{"resume":true}""", "chat"));
                break;
            case PausedQueryPhase.StopQuery:
                SeedPausedQueryQueue(ItemID.CopperHelmet, external: false, "mixed");
                string mixedInput = $$"""{"tasks":[{"type":"inspect","target":"recipe","item":{{ItemID.WoodPlatform}}},{"type":"equip","item":{{ItemID.MoltenGreaves}}}]}""";
                WatchPausedQuery(PausedQueryPhase.Mixed, DispatchPausedQuery(mixedInput, "goal"));
                break;
            case PausedQueryPhase.Mixed:
                SeedPausedQueryQueue(ItemID.CopperHelmet, external: false, "new_goal");
                string replacement = $$"""
                    {"goal":"{{PausedQueryNewGoal}}","resume":true,"completion_conditions":[{"type":"structure_preserved"}],
                     "tasks":[{"type":"equip","item":{{ItemID.GoldHelmet}}}]}
                    """;
                WatchPausedQuery(PausedQueryPhase.NewGoalResume, DispatchPausedQuery(replacement, "chat"));
                break;
        }
    }

    private void VerifyPausedQueryResume(PausedQueryDecision decision, PausedQueryState after, AgentOutcome[] outcomes, ulong ticks)
    {
        bool freshGoal = pausedQueryPhase == PausedQueryPhase.NewGoalResume;
        bool continued = decision.Accepted && decision.Parsed?.Resume == true && decision.Before.Hold &&
            !decision.After.Hold && !decision.After.WaitingForPlayer && decision.After.GoalStatus == "active" &&
            (freshGoal ? decision.After.Goal == PausedQueryNewGoal && decision.After.GoalId != decision.Before.GoalId && decision.After.Queue.Length == 1
                : SamePausedQueryGoal(decision.Before, decision.After) && SamePausedQueryQueue(decision.Before, decision.After) &&
                  SamePausedQueryQueue(pausedQueryOldQueue, decision.After.Queue) && decision.After.Queue.Length == 2);
        AIPlan[] expected = freshGoal ? decision.After.Queue : pausedQueryOldQueue;
        bool outcomesMatch = outcomes.Length == expected.Length && outcomes.Length > 0 && outcomes.Zip(expected).All(pair =>
            pair.First.Status == "succeeded" && PlanTaskIdentity.AreEquivalent(pair.First.Task, pair.Second));
        if (continued && !outcomesMatch && outcomes.All(outcome => outcome.Status != "blocked") && ticks < PausedQueryResumeDeadline) return;
        int armor = pausedQueryPhase switch
        {
            PausedQueryPhase.HoldResume => ItemID.MoltenHelmet,
            PausedQueryPhase.ExternalWaitResume => ItemID.MoltenBreastplate,
            _ => ItemID.GoldHelmet
        };
        bool actuallyEquipped = Loadout.Armor.Any(item => !item.IsAir && item.type == armor);
        bool workingStepsExecuted = freshGoal || (after.WorkingPlan is { steps.Count: 2 } work && work.steps.All(step => step.successfulActions == 1) &&
            after.ExecutionPlan is { tasks.Count: 2 } execution && execution.tasks.Zip(outcomes).All(pair =>
                pair.First.status == "succeeded" && pair.First.outcomeSequence == pair.Second.Sequence));
        pausedQueryChecks.Add(new(pausedQueryPhase + "_native_outcomes", continued && outcomesMatch && actuallyEquipped &&
            workingStepsExecuted && SamePausedQueryGoal(decision.After, after),
            new { decision, after, ticks, expected, outcomes, continued, actuallyEquipped, workingStepsExecuted }));

        if (pausedQueryPhase == PausedQueryPhase.HoldResume)
            StartPausedRecipeCase(external: true);
        else if (pausedQueryPhase == PausedQueryPhase.ExternalWaitResume)
        {
            HandlePlayerMessage(Main.LocalPlayer, "停下");
            PausedQueryState stopped = CapturePausedQuery();
            pausedQueryChecks.Add(new("stop_has_no_goal_or_queue", stopped.Hold && stopped.Goal.Length == 0 && stopped.Queue.Length == 0, stopped));
            WatchPausedQuery(PausedQueryPhase.StopQuery, DispatchPausedQuery(PausedRecipeInput, "chat"));
        }
        else
        {
            bool passed = pausedQueryChecks.All(check => check.Passed);
            FinishPausedQueryNative(passed ? "passed" : "failed", passed ? "" : "One or more paused-query checks failed.");
        }
    }

    private static bool IsNativePausedRecipe(AgentOutcome outcome)
    {
        if (outcome.Status != "succeeded" || outcome.Task.Action != AIActionType.Inspect || outcome.Task.Target != "recipe" ||
            outcome.Task.Item != ItemID.WoodPlatform.ToString()) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(outcome.Result);
            JsonElement root = document.RootElement;
            return root.GetProperty("topic").GetString() == "recipe" && root.GetProperty("found").GetBoolean() &&
                root.GetProperty("recipes").EnumerateArray().Any(row =>
                {
                    int index = row.GetProperty("recipeId").GetInt32();
                    return row.GetProperty("output").GetProperty("id").GetInt32() == ItemID.WoodPlatform &&
                        index >= 0 && index < Recipe.numRecipes && index < Main.recipe.Length &&
                        Main.recipe[index] is { Disabled: false } recipe && recipe.createItem.type == ItemID.WoodPlatform;
                });
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private PausedQueryDecision DispatchPausedQuery(string input, string source)
    {
        PausedQueryState before = CapturePausedQuery();
        AIPlan? parsed = null;
        bool accepted = false;
        string error = "";
        try
        {
            parsed = AIPlanParser.Parse(input);
            accepted = ApplyPlan(parsed, null, source, source == "chat" ? "零 API 受控暂停查询" : null);
        }
        catch (JsonException exception) { error = exception.Message; }
        return new(JsonSerializer.Deserialize<JsonElement>(input), source, parsed, accepted, error, before, CapturePausedQuery(), decisionReceipts.Last);
    }

    private PausedQueryState CapturePausedQuery()
    {
        static IEnumerable<PausedQueryStock> Items(string container, IEnumerable<Item> items) => items
            .Select((item, slot) => new PausedQueryStock(container, slot, item.type, item.stack, item.prefix)).Where(item => item.Count > 0 && item.Item > 0);
        PausedQueryStock[] stock = Items("inventory", Loadout.Inventory).Concat(Items("armor", Loadout.Armor))
            .Concat(Items("accessories", Loadout.Accessories)).ToArray();
        return new(Main.GameUpdateCount, ActiveGoal, GoalIdentity, goalStatus, goalPlanningState.Completion.Revision,
            JsonSerializer.Serialize(goalPlanningState.Completion.Conditions), goalPlanningState.WorkingPlan, goalPlanningState.AcceptedExecutionPlan,
            Executor.CurrentAction, Executor.HasCurrentPlan,
            Executor.SnapshotTasks().ToArray(), Executor.HoldPosition, goalWaitingForPlayer, goalReviewPending, agentJournal.LastSequence,
            CompanionNpc?.Center.X ?? 0, CompanionNpc?.Bottom.Y ?? 0, Main.LocalPlayer.Center.X, stock);
    }

    private static bool SamePausedQueryGoal(PausedQueryState before, PausedQueryState after) => before.Goal == after.Goal &&
        before.GoalId == after.GoalId && before.ContractRevision == after.ContractRevision && before.Contract == after.Contract;

    private static bool SamePausedQueryQueue(AIPlan[] before, AIPlan[] after) => before.Length == after.Length &&
        before.Zip(after).All(pair => ReferenceEquals(pair.First, pair.Second));

    private static bool SamePausedQueryQueue(PausedQueryState before, PausedQueryState after) => SamePausedQueryQueue(before.Queue, after.Queue);

    private static bool SamePausedQueryState(PausedQueryState before, PausedQueryState after) => SamePausedQueryGoal(before, after) &&
        SamePausedQueryQueue(before, after) && before.GoalStatus == after.GoalStatus && before.Action == after.Action &&
        before.HasPlan == after.HasPlan && before.Hold == after.Hold && before.WaitingForPlayer == after.WaitingForPlayer &&
        before.ReviewPending == after.ReviewPending &&
        JsonSerializer.Serialize(before.WorkingPlan) == JsonSerializer.Serialize(after.WorkingPlan) &&
        JsonSerializer.Serialize(before.ExecutionPlan) == JsonSerializer.Serialize(after.ExecutionPlan);

    private void FinishPausedQueryNative(string status, string error)
    {
        pausedQueryRunning = false;
        try
        {
            ShelterPreservationReport? preservation = pausedQueryAudit?.CheckPreservation();
            if (status == "passed" && preservation is not { preserved: true, coverageComplete: true, diffCount: 0 })
            { status = "failed"; error = "Original-house protection comparison failed."; }
            string directory = Path.Combine(Main.SavePath, "AITeammate");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "paused-query-native-result.json"), JsonSerializer.Serialize(new
            {
                status, error, world = Main.worldName, worldId = Main.worldID,
                primaryRequests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount,
                warps = Executor.NavigationTeleportCount, llmSettingsNull = GetLlmSettings() == null, preservation,
                copiedInputHashes = pausedQueryInputHashes, isolatedFileHashesAtFinish = ReadBossReviewHashes(), checks = pausedQueryChecks,
                scope = "Controlled original-house copy. Scripted public JSON through production parser and ApplyPlan. Seeded equipment and one actor setup, native ticks, no fixture tile edits."
            }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        }
        finally { Stop(); }
        TestLog($"{PausedQueryNativeCase} {status}: {error}");
    }
}
