using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string PausedAnswerNativeCase = "paused_answer_native";
    private const string PausedAnswerFixtureKind = "paused_answer";
    private const string PausedAnswerQuestion = "木平台的原生配方是什么？";
    private const string PausedAnswerReply = "受控回包：木平台的原生配方已经查询。";
    private const string PausedAnswerForbiddenReply = "受控回包：不得把这条回复作为玩家授权。";
    private const string PausedAnswerNewGoal = "受控新任务先核对木材";
    private const int PausedAnswerCaseCount = 7;
    private static readonly FieldInfo? PausedAnswerPendingField = typeof(AITeammateSystem).GetField("pendingPausedQuestion",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private sealed record PausedAnswerProbe(string Name, string Kind, string Json, bool ExternalWait = false,
        bool AllowAnswer = false, bool AllowResume = false, bool AllowNewGoal = false);
    private sealed record PausedAnswerDelivery(PausedAnswerProbe Probe, DecisionTicket Ticket, bool CurrentTicket, bool ActorPresent,
        AIPlan? Parsed, bool Accepted, string Error, PausedQueryState Before, PausedQueryState After, PlanDecisionReceipt? Receipt,
        string ReplyBefore, string ReplyAfter, bool PendingFieldAvailable, string? PendingBefore, string? PendingAfter,
        string? CommandBefore, string? CommandAfter);
    private sealed record PausedAnswerCheck(string Name, bool Passed, object Evidence);

    private bool pausedAnswerRunning, pausedAnswerStateStable, pausedAnswerStockStable, pausedAnswerFollowupClear;
    private int pausedAnswerCaseIndex;
    private float pausedAnswerMaximumTravel;
    private PausedAnswerDelivery? pausedAnswerDelivery;
    private ShelterWorldAudit? pausedAnswerAudit;
    private Dictionary<string, string> pausedAnswerInputHashes = new();
    private readonly List<PausedAnswerCheck> pausedAnswerChecks = new();

    private void BeginPausedAnswerNative()
    {
        Stop();
        Executor.ResetNavigationTelemetry();
        if (GetLlmSettings() != null || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 ||
            CompanionNpc is not { active: true } npc || Main.LocalPlayer.dead)
        { FinishPausedAnswerNative("failed_setup", "Requires disabled LLM and a live companion and observer."); return; }

        pausedAnswerInputHashes = ReadBossReviewHashes();
        Player player = Main.LocalPlayer;
        npc.Center = Stand(3170, 400);
        player.Center = Stand(3162, 400);
        npc.velocity = player.velocity = Vector2.Zero;
        if (!TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, 0, npc.width) ||
            !TilePathfinder.IsGroundedAt(player.Center.X, player.Bottom.Y, 0, player.width))
        { FinishPausedAnswerNative("failed_setup", "The measured original-house exterior positions were unavailable."); return; }

        int[] supplied = { ItemID.MoltenHelmet, ItemID.MoltenBreastplate, ItemID.GoldHelmet, ItemID.CopperHelmet };
        if (supplied.Any(item => !Loadout.AddItem(item, 1)))
        { FinishPausedAnswerNative("failed_setup", "The controlled equipment did not fit."); return; }
        pausedAnswerAudit = ShelterWorldAudit.Capture(3220, 390);
        ShelterPreservationReport preservation = pausedAnswerAudit.CheckPreservation();
        bool protectedHouse = preservation is { preserved: true, coverageComplete: true, diffCount: 0 };
        pausedAnswerChecks.Add(new("original_house_baseline", protectedHouse, new { preservation, supplied, setup = CapturePausedQuery() }));
        if (!protectedHouse)
        { FinishPausedAnswerNative("failed_setup", "Original-house protection baseline was incomplete."); return; }

        pausedAnswerRunning = true;
        StartPausedAnswerCase();
    }

    private static PausedAnswerProbe GetPausedAnswerProbe(int index) => index switch
    {
        0 => new("automatic_reply_only", PausedAnswerFixtureKind,
            $$"""{"reply":"{{PausedAnswerReply}}","tasks":[]}""", ExternalWait: true, AllowAnswer: true),
        1 => new("automatic_new_goal_resume_equip_rejected", PausedAnswerFixtureKind,
            $$"""{"reply":"{{PausedAnswerForbiddenReply}}","goal":"未授权的自动换装目标","resume":true,"tasks":[{"type":"equip","item":{{ItemID.MoltenBreastplate}}}]}"""),
        2 => new("automatic_repeat_inspect_rejected", PausedAnswerFixtureKind, PausedRecipeInput),
        3 => new("automatic_cancel_rejected", PausedAnswerFixtureKind,
            $$"""{"reply":"{{PausedAnswerForbiddenReply}}","goal_status":"cancelled","tasks":[]}""", ExternalWait: true),
        4 => new("goal_resume_rejected_while_held", "goal", """{"resume":true}"""),
        5 => new("new_player_explicit_resume_executes_original_queue", "chat", """{"resume":true}""", ExternalWait: true, AllowResume: true),
        _ => new("new_player_goal_with_first_inspection_is_adopted", "chat",
            $$"""{"goal":"{{PausedAnswerNewGoal}}","completion_conditions":[{"type":"structure_preserved"}],"tasks":[{"type":"inspect","target":"item","item":{{ItemID.Wood}}}]}""",
            AllowNewGoal: true)
    };

    private string? ReadPendingPausedAnswer() => PausedAnswerPendingField?.GetValue(this) as string;

    private void StartPausedAnswerCase()
    {
        PausedAnswerProbe probe = GetPausedAnswerProbe(pausedAnswerCaseIndex);
        int oldChecks = pausedQueryChecks.Count;
        int equip = probe.AllowResume ? ItemID.GoldHelmet : probe.AllowNewGoal ? ItemID.CopperHelmet : ItemID.MoltenHelmet;
        SeedPausedQueryQueue(equip, probe.ExternalWait, probe.Name);
        PausedQueryCheck seeded = pausedQueryChecks[oldChecks];
        pausedAnswerChecks.Add(new(probe.Name + "_seed", seeded.Passed, seeded.Evidence));

        if (probe.Kind == PausedAnswerFixtureKind)
        {
            PausedQueryDecision lookup = DispatchPausedQuery(PausedRecipeInput, "chat");
            AgentOutcome[] fresh = agentJournal.Outcomes.Where(outcome => outcome.Sequence > lookup.Before.Sequence).ToArray();
            bool actualRecipe = lookup.Accepted && fresh.Length == 1 && IsNativePausedRecipe(fresh[0]) &&
                SamePausedQueryState(lookup.Before, lookup.After) && lookup.Before.Stock.SequenceEqual(lookup.After.Stock);
            pausedAnswerChecks.Add(new(probe.Name + "_first_native_recipe", actualRecipe,
                new { lookup, outcomes = fresh, productionPendingBeforeInjection = ReadPendingPausedAnswer() }));
            PausedAnswerPendingField?.SetValue(this, PausedAnswerQuestion);
        }
        else if (probe.AllowResume || probe.AllowNewGoal)
            InvalidateAgentIntent();

        string message = probe.Kind == PausedAnswerFixtureKind ? PausedAnswerQuestion : probe.AllowResume ? "请继续原任务" :
            probe.AllowNewGoal ? "我有一个新任务，请先核对木材" : "";
        var actor = CompanionNpc?.ModNPC;
        DecisionTicket ticket = primaryTicket = decisionGate.Begin(probe.Kind);
        bool current = decisionGate.IsCurrent(ticket);
        PausedQueryState before = CapturePausedQuery();
        string beforeReply = LastReply;
        string? pendingBefore = ReadPendingPausedAnswer();
        string? commandBefore = pendingCommand;
        AIPlan? parsed = null;
        bool accepted = false;
        string error = "";
        try
        {
            parsed = AIPlanParser.Parse(probe.Json);
            accepted = ApplyPrimaryPlanResponse(parsed, probe.Kind, message, ticket, actor);
        }
        catch (JsonException exception) { error = exception.Message; }

        PausedQueryState after = CapturePausedQuery();
        pausedAnswerDelivery = new(probe, ticket, current, actor != null, parsed, accepted, error, before, after, decisionReceipts.Last,
            beforeReply, LastReply, PausedAnswerPendingField != null, pendingBefore, ReadPendingPausedAnswer(), commandBefore, pendingCommand);
        pausedAnswerStateStable = SamePausedQueryState(before, after);
        pausedAnswerStockStable = before.Stock.SequenceEqual(after.Stock);
        pausedAnswerFollowupClear = ReadPendingPausedAnswer() == null && pendingCommand == null;
        pausedAnswerMaximumTravel = Math.Abs(after.ActorX - before.ActorX);
    }

    private void TickPausedAnswerNative()
    {
        if (CompanionNpc is not { active: true } || Main.LocalPlayer.dead || GetLlmSettings() != null ||
            PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 || Executor.NavigationTeleportCount != 0)
        { FinishPausedAnswerNative("failed", "Missing actor, enabled LLM, API call or warp."); return; }
        Main.LocalPlayer.immune = true;
        Main.LocalPlayer.immuneTime = 600;
        foreach (NPC hostile in Main.npc)
            if (hostile.active && !hostile.friendly) hostile.active = false;

        PausedAnswerDelivery delivery = pausedAnswerDelivery!;
        PausedQueryState current = CapturePausedQuery();
        ulong ticks = current.Tick - delivery.After.Tick;
        AgentOutcome[] outcomes = agentJournal.Outcomes.Where(outcome => outcome.Sequence > delivery.Before.Sequence).ToArray();
        pausedAnswerFollowupClear &= ReadPendingPausedAnswer() == null && pendingCommand == null;
        bool validDelivery = delivery.CurrentTicket && delivery.ActorPresent && delivery.Parsed != null && delivery.Before.Hold &&
            delivery.Before.Queue.Length == 2 &&
            (delivery.Probe.Kind != PausedAnswerFixtureKind || !delivery.PendingFieldAvailable || delivery.PendingBefore == PausedAnswerQuestion);
        if (delivery.Probe.AllowResume)
        {
            bool resumed = validDelivery && delivery.Accepted && !delivery.After.Hold && !delivery.After.WaitingForPlayer &&
                SamePausedQueryGoal(delivery.Before, delivery.After) && SamePausedQueryQueue(delivery.Before, delivery.After);
            bool completed = outcomes.Length == 2 && outcomes.Zip(delivery.Before.Queue).All(pair =>
                pair.First.Status == "succeeded" && PlanTaskIdentity.AreEquivalent(pair.First.Task, pair.Second));
            if (resumed && !completed && outcomes.All(outcome => outcome.Status != "blocked") && ticks < PausedQueryResumeDeadline) return;
            bool equipped = Loadout.Armor.Any(item => !item.IsAir && item.type == ItemID.GoldHelmet);
            bool workingStepsExecuted = current.WorkingPlan is { steps.Count: 2 } work && work.steps.All(step => step.successfulActions == 1);
            pausedAnswerChecks.Add(new(delivery.Probe.Name, resumed && completed && equipped && workingStepsExecuted && pausedAnswerFollowupClear &&
                SamePausedQueryGoal(delivery.Before, current), new { delivery, current, outcomes, ticks, resumed, completed, equipped, workingStepsExecuted }));
            AdvancePausedAnswerCase();
            return;
        }

        if (delivery.Probe.AllowNewGoal)
        {
            pausedAnswerStockStable &= delivery.Before.Stock.SequenceEqual(current.Stock);
            pausedAnswerMaximumTravel = Math.Max(pausedAnswerMaximumTravel, Math.Abs(current.ActorX - delivery.Before.ActorX));
            if (ticks < PausedQueryObserveTicks) return;
            bool adopted = validDelivery && delivery.Accepted && delivery.Parsed?.Resume == false &&
                delivery.After.Goal == PausedAnswerNewGoal && delivery.After.GoalId != delivery.Before.GoalId &&
                !delivery.After.Hold && !delivery.After.WaitingForPlayer && delivery.After.GoalStatus == "active" &&
                delivery.After.Queue is [{ Action: AIActionType.Inspect, Target: "item" }] &&
                SamePausedQueryGoal(delivery.After, current) && !current.Hold && current.GoalStatus == "active";
            bool observed = outcomes.Length == 1 && IsNativePausedAnswerWoodInspection(outcomes[0]);
            pausedAnswerChecks.Add(new(delivery.Probe.Name, adopted && observed && pausedAnswerStockStable && pausedAnswerFollowupClear &&
                pausedAnswerMaximumTravel <= 0.25f, new { delivery, current, outcomes, ticks, adopted, observed,
                    stockStable = pausedAnswerStockStable, followupClear = pausedAnswerFollowupClear, maximumHorizontalPixels = pausedAnswerMaximumTravel }));
            AdvancePausedAnswerCase();
            return;
        }

        pausedAnswerStateStable &= SamePausedQueryState(delivery.Before, current);
        pausedAnswerStockStable &= delivery.Before.Stock.SequenceEqual(current.Stock);
        pausedAnswerMaximumTravel = Math.Max(pausedAnswerMaximumTravel, Math.Abs(current.ActorX - delivery.Before.ActorX));
        if (ticks < PausedQueryObserveTicks) return;

        bool disposition = delivery.Probe.AllowAnswer
            ? delivery.Accepted && delivery.ReplyBefore != PausedAnswerReply && delivery.ReplyAfter == PausedAnswerReply && LastReply == PausedAnswerReply
            : !delivery.Accepted && delivery.Receipt?.status == "rejected" && delivery.ReplyAfter != PausedAnswerForbiddenReply;
        bool passed = validDelivery && disposition && pausedAnswerStateStable && pausedAnswerStockStable && pausedAnswerFollowupClear &&
            pausedAnswerMaximumTravel <= 0.25f && outcomes.Length == 0;
        pausedAnswerChecks.Add(new(delivery.Probe.Name, passed, new { delivery, current, outcomes, ticks, disposition,
            stateStable = pausedAnswerStateStable, stockStable = pausedAnswerStockStable, followupClear = pausedAnswerFollowupClear,
            maximumHorizontalPixels = pausedAnswerMaximumTravel, currentReply = LastReply, currentPending = ReadPendingPausedAnswer(), pendingCommand }));
        AdvancePausedAnswerCase();
    }

    private static bool IsNativePausedAnswerWoodInspection(AgentOutcome outcome)
    {
        if (outcome.Status != "succeeded" || outcome.Task.Action != AIActionType.Inspect || outcome.Task.Target != "item" ||
            outcome.Task.Item != ItemID.Wood.ToString()) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(outcome.Result);
            JsonElement root = document.RootElement;
            return root.GetProperty("topic").GetString() == "item" && root.GetProperty("items").EnumerateArray()
                .Any(item => item.GetProperty("details").GetProperty("id").GetInt32() == ItemID.Wood);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private void AdvancePausedAnswerCase()
    {
        if (++pausedAnswerCaseIndex < PausedAnswerCaseCount)
        {
            StartPausedAnswerCase();
            return;
        }
        bool passed = pausedAnswerChecks.All(check => check.Passed);
        FinishPausedAnswerNative(passed ? "passed" : "failed", passed ? "" : "One or more paused-answer checks failed.");
    }

    private void FinishPausedAnswerNative(string status, string error)
    {
        pausedAnswerRunning = false;
        try
        {
            ShelterPreservationReport? preservation = pausedAnswerAudit?.CheckPreservation();
            if (status == "passed" && preservation is not { preserved: true, coverageComplete: true, diffCount: 0 })
            { status = "failed"; error = "Original-house protection comparison failed."; }
            string directory = Path.Combine(Main.SavePath, "AITeammate");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "paused-answer-native-result.json"), JsonSerializer.Serialize(new
            {
                status, error, world = Main.worldName, worldId = Main.worldID, completedCases = pausedAnswerCaseIndex,
                primaryRequests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount,
                warps = Executor.NavigationTeleportCount, llmSettingsNull = GetLlmSettings() == null, preservation,
                copiedInputHashes = pausedAnswerInputHashes, isolatedFileHashesAtFinish = ReadBossReviewHashes(), checks = pausedAnswerChecks,
                scope = "Controlled original-house copy. Real paused recipe lookup and public JSON parser, then synthetic tickets through ApplyPrimaryPlanResponse. " +
                    "The pending answer marker is supplied through reflection when that field exists. Scope covers callback behavior with zero transport and real game ticks."
            }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        }
        finally { Stop(); }
        TestLog($"{PausedAnswerNativeCase} {status}: {error}");
    }
}
