using System;
using System.Collections.Generic;

namespace TerrariaAITeammate.Common;

/// <summary>Immutable membership of one initially observed resource source; independent of game globals.</summary>
public sealed class DirectedMiningSource
{
    public const int MaximumCells = 256;
    private readonly List<(int X, int Y)> cells = new();
    public IReadOnlyList<(int X, int Y)> Cells => cells;
    public bool Capped { get; private set; }

    public static (int X, int Y)? SelectSeed(int? x, int? y, Func<(int X, int Y)?> automatic)
        => x.HasValue != y.HasValue ? null : x.HasValue ? (x.Value, y!.Value) : automatic();

    public static DirectedMiningSource Capture((int X, int Y) seed, Func<int, int, bool> allowed)
    {
        var source = new DirectedMiningSource();
        if (!allowed(seed.X, seed.Y)) return source;
        var seen = new HashSet<(int, int)> { seed };
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            source.cells.Add(current);
            foreach (var next in new[] { (current.X - 1, current.Y), (current.X + 1, current.Y),
                         (current.X, current.Y - 1), (current.X, current.Y + 1) })
            {
                if (!seen.Add(next) || !allowed(next.Item1, next.Item2)) continue;
                if (source.cells.Count + queue.Count >= MaximumCells) { source.Capped = true; continue; }
                queue.Enqueue(next);
            }
        }
        return source;
    }

    public (int X, int Y)? Next(Func<int, int, bool> stillPresent)
    {
        foreach (var cell in cells) if (stillPresent(cell.X, cell.Y)) return cell;
        return null;
    }
}
