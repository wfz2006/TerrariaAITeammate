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
    private const string PausedInitialAnswerNativeCase = "paused_initial_answer_native";
    private const string PausedInitialAnswerReply = "受控首答：材料情况已说明。";
    private const string PausedInitialAnswerFlashReply = "受控纯聊天：OK。";
    private const string PausedInitialAnswerNewGoal = "受控新任务等待下一段计划";

    private enum PausedInitialAnswerPhase { ReplyOnly, ReplyWithEmptyTasks, ResumeOriginal, ExplicitClear, NewGoal, NewGoalWithoutTasks }
    private sealed record PausedInitialAnswerDelivery(JsonElement Input, DecisionTicket Ticket, bool CurrentTicket, bool ActorPresent,
        AIPlan? Parsed, bool Accepted, string Error, PausedQueryState Before, PausedQueryState After, PlanDecisionReceipt? Receipt,
        string ReplyBefore, string ReplyAfter);
    private sealed record PausedInitialAnswerCheck(string Name, bool Passed, object Evidence);

    private bool pausedInitialAnswerRunning, pausedInitialAnswerStateStable, pausedInitialAnswerStockStable;
    private float pausedInitialAnswerMaximumTravel;
    private PausedInitialAnswerPhase pausedInitialAnswerPhase;
    private PausedInitialAnswerDelivery? pausedInitialAnswerDelivery;
    private AIPlan[] pausedInitialAnswerOriginalQueue = Array.Empty<AIPlan>();
    private ShelterWorldAudit? pausedInitialAnswerAudit;
    private Dictionary<string, string> pausedInitialAnswerInputHashes = new();
    private readonly List<PausedInitialAnswerCheck> pausedInitialAnswerChecks = new();

    private void BeginPausedInitialAnswerNative()
    {
        Stop();
        Executor.ResetNavigationTelemetry();
        if (GetLlmSettings() != null || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 ||
            CompanionNpc is not { active: true } npc || Main.LocalPlayer.dead)
        { FinishPausedInitialAnswerNative("failed_setup", "Requires disabled LLM and a live companion and observer."); return; }

        pausedInitialAnswerInputHashes = ReadBossReviewHashes();
        Player player = Main.LocalPlayer;
        npc.Center = Stand(3170, 400);
        player.Center = Stand(3162, 400);
        npc.velocity = player.velocity = Vector2.Zero;
        if (!TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, 0, npc.width) ||
            !TilePathfinder.IsGroundedAt(player.Center.X, player.Bottom.Y, 0, player.width))
        { FinishPausedInitialAnswerNative("failed_setup", "The measured original-house exterior positions were unavailable."); return; }

        int[] supplied = { ItemID.MoltenHelmet, ItemID.CopperHelmet };
        if (supplied.Any(item => !Loadout.AddItem(item, 1)))
        { FinishPausedInitialAnswerNative("failed_setup", "The controlled equipment did not fit."); return; }
        pausedInitialAnswerAudit = ShelterWorldAudit.Capture(3220, 390);
        ShelterPreservationReport preservation = pausedInitialAnswerAudit.CheckPreservation();
        bool protectedHouse = preservation is { preserved: true, coverageComplete: true, diffCount: 0 };
        pausedInitialAnswerChecks.Add(new("original_house_baseline", protectedHouse, new { preservation, supplied, setup = CapturePausedQuery() }));
        if (!protectedHouse)
        { FinishPausedInitialAnswerNative("failed_setup", "Original-house protection baseline was incomplete."); return; }

        pausedInitialAnswerRunning = true;
        SeedPausedInitialAnswer(PausedInitialAnswerPhase.ReplyOnly);
    }

    private void SeedPausedInitialAnswer(PausedInitialAnswerPhase phase)
    {
        int oldChecks = pausedQueryChecks.Count;
        bool initialReply = phase == PausedInitialAnswerPhase.ReplyOnly;
        SeedPausedQueryQueue(initialReply ? ItemID.MoltenHelmet : ItemID.CopperHelmet, external: !initialReply, label: phase.ToString());
        PausedQueryCheck seeded = pausedQueryChecks[oldChecks];
        pausedInitialAnswerChecks.Add(new(phase + "_seed", seeded.Passed, seeded.Evidence));
        string json = phase switch
        {
            PausedInitialAnswerPhase.ReplyOnly => $$"""{"reply":"{{PausedInitialAnswerReply}}","tasks":[]}""",
            PausedInitialAnswerPhase.ExplicitClear => $$"""{"reply":"{{PausedInitialAnswerReply}}","clear_tasks":true,"keep_tasks":false,"tasks":[]}""",
            PausedInitialAnswerPhase.NewGoal => $$"""{"goal":"{{PausedInitialAnswerNewGoal}}","completion_conditions":[{"type":"structure_preserved"}],"tasks":[]}""",
            _ => $$"""{"goal":"{{PausedInitialAnswerNewGoal}}","completion_conditions":[{"type":"structure_preserved"}],"keep_tasks":false}"""
        };
        DeliverPausedInitialAnswer(phase, json);
    }

    private void DeliverPausedInitialAnswer(PausedInitialAnswerPhase phase, string json)
    {
        InvalidateAgentIntent();
        var actor = CompanionNpc?.ModNPC;
        DecisionTicket ticket = primaryTicket = decisionGate.Begin("chat");
        bool current = decisionGate.IsCurrent(ticket);
        PausedQueryState before = CapturePausedQuery();
        string beforeReply = LastReply;
        AIPlan? parsed = null;
        bool accepted = false;
        string error = "";
        try
        {
            parsed = AIPlanParser.Parse(json);
            string message = phase switch
            {
                PausedInitialAnswerPhase.ResumeOriginal => "请继续原任务",
                PausedInitialAnswerPhase.ExplicitClear => "请清空暂停任务，继续保持暂停",
                PausedInitialAnswerPhase.NewGoal or PausedInitialAnswerPhase.NewGoalWithoutTasks => "请开始一个新的任务",
                _ => "请说明材料情况"
            };
            accepted = ApplyPrimaryPlanResponse(parsed, "chat", message, ticket, actor);
        }
        catch (JsonException exception) { error = exception.Message; }
        PausedQueryState after = CapturePausedQuery();
        pausedInitialAnswerPhase = phase;
        pausedInitialAnswerDelivery = new(JsonSerializer.Deserialize<JsonElement>(json), ticket, current, actor != null,
            parsed, accepted, error, before, after, decisionReceipts.Last, beforeReply, LastReply);
        bool replyOnly = phase is PausedInitialAnswerPhase.ReplyOnly or PausedInitialAnswerPhase.ReplyWithEmptyTasks;
        pausedInitialAnswerStateStable = !replyOnly || SamePausedQueryState(before, after);
        pausedInitialAnswerStockStable = before.Stock.SequenceEqual(after.Stock);
        pausedInitialAnswerMaximumTravel = Math.Abs(after.ActorX - before.ActorX);
    }

    private void TickPausedInitialAnswerNative()
    {
        if (CompanionNpc is not { active: true } || Main.LocalPlayer.dead || GetLlmSettings() != null ||
            PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 || Executor.NavigationTeleportCount != 0)
        { FinishPausedInitialAnswerNative("failed", "Missing actor, enabled LLM, API call or warp."); return; }
        Main.LocalPlayer.immune = true;
        Main.LocalPlayer.immuneTime = 600;
        foreach (NPC hostile in Main.npc)
            if (hostile.active && !hostile.friendly) hostile.active = false;

        PausedInitialAnswerDelivery delivery = pausedInitialAnswerDelivery!;
        PausedQueryState current = CapturePausedQuery();
        ulong ticks = current.Tick - delivery.After.Tick;
        AgentOutcome[] outcomes = agentJournal.Outcomes.Where(outcome => outcome.Sequence > delivery.Before.Sequence).ToArray();
        bool valid = delivery.CurrentTicket && delivery.ActorPresent && delivery.Parsed != null && delivery.Accepted && delivery.Before.Hold;
        if (pausedInitialAnswerPhase == PausedInitialAnswerPhase.ResumeOriginal)
        {
            bool resumed = valid && delivery.Parsed?.Resume == true && !delivery.After.Hold && !delivery.After.WaitingForPlayer &&
                SamePausedQueryGoal(delivery.Before, delivery.After) && SamePausedQueryQueue(delivery.Before, delivery.After) &&
                SamePausedQueryQueue(pausedInitialAnswerOriginalQueue, delivery.After.Queue) && delivery.After.Queue.Length == 2;
            bool completed = outcomes.Length == 2 && outcomes.Zip(pausedInitialAnswerOriginalQueue).All(pair =>
                pair.First.Status == "succeeded" && PlanTaskIdentity.AreEquivalent(pair.First.Task, pair.Second));
            if (resumed && !completed && outcomes.All(outcome => outcome.Status != "blocked") && ticks < PausedQueryResumeDeadline) return;
            bool equipped = Loadout.Armor.Any(item => !item.IsAir && item.type == ItemID.MoltenHelmet);
            bool workingStepsExecuted = current.WorkingPlan is { steps.Count: 2 } work && work.steps.All(step => step.successfulActions == 1);
            pausedInitialAnswerChecks.Add(new("explicit_resume_executes_preserved_original_queue", resumed && completed && equipped && workingStepsExecuted &&
                SamePausedQueryGoal(delivery.Before, current), new { delivery, current, ticks, outcomes, resumed, completed, equipped, workingStepsExecuted }));
            SeedPausedInitialAnswer(PausedInitialAnswerPhase.ExplicitClear);
            return;
        }

        bool replyOnly = pausedInitialAnswerPhase is PausedInitialAnswerPhase.ReplyOnly or PausedInitialAnswerPhase.ReplyWithEmptyTasks;
        PausedQueryState expected = replyOnly ? delivery.Before : delivery.After;
        pausedInitialAnswerStateStable &= SamePausedQueryState(expected, current);
        pausedInitialAnswerStockStable &= delivery.Before.Stock.SequenceEqual(current.Stock);
        pausedInitialAnswerMaximumTravel = Math.Max(pausedInitialAnswerMaximumTravel, Math.Abs(current.ActorX - delivery.Before.ActorX));
        if (ticks < PausedQueryObserveTicks) return;

        bool noPhysicalWork = outcomes.Length == 0 && pausedInitialAnswerStockStable && pausedInitialAnswerMaximumTravel <= 0.25f;
        bool implicitKeepFlagAbsent = !delivery.Input.TryGetProperty("keep_tasks", out _);
        bool validNewGoalInput = pausedInitialAnswerPhase == PausedInitialAnswerPhase.NewGoalWithoutTasks
            ? !delivery.Input.TryGetProperty("tasks", out _) && delivery.Input.TryGetProperty("keep_tasks", out JsonElement replacement) &&
                replacement.ValueKind == JsonValueKind.False
            : implicitKeepFlagAbsent;
        bool expectedDisposition = pausedInitialAnswerPhase switch
        {
            PausedInitialAnswerPhase.ReplyOnly => implicitKeepFlagAbsent && delivery.Before.Queue.Length == 2 &&
                delivery.ReplyBefore != PausedInitialAnswerReply && delivery.ReplyAfter == PausedInitialAnswerReply,
            PausedInitialAnswerPhase.ReplyWithEmptyTasks => delivery.Input.GetProperty("keep_tasks").ValueKind == JsonValueKind.False &&
                delivery.Input.GetProperty("tasks").GetArrayLength() == 0 && delivery.Before.Queue.Length == 2 &&
                delivery.ReplyAfter == PausedInitialAnswerFlashReply,
            PausedInitialAnswerPhase.ExplicitClear => delivery.Parsed?.ClearTasks == true &&
                delivery.Before.Queue.Length == 2 && SamePausedQueryGoal(delivery.Before, delivery.After) &&
                delivery.After.Hold && delivery.After.WaitingForPlayer == delivery.Before.WaitingForPlayer &&
                delivery.After.GoalStatus == delivery.Before.GoalStatus && !delivery.After.ReviewPending &&
                delivery.After.Action == AIActionType.Wait && !delivery.After.HasPlan && delivery.After.Queue.Length == 0,
            _ => validNewGoalInput && delivery.Before.Queue.Length == 2 && delivery.After.Goal == PausedInitialAnswerNewGoal &&
                delivery.After.GoalId != delivery.Before.GoalId && delivery.After.GoalStatus == "active" && delivery.After.ReviewPending &&
                !delivery.After.Hold && !delivery.After.WaitingForPlayer && delivery.After.Action == AIActionType.Wait &&
                !delivery.After.HasPlan && delivery.After.Queue.Length == 0 && delivery.After.WorkingPlan == null
        };
        pausedInitialAnswerChecks.Add(new(pausedInitialAnswerPhase + "_native_ticks", valid && expectedDisposition && pausedInitialAnswerStateStable && noPhysicalWork,
            new { delivery, current, ticks, outcomes, expectedDisposition, stateStable = pausedInitialAnswerStateStable,
                stockStable = pausedInitialAnswerStockStable, maximumHorizontalPixels = pausedInitialAnswerMaximumTravel }));

        switch (pausedInitialAnswerPhase)
        {
            case PausedInitialAnswerPhase.ReplyOnly:
                pausedInitialAnswerOriginalQueue = delivery.Before.Queue;
                DeliverPausedInitialAnswer(PausedInitialAnswerPhase.ReplyWithEmptyTasks,
                    $$"""{"reply":"{{PausedInitialAnswerFlashReply}}","keep_tasks":false,"tasks":[]}""");
                break;
            case PausedInitialAnswerPhase.ReplyWithEmptyTasks:
                DeliverPausedInitialAnswer(PausedInitialAnswerPhase.ResumeOriginal, """{"resume":true}""");
                break;
            case PausedInitialAnswerPhase.ExplicitClear:
                SeedPausedInitialAnswer(PausedInitialAnswerPhase.NewGoal);
                break;
            case PausedInitialAnswerPhase.NewGoal:
                SeedPausedInitialAnswer(PausedInitialAnswerPhase.NewGoalWithoutTasks);
                break;
            case PausedInitialAnswerPhase.NewGoalWithoutTasks:
                bool passed = pausedInitialAnswerChecks.All(check => check.Passed);
                FinishPausedInitialAnswerNative(passed ? "passed" : "failed", passed ? "" : "One or more initial-answer checks failed.");
                break;
        }
    }

    private void FinishPausedInitialAnswerNative(string status, string error)
    {
        pausedInitialAnswerRunning = false;
        try
        {
            ShelterPreservationReport? preservation = pausedInitialAnswerAudit?.CheckPreservation();
            if (status == "passed" && preservation is not { preserved: true, coverageComplete: true, diffCount: 0 })
            { status = "failed"; error = "Original-house protection comparison failed."; }
            string directory = Path.Combine(Main.SavePath, "AITeammate");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "paused-initial-answer-native-result.json"), JsonSerializer.Serialize(new
            {
                status, error, world = Main.worldName, worldId = Main.worldID,
                primaryRequests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount,
                warps = Executor.NavigationTeleportCount, llmSettingsNull = GetLlmSettings() == null, preservation,
                copiedInputHashes = pausedInitialAnswerInputHashes, isolatedFileHashesAtFinish = ReadBossReviewHashes(), checks = pausedInitialAnswerChecks,
                scope = "Controlled original-house copy. Public JSON through the actual parser and the shared primary chat response handler, with synthetic current tickets and no transport. Native ticks and one explicit resume exercise the original queued tasks."
            }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        }
        finally { Stop(); }
        TestLog($"{PausedInitialAnswerNativeCase} {status}: {error}");
    }
}
