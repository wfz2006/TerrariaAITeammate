using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TerrariaAITeammate.Common;

internal sealed record OrderedGoalProgress(IReadOnlyList<GoalOrderedAction> Actions, int CompletedActions, int CraftedForNext);

/// <summary>Detached, monotone goal contract. It never derives a layout or evidence from model promises.</summary>
public sealed class GoalCompletionState
{
    private static readonly Regex PlayerPreservationClause = new(
        @"(?:^|[，,。！？!?；;\r\n])\s*(?:请\s*)?(?:(?:全程|必须|务必)?\s*(?:保留|保护)|(?:不要|别|不能|不得|禁止)[^，,。！？!?；;\r\n]{0,24}?(?:拆改|改动|修改|拆除|破坏|拆|动))\s*(?:原有?|现有|已有)的?(?:建筑|房屋|屋)(?=$|[\s，,。！？!?；;、]|和|及|与)",
        RegexOptions.CultureInvariant);

    internal static bool PlayerRequiresPreservation(string? request) =>
        !string.IsNullOrWhiteSpace(request) && PlayerPreservationClause.IsMatch(request);

    internal static IReadOnlyList<GoalCondition>? IncludePlayerPreservation(IReadOnlyList<GoalCondition>? proposed, string? request)
    {
        // The player's restriction is binding, but it cannot supply a missing objective.
        if (proposed is not { Count: > 0 } || !PlayerRequiresPreservation(request) ||
            proposed.Any(condition => string.Equals(condition?.Type?.Trim(), "structure_preserved", StringComparison.OrdinalIgnoreCase)))
            return proposed;
        return proposed.Append(new GoalCondition { Type = "structure_preserved" }).ToArray();
    }

    private GoalCondition[] conditions = Array.Empty<GoalCondition>();
    private readonly List<string> reasons = new();
    private IReadOnlyList<GoalOrderedAction>? orderedActions;
    private int nextOrderedAction, craftedForAction;
    public IReadOnlyList<GoalCondition> Conditions => conditions.Select(c => c with { }).ToArray();
    public int Revision { get; private set; }
    public string RevisionReason { get; private set; } = "";
    public IReadOnlyList<string> RevisionHistory => reasons.ToArray();
    public int RevisionHistoryOmitted => Math.Max(0, Revision - reasons.Count);

    public void Clear()
    {
        conditions = Array.Empty<GoalCondition>();
        orderedActions = null;
        nextOrderedAction = craftedForAction = 0;
        reasons.Clear();
        Revision = 0;
        RevisionReason = "";
    }

    public bool TryRevise(IReadOnlyList<GoalCondition>? proposed, string reason, out string error)
    {
        error = "";
        if (proposed == null) return true;
        var next = new GoalCondition[proposed.Count];
        for (int i = 0; i < proposed.Count; i++)
            if (!TryNormalize(proposed[i], out next[i], out error)) return false;
        if (next.Count(condition => condition.Type == "ordered_actions") > 1)
        { error = "每个目标只能有一条ordered_actions。"; return false; }
        if (conditions.Length == next.Length && conditions.Zip(next).All(pair => SameCondition(pair.First, pair.Second))) return true;
        if (conditions.Any(old => !next.Any(candidate => AtLeastAsStrong(old, candidate))))
        { error = "同一目标不能丢弃或弱化已有验收条件；保留原条件后添加/加强，或给未绑定坐标绑定。"; return false; }
        string revisionReason = (reason ?? "").Trim();
        if (conditions.Length > 0 && revisionReason.Length == 0)
        { error = "修改已有验收合同需要contract_revision_reason。"; return false; }
        conditions = next;
        orderedActions ??= next.FirstOrDefault(condition => condition.Type == "ordered_actions")?.Actions;
        Revision++;
        RevisionReason = revisionReason;
        reasons.Add(revisionReason);
        if (reasons.Count > 16) reasons.RemoveAt(0);
        return true;
    }

    public static bool TryNormalize(GoalCondition? raw, out GoalCondition condition, out string error)
    {
        condition = raw == null ? new GoalCondition() : raw with { };
        condition.Type = (condition.Type ?? "").Trim().ToLowerInvariant();
        condition.Description = (condition.Description ?? "").Trim();
        error = "";
        string type = condition.Type;
        bool inventory = type == "inventory_at_least", equipped = type == "equipped", near = type == "near_player";
        bool area = type == "area_matches", housing = type is "housing_valid" or "new_housing_valid", visit = type == "visited_location";
        bool preservation = type == "structure_preserved";
        bool boss = type is "boss_defeated" or "boss_loot", ordered = type == "ordered_actions";
        if (!(inventory || equipped || near || area || housing || visit || preservation || boss || ordered || type == "unverified"))
            error = "不支持的completion condition类型。";
        else if ((!inventory && !equipped && condition.ItemId.HasValue) || (!inventory && condition.Count.HasValue) ||
                 (!near && (condition.HorizontalPixels.HasValue || condition.VerticalPixels.HasValue)) ||
                 (!area && !housing && !visit && (condition.X.HasValue || condition.Y.HasValue)) ||
                 (!area && !housing && (condition.Width.HasValue || condition.Height.HasValue)) ||
                 (!area && (condition.TileType.HasValue || condition.WallType.HasValue)) || (!boss && condition.NpcType.HasValue) ||
                 (!ordered && condition.Actions != null))
            error = "验收条件含不属于该type的参数；不能静默忽略额外要求。";
        else if ((inventory || equipped) && condition.ItemId is not > 0)
            error = "物品验收条件需要正整数item_id。";
        else if (inventory && condition.Count is not > 0)
            error = "inventory_at_least需要正整数count。";
        else if (boss && condition.NpcType is not > 0)
            error = "Boss验收需要正整数npc_type。";
        else if (near)
        {
            float horizontal = condition.HorizontalPixels ?? 48, vertical = condition.VerticalPixels ?? 24;
            if (!float.IsFinite(horizontal) || !float.IsFinite(vertical) || horizontal < 0 || vertical < 0 || horizontal > 48 || vertical > 24)
                error = "near_player阈值必须有限非负，水平不超过48像素、竖向不超过24像素。";
            else { condition.HorizontalPixels = horizontal; condition.VerticalPixels = vertical; }
        }
        else if (visit)
        {
            if (condition.X.HasValue != condition.Y.HasValue || condition.X < 0 || condition.Y < 0)
                error = "visited_location需要合法成对x/y；未选地点时两者都不填，绑定后不能更换地点。";
        }
        else if (area || housing)
        {
            if (condition.X.HasValue != condition.Y.HasValue || condition.X < 0 || condition.Y < 0 ||
                condition.Width.HasValue != condition.Height.HasValue || condition.Width <= 0 || condition.Height <= 0 ||
                (long)(condition.X ?? 0) + (condition.Width ?? 0) > int.MaxValue ||
                (long)(condition.Y ?? 0) + (condition.Height ?? 0) > int.MaxValue)
                error = "原生区域验收需要合法成对x/y、成对正整数width/height，且坐标不能溢出。";
            else if (area && (!condition.X.HasValue || !condition.Width.HasValue ||
                     !condition.TileType.HasValue && !condition.WallType.HasValue || condition.TileType < -1 || condition.WallType < 0))
                error = "area_matches需要x/y/width/height及tile_type(-1表示无方块)和/或wall_type(0表示无墙)。";
        }
        else if (ordered)
        {
            if (!TryNormalizeOrder(condition.Actions, out var actions, out error)) return false;
            condition.Actions = actions;
        }
        else if (type == "unverified" && condition.Description.Length == 0)
            error = "unverified条件需要description说明尚未具备的验收能力。";
        return error.Length == 0;
    }

    private static bool AtLeastAsStrong(GoalCondition old, GoalCondition next)
    {
        if (old.Type != next.Type) return false;
        return old.Type switch
        {
            "inventory_at_least" => old.ItemId == next.ItemId && next.Count >= old.Count,
            "near_player" => next.HorizontalPixels <= old.HorizontalPixels && next.VerticalPixels <= old.VerticalPixels,
            "equipped" => old.ItemId == next.ItemId,
            "visited_location" => !old.X.HasValue || old.X == next.X && old.Y == next.Y,
            "boss_defeated" or "boss_loot" => old.NpcType == next.NpcType,
            "area_matches" => old.X == next.X && old.Y == next.Y && old.Width == next.Width && old.Height == next.Height &&
                (!old.TileType.HasValue || old.TileType == next.TileType) && (!old.WallType.HasValue || old.WallType == next.WallType),
            "housing_valid" or "new_housing_valid" => (!old.X.HasValue || old.X == next.X && old.Y == next.Y) &&
                (old.Width.HasValue ? old.Width == next.Width && old.Height == next.Height
                    : !old.X.HasValue || !next.Width.HasValue),
            "unverified" => old.Description == next.Description,
            "structure_preserved" => true,
            "ordered_actions" => SameOrder(old.Actions, next.Actions),
            _ => false
        };
    }

    private static bool TryNormalizeOrder(IReadOnlyList<GoalOrderedAction>? input,
        out IReadOnlyList<GoalOrderedAction>? actions, out string error)
    {
        actions = null;
        error = "ordered_actions需要2至6个come/craft动作。come可附带至多8种互异的正整数item_id/count；craft需正整数item_id和count。";
        if (input is not { Count: >= 2 and <= 6 }) return false;
        var normalized = new List<GoalOrderedAction>(input.Count);
        foreach (GoalOrderedAction? action in input)
        {
            if (action?.Type == "craft" && action.ItemId is > 0 && action.Count is > 0 && action.Carrying == null)
                normalized.Add(action);
            else if (action?.Type == "come" && action.ItemId == null && action.Count == null)
            {
                var carrying = action.Carrying ?? Array.Empty<GoalCarriedItem>();
                if (carrying.Count > 8 || carrying.Any(item => item == null || item.ItemId <= 0 || item.Count <= 0) ||
                    carrying.Select(item => item.ItemId).Distinct().Count() != carrying.Count) return false;
                normalized.Add(action with { Carrying = Array.AsReadOnly(carrying.OrderBy(item => item.ItemId).ToArray()) });
            }
            else return false;
        }
        actions = normalized.AsReadOnly();
        error = "";
        return true;
    }

    private static bool SameCondition(GoalCondition a, GoalCondition b) => a.Actions == null && b.Actions == null ? a == b :
        (a with { Actions = null }) == (b with { Actions = null }) && SameOrder(a.Actions, b.Actions);

    private static bool SameOrder(IReadOnlyList<GoalOrderedAction>? a, IReadOnlyList<GoalOrderedAction>? b) =>
        a != null && b != null && a.Count == b.Count && a.Zip(b).All(pair =>
            pair.First.Type == pair.Second.Type && pair.First.ItemId == pair.Second.ItemId && pair.First.Count == pair.Second.Count &&
            (pair.First.Carrying ?? Array.Empty<GoalCarriedItem>()).SequenceEqual(pair.Second.Carrying ?? Array.Empty<GoalCarriedItem>()));

    private GoalOrderedAction? NextOrderedAction => orderedActions != null && nextOrderedAction < orderedActions.Count
        ? orderedActions[nextOrderedAction] : null;

    internal OrderedGoalProgress? CaptureOrderedProgress() =>
        orderedActions != null && (nextOrderedAction > 0 || craftedForAction > 0)
            ? new(orderedActions, nextOrderedAction, craftedForAction)
            : null;

    internal bool RestoreOrderedProgress(OrderedGoalProgress progress)
    {
        if (orderedActions == null || nextOrderedAction != 0 || craftedForAction != 0 ||
            !TryNormalizeOrder(progress.Actions, out var savedActions, out _) || !SameOrder(orderedActions, savedActions) ||
            progress.CompletedActions < 0 || progress.CompletedActions > orderedActions.Count || progress.CraftedForNext < 0)
            return false;
        if (progress.CraftedForNext > 0 &&
            (progress.CompletedActions == orderedActions.Count ||
                orderedActions[progress.CompletedActions] is not { Type: "craft", Count: int required } || progress.CraftedForNext >= required))
            return false;
        nextOrderedAction = progress.CompletedActions;
        craftedForAction = progress.CraftedForNext;
        return true;
    }

    internal bool ExpectsOrderedReturn => NextOrderedAction?.Type == "come";

    internal bool RecordOrderedReturn(Func<int, int> inventory)
    {
        if (NextOrderedAction is not { Type: "come", Carrying: { } carrying } || carrying.Any(item => inventory(item.ItemId) < item.Count)) return false;
        nextOrderedAction++;
        return true;
    }

    internal string? OrderedCraftBlockReason(int itemId)
    {
        if (NextOrderedAction is not { } next || next is { Type: "craft" } && next.ItemId == itemId ||
            !orderedActions!.Skip(nextOrderedAction + 1).Any(action => action.Type == "craft" && action.ItemId == itemId)) return null;
        return $"制作物品{itemId}前，须先满足已绑定顺序中的第{nextOrderedAction + 1}步：{Describe(next)}。";
    }

    internal bool RecordOrderedCraft(int itemId, int producedCount)
    {
        if (producedCount <= 0 || NextOrderedAction is not { Type: "craft", Count: int required } next || next.ItemId != itemId) return false;
        craftedForAction += Math.Min(required - craftedForAction, producedCount);
        if (craftedForAction >= required)
        {
            nextOrderedAction++;
            craftedForAction = 0;
        }
        return true;
    }

    internal GoalNativeConditionResult ReadOrderedActions(GoalCondition condition)
    {
        if (!SameOrder(orderedActions, condition.Actions)) return new(null, "当前目标没有此顺序的原生记录。");
        string progress = $"已按顺序完成{nextOrderedAction}/{orderedActions!.Count}个动作。";
        return NextOrderedAction is { } next
            ? new(false, progress + $"下一步：{Describe(next)}。")
            : new(true, progress);
    }

    private string Describe(GoalOrderedAction action) => action.Type == "craft"
        ? $"制作物品{action.ItemId}（已制作{craftedForAction}/{action.Count}）"
        : action.Carrying is { Count: > 0 } carrying
            ? "携带" + string.Join("、", carrying.Select(item => $"物品{item.ItemId}×{item.Count}")) + "走到玩家身边"
            : "走到玩家身边";
}
