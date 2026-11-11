using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string PausedStructureNativeCase = "paused_structure_native";
    private const string PausedStructureGoal = "受控原结构详情续行";
    private static readonly StructureAnchor PausedStructureAnchorA = new(3200, 390);
    private static readonly StructureAnchor PausedStructureAnchorB = new(3219, 399);

    private sealed record PausedStructureSelection(int World, int SelectedX, int SelectedY, int RequestedX, int RequestedY,
        object? Latest, object? Reference);
    private sealed record PausedStructureRead(string Topic, bool Available, bool Found, int? World, ulong? Tick,
        StructureAnchor? Requested, StructureAnchor? Sampled, JsonNode? Bounds, string? Signature, string ResultSha256)
    {
        public bool Matches(string topic, StructureAnchor anchor) => Topic == topic && Available && Found && Requested == anchor;
    }

    private bool pausedStructureRunning, pausedStructureSelectionStable, pausedStructureQueueStable;
    private long pausedStructureResumeSequence;
    private DateTime pausedStructureDeadline;
    private PausedStructureSelection? pausedStructureInitialSelection;
    private PausedQueryState? pausedStructureOriginalState;
    private ShelterWorldAudit? pausedStructureAudit;
    private Dictionary<string, string> pausedStructureInputHashes = new();
    private readonly List<object> pausedStructureEvents = new();

    private static string PausedStructureResultPath => Path.Combine(Main.SavePath, "AITeammate", "paused-structure-native-result.json");

    private void BeginPausedStructureNative()
    {
        if (!HouseMode || Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") != PausedStructureNativeCase) return;
        if (File.Exists(PausedStructureResultPath))
        { TestLog("paused structure requires a fresh isolated copy"); return; }
        if (GetLlmSettings() != null || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 ||
            CompanionNpc is not { active: true } npc || Main.LocalPlayer.dead)
        { FinishPausedStructureNative("failed_setup", "A live companion and disabled LLM are required."); return; }

        Stop();
        Executor.ResetNavigationTelemetry();
        pausedStructureInputHashes = ReadBossReviewHashes();
        pausedStructureAudit = ShelterWorldAudit.Capture(3220, 390);
        if (pausedStructureAudit.CheckPreservation() is not { protectedCells: 5803, preserved: true, coverageComplete: true, diffCount: 0 })
        { FinishPausedStructureNative("failed_setup", "The original-house protection baseline was unavailable."); return; }

        Player player = Main.LocalPlayer;
        npc.Center = Stand(3170, 400);
        player.Center = Stand(3162, 400);
        npc.velocity = player.velocity = Vector2.Zero;
        if (!TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, 0, npc.width))
        { FinishPausedStructureNative("failed_setup", "The original-house observation stand was unavailable."); return; }

        try
        {
            PausedStructureRead a = DecodePausedStructureRead(AgentKnowledge.Query("", "structure", npc, player, Executor,
                PausedStructureAnchorA.x, PausedStructureAnchorA.y));
            PausedStructureSelection selected = CapturePausedStructureSelection();
            pausedStructureInitialSelection = selected;
            pausedStructureEvents.Add(new { kind = "original_selection", reading = a, selection = DescribePausedStructureSelection(selected) });
            if (!a.Matches("structure", PausedStructureAnchorA) || selected.RequestedX != PausedStructureAnchorA.x ||
                selected.RequestedY != PausedStructureAnchorA.y || selected.Latest == null || selected.Reference == null)
            { FinishPausedStructureNative("failed_setup", "The original anchor A did not return a found native observation."); return; }

            manualControlActive = false;
            Executor.SetHold(false);
            var originalPlan = AIPlanParser.Parse($$"""
                {"goal":"{{PausedStructureGoal}}","completion_conditions":[{"type":"structure_preserved"}],
                  "tasks":[{"type":"inspect","target":"structure_detail"}]}
                """);
            if (!ApplyPlan(originalPlan, null, "chat", PausedStructureGoal) ||
                !ApplyPlan(new AIPlan { Hold = true, KeepTasks = true }, null, "chat"))
            { FinishPausedStructureNative("failed_setup", "Could not seed and pause the native structure detail task."); return; }
            PausedQueryState before = CapturePausedQuery();
            pausedStructureOriginalState = before;
            if (!before.Hold || !before.HasPlan || before.GoalStatus != "paused" || before.Queue is not
                [{ Action: AIActionType.Inspect, Target: "structure_detail", X: null, Y: null }])
            { FinishPausedStructureNative("failed_setup", "The suspended queue did not contain the original coordinate-free detail task."); return; }

            PausedStructureSelection beforeSelection = CapturePausedStructureSelection();
            var sidePlan = AIPlanParser.Parse($$"""
                {"tasks":[{"type":"inspect","target":"structure","x":{{PausedStructureAnchorB.x}},"y":{{PausedStructureAnchorB.y}}}]}
                """);
            bool sideAccepted = ApplyPlan(sidePlan, null, "chat", "暂停中旁问另一个原屋锚点的结构");
            PlanDecisionReceipt? sideReceipt = decisionReceipts.Last;
            PausedQueryState after = CapturePausedQuery();
            PausedStructureSelection afterSelection = CapturePausedStructureSelection();
            AgentOutcome[] newOutcomes = agentJournal.Outcomes.Where(outcome => outcome.Sequence > before.Sequence).ToArray();
            AgentOutcome? sideOutcome = newOutcomes.Length == 1 ? newOutcomes[0] : null;
            PausedStructureRead? b = sideOutcome == null ? null : DecodePausedStructureRead(sideOutcome.Result);
            bool foundB = sideOutcome is { Status: "succeeded", Task: { Action: AIActionType.Inspect, Target: "structure" } } &&
                sideOutcome.Task.X == PausedStructureAnchorB.x && sideOutcome.Task.Y == PausedStructureAnchorB.y &&
                b?.Matches("structure", PausedStructureAnchorB) == true;
            pausedStructureSelectionStable = SamePausedStructureSelection(beforeSelection, afterSelection);
            pausedStructureQueueStable = SamePausedQueryState(before, after) && before.Stock.SequenceEqual(after.Stock) &&
                before.ActorX == after.ActorX && before.ActorFeet == after.ActorFeet && before.Tick == after.Tick;
            pausedStructureEvents.Add(new { kind = "paused_side_query", sideAccepted, sideReceipt, foundB, reading = b,
                sequence = sideOutcome?.Sequence, before, after, queueStable = pausedStructureQueueStable,
                selectionStable = pausedStructureSelectionStable, selectionBefore = DescribePausedStructureSelection(beforeSelection),
                selectionAfter = DescribePausedStructureSelection(afterSelection) });
            if (!foundB)
            { FinishPausedStructureNative("failed_setup", "The side query did not journal a found native observation of anchor B."); return; }
            if (!sideAccepted || sideReceipt?.code != "paused_inspections_completed" || !pausedStructureQueueStable)
            { FinishPausedStructureNative("failed", "The side query changed the old goal or paused queue."); return; }

            // The next explicit player instruction has a new intent, as it does in live chat.
            InvalidateAgentIntent();
            var resume = AIPlanParser.Parse("""{"resume":true,"keep_tasks":true,"tasks":[]}""");
            bool resumed = ApplyPlan(resume, null, "chat", "继续原先选定的结构详情查询");
            PausedQueryState resumedState = CapturePausedQuery();
            pausedStructureEvents.Add(new { kind = "explicit_resume", resumed, receipt = decisionReceipts.Last, state = resumedState });
            if (!resumed || resumedState.Hold || !SamePausedQueryGoal(before, resumedState) || !SamePausedQueryQueue(before, resumedState))
            { FinishPausedStructureNative("failed", "Explicit resume did not retain the old coordinate-free task."); return; }
            pausedStructureResumeSequence = agentJournal.LastSequence;
            pausedStructureDeadline = DateTime.UtcNow.AddSeconds(10);
            pausedStructureRunning = true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { FinishPausedStructureNative("failed_setup", exception.Message); }
    }

    private void TickPausedStructureNative()
    {
        if (!pausedStructureRunning) return;
        if (!HouseMode || GetLlmSettings() != null || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 ||
            Executor.NavigationTeleportCount != 0 || CompanionNpc is not { active: true } || Main.LocalPlayer.dead ||
            DateTime.UtcNow >= pausedStructureDeadline)
        { FinishPausedStructureNative("failed_runtime", "The resumed native detail exceeded its deadline or lost its zero-request isolation."); return; }

        AgentOutcome? outcome = agentJournal.Outcomes.FirstOrDefault(candidate => candidate.Sequence > pausedStructureResumeSequence);
        if (outcome == null) return;
        try
        {
            PausedStructureRead detail = DecodePausedStructureRead(outcome.Result);
            PausedStructureSelection selection = CapturePausedStructureSelection();
            PausedQueryState after = CapturePausedQuery();
            PausedQueryState before = pausedStructureOriginalState!;
            bool originalTask = outcome.Status == "succeeded" && TaskFingerprint(outcome.Task) == TaskFingerprint(before.Queue[0]);
            bool detailAtA = detail.Matches("structure_detail", PausedStructureAnchorA);
            bool detailAtB = detail.Matches("structure_detail", PausedStructureAnchorB);
            bool originalReference = ReferenceEquals(selection.Reference, pausedStructureInitialSelection!.Reference);
            bool passed = originalTask && detailAtA && originalReference && pausedStructureSelectionStable && pausedStructureQueueStable &&
                SamePausedQueryGoal(before, after) && before.Stock.SequenceEqual(after.Stock);
            pausedStructureEvents.Add(new { kind = "resumed_native_detail", outcome.Sequence, outcome.Status, outcome.Task, reading = detail,
                originalTask, detailAtA, detailAtB, originalReference, state = after, selection = DescribePausedStructureSelection(selection) });
            FinishPausedStructureNative(passed ? "passed" : "failed",
                passed ? "" : detailAtB ? "The retained detail query read the side query's anchor B after resuming." :
                    "The original selection, reference or resumed anchor was not preserved.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { FinishPausedStructureNative("failed", exception.Message); }
    }

    private static PausedStructureSelection CapturePausedStructureSelection()
    {
        object? Read(string field) => (typeof(StructureWorldObserver).GetField(field, BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException($"Missing native structure selection field {field}.")).GetValue(null);
        return new((int)Read("selectedWorld")!, (int)Read("selectedX")!, (int)Read("selectedY")!,
            (int)Read("requestedX")!, (int)Read("requestedY")!, Read("latest"), Read("reference"));
    }

    private static bool SamePausedStructureSelection(PausedStructureSelection before, PausedStructureSelection after) =>
        before.World == after.World && before.SelectedX == after.SelectedX && before.SelectedY == after.SelectedY &&
        before.RequestedX == after.RequestedX && before.RequestedY == after.RequestedY &&
        ReferenceEquals(before.Latest, after.Latest) && ReferenceEquals(before.Reference, after.Reference);

    private static object DescribePausedStructureSelection(PausedStructureSelection value) => new
    {
        value.World, value.SelectedX, value.SelectedY, value.RequestedX, value.RequestedY,
        latestIdentity = value.Latest == null ? (int?)null : RuntimeHelpers.GetHashCode(value.Latest),
        referenceIdentity = value.Reference == null ? (int?)null : RuntimeHelpers.GetHashCode(value.Reference)
    };

    private static PausedStructureRead DecodePausedStructureRead(string raw)
    {
        JsonNode? result = JsonNode.Parse(raw);
        JsonNode? observation = result?["observation"], sampling = result?["sampling"];
        return new(result?["topic"]?.GetValue<string>() ?? "", result?["available"]?.GetValue<bool>() == true,
            observation?["found"]?.GetValue<bool>() == true, result?["worldId"]?.GetValue<int>(), result?["observedAtTick"]?.GetValue<ulong>(),
            sampling?["requestedAnchor"]?.Deserialize<StructureAnchor>(), sampling?["sampledCenter"]?.Deserialize<StructureAnchor>(),
            observation?["bounds"]?.DeepClone(), observation?["signature"]?.GetValue<string>(),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))));
    }

    private void FinishPausedStructureNative(string status, string error)
    {
        pausedStructureRunning = false;
        try
        {
            var protection = pausedStructureAudit?.CheckPreservation();
            Dictionary<string, string> finalHashes = ReadBossReviewHashes();
            bool sameFiles = pausedStructureInputHashes.Count > 0 && pausedStructureInputHashes.Count == finalHashes.Count &&
                pausedStructureInputHashes.All(pair => finalHashes.TryGetValue(pair.Key, out string? hash) && hash == pair.Value);
            if (status == "passed" && (!sameFiles || protection is not { protectedCells: 5803, preserved: true, coverageComplete: true, diffCount: 0 }))
            { status = "failed"; error = "The isolated files or original-house protection changed."; }
            Directory.CreateDirectory(Path.GetDirectoryName(PausedStructureResultPath)!);
            File.WriteAllText(PausedStructureResultPath, JsonSerializer.Serialize(new
            {
                status, error, profile = PausedStructureNativeCase, worldId = Main.worldID, epoch = WorldKnowledgeObserver.LoadEpoch,
                anchorA = PausedStructureAnchorA, anchorB = PausedStructureAnchorB, events = pausedStructureEvents,
                selectionStable = pausedStructureSelectionStable, queueStable = pausedStructureQueueStable,
                requests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount, warps = Executor.NavigationTeleportCount,
                protection, sameFiles, copiedInputHashes = pausedStructureInputHashes, isolatedFileHashesAtFinish = finalHashes,
                scope = "Two requested anchors in the original-house area, native survey and retained native detail. Controlled requests with zero model transport."
            }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        }
        finally { Stop(); }
        TestLog($"{PausedStructureNativeCase} {status}: {error}");
    }
}
