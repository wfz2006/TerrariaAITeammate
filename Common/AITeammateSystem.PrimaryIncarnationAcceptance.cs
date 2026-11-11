using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string PrimaryAbsentResponseCase = "primary_absent_response_native";
    private const string PrimaryRespawnResponseCase = "primary_respawn_response_native";
    private const string PrimaryIncarnationGoal = "受控同伴重生后主规划复核";
    private const string PrimaryIncarnationSentinel = "主规划验收旧实例哨兵回复";
    private const int PrimaryIncarnationObserveTicks = 15;

    private bool primaryIncarnationRunning, primaryIncarnationFinished;
    private bool primaryIncarnationStaleApplied, primaryIncarnationStaleRejectedClean;
    private bool? primaryIncarnationOldAccepted, primaryIncarnationFreshAccepted;
    private int primaryIncarnationOldSlot, primaryIncarnationOnKills, primaryIncarnationRevision;
    private uint primaryIncarnationResponseTick;
    private long primaryIncarnationInitialSequence, primaryIncarnationFreshSequence;
    private DateTime primaryIncarnationStartedAt;
    private string primaryIncarnationGoalId = "", primaryIncarnationContract = "";
    private ModNPC? primaryIncarnationOldActor, primaryIncarnationNewActor;
    private ShelterWorldAudit? primaryIncarnationAudit;
    private PlanDecisionReceipt? primaryIncarnationOldReceipt, primaryIncarnationFreshReceipt;
    private Dictionary<int, int> primaryIncarnationInitialStock = new();
    private Dictionary<string, string> primaryIncarnationInputHashes = new();
    private readonly List<object> primaryIncarnationEvents = new();

    private static string PrimaryIncarnationCase => Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") ?? "";
    private static string PrimaryIncarnationResultPath => Path.Combine(Main.SavePath, "AITeammate",
        PrimaryIncarnationCase.Replace('_', '-') + "-result.json");

    private void BeginPrimaryIncarnationNative()
    {
        if (!HouseMode || PrimaryIncarnationCase is not (PrimaryAbsentResponseCase or PrimaryRespawnResponseCase) ||
            primaryIncarnationRunning || primaryIncarnationFinished) return;
        if (File.Exists(PrimaryIncarnationResultPath))
        { TestLog("primary incarnation fixture requires a fresh isolated copy"); return; }

        primaryIncarnationStartedAt = DateTime.UtcNow;
        if (GetLlmSettings() != null || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 ||
            CompanionNpc is not { active: true, life: > 0, ModNPC: { } oldActor } npc || Main.LocalPlayer.dead)
        { FinishPrimaryIncarnationNative("failed_setup", "A live companion, live player and disabled LLM are required."); return; }

        primaryIncarnationInputHashes = ReadBossReviewHashes();
        primaryIncarnationAudit = ShelterWorldAudit.Capture(3220, 390);
        if (primaryIncarnationAudit.CheckPreservation() is not { protectedCells: 5803, preserved: true, coverageComplete: true, diffCount: 0 })
        { FinishPrimaryIncarnationNative("uncovered_house", "The original-house protection baseline was unavailable."); return; }

        Stop();
        CombatPlannerEnabled = false;
        Executor.ResetNavigationTelemetry();
        Loadout.Reset();
        Loadout.EnsureStarterTools();
        if (!Loadout.AddItem(ItemID.MoltenHelmet, 1))
        { FinishPrimaryIncarnationNative("failed_setup", "The fixture helmet did not fit."); return; }
        primaryIncarnationInitialStock = CompanionCrafting.InventoryCounts(Loadout);

        manualControlActive = false;
        Executor.SetHold(false);
        var goal = new AIPlan { Goal = PrimaryIncarnationGoal, ReplaceTasks = true,
            CompletionConditions = new() { new() { Type = "structure_preserved" } } };
        if (!ApplyPlan(goal, null, "chat", PrimaryIncarnationGoal) || !PrimaryIncarnationIdleReview())
        { FinishPrimaryIncarnationNative("failed_setup", "The fixture did not enter a real active, empty goal review."); return; }
        primaryIncarnationGoalId = GoalIdentity;
        primaryIncarnationRevision = goalPlanningState.Completion.Revision;
        primaryIncarnationContract = JsonSerializer.Serialize(goalPlanningState.Completion.Conditions);
        primaryIncarnationInitialSequence = agentJournal.LastSequence;
        primaryIncarnationOldActor = oldActor;
        primaryIncarnationOldSlot = npc.whoAmI;
        primaryIncarnationRunning = true;

        DecisionTicket ticket = BeginPrimaryIncarnationTicket(out CancellationTokenSource cancellation);
        primaryIncarnationEvents.Add(new { kind = "old_ticket_sent", state = ObservePrimaryIncarnation(ticket, oldActor) });
        npc.StrikeInstantKill();
        if (primaryIncarnationOnKills != 1 || CompanionNpc != null)
        { FinishPrimaryIncarnationNative("uncovered_death", "The original companion did not produce exactly one native OnKill and become absent."); return; }

        bool respawnBeforeResponse = PrimaryIncarnationCase == PrimaryRespawnResponseCase;
        if (respawnBeforeResponse && !SpawnPrimaryIncarnationReplacement(requireSameSlot: true)) return;
        if (!decisionGate.IsCurrent(ticket) || primaryTicket != ticket || agentJournal.LastSequence != primaryIncarnationInitialSequence ||
            !PrimaryIncarnationGoalPreserved())
        { FinishPrimaryIncarnationNative("uncovered_ticket", "A ticket version, task outcome or original goal changed before the old response."); return; }

        object atResponse = ObservePrimaryIncarnation(ticket, oldActor);
        var plan = AIPlanParser.Parse($$"""{"reply":"{{PrimaryIncarnationSentinel}}","tasks":[{"type":"equip","item":231}]}""");
        ReleasePrimary(cancellation);
        primaryIncarnationOldAccepted = ApplyPrimaryPlanResponse(plan, "goal", "", ticket, oldActor);
        primaryIncarnationOldReceipt = decisionReceipts.Last;
        primaryIncarnationResponseTick = Main.GameUpdateCount;
        primaryIncarnationEvents.Add(new { kind = "old_response", accepted = primaryIncarnationOldAccepted, atResponse,
            after = ObservePrimaryIncarnation(ticket, oldActor), receipt = primaryIncarnationOldReceipt });

        if (!respawnBeforeResponse) SpawnPrimaryIncarnationReplacement(requireSameSlot: false);
    }

    private DecisionTicket BeginPrimaryIncarnationTicket(out CancellationTokenSource cancellation)
    {
        cancellation = new CancellationTokenSource();
        primaryCancellation = cancellation;
        goalReviewPending = false;
        return primaryTicket = decisionGate.Begin("goal");
    }

    private bool SpawnPrimaryIncarnationReplacement(bool requireSameSlot)
    {
        SpawnCompanion(Main.LocalPlayer);
        NPC? npc = CompanionNpc;
        primaryIncarnationNewActor = npc?.ModNPC;
        bool distinct = primaryIncarnationNewActor != null && !ReferenceEquals(primaryIncarnationNewActor, primaryIncarnationOldActor);
        bool sameSlot = npc?.whoAmI == primaryIncarnationOldSlot;
        primaryIncarnationEvents.Add(new { kind = "native_respawn", tick = Main.GameUpdateCount, slot = npc?.whoAmI,
            oldSlot = primaryIncarnationOldSlot, sameSlot, distinct, actor = PrimaryIncarnationActor(npc) });
        if (npc is { active: true, life: > 0 } && distinct && (!requireSameSlot || sameSlot)) return true;
        FinishPrimaryIncarnationNative("uncovered_respawn", "The native spawn did not produce the required replacement instance and slot.");
        return false;
    }

    internal void RecordPrimaryIncarnationKill(NPC npc)
    {
        if (!primaryIncarnationRunning || primaryIncarnationOldActor == null ||
            !ReferenceEquals(npc.ModNPC, primaryIncarnationOldActor)) return;
        primaryIncarnationOnKills++;
        primaryIncarnationEvents.Add(new { kind = "native_on_kill", tick = Main.GameUpdateCount, actor = PrimaryIncarnationActor(npc) });
    }

    private void TickPrimaryIncarnationNative()
    {
        if (!primaryIncarnationRunning) return;
        if (!HouseMode || GetLlmSettings() != null || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 ||
            Executor.NavigationTeleportCount != 0 || Main.LocalPlayer.dead || CompanionNpc is not { active: true, life: > 0 } npc ||
            primaryIncarnationNewActor == null || !ReferenceEquals(npc.ModNPC, primaryIncarnationNewActor) ||
            DateTime.UtcNow - primaryIncarnationStartedAt > TimeSpan.FromSeconds(30))
        { FinishPrimaryIncarnationNative("failed_runtime", "Lost the isolated replacement, exceeded the deadline or observed API use/warp."); return; }

        if (!primaryIncarnationFreshAccepted.HasValue)
        {
            if (Main.GameUpdateCount - primaryIncarnationResponseTick < PrimaryIncarnationObserveTicks) return;
            ObservePrimaryIncarnationOldResponse();
            ApplyPrimaryIncarnationFreshResponse();
            return;
        }

        AgentOutcome? inspection = agentJournal.Outcomes.FirstOrDefault(outcome => outcome.Sequence > primaryIncarnationFreshSequence &&
            outcome.Task is { Action: AIActionType.Inspect, Target: "item", Item: "231" });
        if (inspection == null && Main.GameUpdateCount - primaryIncarnationResponseTick < 120) return;

        bool freshWorked = primaryIncarnationFreshAccepted == true && primaryIncarnationFreshReceipt?.status == "accepted" &&
            inspection?.Status == "succeeded" && PrimaryIncarnationGoalPreserved() && PrimaryIncarnationIdleReview();
        primaryIncarnationEvents.Add(new { kind = "fresh_outcome", inspection, freshWorked,
            state = ObservePrimaryIncarnation(primaryTicket, primaryIncarnationNewActor) });
        string error = !freshWorked ? "The current-instance response did not run its native read-only inspection." :
            primaryIncarnationStaleApplied ? "The old response was accepted and equipped the helmet after the original companion died." :
            !primaryIncarnationStaleRejectedClean ? "The old response did not leave an unchanged active goal pending review." : "";
        FinishPrimaryIncarnationNative(error.Length == 0 ? "passed" : "failed", error);
    }

    private void ObservePrimaryIncarnationOldResponse()
    {
        var outcomes = agentJournal.Outcomes.Where(outcome => outcome.Sequence > primaryIncarnationInitialSequence).ToArray();
        bool equipped = Loadout.Armor.Any(item => item.type == ItemID.MoltenHelmet && !item.IsAir);
        bool replyPublished = LastReply == PrimaryIncarnationSentinel;
        Dictionary<int, int> stock = CompanionCrafting.InventoryCounts(Loadout);
        bool unchangedStock = stock.Count == primaryIncarnationInitialStock.Count &&
            stock.All(pair => primaryIncarnationInitialStock.GetValueOrDefault(pair.Key) == pair.Value);
        primaryIncarnationStaleApplied = primaryIncarnationOldAccepted == true && primaryIncarnationOldReceipt?.code == "tasks_applied" &&
            equipped && replyPublished && outcomes.Any(outcome => outcome.Status == "succeeded" &&
                outcome.Task is { Action: AIActionType.Equip, Item: "231" });
        primaryIncarnationStaleRejectedClean = primaryIncarnationOldAccepted == false && primaryIncarnationOldReceipt?.status == "superseded" &&
            !equipped && !replyPublished && outcomes.Length == 0 && agentJournal.LastSequence == primaryIncarnationInitialSequence && unchangedStock &&
            PrimaryIncarnationGoalPreserved() && PrimaryIncarnationIdleReview();
        primaryIncarnationEvents.Add(new { kind = "old_native_observation", ticks = Main.GameUpdateCount - primaryIncarnationResponseTick,
            equipped, replyPublished, unchangedStock, stock, outcomes, staleApplied = primaryIncarnationStaleApplied,
            staleRejectedClean = primaryIncarnationStaleRejectedClean, state = ObservePrimaryIncarnation(primaryTicket, primaryIncarnationOldActor) });
    }

    private void ApplyPrimaryIncarnationFreshResponse()
    {
        primaryIncarnationFreshSequence = agentJournal.LastSequence;
        DecisionTicket ticket = BeginPrimaryIncarnationTicket(out CancellationTokenSource cancellation);
        object atResponse = ObservePrimaryIncarnation(ticket, primaryIncarnationNewActor);
        var plan = AIPlanParser.Parse("""{"tasks":[{"type":"inspect","target":"item","item":231}]}""");
        ReleasePrimary(cancellation);
        primaryIncarnationFreshAccepted = ApplyPrimaryPlanResponse(plan, "goal", "", ticket, primaryIncarnationNewActor);
        primaryIncarnationFreshReceipt = decisionReceipts.Last;
        primaryIncarnationResponseTick = Main.GameUpdateCount;
        primaryIncarnationEvents.Add(new { kind = "new_response", accepted = primaryIncarnationFreshAccepted, atResponse,
            after = ObservePrimaryIncarnation(ticket, primaryIncarnationNewActor), receipt = primaryIncarnationFreshReceipt });
    }

    private bool PrimaryIncarnationGoalPreserved() => ActiveGoal == PrimaryIncarnationGoal && GoalIdentity == primaryIncarnationGoalId &&
        goalPlanningState.Completion.Revision == primaryIncarnationRevision &&
        JsonSerializer.Serialize(goalPlanningState.Completion.Conditions) == primaryIncarnationContract;

    private bool PrimaryIncarnationIdleReview() => HasActiveModelGoal && goalStatus == "active" && goalReviewPending &&
        !goalWaitingForPlayer && !Executor.HoldPosition && !Executor.IsBusy && !Executor.HasCurrentPlan &&
        Executor.CurrentAction == AIActionType.Wait && Executor.SnapshotTasks().Count == 0;

    private static object? PrimaryIncarnationActor(NPC? npc) => npc == null ? null : new
    {
        slot = npc.whoAmI, instance = npc.ModNPC == null ? (int?)null : RuntimeHelpers.GetHashCode(npc.ModNPC), npc.active,
        hp = npc.life, x = npc.Center.X, y = npc.Center.Y, feetY = npc.Bottom.Y
    };

    private object ObservePrimaryIncarnation(DecisionTicket ticket, ModNPC? expectedActor) => new
    {
        tick = Main.GameUpdateCount, ticket,
        current = new { decisionGate.World, decisionGate.Intent, decisionGate.State, primaryTicket.Request },
        isCurrent = decisionGate.IsCurrent(ticket), actor = PrimaryIncarnationActor(CompanionNpc),
        sameInstance = ReferenceEquals(CompanionNpc?.ModNPC, expectedActor), activeGoal, goalId = GoalIdentity, goalStatus,
        goalReviewPending, goalWaitingForPlayer, contractRevision = goalPlanningState.Completion.Revision,
        contract = goalPlanningState.Completion.Conditions.ToArray(), sequence = agentJournal.LastSequence,
        action = Executor.CurrentAction, hold = Executor.HoldPosition, queue = Executor.SnapshotTasks(),
        stock = CompanionCrafting.InventoryCounts(Loadout), armor = Loadout.Armor.Select(item => item.type).ToArray()
    };

    private void FinishPrimaryIncarnationNative(string status, string error)
    {
        primaryIncarnationRunning = false;
        primaryIncarnationFinished = true;
        try
        {
            ShelterPreservationReport? preservation = primaryIncarnationAudit?.CheckPreservation();
            if (status == "passed" && preservation is not { protectedCells: 5803, preserved: true, coverageComplete: true, diffCount: 0 })
            { status = "failed"; error = "The original-house protection check failed."; }
            Directory.CreateDirectory(Path.GetDirectoryName(PrimaryIncarnationResultPath)!);
            File.WriteAllText(PrimaryIncarnationResultPath, JsonSerializer.Serialize(new
            {
                status, error, profile = PrimaryIncarnationCase, worldId = Main.worldID, nativeOnKills = primaryIncarnationOnKills,
                oldResponseAccepted = primaryIncarnationOldAccepted, newResponseAccepted = primaryIncarnationFreshAccepted,
                oldResponseReceipt = primaryIncarnationOldReceipt, newResponseReceipt = primaryIncarnationFreshReceipt,
                staleApplied = primaryIncarnationStaleApplied, staleRejectedClean = primaryIncarnationStaleRejectedClean,
                requests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount, warps = Executor.NavigationTeleportCount,
                initialStock = primaryIncarnationInitialStock, events = primaryIncarnationEvents, outcomes = agentJournal.Outcomes,
                preservation, isolatedInputHashes = primaryIncarnationInputHashes, isolatedFileHashesAtFinish = ReadBossReviewHashes(),
                scope = "Real native companion death and spawn, controlled public JSON and production success handler. Zero HTTP transport. No asynchronous network race or staged history was exercised."
            }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        }
        finally { Stop(); }
        TestLog($"{PrimaryIncarnationCase} {status}: {error}");
    }
}

public sealed class PrimaryIncarnationKillWitness : GlobalNPC
{
    public override void OnKill(NPC npc) => AITeammateSystem.Instance?.RecordPrimaryIncarnationKill(npc);
}
