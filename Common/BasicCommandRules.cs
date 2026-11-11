using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TerrariaAITeammate.Common;

public sealed record AttackCommandTarget(bool Valid, bool Nearest, int? NpcType);

/// <summary>Command boundaries only; these rules do not choose a route or a world task.</summary>
public static class BasicCommandRules
{
    public static bool MayAutoDefend(bool held, AIActionType action, bool hasCurrentPlan, bool hasPendingTasks, bool hasGoal)
        => !held && action == AIActionType.Follow && !hasCurrentPlan && !hasPendingTasks && !hasGoal;

    public static AttackCommandTarget ResolveAttackTarget(string query, int npcTypeCount, Func<string, int?> resolveName)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0 || query.Equals("nearest", StringComparison.OrdinalIgnoreCase)) return new(true, true, null);
        int? type = int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out int numeric)
            ? numeric : resolveName(query);
        return type is > 0 && type < npcTypeCount ? new(true, false, type) : new(false, false, null);
    }

    public static bool PickupTargetStillMatches(bool active, bool isAir, bool queryMatches, int actualType, int selectedType, int stack)
        => active && !isAir && queryMatches && actualType == selectedType && stack > 0;
}

public sealed record PickupAmount(int ItemType, int Amount);

/// <summary>Only actual ground-to-inventory transfers contribute; prior inventory is not counted.</summary>
public sealed class PickupProgress
{
    private readonly Dictionary<int, int> amounts = new();
    public int? RequestedCount { get; }
    public int TotalAdded { get; private set; }
    public int? RemainingCount => RequestedCount.HasValue ? Math.Max(0, RequestedCount.Value - TotalAdded) : null;
    public bool QuotaReached => RemainingCount == 0;
    public IReadOnlyList<PickupAmount> Amounts => amounts.OrderBy(pair => pair.Key).Select(pair => new PickupAmount(pair.Key, pair.Value)).ToArray();

    public PickupProgress(int requestedCount)
    {
        if (requestedCount < 0 || requestedCount > 999) throw new ArgumentOutOfRangeException(nameof(requestedCount));
        RequestedCount = requestedCount > 0 ? requestedCount : null;
    }

    public int RequestFromStack(int stack) => Math.Max(0, Math.Min(stack, RemainingCount ?? stack));

    public void RecordTransfer(int itemType, int actualAdded)
    {
        if (itemType <= 0 || actualAdded <= 0 || actualAdded > (RemainingCount ?? int.MaxValue))
            throw new ArgumentException("Pickup records must be actual positive transfers within the remaining command quota.");
        amounts[itemType] = checked(amounts.GetValueOrDefault(itemType) + actualAdded);
        TotalAdded = checked(TotalAdded + actualAdded);
    }
}
