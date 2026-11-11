using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace TerrariaAITeammate.Common;

public sealed record MiningResourceCandidate(Point Seed, int TileType, float Score);
public sealed record MiningCandidateObservation(MiningResourceCandidate Candidate, MiningApproachProbeResult Approach);
public sealed record MiningCandidateSelectionResult(string Status, int ObservedCandidateCount, int ConsideredCandidateCount,
    int RouteProbeCount, MiningResourceCandidate? Selected, MiningApproachProbeResult? SelectedApproach,
    IReadOnlyList<MiningCandidateObservation> Observations)
{
    public bool CandidatesTruncated => ObservedCandidateCount > ConsideredCandidateCount;
    public int GeometryRejectedCandidateCount { get; init; }
    public int GeometricCandidateCount { get; init; }
    public bool CheapCheckLimitReached { get; init; }
    public bool ObservationSamplesTruncated => ConsideredCandidateCount > Observations.Count;
}

/// <summary>One selection's finite graph work. Sharing a stand across nearby seeds never repeats its route search.</summary>
public sealed class MiningRouteProbeCache
{
    public const int MaximumRouteProbes = 32;
    private readonly Dictionary<Point, MiningApproachRoute> routes = new();
    private readonly Dictionary<(Vector2 From, Vector2 Origin), MiningReturnCheck> returns = new();
    public int Count => routes.Count + returns.Count;
    public int OutboundCount => routes.Count;
    public int ReturnCount => returns.Count;
    public bool Exhausted => Count >= MaximumRouteProbes;
    public bool CanProbe(Point node) => routes.ContainsKey(node) || !Exhausted;
    public MiningApproachRoute GetOrFind(Vector2 actual, MiningApproachStand stand,
        Func<Vector2, MiningApproachStand, MiningApproachRoute> find)
    {
        if (routes.TryGetValue(stand.Node, out MiningApproachRoute? cached)) return cached;
        if (Exhausted) return new(null, "shared_auto_route_budget_exhausted");
        MiningApproachRoute route = find(actual, stand);
        routes.Add(stand.Node, route);
        return route;
    }
    public MiningReturnCheck GetOrFindReturn(Vector2 from, Vector2 origin, Func<Vector2, MiningReturnCheck> find)
    {
        if (returns.TryGetValue((from, origin), out MiningReturnCheck? cached)) return cached;
        if (Exhausted) return new("shared_route_budget_exhausted", "Forward and return graph searches share the same32-query bound; return remains unknown.");
        MiningReturnCheck result = find(from);
        returns.Add((from, origin), result);
        return result;
    }
}

/// <summary>Selects within the caller's existing observed scan, never generates a goal or extends its region.</summary>
public static class MiningCandidateSelection
{
    public const int MaximumCandidates = 12;
    public const int MaximumGeometryChecks = 512;
    public const int MaximumCheapFailureSamples = 12;
    public const int SpatialBucketSize = 8;

    public static MiningCandidateSelectionResult Select(IReadOnlyList<MiningResourceCandidate> candidates,
        Func<MiningResourceCandidate, MiningWorkStandSelection, int, MiningApproachProbeResult> probe, Func<int> routeCount)
    {
        // One nearest face per spatial bucket before returning to adjacent cells in the same face.
        var buckets = candidates.OrderBy(c => c.Score).ThenBy(c => c.Seed.X).ThenBy(c => c.Seed.Y)
            .GroupBy(c => ((int)MathF.Floor(c.Seed.X / (float)SpatialBucketSize), (int)MathF.Floor(c.Seed.Y / (float)SpatialBucketSize)))
            .Select(group => new Queue<MiningResourceCandidate>(group)).ToArray();
        var selected = new List<(MiningResourceCandidate Candidate, MiningWorkStandSelection Budget, int Observation)>();
        var observations = new List<MiningCandidateObservation>();
        int considered = 0, geometricRejected = 0, cheapSamples = 0;
        MiningCandidateSelectionResult Result(string status, MiningResourceCandidate? chosen = null, MiningApproachProbeResult? approach = null)
            => new(status, candidates.Count, considered, routeCount(), chosen, approach, observations.ToArray())
            {
                GeometryRejectedCandidateCount = geometricRejected, GeometricCandidateCount = selected.Count,
                CheapCheckLimitReached = considered >= MaximumGeometryChecks && considered < candidates.Count
            };
        while (selected.Count < MaximumCandidates && considered < MaximumGeometryChecks)
        {
            bool any = false;
            foreach (var bucket in buckets)
            {
                if (bucket.Count == 0) continue;
                MiningResourceCandidate candidate = bucket.Dequeue(); any = true; considered++;
                var budget = new MiningWorkStandSelection();
                MiningApproachProbeResult approach = probe(candidate, budget, 2);
                if (!approach.ActualInToolReach && approach.CandidateCount == 0)
                {
                    // No legal geometric stand (or a failed non-route prerequisite)
                    // consumes a cheap check, not one of the graph-eligible slots.
                    geometricRejected++;
                    if (cheapSamples++ < MaximumCheapFailureSamples) observations.Add(new(candidate, approach));
                }
                else
                {
                    selected.Add((candidate, budget, observations.Count));
                    observations.Add(new(candidate, approach));
                    if (approach.CanApproach) return Result("approachable_candidate_found", candidate, approach);
                }
                if (selected.Count == MaximumCandidates || considered == MaximumGeometryChecks) break;
            }
            if (!any) break;
        }
        foreach (int limit in new[] { 4, 8, MiningWorkStandSelection.MaximumProbes })
            for (int i = 0; i < selected.Count; i++)
            {
                var candidate = selected[i];
                MiningApproachProbeResult previous = observations[candidate.Observation].Approach;
                if (previous.ActualInToolReach || previous.TotalProbeCount >= previous.CandidateCount) continue;
                MiningApproachProbeResult approach = probe(candidate.Candidate, candidate.Budget, limit);
                observations[candidate.Observation] = new(candidate.Candidate, approach);
                if (approach.CanApproach)
                    return Result("approachable_candidate_found", candidate.Candidate, approach);
            }
        return Result(candidates.Count == 0 ? "no_allowed_candidates_in_scan" :
            routeCount() >= MiningRouteProbeCache.MaximumRouteProbes ? "bounded_route_budget_exhausted" :
            considered >= MaximumGeometryChecks && considered < candidates.Count ? "bounded_geometry_check_limit" : "no_approach_in_considered_candidates");
    }
}
