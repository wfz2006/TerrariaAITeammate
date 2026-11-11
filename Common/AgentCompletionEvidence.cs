using System.Collections.Generic;
using System.Linq;

namespace TerrariaAITeammate.Common;

public static class AgentCompletionEvidence
{
    public static bool CanComplete(IReadOnlyList<AgentOutcome> outcomes, long goalStartSequence, string completionAction)
    {
        // Kept for historical callers. Action success alone cannot verify a goal or bypass a pending contract.
        return false;
    }

    public static GoalCompletionEvaluation Evaluate(IReadOnlyList<GoalCondition> conditions, GoalEvidenceSnapshot snapshot)
    {
        var evaluated = new List<GoalConditionEvaluation>();
        for (int index = 0; index < conditions.Count; index++)
        {
            if (!GoalCompletionState.TryNormalize(conditions[index], out GoalCondition condition, out string error))
            { evaluated.Add(new(index, condition.Type, "unverified", error)); continue; }
            bool? met = null;
            string detail = "本轮尚无此条件的原生事实。";
            switch (condition.Type)
            {
                case "inventory_at_least":
                    if (snapshot.Inventory != null)
                    {
                        int actual = snapshot.Inventory.TryGetValue(condition.ItemId!.Value, out int n) ? n : 0;
                        met = actual >= condition.Count;
                        detail = $"小星实际库存{actual}，要求至少{condition.Count}。";
                    }
                    break;
                case "near_player":
                    if (snapshot.HorizontalPixels is float h && snapshot.VerticalPixels is float v &&
                        float.IsFinite(h) && float.IsFinite(v) && h >= 0 && v >= 0)
                    {
                        met = h <= condition.HorizontalPixels && v <= condition.VerticalPixels;
                        detail = $"实际水平{h:0.###}/竖向{v:0.###}像素，要求≤{condition.HorizontalPixels}/≤{condition.VerticalPixels}。";
                    }
                    break;
                case "equipped":
                    if (snapshot.Equipped != null)
                    { met = snapshot.Equipped.Contains(condition.ItemId!.Value); detail = met.Value ? "目标物品实际已装备。" : "目标物品当前未装备。"; }
                    break;
                case "boss_defeated":
                case "boss_loot":
                    var bossFacts = condition.Type == "boss_defeated" ? snapshot.BossDefeats : snapshot.BossLootPickups;
                    if (bossFacts != null)
                    {
                        int actual = bossFacts.TryGetValue(condition.NpcType!.Value, out int n) ? n : 0;
                        met = actual > 0;
                        detail = $"本目标边界内实际{(condition.Type == "boss_defeated" ? "击败证据" : "标记战利品拾取")}次数{actual}。";
                    }
                    break;
                case "housing_valid" or "new_housing_valid" when !condition.X.HasValue:
                    detail = "住房验收位置尚未绑定，不能把模板/技能成功当成原生住房证据。";
                    break;
                case "visited_location" when !condition.X.HasValue:
                    detail = "到访地点尚未绑定；查询位置、图路径或当前在玩家身边都不能证明去过目标。";
                    break;
                case "visited_location":
                case "area_matches":
                case "housing_valid":
                case "new_housing_valid":
                case "structure_preserved":
                case "ordered_actions":
                    if (snapshot.NativeConditions.TryGetValue(index, out GoalNativeConditionResult? native))
                    { met = native.Met; detail = native.Detail; }
                    break;
                case "unverified":
                    detail = condition.Description;
                    break;
            }
            evaluated.Add(new(index, condition.Type, met.HasValue ? met.Value ? "met" : "unmet" : "unverified", detail));
        }
        bool verified = evaluated.Count > 0 && evaluated.All(result => result.Status == "met");
        return new GoalCompletionEvaluation
        {
            Verified = verified, SpecCount = evaluated.Count, Conditions = evaluated,
            Status = verified ? "verified" : evaluated.Count == 0 ? "pending" : evaluated.Any(result => result.Status == "unverified") ? "unverified" : "unmet"
        };
    }
}
