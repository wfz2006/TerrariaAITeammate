using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrariaAITeammate.Common;

/// <summary>Task-local directed-edge progress. Jumping/jitter and a fresh graph do not renew the same failed approach.</summary>
public sealed class NavigationEdgeProgress
{
    private readonly Dictionary<(int, int, int, int), (float Best, int Idle)> edges = new();
    public int NoProgressTicks { get; private set; }
    public bool Observe(int fromX, int fromY, int toX, int toY, float remainingPixels)
    {
        if (!float.IsFinite(remainingPixels) || remainingPixels < 0) return false;
        var key = (fromX, fromY, toX, toY);
        if (!edges.TryGetValue(key, out var value))
        {
            if (edges.Count >= 256) edges.Remove(edges.First().Key);
            value = (remainingPixels, 0);
        }
        else if (remainingPixels <= value.Best - 2)
            value = (remainingPixels, 0);
        else value.Idle++;
        edges[key] = value; NoProgressTicks = value.Idle;
        return value.Idle <= 90;
    }
    public void Reached(int fromX, int fromY, int toX, int toY)
    { edges.Remove((fromX, fromY, toX, toY)); NoProgressTicks = 0; }
    public void ForgetPoint(int x, int y)
    {
        foreach (var key in edges.Keys.Where(key => key.Item1 == x && key.Item2 == y || key.Item3 == x && key.Item4 == y).ToArray())
            edges.Remove(key);
    }
    public void Reset() { edges.Clear(); NoProgressTicks = 0; }
}

public static class NavigationTravelBudget
{
    public static bool TargetNeedsRefresh(float distance, bool routeRemaining)
        => !float.IsFinite(distance) || distance > (routeRemaining ? 32f : 0.5f);

    public static int TicksForDistance(float pixels)
    {
        if (float.IsNaN(pixels) || pixels < 0) return 180 * 60;
        // Conservative walking speed and detour allowance, capped at fifteen minutes.
        return (int)Math.Clamp(Math.Ceiling(pixels / 3d * 2d + 1800d), 180 * 60, 900 * 60);
    }
}
