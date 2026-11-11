using System.Collections.Generic;

namespace TerrariaAITeammate.Common;

/// <summary>Bounds task stalls and failed candidates. Only actual yield/hits or a newly reached route node renew idle time.</summary>
public sealed class MiningProgressBudget
{
    private readonly HashSet<(int X, int Y)> reachedWaypoints = new();
    public const int MaximumIdleTicks = 600;
    public const int MaximumFailedCandidates = 3;
    public int IdleTicks { get; private set; }
    public int FailedCandidates { get; private set; }
    public bool Exhausted => IdleTicks >= MaximumIdleTicks || FailedCandidates >= MaximumFailedCandidates;
    public void Reset() { IdleTicks = FailedCandidates = 0; reachedWaypoints.Clear(); }
    public void Tick(bool actualProgress) => IdleTicks = actualProgress ? 0 : IdleTicks + 1;
    public bool NoteTravelProgress(int x, int y)
    {
        // Task-local deduplication survives candidate changes and path replanning.
        // Only the execution controller calls this after observing a real arrival.
        if (!reachedWaypoints.Add((x, y))) return false;
        IdleTicks = 0;
        return true;
    }
    public void RejectCandidate() => FailedCandidates++;
}
