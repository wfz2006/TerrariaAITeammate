using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace TerrariaAITeammate.Common;

/// <summary>Allows bounded model correction after a dispatch rejection, never an unlimited paid retry loop.</summary>
public sealed class DispatchRepairBudget
{
    public const int MaximumAutomaticRepairs = 2;
    public int Rejections { get; private set; }
    public string LastCode { get; private set; } = "";
    private bool requiresProgress;
    public bool IsPaused => Rejections > MaximumAutomaticRepairs;
    public int RemainingAutomaticRepairs => Math.Max(0, MaximumAutomaticRepairs - Rejections);
    public bool Reject(string code, bool untilProgress = false)
    {
        Rejections++;
        LastCode = code;
        requiresProgress |= untilProgress;
        return !IsPaused;
    }
    public void AcceptDecision() { if (!requiresProgress) Reset(); }
    public void ObserveProgress() { if (requiresProgress) Reset(); }
    public void Reset() { Rejections = 0; LastCode = ""; requiresProgress = false; }
}

public sealed record TaskRetryDecision(bool allowed, int actualFailures, int failureLimit, string code, string note);

/// <summary>Bounded retry accounting for actual failed attempts, scoped by operation and relevant observed conditions.</summary>
public sealed class TaskRetryLedger
{
    public const int FailureLimit = 2;
    private const int MaximumConditions = 24;
    private readonly Dictionary<(string task, string conditions), (int count, long sequence)> failures = new();

    public TaskRetryDecision Evaluate(string task, string conditions)
    {
        int count = failures.GetValueOrDefault((task, conditions)).count;
        return new TaskRetryDecision(count < FailureLimit, count, FailureLimit,
            count < FailureLimit ? "retry_budget_available" : "unchanged_preconditions_retry_exhausted",
            "This is a bounded signature of relevant local position, geometry and task prerequisites, not proof that the whole world is unchanged or the goal is impossible. Changed relevant conditions or another model-chosen operation can be reconsidered. Inspect success alone does not erase another operation's failures.");
    }

    public TaskRetryDecision EvaluateStart(bool activeModelGoal, string task, Func<string> currentNativeConditions)
        => activeModelGoal ? Evaluate(task, currentNativeConditions()) : new(true, 0, FailureLimit,
            "not_an_active_model_goal", "Manual and non-goal tasks are not bound by an earlier goal's retry history.");

    public void RecordFailure(string task, string conditions, long sequence)
    {
        if (task.Length == 0) return;
        var key = (task, conditions);
        if (failures.TryGetValue(key, out var previous) && sequence <= previous.sequence) return;
        if (!failures.ContainsKey(key) && failures.Count >= MaximumConditions)
            failures.Remove(failures.MinBy(pair => pair.Value.sequence).Key);
        failures[key] = (Math.Min(FailureLimit, previous.count + 1), sequence);
    }

    public void RecordSuccess(string task)
    {
        foreach (var key in failures.Keys.Where(key => key.task == task).ToArray()) failures.Remove(key);
    }

    public void Clear() => failures.Clear();
}

/// <summary>Responses remain provisional until the game thread accepts their decision ticket and operation.</summary>
public sealed class DeferredPlanHistory
{
    private sealed record Pending(string key, LlmHistorySnapshot snapshot, string user, string assistant);
    private readonly ConditionalWeakTable<AIPlan, Pending> pending = new();

    public void Stage(AIPlan plan, string key, LlmHistorySnapshot snapshot, string user, string assistant)
    {
        pending.Remove(plan);
        pending.Add(plan, new Pending(key, snapshot with { Messages = snapshot.Messages.ToList() }, user, assistant));
    }

    public bool ApplyAndCommit(AIPlan plan, DecisionGate gate, DecisionTicket ticket, Func<bool> apply)
    {
        pending.TryGetValue(plan, out Pending? value);
        pending.Remove(plan);
        if (!gate.IsCurrent(ticket)) return false;
        bool accepted = apply();
        // Applying an accepted decision can itself change goal/state versions. Only
        // the pre-apply ticket check and the store's generation/epoch guard apply here.
        if (accepted && value != null)
            LlmConversationStore.TryAppendPair(value.key, value.snapshot, value.user, value.assistant, 12);
        return accepted;
    }

    public void Discard(AIPlan plan) => pending.Remove(plan);
}

public sealed record DecisionTaskReceipt(string identity, string type, string target, string item, string slot,
    int? recipe_id, int count, string blueprint, int? x, int? y, int width, int height, bool designOmitted)
{
    public string plan_step { get; init; } = "";
}

public sealed record PlanDecisionReceipt(long receiptSequence, long? requestId, long? worldVersion, long? intentVersion,
    long? stateVersion, string source, string status, string code, string detail,
    IReadOnlyList<DecisionTaskReceipt> requestedTasks, int requestedTaskCount, bool requestedTasksTruncated,
    IReadOnlyList<string> currentTaskIdentities, bool holdPosition)
{
    public bool executionProven => false;
    public string note => "Decision-dispatch receipt only: accepted is not physical completion. Current state.tasks describes the adopted queue; actual outcomes and live-world facts determine progress. A superseded response was not applied. Design payloads are omitted; identities include their actual content.";
}

public sealed class PlanDecisionReceipts
{
    private long sequence;
    public PlanDecisionReceipt? Last { get; private set; }

    public void Record(AIPlan plan, string source, string status, string code, string detail,
        IReadOnlyList<AIPlan> currentTasks, bool hold, DecisionTicket? ticket = null)
    {
        if (status is not ("accepted" or "rejected" or "superseded")) throw new ArgumentException("Unknown decision disposition.");
        IReadOnlyList<AIPlan> requested = plan.Steps.Count > 0 ? plan.Steps : plan.Action == AIActionType.None ? Array.Empty<AIPlan>() : new[] { plan };
        var tasks = requested.Take(AIPlanParser.MaxTasks).Select(task => new DecisionTaskReceipt(PlanTaskIdentity.Fingerprint(task),
            task.Action.ToString().ToLowerInvariant(), task.Target, task.Item, task.Slot, task.RecipeId, task.Count, task.Blueprint,
            task.X, task.Y, task.Width, task.Height, task.Design.HasValue) { plan_step = task.PlanStep }).ToArray();
        Last = new PlanDecisionReceipt(++sequence, ticket?.Request, ticket?.World, ticket?.Intent, ticket?.State,
            source, status, code, detail, Array.AsReadOnly(tasks), requested.Count, requested.Count > tasks.Length,
            Array.AsReadOnly(currentTasks.Take(AIPlanParser.MaxTasks).Select(PlanTaskIdentity.Fingerprint).ToArray()), hold);
    }

    public void Clear() { Last = null; sequence = 0; }
}
