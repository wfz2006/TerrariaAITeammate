using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace TerrariaAITeammate.Common;

public sealed record MiningApproachStand(Point Node, Vector2 Center, float FeetY);
public sealed record MiningApproachRoute(int? WaypointCount, string Diagnostic, MiningApproachStand? Endpoint = null);
public sealed record MiningApproachFailure(MiningApproachStand Stand, string Reason, string Diagnostic);
public sealed record MiningApproachGeometry(float ActualDistancePixels, bool ActualInToolReach,
    IReadOnlyList<MiningApproachStand> Candidates, int GridChecks);
public sealed record MiningApproachProbeResult(string Status, bool ActualInToolReach, float ActualDistancePixels,
    int CandidateCount, int NewProbeCount, int TotalProbeCount, MiningApproachStand? SelectedStand,
    MiningApproachRoute? SelectedRoute, IReadOnlyList<MiningApproachFailure> Failures)
{
    public MiningReturnCheck? ReturnCheck { get; init; }
    public bool CanApproach => (ActualInToolReach || SelectedStand != null) && (ReturnCheck == null || ReturnCheck.ProvenInCurrentGraph);
}

/// <summary>Same bounded work-stand selection for actual mining and a read-only preview; owns no live state.</summary>
public static class MiningApproachProbe
{
    public const float ToolReachPixels = 5.5f * 16f;
    public const int RadiusX = 6, RadiusY = 7;
    public const int MaximumGeometryChecks = (RadiusX * 2 + 1) * (RadiusY * 2 + 1);

    public static MiningApproachGeometry EnumerateGeometry(Vector2 actualCenter, int bodyHeight, Vector2 targetCenter,
        Func<int, int, bool> isSafeStand, Func<Point, int, MiningApproachStand> describeStand)
    {
        var candidates = new List<MiningApproachStand>();
        int tx = (int)(targetCenter.X / 16f), ty = (int)(targetCenter.Y / 16f), checks = 0;
        for (int x = tx - RadiusX; x <= tx + RadiusX; x++)
            for (int y = ty - RadiusY; y <= ty + RadiusY; y++)
            {
                checks++;
                if (!isSafeStand(x, y)) continue;
                MiningApproachStand stand = describeStand(new Point(x, y), bodyHeight);
                if (Vector2.Distance(stand.Center, targetCenter) <= ToolReachPixels) candidates.Add(stand);
            }
        float actualDistance = Vector2.Distance(actualCenter, targetCenter);
        return new(actualDistance, actualDistance <= ToolReachPixels, candidates, checks);
    }

    public static MiningApproachProbeResult Find(Vector2 actualCenter, int bodyHeight, Vector2 targetCenter,
        Func<int, int, bool> isSafeStand, Func<Point, int, MiningApproachStand> describeStand,
        Func<Vector2, MiningApproachStand, MiningApproachRoute> findRoute,
        Func<int, int, bool> tryProbe, Func<int> probeCount,
        int maximumProbes = MiningWorkStandSelection.MaximumProbes, Func<bool>? externalBudgetExhausted = null,
        Func<Vector2, MiningReturnCheck>? checkReturnToOrigin = null)
    {
        float actualDistance = Vector2.Distance(actualCenter, targetCenter);
        if (actualDistance <= ToolReachPixels)
        {
            MiningReturnCheck? back = checkReturnToOrigin?.Invoke(actualCenter);
            return new(back is { ProvenInCurrentGraph: false } ? "actual_tool_reach_return_not_proven" : "already_in_actual_tool_reach",
                true, actualDistance, 0, 0, probeCount(), null, null, Array.Empty<MiningApproachFailure>()) { ReturnCheck = back };
        }

        IReadOnlyList<MiningApproachStand> candidates = EnumerateGeometry(actualCenter, bodyHeight, targetCenter,
            isSafeStand, describeStand).Candidates;

        var failures = new List<MiningApproachFailure>();
        MiningReturnCheck? lastReturn = null;
        int tested = 0;
        foreach (MiningApproachStand stand in candidates.OrderBy(candidate => Vector2.DistanceSquared(actualCenter, candidate.Center)))
        {
            if (!tryProbe(stand.Node.X, stand.Node.Y)) continue;
            tested++;
            MiningApproachRoute route = findRoute(actualCenter, stand);
            float? endpointDistance = route.Endpoint == null ? null : Vector2.Distance(route.Endpoint.Center, targetCenter);
            if (MiningWorkStandSelection.CanUseRoute(false, route.WaypointCount, endpointDistance))
            {
                lastReturn = checkReturnToOrigin?.Invoke(route.Endpoint!.Center);
                if (lastReturn is { ProvenInCurrentGraph: false })
                { failures.Add(new(stand, "return_to_task_origin_" + lastReturn.Status, lastReturn.Diagnostic)); continue; }
                return new("work_stand_route_found", false, actualDistance, candidates.Count, tested, probeCount(), route.Endpoint, route, failures) { ReturnCheck = lastReturn };
            }
            failures.Add(new(stand, route.WaypointCount == null ? "no_graph_route" : route.WaypointCount == 0
                ? "empty_projection_outside_actual_tool_reach" : route.Endpoint == null
                ? "route_endpoint_unobserved" : "route_endpoint_outside_tool_reach", route.Diagnostic));
        }

        string status = candidates.Count == 0 ? "no_in_range_work_stand" :
            externalBudgetExhausted?.Invoke() == true ? "shared_route_budget_exhausted" :
            probeCount() >= maximumProbes ? "probe_budget_exhausted" : "no_route_in_tested_stands";
        if (lastReturn is { ProvenInCurrentGraph: false } && status == "no_route_in_tested_stands") status = "return_to_task_origin_not_proven";
        return new(status, false, actualDistance, candidates.Count, tested, probeCount(), null, null, failures) { ReturnCheck = lastReturn };
    }
}
