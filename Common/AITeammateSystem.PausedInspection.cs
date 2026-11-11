using System.Text.RegularExpressions;
using Terraria;
using Microsoft.Xna.Framework;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private static bool IsAnswerWithoutPlanControl(AIPlan plan) => !plan.Resume && !plan.Hold && !plan.WaitForPlayer && !plan.ClearTasks &&
        !plan.GoalDone && (plan.GoalStatus is "" or "active") && !plan.ReplanAfterStep &&
        plan.Action == AIActionType.None && plan.Steps.Count == 0 && string.IsNullOrWhiteSpace(plan.Goal) &&
        plan.WorkingPlan == null && plan.CompletionConditions is not { Count: > 0 } &&
        string.IsNullOrWhiteSpace(plan.SuccessCriteria) && string.IsNullOrWhiteSpace(plan.ContractRevisionReason) &&
        string.IsNullOrWhiteSpace(plan.CompletionAction) && string.IsNullOrWhiteSpace(plan.CompletionTarget);

    private void PublishReadOnlyAnswer(AIPlan plan, string? suffix = null)
    {
        if (!string.IsNullOrWhiteSpace(plan.Remember)) Memory.Remember(plan.Remember);
        if (string.IsNullOrWhiteSpace(plan.Reply)) return;
        LastReply = plan.Reply;
        Main.NewText($"小星：{plan.Reply}{(suffix == null ? "" : " " + suffix)}", Color.LightGreen);
        Memory.Add($"小星：{plan.Reply}");
    }

    private void RecordPausedInspection(AIPlan task, NPC observer)
    {
        string query = task.Item.Trim();
        if (task.Target == "recipe" && task.Count > 1 && !query.Contains('×') &&
            !Regex.IsMatch(query, @"\s+x\s*\d+$", RegexOptions.IgnoreCase)) query += $" x{task.Count}";
        // AgentKnowledge's synchronous readers do not move the actor or alter
        // the executor's active inspection/navigation sessions. Route/sources
        // explicitly report the limits of their bounded compatibility reads.
        string result = StructureWorldObserver.ReadWithoutChangingSelection(() =>
            AgentKnowledge.Query(query, task.Target, observer, Main.LocalPlayer, Executor, task.X, task.Y, task.Design));
        AgentOutcome outcome = agentJournal.Record(task, result, false);
        goalPlanningState.RecordSupplementalObservation(outcome, Main.worldID, WorldKnowledgeObserver.LoadEpoch, Main.GameUpdateCount);
        decisionGate.StateChanged();
        lastGoalResult = result;
        TestLog($"agent paused observation task={task.Action} target={task.Target} item={task.Item} result={result}");
    }
}
