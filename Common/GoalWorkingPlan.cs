using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

public sealed record GoalWorkingStepView(string id, string objective, string success_criteria, IReadOnlyList<string> depends_on,
    string status, long successfulActions, long successfulInspections, long blockedCalls, long? lastOutcomeSequence, long revision);
public sealed record GoalSupersededStep(long supersededAtRevision, GoalWorkingStepView step);
public sealed record GoalWorkingPlanView(long goalStartSequence, long revision, long adoptedAtSequence, string summary,
    IReadOnlyList<GoalWorkingStepView> steps, IReadOnlyList<GoalSupersededStep> supersededSteps, long omittedSupersededSteps)
{
    public bool completionProven => false;
    public string note => "Model-owned revisable strategy, separate from the player's goal and completion contract. Earlier actions retain their original bound version. In one dispatch, physical tasks bound to dependent steps must follow their submitted prerequisites. Dependencies do not schedule actions or prove completion.";
}
public sealed record GoalTaskView(string identity, string type, string target, string item, string slot, int? recipe_id,
    int count, string blueprint, int? x, int? y, int width, int height, string reason, string success_criteria,
    string plan_step, bool designOmitted);
public sealed record GoalExecutionTaskView(int index, long workingPlanRevision, GoalTaskView task, string status, long? outcomeSequence, long? planStepRevision);
public sealed record GoalExecutionPlanView(long goalStartSequence, long revision, long acceptedAtSequence, string reason,
    string status, bool paused, long? supersededBy, IReadOnlyList<GoalExecutionTaskView> tasks)
{
    public bool autoResumeDependencies => false;
    public string note => "Accepted short action segment, not a completion claim. not_started/pending_reconsideration entries are proposals, never successful execution. Current executor queue and real outcomes take priority. Design payloads are omitted and must not be equated with latestDesign.";
}
public sealed record GoalExecutionSummary(long revision, long acceptedAtSequence, string status, string reason,
    bool reasonTruncated, int successfulActions, int blockedActions, int unexecutedActions, long? supersededBy);
public sealed record GoalExecutionHistory(long totalAcceptedPlans, long omittedPlans, IReadOnlyList<GoalExecutionSummary> plans)
{
    public string note => "Bounded historical dispatch summaries, not complete old queues. Current segment and the last interrupted model proposal retain their ordinary task parameters separately.";
}
public sealed record GoalActionCounts(string type, long successfulCalls, long blockedCalls);
public sealed record GoalActionEvidence(long sequence, GoalTaskView task, string status, string result, bool resultTruncated,
    long? workingPlanRevision = null, long? planStepRevision = null);
public sealed record GoalActionEvidenceView(long goalStartSequence, long lastOutcomeSequence, long successfulPhysicalActions,
    long blockedPhysicalActions, long successfulInspections, long blockedInspections, IReadOnlyList<GoalActionCounts> actionCounts,
    IReadOnlyList<GoalActionEvidence> recentPhysicalOutcomes, long omittedPhysicalOutcomes)
{
    public bool completionProven => false;
    public string note => "Counts aggregate actual calls/actions for this goal, never item quantities or goal completion. Recent result excerpts may be truncated explicitly. Inventory, position, native structure and current-event evidence must independently verify the completion contract.";
}

/// <summary>Pure goal-owned observations of model plans and real dispatch/outcome events. Never executes or creates a task.</summary>
internal sealed class GoalWorkingPlanState
{
    private const int MaxPlans = 8, MaxSteps = 12, MaxKnownSteps = 128, MaxEvidence = 12;
    private sealed class Step
    {
        public string Id = "", Objective = "", Criteria = "";
        public string[] Dependencies = Array.Empty<string>();
        public long Revision;
        public long Successes, Inspections, Blocked;
        public long? LastSequence;
        public string LastStatus = "";
        public bool Running, Paused;
        public GoalWorkingStepView View() => new(Id, Objective, Criteria, Array.AsReadOnly(Dependencies.ToArray()),
            Running ? "running" : Paused ? "paused" : LastStatus == "blocked" ? "blocked" : Successes > 0 ? "has_successful_actions" :
                Inspections > 0 ? "has_successful_observations" : "proposed", Successes, Inspections, Blocked, LastSequence, Revision);
    }
    private sealed class TaskEntry
    {
        public required GoalTaskView Task;
        // Executor ApplyTasks can keep the exact in-flight action while replacing its tail.
        // This reference identifies that dispatch; equal physical parameters alone do not.
        public required AIPlan DispatchTask;
        public long GraphRevision;
        public Step? BoundStep;
        public string Status = "not_started";
        public long? OutcomeSequence;
        public GoalExecutionTaskView View(int index) => new(index, GraphRevision, Task, Status, OutcomeSequence, BoundStep?.Revision);
    }
    private sealed class Segment
    {
        public long Revision, AcceptedAt;
        public string Reason = "", Status = "active";
        public long? SupersededBy;
        public readonly List<TaskEntry> Tasks = new();
    }

    private long start, graphRevision, graphAcceptedAt, totalPlans, lastOutcome, supersededCount, physicalOutcomes;
    private bool paused;
    private string summary = "", graphIdentity = "";
    private readonly Dictionary<string, Step> knownSteps = new(StringComparer.Ordinal);
    private readonly List<Step> currentSteps = new();
    private readonly List<(long revision, Step step)> retired = new();
    private readonly List<Segment> segments = new();
    private readonly Dictionary<AIActionType, (long success, long blocked)> counts = new();
    private readonly List<GoalActionEvidence> evidence = new();

    public GoalWorkingPlanView? WorkingPlan => graphRevision == 0 ? null : new(start, graphRevision, graphAcceptedAt, summary,
        Array.AsReadOnly(currentSteps.Select(step => step.View()).ToArray()),
        Array.AsReadOnly(retired.Select(entry => new GoalSupersededStep(entry.revision, entry.step.View())).ToArray()),
        supersededCount - retired.Count);
    public GoalExecutionPlanView? AcceptedExecutionPlan => segments.Count == 0 ? null : View(segments[^1]);
    public GoalExecutionHistory ExecutionPlans => new(totalPlans, totalPlans - segments.Count,
        Array.AsReadOnly(segments.Select(plan => new GoalExecutionSummary(plan.Revision, plan.AcceptedAt, plan.Status,
            Excerpt(plan.Reason, 180), plan.Reason.Length > 180, plan.Tasks.Count(task => task.Status == "succeeded"),
            plan.Tasks.Count(task => task.Status == "blocked"), plan.Tasks.Count(task => task.OutcomeSequence == null), plan.SupersededBy)).ToArray()));
    public GoalActionEvidenceView Evidence => new(start, lastOutcome, Sum(true), Sum(false),
        counts.GetValueOrDefault(AIActionType.Inspect).success, counts.GetValueOrDefault(AIActionType.Inspect).blocked,
        Array.AsReadOnly(counts.OrderBy(pair => pair.Key).Select(pair => new GoalActionCounts(pair.Key.ToString().ToLowerInvariant(), pair.Value.success, pair.Value.blocked)).ToArray()),
        Array.AsReadOnly(evidence.ToArray()), physicalOutcomes - evidence.Count);

    public bool ValidateDecision(AIPlan decision, out string error)
    {
        error = "";
        var graph = decision.WorkingPlan;
        var proposedIds = new HashSet<string>(StringComparer.Ordinal);
        if (graph != null)
        {
            if (graph.Steps == null || graph.Steps.Count is < 1 or > MaxSteps)
            { error = "working_plan requires 1-12 measured-purpose steps."; return false; }
            foreach (WorkingPlanStep? step in graph.Steps)
            {
                if (step == null || string.IsNullOrWhiteSpace(step.Id) || string.IsNullOrWhiteSpace(step.Objective) ||
                    step.DependsOn == null || !proposedIds.Add(step.Id))
                { error = "working_plan has an empty/duplicate step identity, purpose or dependency list."; return false; }
            }
            if (knownSteps.Count + proposedIds.Count(id => !knownSteps.ContainsKey(id)) > MaxKnownSteps)
            { error = "This goal has reached its bounded step-identity history limit."; return false; }
            var proposal = graph.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
            bool Visit(string id)
            {
                if (visited.Contains(id)) return true;
                if (!visiting.Add(id)) return false;
                foreach (string dependency in proposal[id].DependsOn)
                    if (!proposal.ContainsKey(dependency) || !Visit(dependency)) return false;
                visiting.Remove(id); visited.Add(id); return true;
            }
            if (graph.Steps.Any(step => step.DependsOn.Count != step.DependsOn.Distinct(StringComparer.Ordinal).Count()) || !proposedIds.All(Visit))
            { error = "working_plan has duplicate/missing/cyclic dependencies."; return false; }
        }
        AIPlan[] tasks = DecisionTasks(decision).ToArray();
        foreach (AIPlan task in tasks)
            if (task.PlanStep.Length > 0 && (graph != null ? !proposedIds.Contains(task.PlanStep) : !currentSteps.Any(step => step.Id == task.PlanStep)))
            { error = "Task plan_step must reference this decision's replacement graph, or the current graph when none is proposed."; return false; }

        string[] stages = tasks.Where(task => Physical(task.Action) && task.PlanStep.Length > 0).Select(task => task.PlanStep).ToArray();
        if (stages.Length < 2) return true;
        var dependencies = graph != null
            ? graph.Steps.ToDictionary(step => step.Id, step => (IReadOnlyList<string>)step.DependsOn, StringComparer.Ordinal)
            : currentSteps.ToDictionary(step => step.Id, step => (IReadOnlyList<string>)step.Dependencies, StringComparer.Ordinal);
        bool DependsOn(string step, string prerequisite) => dependencies[step].Any(id => id == prerequisite || DependsOn(id, prerequisite));
        for (int earlier = 0; earlier < stages.Length - 1; earlier++)
        for (int later = earlier + 1; later < stages.Length; later++)
            if (DependsOn(stages[earlier], stages[later]))
            {
                error = $"tasks中步骤{stages[earlier]}依赖{stages[later]}，却排在它前面。请按依赖修订本次行动段。";
                return false;
            }
        return true;
    }

    public void AdoptDecision(AIPlan decision, long sequence, IReadOnlyList<AIPlan> queue)
    {
        if (sequence < start || !ValidateDecision(decision, out _)) return;
        if (decision.WorkingPlan is { } graph)
        {
            string identity = JsonSerializer.Serialize(graph);
            if (identity != graphIdentity)
            {
                graphRevision++; graphAcceptedAt = sequence; graphIdentity = identity; summary = graph.Summary;
                var previous = currentSteps.ToDictionary(step => step.Id, StringComparer.Ordinal);
                var proposed = graph.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
                foreach (Step old in currentSteps.Where(step => !proposed.TryGetValue(step.Id, out WorkingPlanStep? next) || !SameDefinition(step, next)))
                    Retire(old);
                currentSteps.Clear();
                foreach (WorkingPlanStep proposal in graph.Steps)
                {
                    if (!previous.TryGetValue(proposal.Id, out Step? step) || !SameDefinition(step, proposal))
                        step = new Step { Id = proposal.Id, Objective = proposal.Objective, Criteria = proposal.SuccessCriteria,
                            Dependencies = proposal.DependsOn.ToArray(), Revision = graphRevision };
                    knownSteps[proposal.Id] = step;
                    currentSteps.Add(step);
                }
            }
        }
        if (decision.KeepTasks || decision.Hold || decision.WaitForPlayer || decision.Action == AIActionType.Stop ||
            !decision.ReplaceTasks && decision.Steps.Count == 0 && decision.Action == AIActionType.None)
        { RefreshActivity(); return; }
        long revision = ++totalPlans;
        if (segments.Count > 0)
        {
            Segment old = segments[^1]; old.SupersededBy = revision;
            if (old.Tasks.Any(task => task.OutcomeSequence == null)) old.Status = "superseded";
        }
        var segment = new Segment { Revision = revision, AcceptedAt = sequence, Reason = decision.Reason };
        foreach (AIPlan task in queue.Take(AIPlanParser.MaxTasks))
        {
            TaskEntry? continued = segment.Tasks.Count == 0 && segments.Count > 0 ? segments[^1].Tasks.FirstOrDefault(entry =>
                entry.OutcomeSequence == null && ReferenceEquals(entry.DispatchTask, task)) : null;
            segment.Tasks.Add(new TaskEntry { Task = continued?.Task ?? Snapshot(task), DispatchTask = task,
                GraphRevision = continued?.GraphRevision ?? graphRevision,
                BoundStep = continued != null ? continued.BoundStep : knownSteps.GetValueOrDefault(task.PlanStep) });
        }
        if (segment.Tasks.Count == 0) segment.Status = "finished_actions";
        else segment.Tasks[0].Status = paused ? "paused" : "running";
        segments.Add(segment); if (segments.Count > MaxPlans) segments.RemoveAt(0);
        RefreshActivity();
    }

    public void RecordOutcome(AgentOutcome outcome, IReadOnlyList<AIPlan> remaining)
    {
        if (outcome.Sequence <= start || outcome.Sequence <= lastOutcome) return;
        lastOutcome = outcome.Sequence;
        var value = counts.GetValueOrDefault(outcome.Task.Action);
        if (outcome.Status == "succeeded") value.success++; else if (outcome.Status == "blocked") value.blocked++;
        counts[outcome.Task.Action] = value;
        if (Physical(outcome.Task.Action))
        {
            physicalOutcomes++;
            evidence.Add(new GoalActionEvidence(outcome.Sequence, Snapshot(outcome.Task), outcome.Status,
                Excerpt(outcome.Result, 360), outcome.Result.Length > 360));
            if (evidence.Count > MaxEvidence) evidence.RemoveAt(0);
        }
        string identity = PlanTaskIdentity.Fingerprint(outcome.Task);
        foreach (Segment plan in segments.AsEnumerable().Reverse())
        {
            TaskEntry? entry = plan.Tasks.FirstOrDefault(task => task.OutcomeSequence == null && task.Task.identity == identity && task.Task.plan_step == outcome.Task.PlanStep);
            if (entry == null) continue;
            entry.Status = outcome.Status; entry.OutcomeSequence = outcome.Sequence;
            if (entry.BoundStep is { } step)
            {
                if (outcome.Status == "blocked") step.Blocked++;
                else if (outcome.Task.Action == AIActionType.Inspect) step.Inspections++;
                else if (Physical(outcome.Task.Action)) step.Successes++;
                step.LastStatus = outcome.Status; step.LastSequence = outcome.Sequence;
            }
            if (Physical(outcome.Task.Action))
                evidence[^1] = evidence[^1] with { workingPlanRevision = entry.GraphRevision, planStepRevision = entry.BoundStep?.Revision };
            foreach (TaskEntry pending in plan.Tasks.Where(task => task.OutcomeSequence == null))
                pending.Status = outcome.Status == "blocked" ? "pending_reconsideration" : remaining.Any(task =>
                    PlanTaskIdentity.Fingerprint(task) == pending.Task.identity && task.PlanStep == pending.Task.plan_step) ? "not_started" : "superseded";
            plan.Status = outcome.Status == "blocked" ? "blocked" : plan.Tasks.All(task => task.OutcomeSequence != null) ? "finished_actions" : "awaiting_next_action";
            break;
        }
        RefreshActivity();
    }

    public void SynchronizeQueue(IReadOnlyList<AIPlan> queue, bool hasCurrentPlan)
    {
        if (segments.Count == 0) return;
        Segment plan = segments[^1];
        foreach (TaskEntry entry in plan.Tasks.Where(task => task.Status is "running" or "paused")) entry.Status = "not_started";
        if (hasCurrentPlan && queue.Count > 0)
        {
            string identity = PlanTaskIdentity.Fingerprint(queue[0]);
            TaskEntry? active = plan.Tasks.FirstOrDefault(task => task.OutcomeSequence == null && task.Task.identity == identity && task.Task.plan_step == queue[0].PlanStep);
            if (active != null) { active.Status = paused ? "paused" : "running"; plan.Status = paused ? "paused" : "active"; }
        }
        RefreshActivity();
    }

    public void MarkPaused(bool value)
    {
        paused = value;
        if (segments.Count > 0)
        {
            Segment plan = segments[^1];
            foreach (TaskEntry task in plan.Tasks)
                if (value && task.Status == "running") task.Status = "paused";
                else if (!value && task.Status == "paused") task.Status = "running";
            if (plan.Tasks.Any(task => task.Status is "running" or "paused")) plan.Status = value ? "paused" : "active";
        }
        RefreshActivity();
    }

    public void Reset(long startSequence = 0)
    {
        start = startSequence; graphRevision = graphAcceptedAt = totalPlans = lastOutcome = supersededCount = physicalOutcomes = 0;
        paused = false; summary = graphIdentity = ""; knownSteps.Clear(); currentSteps.Clear(); retired.Clear(); segments.Clear(); counts.Clear(); evidence.Clear();
    }
    private void RefreshActivity()
    {
        foreach (Step step in knownSteps.Values.Concat(retired.Select(entry => entry.step))
            .Concat(segments.SelectMany(segment => segment.Tasks).Select(task => task.BoundStep).OfType<Step>()).Distinct())
            step.Running = step.Paused = false;
        if (segments.Count == 0) return;
        foreach (TaskEntry task in segments[^1].Tasks)
            if (task.BoundStep != null)
            { task.BoundStep.Running |= task.Status == "running"; task.BoundStep.Paused |= task.Status == "paused"; }
    }
    private static bool SameDefinition(Step old, WorkingPlanStep next) => old.Objective == next.Objective &&
        old.Criteria == next.SuccessCriteria && old.Dependencies.ToHashSet(StringComparer.Ordinal).SetEquals(next.DependsOn);
    private void Retire(Step step)
    {
        retired.Add((graphRevision, step)); supersededCount++;
        if (retired.Count <= MaxSteps) return;
        // Keep versions still bound to the current unfinished queue visible even
        // when repeated strategy revisions age out unrelated history.
        int discard = retired.FindIndex(entry => segments.Count == 0 || !segments[^1].Tasks.Any(task =>
            task.OutcomeSequence == null && ReferenceEquals(task.BoundStep, entry.step)));
        retired.RemoveAt(discard >= 0 ? discard : 0);
    }
    private GoalExecutionPlanView View(Segment plan) => new(start, plan.Revision, plan.AcceptedAt, plan.Reason, plan.Status,
        paused, plan.SupersededBy, Array.AsReadOnly(plan.Tasks.Select((task, index) => task.View(index)).ToArray()));
    private long Sum(bool success) => counts.Where(pair => Physical(pair.Key)).Sum(pair => success ? pair.Value.success : pair.Value.blocked);
    private static bool Physical(AIActionType action) => action is not (AIActionType.None or AIActionType.Inspect or AIActionType.Wait or AIActionType.Follow or AIActionType.Stop);
    private static string Excerpt(string text, int length) => text.Length <= length ? text : text[..length];
    private static IEnumerable<AIPlan> DecisionTasks(AIPlan decision) => decision.Steps.Count > 0 ? decision.Steps : decision.Action == AIActionType.None ? Array.Empty<AIPlan>() : new[] { decision };
    private static GoalTaskView Snapshot(AIPlan task) => new(PlanTaskIdentity.Fingerprint(task), task.Action.ToString().ToLowerInvariant(),
        task.Target, task.Item, task.Slot, task.RecipeId, task.Count, task.Blueprint, task.X, task.Y, task.Width, task.Height,
        task.Reason, task.SuccessCriteria, task.PlanStep, task.Design.HasValue);
}
