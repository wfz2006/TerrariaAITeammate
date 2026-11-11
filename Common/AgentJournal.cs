using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

public sealed record AgentOutcome(long Sequence, AIPlan Task, string Status, string Result);

public sealed record GoalDesignDraft(long goalStartSequence, long sequence, string designHash, JsonElement design,
    string? reviewResult, long? reviewSequence)
{
    public string origin => "model_proposal";
    public bool constructionExecuted => false;
    public bool reviewIsReadOnly => true;
}

public sealed record InterruptedModelTask(string type, string target, string item, string slot, int? recipe_id, int count,
    string blueprint, int? x, int? y, int width, int height, string reason, string success_criteria, string plan_step, bool designOmitted);

public sealed record GoalInterruptedPlan(long goalStartSequence, long capturedAtSequence, int originalTaskCount,
    IReadOnlyList<InterruptedModelTask> capturedQueue)
{
    public string origin => "interrupted_model_proposal";
    public int failedTaskIndex => 0;
    public int capturedTaskCount => capturedQueue.Count;
    public bool queueTruncated => capturedQueue.Count < originalTaskCount;
    public bool autoResume => false;
    public bool capturedQueueIsCurrent => false;
    public string note => "Historical model proposal captured at this real blocked outcome before the executor cleared its queue. Entry 0 is the blocked attempt and may have partially executed; entries after it had not started in that interrupted queue at capturedAtSequence. This does not say they remain unexecuted now or establish success. Current tasks, actual outcomes and live-world facts take priority. Reconsider prerequisites and submit a new plan; never resume these tasks automatically. Any designOmitted entry excludes its design payload, and must not be equated with latestDesign, which records only an actually submitted design.";
}

/// <summary>Current goal's proposal and literal player intent, independent of bounded execution history.</summary>
public sealed class GoalPlanningState
{
    private string? goal;
    private long startSequence;
    private string? sourcePlayerRequest;
    private GoalDesignDraft? latestDesign;
    private GoalInterruptedPlan? interruptedPlan;
    private readonly GoalWorkingPlanState work = new();
    private readonly GoalRecoveryMemory recovery = new();
    private long latestOutcomeSequence;

    public bool HasGoal => goal != null;
    public string InstanceId { get; private set; } = "";
    public string? SourcePlayerRequest => sourcePlayerRequest;
    public GoalDesignDraft? LatestDesign => latestDesign;
    public GoalInterruptedPlan? InterruptedPlan => interruptedPlan;
    public GoalCompletionState Completion { get; } = new();
    public GoalWorkingPlanView? WorkingPlan => work.WorkingPlan;
    public GoalExecutionPlanView? AcceptedExecutionPlan => work.AcceptedExecutionPlan;
    public GoalExecutionHistory ExecutionPlans => work.ExecutionPlans;
    public GoalActionEvidenceView Evidence => work.Evidence;
    public GoalRecoveryView Recovery => recovery.View;
    public WorldObservationArchive WorldObservations { get; } = new();
    public long AcknowledgedOutcomeSequence => WorldObservations.AcknowledgedOutcomeSequence;
    public void BeginWorldObservations(int worldId, string epoch)
    {
        if (!HasGoal) return;
        WorldObservations.Begin(worldId, epoch, startSequence, goal!);
        recovery.BeginWorld(worldId, epoch);
    }
    public string? ArchiveWorldOutcome(AgentOutcome outcome, int worldId, string epoch, ulong tick)
        => HasGoal ? WorldObservations.Record(outcome, worldId, epoch, startSequence, tick) : null;
    public void RecordSupplementalObservation(AgentOutcome outcome, int worldId, string epoch, ulong tick)
    {
        if (!HasGoal) return;
        latestOutcomeSequence = Math.Max(latestOutcomeSequence, outcome.Sequence);
        ArchiveWorldOutcome(outcome, worldId, epoch, tick);
        // This question did not execute or replace a suspended task, even if
        // its parameters match an inspection already in the retained queue.
    }
    public bool ValidateDecision(AIPlan decision, out string error) => work.ValidateDecision(decision, out error);
    public void AdoptDecision(AIPlan decision, long sequence, IReadOnlyList<AIPlan> currentQueue)
    {
        if (!HasGoal) return;
        work.AdoptDecision(decision, sequence, currentQueue);
        AcknowledgeObservationReceipt(decision);
    }
    public void AcknowledgeObservationReceipt(AIPlan decision)
    {
        if (HasGoal && decision.GoalStatus != "cancelled")
            WorldObservations.Acknowledge(decision.NativeObservationReceipt as WorldObservationReceipt, latestOutcomeSequence);
    }
    public void RecordOutcome(AgentOutcome outcome, IReadOnlyList<AIPlan> remaining)
    {
        if (!HasGoal) return;
        latestOutcomeSequence = Math.Max(latestOutcomeSequence, outcome.Sequence);
        work.RecordOutcome(outcome, remaining);
        recovery.Record(outcome);
    }
    public void SynchronizeQueue(IReadOnlyList<AIPlan> currentQueue, bool hasCurrentPlan)
    { if (HasGoal) work.SynchronizeQueue(currentQueue, hasCurrentPlan); }
    public void MarkPaused(bool value) { if (HasGoal) work.MarkPaused(value); }

    public bool BeginGoal(string goal, long startSequence, string? sourcePlayerRequest, bool keepTasks = false)
    {
        goal = goal.Trim();
        if (goal.Length == 0 || this.goal != null && (keepTasks || this.goal == goal)) return false;
        InstanceId = Guid.NewGuid().ToString("N");
        this.goal = goal;
        this.startSequence = startSequence;
        this.sourcePlayerRequest = string.IsNullOrWhiteSpace(sourcePlayerRequest) ? null : sourcePlayerRequest;
        latestDesign = null;
        interruptedPlan = null;
        latestOutcomeSequence = startSequence;
        WorldObservations.Clear();
        work.Reset(startSequence);
        recovery.Reset(startSequence, InstanceId);
        Completion.Clear();
        return true;
    }

    public bool CaptureInterruptedPlan(AgentOutcome outcome, IReadOnlyList<AIPlan> queue)
    {
        // A single recovery query must not erase an earlier multi-step intention.
        if (!HasGoal || outcome.Status != "blocked" || outcome.Sequence <= startSequence ||
            interruptedPlan?.capturedAtSequence >= outcome.Sequence || queue.Count < 2 ||
            queue[0] == null || !SameIdentity(queue[0], outcome.Task)) return false;

        int count = Math.Min(queue.Count, AIPlanParser.MaxTasks);
        var captured = new InterruptedModelTask[count];
        for (int index = 0; index < count; index++)
        {
            AIPlan task = queue[index];
            if (task == null) return false;
            captured[index] = new InterruptedModelTask(task.Action.ToString().ToLowerInvariant(), task.Target,
                task.Item, task.Slot, task.RecipeId, task.Count, task.Blueprint, task.X, task.Y, task.Width, task.Height,
                task.Reason, task.SuccessCriteria, task.PlanStep, task.Design.HasValue);
        }
        interruptedPlan = new GoalInterruptedPlan(startSequence, outcome.Sequence, queue.Count, Array.AsReadOnly(captured));
        return true;
    }

    private static bool SameIdentity(AIPlan left, AIPlan right) => left.Action == right.Action &&
        left.Target == right.Target && left.Item == right.Item && left.Slot == right.Slot && left.RecipeId == right.RecipeId && left.Count == right.Count &&
        left.Blueprint == right.Blueprint && left.X == right.X && left.Y == right.Y &&
        left.Width == right.Width && left.Height == right.Height;

    public void SubmitProposal(JsonElement design, long proposalSequence)
    {
        if (!HasGoal || proposalSequence <= startSequence || latestDesign?.sequence >= proposalSequence) return;
        latestDesign = new GoalDesignDraft(startSequence, proposalSequence, Hash(design), design.Clone(), null, null);
    }

    public void RecordReview(AgentOutcome outcome)
    {
        if (latestDesign == null || outcome.Sequence <= latestDesign.sequence || latestDesign.reviewSequence >= outcome.Sequence ||
            !IsDesignTask(outcome.Task) || Hash(outcome.Task.Design!.Value) != latestDesign.designHash) return;
        latestDesign = latestDesign with { reviewResult = outcome.Result, reviewSequence = outcome.Sequence };
    }

    public void Clear()
    {
        InstanceId = "";
        goal = sourcePlayerRequest = null;
        startSequence = 0;
        latestDesign = null;
        interruptedPlan = null;
        latestOutcomeSequence = 0;
        WorldObservations.Clear();
        work.Reset();
        recovery.Reset();
        Completion.Clear();
    }

    public static bool IsDesignTask(AIPlan? task) => task is { Action: AIActionType.Inspect, Design: not null } &&
        string.Equals(task.Target, "design", StringComparison.OrdinalIgnoreCase);

    private static string Hash(JsonElement design) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(design.GetRawText())));
}

public sealed class AgentJournal
{
    private readonly List<AgentOutcome> outcomes = new();
    private long sequence;
    public long LastSequence => sequence;
    // A proposal is an identity boundary, not an execution outcome or completion evidence.
    public long MarkPlanningProposal() => ++sequence;
    public IReadOnlyList<AgentOutcome> Outcomes => outcomes;
    public AgentOutcome Record(AIPlan? task, string result, bool blocked)
    {
        task ??= new AIPlan();
        var snapshot = new AIPlan
        {
            Action = task.Action, Target = task.Target, Item = task.Item, Slot = task.Slot, RecipeId = task.RecipeId,
            Count = task.Count, Blueprint = task.Blueprint, X = task.X, Y = task.Y,
            Width = task.Width, Height = task.Height, Reason = task.Reason, SuccessCriteria = task.SuccessCriteria, PlanStep = task.PlanStep,
            Design = task.Design?.Clone()
        };
        var outcome = new AgentOutcome(++sequence, snapshot, blocked ? "blocked" : "succeeded", result);
        outcomes.Add(outcome);
        if (outcomes.Count > 12) outcomes.RemoveAt(0);
        return outcome;
    }
    public void Clear() { outcomes.Clear(); sequence = 0; }
}
