using System;
using System.Linq;

namespace TerrariaAITeammate.Common;

public sealed record MiningTreeSourceCheck(DirectedMiningSource Source, string? Failure)
{
    public bool Safe => Failure == null;
}

/// <summary>Observe the bounded whole same-type source first; protected members must not vanish from cascade safety checks.</summary>
public static class MiningTreeSourceSafety
{
    public static MiningTreeSourceCheck Inspect((int X, int Y) seed, Func<int, int, bool> sameType,
        Func<int, int, bool> canHarvest)
    {
        DirectedMiningSource source = DirectedMiningSource.Capture(seed, sameType);
        string? failure = source.Cells.Count == 0 ? "native_tree_source_seed_changed" : source.Capped
            ? "native_tree_source_exceeds_initial_safety_bound" : source.Cells.Any(c => !canHarvest(c.X, c.Y))
            ? "native_tree_source_contains_protected_or_native_refused_member" : null;
        return new(source, failure);
    }
}
