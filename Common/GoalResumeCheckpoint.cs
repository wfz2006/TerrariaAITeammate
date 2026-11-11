using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerrariaAITeammate.Common;

internal sealed record SavedGoalAction(string Type, string Item, string Status, string Detail);

internal sealed record SavedGoalHistory(string StatusAtSave, SavedGoalAction[] RecentActions)
{
    public bool Historical => true;
    public bool CompletionProven => false;
}

/// <summary>Goal requirements and advice for a fresh review. This schema carries no executable state.</summary>
internal sealed record GoalResumeCheckpoint(int Schema, int WorldId, Guid WorldFileId, string Objective, string? PlayerRequest,
    string Criteria, string CompletionAction, string CompletionTarget, GoalCondition[] Conditions, WorkingPlanProposal? WorkingPlan,
    SavedGoalHistory History)
{
    private const int Version = 1;
    private const int MaxActions = 8;
    private const int MaxJsonLength = 128 * 1024;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OrderedGoalProgress? OrderedProgress { get; init; }

    public static GoalResumeCheckpoint Capture(int worldId, Guid worldFileId, string objective, string criteria,
        string completionAction, string completionTarget, string status, GoalPlanningState planning, SavedGoalHistory? previousHistory)
    {
        var work = planning.WorkingPlan;
        var graph = work == null ? null : new WorkingPlanProposal
        {
            Summary = work.summary,
            Steps = work.steps.Select(step => new WorkingPlanStep
            {
                Id = step.id, Objective = step.objective, SuccessCriteria = step.success_criteria, DependsOn = step.depends_on.ToList()
            }).ToList()
        };
        var recovery = planning.Recovery;
        var actions = recovery.recentSuccesses.Concat(recovery.recentFailures).OrderBy(action => action.sequence)
            .Select(action => new SavedGoalAction(action.task.type, action.task.item, action.status, action.detail));
        var history = (previousHistory?.RecentActions ?? Array.Empty<SavedGoalAction>()).Concat(actions).TakeLast(MaxActions).ToArray();
        return new(Version, worldId, worldFileId, objective, planning.SourcePlayerRequest, criteria, completionAction, completionTarget,
            planning.Completion.Conditions.ToArray(), graph, new SavedGoalHistory(status, history))
        { OrderedProgress = planning.Completion.CaptureOrderedProgress() };
    }

    public string? Encode()
    {
        if (WorldFileId == Guid.Empty) return null;
        string data = JsonSerializer.Serialize(this);
        return data.Length <= MaxJsonLength ? data : null;
    }

    public static GoalResumeCheckpoint? Decode(string data, int worldId, Guid worldFileId)
    {
        if (string.IsNullOrWhiteSpace(data) || data.Length > MaxJsonLength || worldFileId == Guid.Empty) return null;
        GoalResumeCheckpoint? checkpoint;
        try { checkpoint = JsonSerializer.Deserialize<GoalResumeCheckpoint>(data); }
        catch (JsonException) { return null; }
        if (checkpoint == null || checkpoint.Schema != Version || checkpoint.WorldId != worldId || checkpoint.WorldFileId != worldFileId ||
            string.IsNullOrWhiteSpace(checkpoint.Objective) || checkpoint.Criteria == null || checkpoint.CompletionAction == null ||
            checkpoint.CompletionTarget == null || checkpoint.Conditions == null ||
            checkpoint.History?.StatusAtSave is not ("active" or "paused" or "waiting_for_player") ||
            checkpoint.History.RecentActions is not { Length: <= MaxActions } ||
            checkpoint.History.RecentActions.Any(action => action == null || action.Type == null || action.Item == null ||
                action.Detail == null || action.Status is not ("succeeded" or "blocked")))
            return null;
        var validation = new GoalPlanningState();
        if (!validation.Completion.TryRevise(checkpoint.Conditions, "", out _) ||
            !validation.ValidateDecision(new AIPlan { WorkingPlan = checkpoint.WorkingPlan }, out _))
            return null;
        return checkpoint.OrderedProgress is { } progress && !validation.Completion.RestoreOrderedProgress(progress)
            ? checkpoint with { OrderedProgress = null }
            : checkpoint;
    }

    public void RestorePlanning(GoalPlanningState planning, long sequence)
    {
        planning.Completion.TryRevise(Conditions, "", out _);
        if (OrderedProgress is { } progress) planning.Completion.RestoreOrderedProgress(progress);
        if (WorkingPlan != null)
            planning.AdoptDecision(new AIPlan { WorkingPlan = WorkingPlan }, sequence, Array.Empty<AIPlan>());
        planning.MarkPaused(true);
    }

    public static bool IsExplicitContinue(string message) => message.Trim().TrimEnd('。', '！', '.', '!') is
        "继续" or "继续任务" or "继续之前的任务" or "继续上次任务" or "resume";
}
