using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string AgentRecoveryNativeCase = "agent_recovery_native";
    private const string AgentRecoveryGoal = "受控空任务恢复诊断";
    private const string AgentRecoveryEmpty = """{"tasks":[],"keep_tasks":false}""";
    private const string AgentRecoveryResume = """{"resume":true}""";
    private const string AgentRecoveryCompleted = """{"goal_status":"completed","tasks":[],"keep_tasks":false}""";
    private const int AgentRecoveryObserveTicks = 15;

    private enum AgentRecoveryPhase { Active, Hold, HoldResumed, ExternalWait, ExternalWaitResumed }
    private sealed record AgentRecoveryState(ulong Tick, DateTime Utc, string Goal, string GoalId, string Status,
        AIActionType Action, bool HasTask, AIPlan[] Queue, bool Hold, bool WaitingForPlayer, bool ReviewPending,
        int EmptyDecisions, DateTime NextDecisionAt, long OutcomeSequence, float ActorX, float ActorFeetY, float ObserverX)
    {
        public double DelayMilliseconds => (NextDecisionAt - Utc).TotalMilliseconds;
    }
    private sealed record AgentRecoveryDispatch(JsonElement Input, AIPlan? Parsed, bool Accepted, string Error,
        AgentRecoveryState Before, AgentRecoveryState After, PlanDecisionReceipt? Receipt);
    private sealed record AgentRecoveryCheck(string Name, bool Passed, object Evidence);
    private sealed record AgentRecoveryScanCenter(int x, int y);
    private sealed record AgentRecoveryExploration(int? MinX, int? MaxX, string[]? Discoveries, AgentRecoveryScanCenter[]? ScanCenters)
    {
        public bool HasScan => MinX.HasValue && MaxX.HasValue && MinX.Value <= MaxX.Value &&
            Discoveries != null && ScanCenters is { Length: > 0 };
    }

    private bool agentRecoveryRunning;
    private int agentRecoveryFrames, agentRecoveryPhaseStarted;
    private float agentRecoveryMaximumTravel;
    private AgentRecoveryPhase agentRecoveryPhase;
    private AgentRecoveryState? agentRecoveryWatchStart;
    private AgentRecoveryExploration? agentRecoveryBeforePausedReplacement;
    private Dictionary<int, int> agentRecoveryWatchStock = new();
    private Dictionary<string, string> agentRecoveryInputHashes = new();
    private ShelterWorldAudit? agentRecoveryAudit;
    private readonly List<AgentRecoveryCheck> agentRecoveryChecks = new();

    private void BeginAgentRecoveryNative()
    {
        Stop();
        Executor.ResetNavigationTelemetry();
        if (GetLlmSettings() != null || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 ||
            CompanionNpc is not { active: true } npc || Main.LocalPlayer.dead)
        { FinishAgentRecoveryNative("failed_setup", "This fixture requires a live companion and disabled LLM."); return; }

        agentRecoveryInputHashes = ReadBossReviewHashes();
        Player player = Main.LocalPlayer;
        // The original-house boss fixture measured dry, uninterrupted runway x3162..3170 on floor 400.
        npc.Center = Stand(3170, 400);
        player.Center = Stand(3162, 400);
        npc.velocity = player.velocity = Vector2.Zero;
        if (!TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, 0, npc.width) ||
            !TilePathfinder.IsGroundedAt(player.Center.X, player.Bottom.Y, 0, player.width))
        { FinishAgentRecoveryNative("failed_setup", "The original-house exterior stand was unavailable."); return; }

        agentRecoveryAudit = ShelterWorldAudit.Capture(3220, 390);
        ShelterPreservationReport preservation = agentRecoveryAudit.CheckPreservation();
        RecordAgentRecovery("original_house_baseline", preservation is { preserved: true, coverageComplete: true, diffCount: 0 }, preservation);
        if (!preservation.preserved || !preservation.coverageComplete)
        { FinishAgentRecoveryNative("failed_setup", "Original-house protection coverage was incomplete."); return; }

        agentRecoveryRunning = true;
        SeedAgentRecoveryQueue("active_seed");
        double previousDelay = 0;
        for (int i = 1; i <= 3; i++)
        {
            AgentRecoveryDispatch dispatch = DispatchAgentRecovery(AgentRecoveryEmpty);
            var before = dispatch.Before;
            var after = dispatch.After;
            bool transition = dispatch.Accepted && IsExplicitAgentRecoveryEmpty(dispatch.Parsed) && SameAgentRecoveryGoal(before, after) &&
                AgentRecoveryIdleReview(after) && after.OutcomeSequence == before.OutcomeSequence;
            RecordAgentRecovery($"active_empty_{i}_review_without_task", transition, dispatch);
            bool backoff = after.EmptyDecisions == before.EmptyDecisions + 1 && after.DelayMilliseconds >= 1000 &&
                (i == 1 || after.DelayMilliseconds > previousDelay + 500);
            RecordAgentRecovery($"active_empty_{i}_backoff", backoff, new { before, after, previousDelay });
            previousDelay = after.DelayMilliseconds;
        }
        WatchAgentRecovery(AgentRecoveryPhase.Active);
    }

    private void SeedAgentRecoveryQueue(string check, bool allowCompletion = false)
    {
        Stop();
        manualControlActive = false;
        string contract = allowCompletion ? """[{"type":"structure_preserved"}]""" :
            """[{"type":"structure_preserved"},{"type":"inventory_at_least","item_id":9,"count":30}]""";
        string input = $$"""
            {"goal":"{{AgentRecoveryGoal}}","completion_conditions":{{contract}},"tasks":[{"type":"come"},{"type":"inspect","target":"nearby"}]}
            """;
        AgentRecoveryDispatch seed = DispatchAgentRecovery(input, "chat");
        RecordAgentRecovery(check, seed.Accepted && seed.After.Goal == AgentRecoveryGoal && seed.After.HasTask &&
            seed.After.Action == AIActionType.Come && seed.After.Queue.Length == 2 && !seed.After.Hold &&
            !seed.After.ReviewPending, seed);
    }

    private void BeginAgentRecoveryPause(bool external)
    {
        string label = external ? "external_wait" : "hold";
        SeedAgentRecoveryQueue(label + "_seed");
        NPC npc = CompanionNpc!;
        var actorTile = new AgentRecoveryScanCenter((int)(npc.Center.X / 16f), (int)(npc.Center.Y / 16f));
        MethodInfo? scan = typeof(AIExecutor).GetMethod("ScanDiscoveries", BindingFlags.Instance | BindingFlags.NonPublic);
        scan?.Invoke(Executor, new object[] { npc });
        AgentRecoveryExploration observed = CaptureAgentRecoveryExploration();
        RecordAgentRecovery(label + "_native_exploration_scan", scan != null && observed.HasScan &&
            observed.ScanCenters!.Contains(actorTile) && observed.MinX <= actorTile.x && observed.MaxX >= actorTile.x,
            new { method = "AIExecutor.ScanDiscoveries", tick = Main.GameUpdateCount, actorTile, observed,
                scope = "Controlled, terrain read-only scan of the actual companion location. Native discovery bookkeeping is retained in the isolated session." });

        string pauseInput = external ? """{"wait_for_player":true}""" : """{"hold":true}""";
        AgentRecoveryDispatch pause = DispatchAgentRecovery(pauseInput);
        RecordAgentRecovery(label + "_entered", pause.Accepted && pause.After.Hold && pause.After.WaitingForPlayer == external &&
            pause.After.Status == (external ? "waiting_for_player" : "paused") && pause.After.Queue.Length == 2, pause);
        AgentRecoveryExploration explorationBefore = CaptureAgentRecoveryExploration();
        AgentRecoveryDispatch empty = DispatchAgentRecovery(AgentRecoveryEmpty);
        AgentRecoveryExploration explorationAfter = CaptureAgentRecoveryExploration();
        agentRecoveryBeforePausedReplacement = explorationBefore;
        RecordAgentRecovery(label + "_empty_preserves_exploration", SameAgentRecoveryExploration(observed, explorationBefore) &&
            SameAgentRecoveryExploration(explorationBefore, explorationAfter), new { observed, before = explorationBefore, after = explorationAfter });
        var after = empty.After;
        RecordAgentRecovery(label + "_empty_replacement_stays_paused", empty.Accepted && IsExplicitAgentRecoveryEmpty(empty.Parsed) &&
            SameAgentRecoveryGoal(empty.Before, after) && after.Queue.Length == 0 && !after.HasTask && after.Hold &&
            !after.ReviewPending && after.WaitingForPlayer == external && after.Status == pause.After.Status &&
            after.EmptyDecisions == empty.Before.EmptyDecisions && after.NextDecisionAt == empty.Before.NextDecisionAt &&
            after.OutcomeSequence == empty.Before.OutcomeSequence, empty);
        WatchAgentRecovery(external ? AgentRecoveryPhase.ExternalWait : AgentRecoveryPhase.Hold);
    }

    private void ResumeAgentRecovery(bool external)
    {
        AgentRecoveryExploration before = CaptureAgentRecoveryExploration();
        AgentRecoveryDispatch resume = DispatchAgentRecovery(AgentRecoveryResume, "chat");
        AgentRecoveryExploration after = CaptureAgentRecoveryExploration();
        AgentRecoveryExploration? original = agentRecoveryBeforePausedReplacement;
        RecordAgentRecovery(external ? "external_wait_resume_preserves_exploration" : "hold_resume_preserves_exploration",
            original != null && SameAgentRecoveryExploration(original, before) && SameAgentRecoveryExploration(original, after),
            new { original, before, after });
        RecordAgentRecovery(external ? "external_wait_explicit_resume" : "hold_explicit_resume", resume.Before.Hold &&
            resume.Before.WaitingForPlayer == external && resume.Accepted && resume.Parsed?.Resume == true &&
            SameAgentRecoveryGoal(resume.Before, resume.After) && AgentRecoveryIdleReview(resume.After) &&
            resume.After.OutcomeSequence == resume.Before.OutcomeSequence, resume);
        WatchAgentRecovery(external ? AgentRecoveryPhase.ExternalWaitResumed : AgentRecoveryPhase.HoldResumed);
    }

    private void TickAgentRecoveryNative()
    {
        if (CompanionNpc is not { active: true } npc || Main.LocalPlayer.dead || GetLlmSettings() != null ||
            PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 || Executor.NavigationTeleportCount != 0)
        { FinishAgentRecoveryNative("failed", "Missing actor, LLM enabled, API call or warp."); return; }

        Player player = Main.LocalPlayer;
        player.immune = true;
        player.immuneTime = 600;
        foreach (NPC hostile in Main.npc)
            if (hostile.active && !hostile.friendly) hostile.active = false;

        agentRecoveryFrames++;
        AgentRecoveryState start = agentRecoveryWatchStart!;
        agentRecoveryMaximumTravel = Math.Max(agentRecoveryMaximumTravel, Math.Abs(npc.Center.X - start.ActorX));
        if (agentRecoveryFrames - agentRecoveryPhaseStarted < AgentRecoveryObserveTicks) return;

        AgentRecoveryState after = CaptureAgentRecovery();
        Dictionary<int, int> stock = ShelterCounts(Loadout.Inventory);
        bool sameStock = stock.Count == agentRecoveryWatchStock.Count && stock.All(pair => agentRecoveryWatchStock.GetValueOrDefault(pair.Key) == pair.Value);
        bool paused = agentRecoveryPhase is AgentRecoveryPhase.Hold or AgentRecoveryPhase.ExternalWait;
        bool expectedState = paused
            ? after.Hold && !after.ReviewPending && after.WaitingForPlayer == (agentRecoveryPhase == AgentRecoveryPhase.ExternalWait) &&
              after.Status == (agentRecoveryPhase == AgentRecoveryPhase.ExternalWait ? "waiting_for_player" : "paused") && after.Queue.Length == 0
            : AgentRecoveryIdleReview(after);
        RecordAgentRecovery(agentRecoveryPhase + "_native_ticks", expectedState && SameAgentRecoveryGoal(start, after) &&
            after.OutcomeSequence == start.OutcomeSequence && sameStock && agentRecoveryMaximumTravel <= 0.25f,
            new { start, after, ticks = agentRecoveryFrames - agentRecoveryPhaseStarted, maximumHorizontalPixels = agentRecoveryMaximumTravel,
                stockBefore = agentRecoveryWatchStock, stockAfter = stock });

        switch (agentRecoveryPhase)
        {
            case AgentRecoveryPhase.Active: BeginAgentRecoveryPause(false); break;
            case AgentRecoveryPhase.Hold: ResumeAgentRecovery(false); break;
            case AgentRecoveryPhase.HoldResumed: BeginAgentRecoveryPause(true); break;
            case AgentRecoveryPhase.ExternalWait: ResumeAgentRecovery(true); break;
            case AgentRecoveryPhase.ExternalWaitResumed: VerifyAgentRecoveryTerminalCommands(); break;
        }
    }

    private void VerifyAgentRecoveryTerminalCommands()
    {
        SeedAgentRecoveryQueue("terminal_unproven_seed");
        AgentRecoveryDispatch denied = DispatchAgentRecovery(AgentRecoveryCompleted);
        bool sameQueue = denied.Before.Queue.Select(PlanTaskIdentity.Fingerprint).SequenceEqual(denied.After.Queue.Select(PlanTaskIdentity.Fingerprint));
        RecordAgentRecovery("unproven_completion_retains_queue", !denied.Accepted && denied.Receipt?.code == "completion_evidence_missing" &&
            SameAgentRecoveryGoal(denied.Before, denied.After) && sameQueue && denied.After.HasTask && lastCompletedGoal == null, denied);

        AgentRecoveryDispatch cancelled = DispatchAgentRecovery("""{"goal_status":"cancelled","tasks":[],"keep_tasks":false}""");
        RecordAgentRecovery("cancelled_clears_goal_and_queue", cancelled.Accepted && cancelled.Receipt?.code == "cancelled" &&
            cancelled.After.Status == "cancelled" && cancelled.After.Goal.Length == 0 && cancelled.After.Queue.Length == 0 &&
            !cancelled.After.HasTask && !cancelled.After.ReviewPending && !cancelled.After.WaitingForPlayer, cancelled);

        SeedAgentRecoveryQueue("terminal_verified_seed", allowCompletion: true);
        AgentRecoveryDispatch completed = DispatchAgentRecovery(AgentRecoveryCompleted);
        RecordAgentRecovery("verified_completion_clears_goal_and_queue", completed.Accepted && completed.Receipt?.code == "completion_evidence_verified" &&
            completed.After.Status == "completed" && completed.After.Goal.Length == 0 && completed.After.Queue.Length == 0 &&
            !completed.After.HasTask && !completed.After.ReviewPending && lastCompletedGoal != null,
            new { completed, nativeCompletion = lastCompletedGoal });

        bool passed = agentRecoveryChecks.All(check => check.Passed);
        FinishAgentRecoveryNative(passed ? "passed" : "failed", passed ? "" : "One or more recovery checks failed.");
    }

    private AgentRecoveryDispatch DispatchAgentRecovery(string input, string source = "goal")
    {
        AgentRecoveryState before = CaptureAgentRecovery();
        AIPlan? parsed = null;
        bool accepted = false;
        string error = "";
        try
        {
            parsed = AIPlanParser.Parse(input);
            accepted = ApplyPlan(parsed, null, source, source == "chat" ? "零 API 受控空任务恢复诊断" : null);
        }
        catch (JsonException exception) { error = exception.Message; }
        return new(JsonSerializer.Deserialize<JsonElement>(input), parsed, accepted, error, before, CaptureAgentRecovery(), decisionReceipts.Last);
    }

    private AgentRecoveryState CaptureAgentRecovery() => new(Main.GameUpdateCount, DateTime.UtcNow, ActiveGoal, GoalIdentity, goalStatus,
        Executor.CurrentAction, Executor.HasCurrentPlan, Executor.SnapshotTasks().ToArray(), Executor.HoldPosition, goalWaitingForPlayer,
        goalReviewPending, emptyDecisions, nextDecisionAt, agentJournal.LastSequence, CompanionNpc?.Center.X ?? 0,
        CompanionNpc?.Bottom.Y ?? 0, Main.LocalPlayer.Center.X);

    private static bool IsExplicitAgentRecoveryEmpty(AIPlan? plan) =>
        plan is { KeepTasks: false, ReplaceTasks: true, Action: AIActionType.None } && plan.Steps.Count == 0;

    private static bool SameAgentRecoveryGoal(AgentRecoveryState before, AgentRecoveryState after) =>
        before.Goal == AgentRecoveryGoal && before.GoalId.Length > 0 && before.Goal == after.Goal && before.GoalId == after.GoalId;

    private static bool AgentRecoveryIdleReview(AgentRecoveryState state) => state.Status == "active" && state.Action == AIActionType.Wait &&
        !state.HasTask && state.Queue.Length == 0 && !state.Hold && !state.WaitingForPlayer && state.ReviewPending;

    private AgentRecoveryExploration CaptureAgentRecoveryExploration() => new(
        ContinuousField("sessionObservedMinX") as int?, ContinuousField("sessionObservedMaxX") as int?,
        (ContinuousField("discoveries") as IEnumerable<string>)?.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
        (ContinuousField("exploreScanCenters") as IEnumerable<Point>)?.OrderBy(center => center.X).ThenBy(center => center.Y)
            .Select(center => new AgentRecoveryScanCenter(center.X, center.Y)).ToArray());

    private static bool SameAgentRecoveryExploration(AgentRecoveryExploration before, AgentRecoveryExploration after) =>
        before.HasScan && after.HasScan && before.MinX == after.MinX && before.MaxX == after.MaxX &&
        before.Discoveries!.SequenceEqual(after.Discoveries!, StringComparer.Ordinal) && before.ScanCenters!.SequenceEqual(after.ScanCenters!);

    private void WatchAgentRecovery(AgentRecoveryPhase phase)
    {
        agentRecoveryPhase = phase;
        agentRecoveryPhaseStarted = agentRecoveryFrames;
        agentRecoveryWatchStart = CaptureAgentRecovery();
        agentRecoveryWatchStock = ShelterCounts(Loadout.Inventory);
        agentRecoveryMaximumTravel = 0;
    }

    private void RecordAgentRecovery(string name, bool passed, object evidence) => agentRecoveryChecks.Add(new(name, passed, evidence));

    private void FinishAgentRecoveryNative(string status, string error)
    {
        agentRecoveryRunning = false;
        try
        {
            ShelterPreservationReport? preservation = agentRecoveryAudit?.CheckPreservation();
            if (status == "passed" && preservation is not { preserved: true, coverageComplete: true, diffCount: 0 })
            { status = "failed"; error = "Original-house protection comparison failed."; }
            string directory = Path.Combine(Main.SavePath, "AITeammate");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "agent-recovery-native-result.json"), JsonSerializer.Serialize(new
            {
                status, error, world = Main.worldName, worldId = Main.worldID, frames = agentRecoveryFrames,
                primaryRequests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount,
                warps = Executor.NavigationTeleportCount, llmSettingsNull = GetLlmSettings() == null,
                preservation, copiedInputHashes = agentRecoveryInputHashes, isolatedFileHashesAtFinish = ReadBossReviewHashes(),
                checks = agentRecoveryChecks,
                scope = "Controlled original-house copy. Scripted public JSON passes through production parser and ApplyPlan. One actor setup, native ticks, no fixture world or inventory writes."
            }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        }
        finally { Stop(); }
        TestLog($"{AgentRecoveryNativeCase} {status}: {error}");
    }
}
