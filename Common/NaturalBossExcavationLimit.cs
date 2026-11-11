using System.Collections.Generic;

namespace TerrariaAITeammate.Common;

internal sealed class NaturalBossExcavationLimit
{
    public const int MaximumCells = 8;
    private readonly HashSet<(int X, int Y)> attempted = new();

    public int AttemptedCount => attempted.Count;

    public static bool IsAuxiliary(AIPlan? plan) =>
        plan is { Action: AIActionType.Mine, Target: "stone" or "dirt" or "grass" };

    public bool Allows(AIPlan plan, int x, int y) => IsAuxiliary(plan) &&
        plan.Count == 1 && plan.X == x && plan.Y == y &&
        (attempted.Contains((x, y)) || attempted.Count < MaximumCells);

    public bool TryReserve(AIPlan plan, int x, int y)
    {
        if (!Allows(plan, x, y)) return false;
        attempted.Add((x, y));
        return true;
    }
}
