using System.Collections.Generic;

namespace TerrariaAITeammate.Common;

/// <summary>Tracks candidate-local approach stalls separately from task-wide progress and failed candidates.</summary>
public sealed class MiningApproachBudget
{
    private float bestDistance = float.PositiveInfinity;
    private readonly HashSet<(int X, int Y)> reachedWaypoints = new();
    public int IdleTicks { get; private set; }
    public void Reset()
    {
        IdleTicks = 0;
        bestDistance = float.PositiveInfinity;
        reachedWaypoints.Clear();
    }
    public bool Tick(float distance)
    {
        // Compare with the closest point ever reached, not the preceding frame: jitter
        // and repeated backtracking must not renew the allowance.
        if (float.IsPositiveInfinity(bestDistance)) bestDistance = distance;
        if (distance < bestDistance - 2f)
        {
            bestDistance = distance;
            IdleTicks = 0;
        }
        else IdleTicks++;
        return IdleTicks > 140;
    }
    public void ReachedWaypoint(int x, int y)
    {
        // Candidate-local history survives replanning; replaying a route is not progress.
        if (reachedWaypoints.Add((x, y))) IdleTicks = 0;
    }
}
