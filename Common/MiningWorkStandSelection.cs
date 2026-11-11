using System.Collections.Generic;

namespace TerrariaAITeammate.Common;

/// <summary>One candidate gets bounded, non-repeating route probes; projection alone is not arrival.</summary>
public sealed class MiningWorkStandSelection
{
    public const int MaximumProbes = 16;
    private readonly HashSet<(int X, int Y)> tested = new();
    public int ProbeCount => tested.Count;
    public bool TryProbe(int x, int y) => tested.Count < MaximumProbes && tested.Add((x, y));
    public void Reset() => tested.Clear();
    public static bool CanUseRoute(bool actualInToolReach, int? pathNodeCount, float? endpointDistancePixels = null)
        => actualInToolReach || pathNodeCount is > 0 && endpointDistancePixels.HasValue &&
            float.IsFinite(endpointDistancePixels.Value) && endpointDistancePixels.Value >= 0 &&
            endpointDistancePixels.Value <= MiningApproachProbe.ToolReachPixels;
    public static bool NeedsAlternative(bool actualInToolReach, int? pathNodeCount, int pathIndex)
        => !actualInToolReach && pathNodeCount.HasValue && pathIndex >= pathNodeCount.Value;
}
