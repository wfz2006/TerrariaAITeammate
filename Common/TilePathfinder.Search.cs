using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;

namespace TerrariaAITeammate.Common;

public sealed record TileTransition(Point From, Point To);
public sealed record TilePathSearchLimits(int MaxUniqueExpanded = 30000, int MaxDiscovered = 60000);
public sealed record TilePathSearchSnapshot(string Status, Point? Start, Point? Goal, IReadOnlyList<Point>? Path,
    int UniqueExpanded, int Dequeued, int Discovered, int OpenCount, int MinX, int MaxX, int MinY, int MaxY,
    string Diagnostic, TilePathSearchLimits Limits, Vector2 RequestedStart, Vector2 RequestedTarget)
{
    public bool NeighborExpansionPending { get; init; }
    public TileStandPose? StartPose { get; init; }
    public TileStandPose? GoalPose { get; init; }
    public IReadOnlyList<TileStandPose>? PathPoses { get; init; }
    public Point? InvalidatedPoseNode { get; init; }
    public TileStandPose? FrontierPose { get; init; }
}

public static partial class TilePathfinder
{
    internal const int WideRoutePaddingX = 384;
    // A read-only ranking hint for nearby mining observations. A settled graph
    // node here never replaces the explicit tool, protection or route preflight.
    internal static TilePathSearch BeginMiningStandSurvey(Point start) =>
        BeginReachableStandSurvey(start, 160, 48, new TilePathSearchLimits(1200, 2400), _ => false);

    internal static TilePathSearch BeginReachableStandSurvey(Point start, int radiusX, int radiusY,
        TilePathSearchLimits limits, Func<Point, bool> goalPredicate)
    {
        int minX = Math.Max(5, start.X - radiusX), maxX = Math.Min(Terraria.Main.maxTilesX - 5, start.X + radiusX);
        int minY = Math.Max(5, start.Y - radiusY), maxY = Math.Min(Terraria.Main.maxTilesY - 5, start.Y + radiusY);
        Vector2 center = StandCenter(start);
        return new TilePathSearch(center, center, start, start, minX, maxX, minY, maxY,
            limits, point => Neighbors(point, minX, maxX, minY, maxY), Heuristic,
            null, null, goalPredicate, regionHeuristic: _ => 0f, capturePose: CaptureStandPose,
            poseMatches: MatchesStandPose);
    }

    /// <summary>
    /// Starts a main-thread-only search using the same standing, jump, drop and door graph as FindPath.
    /// Its owner must discard it on world/intent changes and revalidate physical movement separately.
    /// Search time slices do not approve a frontier destination or imply global unreachability.
    /// </summary>
    public static TilePathSearch BeginSearch(Vector2 startWorld, Vector2 targetWorld, int radiusX = 64, int radiusY = 48,
        IReadOnlySet<Point>? blockedNodes = null, IReadOnlySet<TileTransition>? blockedTransitions = null,
        TilePathSearchLimits? limits = null)
    {
        if (radiusX < 0) throw new ArgumentOutOfRangeException(nameof(radiusX));
        if (radiusY < 0) throw new ArgumentOutOfRangeException(nameof(radiusY));
        limits ??= new TilePathSearchLimits();
        if (limits.MaxUniqueExpanded <= 0 || limits.MaxDiscovered <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        Point? start = float.IsFinite(startWorld.X) && float.IsFinite(startWorld.Y) &&
            TryFindStandTile(startWorld, out Point resolvedStart) ? resolvedStart : null;
        Point? goal = float.IsFinite(targetWorld.X) && float.IsFinite(targetWorld.Y) &&
            TryFindStandTile(targetWorld, out Point resolvedGoal, stableOnly: true) ? resolvedGoal : null;
        int minX = 0, maxX = 0, minY = 0, maxY = 0;
        if (start is { } from && goal is { } to)
        {
            minX = (int)Math.Max(5L, Math.Min(from.X, to.X) - (long)radiusX);
            maxX = (int)Math.Min((long)Terraria.Main.maxTilesX - 5, Math.Max(from.X, to.X) + (long)radiusX);
            minY = (int)Math.Max(5L, Math.Min(from.Y, to.Y) - (long)radiusY);
            maxY = (int)Math.Min((long)Terraria.Main.maxTilesY - 5, Math.Max(from.Y, to.Y) + (long)radiusY);
        }
        return new TilePathSearch(startWorld, targetWorld, start, goal, minX, maxX, minY, maxY, limits,
            point => Neighbors(point, minX, maxX, minY, maxY), Heuristic, blockedNodes, blockedTransitions,
            capturePose: CaptureStandPose, poseMatches: pose => MatchesStandPose(pose) &&
                (pose.Node != goal || IsStableStand(pose.Node.X, pose.Node.Y)));
    }

    /// <summary>Finds a reachable standing position from which the frozen target can actually be attacked.</summary>
    public static TilePathSearch BeginSearchForApproach(Vector2 startWorld, Vector2 targetWorld, float attackRange,
        Func<Vector2, bool> hasSight, IReadOnlySet<Point>? blockedNodes = null,
        IReadOnlySet<TileTransition>? blockedTransitions = null, TilePathSearchLimits? limits = null,
        int radiusX = 64, int radiusY = 48, IReadOnlySet<Point>? rejectedFiringStands = null)
    {
        ArgumentNullException.ThrowIfNull(hasSight);
        if (!float.IsFinite(attackRange) || attackRange <= 0f) throw new ArgumentOutOfRangeException(nameof(attackRange));
        if (radiusX < 0) throw new ArgumentOutOfRangeException(nameof(radiusX));
        if (radiusY < 0) throw new ArgumentOutOfRangeException(nameof(radiusY));
        limits ??= new TilePathSearchLimits();
        if (limits.MaxUniqueExpanded <= 0 || limits.MaxDiscovered <= 0) throw new ArgumentOutOfRangeException(nameof(limits));
        Point? start = float.IsFinite(startWorld.X) && float.IsFinite(startWorld.Y) &&
            TryFindStandTile(startWorld, out Point fromStand) ? fromStand : null;
        bool targetFinite = float.IsFinite(targetWorld.X) && float.IsFinite(targetWorld.Y);
        Point? anchor = targetFinite ? new Point((int)(targetWorld.X / 16f), (int)(targetWorld.Y / 16f)) : null;
        int minX = 0, maxX = 0, minY = 0, maxY = 0;
        if (start is { } from && anchor is { } to)
        {
            minX = (int)Math.Max(5L, Math.Min(from.X, to.X) - (long)radiusX);
            maxX = (int)Math.Min((long)Terraria.Main.maxTilesX - 5, Math.Max(from.X, to.X) + (long)radiusX);
            minY = (int)Math.Max(5L, Math.Min(from.Y, to.Y) - (long)radiusY);
            maxY = (int)Math.Min((long)Terraria.Main.maxTilesY - 5, Math.Max(from.Y, to.Y) + (long)radiusY);
        }
        var rejected = rejectedFiringStands == null ? null : new HashSet<Point>(rejectedFiringStands);
        bool AttackStand(Point point)
        {
            if (rejected?.Contains(point) == true || !IsSafeStand(point.X, point.Y)) return false;
            Vector2 center = StandCenter(point);
            return Vector2.DistanceSquared(center, targetWorld) <= attackRange * attackRange && hasSight(center);
        }
        float RegionHeuristic(Point point) => Math.Max(0f, (Vector2.Distance(StandCenter(point), targetWorld) - attackRange) / 16f);
        return new TilePathSearch(startWorld, targetWorld, start, anchor, minX, maxX, minY, maxY, limits,
            point => Neighbors(point, minX, maxX, minY, maxY), Heuristic, blockedNodes, blockedTransitions,
            targetFinite ? AttackStand : null, RegionHeuristic, CaptureStandPose, MatchesStandPose);
    }
}

/// <summary>Resumable A*. A slice can stop between neighbor yields without losing an edge or charging duplicate pops as expansions.</summary>
public sealed class TilePathSearch
{
    private readonly Vector2 requestedStart, requestedTarget;
    private readonly Point? start, goal;
    private readonly int minX, maxX, minY, maxY;
    private readonly TilePathSearchLimits limits;
    private readonly Func<Point, IEnumerable<(Point Point, float Cost)>> neighbors;
    private readonly Func<Point, Point, float> heuristic;
    private readonly Func<Point, bool>? goalPredicate;
    private readonly Func<Point, float>? regionHeuristic;
    private Point? reachedGoal;
    private readonly HashSet<Point>? blockedNodes;
    private readonly HashSet<TileTransition>? blockedTransitions;
    private readonly PriorityQueue<Point, float> open = new();
    private readonly Dictionary<Point, float> scores = new();
    private readonly Dictionary<Point, Point> parents = new();
    private readonly HashSet<Point> closed = new();
    private IEnumerator<(Point Point, float Cost)>? pendingNeighbors;
    private Point expanding;
    private int uniqueExpanded, dequeued;
    private int leftmostExpanded = int.MaxValue, rightmostExpanded = int.MinValue, deepestExpanded = int.MinValue;
    private Point leftmostStand, rightmostStand, deepestStand;
    private string status = "searching", detail = "";
    private IReadOnlyList<Point>? path;
    private IReadOnlyList<TileStandPose>? pathPoses;
    private readonly Func<Point, TileStandPose?>? capturePose;
    private readonly Func<TileStandPose, bool>? poseMatches;
    private readonly Dictionary<Point, TileStandPose> poses = new();
    private Point? invalidatedPoseNode;
    private TileStandPose? nearestSettledPose;
    private float nearestSettledDistance = float.PositiveInfinity;

    internal TilePathSearch(Vector2 requestedStart, Vector2 requestedTarget, Point? start, Point? goal,
        int minX, int maxX, int minY, int maxY, TilePathSearchLimits limits,
        Func<Point, IEnumerable<(Point Point, float Cost)>> neighbors, Func<Point, Point, float> heuristic,
        IReadOnlySet<Point>? blockedNodes, IReadOnlySet<TileTransition>? blockedTransitions,
        Func<Point, bool>? goalPredicate = null, Func<Point, float>? regionHeuristic = null,
        Func<Point, TileStandPose?>? capturePose = null, Func<TileStandPose, bool>? poseMatches = null)
    {
        this.requestedStart = requestedStart; this.requestedTarget = requestedTarget;
        this.start = start; this.goal = goal; this.minX = minX; this.maxX = maxX; this.minY = minY; this.maxY = maxY;
        this.limits = limits; this.neighbors = neighbors; this.heuristic = heuristic;
        this.goalPredicate = goalPredicate; this.regionHeuristic = goalPredicate == null ? null : regionHeuristic;
        this.capturePose = capturePose; this.poseMatches = poseMatches;
        this.blockedNodes = blockedNodes == null ? null : new HashSet<Point>(blockedNodes);
        this.blockedTransitions = blockedTransitions == null ? null : new HashSet<TileTransition>(blockedTransitions);
        if (start == null) { status = "start_unresolved"; detail = "start_has_no_stand"; }
        else if (goal == null) { status = "goal_unresolved"; detail = "goal_has_no_stand"; }
        else if (!Capture(start.Value))
        { status = "invalidated"; detail = "stand_pose_unresolved_at_capture"; invalidatedPoseNode = start; }
        else if (goalPredicate == null && !Capture(goal.Value))
        { status = "invalidated"; detail = "stand_pose_unresolved_at_capture"; invalidatedPoseNode = goal; }
        else if (goalPredicate == null && start == goal)
        {
            status = "found"; detail = "same_projected_stand_not_physical_arrival"; path = Array.Empty<Point>();
            pathPoses = capturePose == null ? null : Array.Empty<TileStandPose>();
        }
        else { scores[start.Value] = 0f; open.Enqueue(start.Value, Estimate(start.Value)); }
    }

    private Point? ReportedGoal => goalPredicate == null ? goal : reachedGoal;
    internal bool IsSearching => status == "searching";
    internal bool HasSettledStand(Point node) => status != "invalidated" && closed.Contains(node);
    internal int? DeepestSettledRow => uniqueExpanded > 0 ? deepestExpanded : null;
    internal (Point West, Point East, Point Deepest)? SettledExtentStands =>
        uniqueExpanded > 0 ? (leftmostStand, rightmostStand, deepestStand) : null;
    // An extended gap jump can be clipped even when the last stand is
    // several columns inside the search window.
    internal bool ShouldWidenHorizontalEnvelope => status == "frontier_exhausted" &&
        Vector2.DistanceSquared(requestedStart, requestedTarget) >= (256f * 16f) * (256f * 16f) &&
        (minX > 5 && leftmostExpanded <= minX + CompanionMovement.ExtendedGapJumpSpanTiles ||
         maxX < Terraria.Main.maxTilesX - 5 && rightmostExpanded >= maxX - CompanionMovement.ExtendedGapJumpSpanTiles);
    private float Estimate(Point point) => regionHeuristic?.Invoke(point) ?? heuristic(point, goal!.Value);
    public TilePathSearchSnapshot Snapshot => new(status, start, ReportedGoal, path, uniqueExpanded, dequeued, scores.Count,
        open.Count, minX, maxX, minY, maxY,
        $"{status} {detail} start={Describe(start)} goal={Describe(ReportedGoal)} uniqueExpanded={uniqueExpanded} dequeued={dequeued} discovered={scores.Count} open={open.Count} bounds={minX},{minY}..{maxX},{maxY}",
        limits, requestedStart, requestedTarget)
        {
            NeighborExpansionPending = pendingNeighbors != null,
            StartPose = Captured(start), GoalPose = Captured(ReportedGoal), PathPoses = pathPoses,
            InvalidatedPoseNode = invalidatedPoseNode,
            FrontierPose = status == "frontier_exhausted" && nearestSettledPose is { } nearest && nearest.Node != start &&
                StillMatches(nearest.Node) ? nearest : null
        };

    private TileStandPose? Captured(Point? point) => point is { } node && poses.TryGetValue(node, out TileStandPose pose) ? pose : null;
    private bool Capture(Point node)
    {
        if (capturePose == null) return true;
        if (poses.TryGetValue(node, out TileStandPose existing)) return poseMatches?.Invoke(existing) != false;
        if (capturePose(node) is not { } pose || pose.Node != node) return false;
        poses.Add(node, pose);
        return poseMatches?.Invoke(pose) != false;
    }
    private bool StillMatches(Point? point)
        => capturePose == null || point is { } node && poses.TryGetValue(node, out TileStandPose pose) && poseMatches?.Invoke(pose) != false;

    /// <summary>
    /// Computes at most the given new unique expansions. A zero millisecond slice performs no work;
    /// +infinity is available for bounded synchronous callers/tests. Clock checks include duplicate
    /// dequeues and each neighbor yield; an individual native predicate cannot be preempted.
    /// </summary>
    public TilePathSearchSnapshot Advance(int maxUniqueExpansions, double maxMilliseconds)
    {
        if (maxUniqueExpansions <= 0) throw new ArgumentOutOfRangeException(nameof(maxUniqueExpansions));
        if (double.IsNaN(maxMilliseconds) || maxMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(maxMilliseconds));
        if (maxMilliseconds == 0) return Snapshot;
        if (status is "searching" or "found")
        {
            Point? changed = !StillMatches(start) ? start : goalPredicate == null && !StillMatches(goal) ? goal :
                pendingNeighbors != null && !StillMatches(expanding) ? expanding : null;
            if (changed is { } node) return InvalidatePose(node, "captured_stand_pose_changed_original_target_retained");
            if (status == "found" && pathPoses != null)
                foreach (TileStandPose pose in pathPoses)
                    if (!StillMatches(pose.Node)) return InvalidatePose(pose.Node, "captured_path_pose_changed_original_target_retained");
        }
        if (status != "searching") return Snapshot;
        long started = Stopwatch.GetTimestamp();
        int expandedInSlice = 0;
        while (true)
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= maxMilliseconds) return Snapshot;
            if (pendingNeighbors != null)
            {
                if (!pendingNeighbors.MoveNext())
                { pendingNeighbors.Dispose(); pendingNeighbors = null; continue; }
                (Point next, float cost) = pendingNeighbors.Current;
                if (closed.Contains(next) || blockedTransitions?.Contains(new TileTransition(expanding, next)) == true) continue;
                float penalty = (goalPredicate != null || next != goal) && blockedNodes?.Contains(next) == true ? 28f : 0f;
                float tentative = scores[expanding] + cost + penalty;
                bool known = scores.TryGetValue(next, out float previous);
                if (known && tentative >= previous) continue;
                if (!known && scores.Count >= limits.MaxDiscovered) return Finish("budget_exhausted", "discovered_limit");
                if (!Capture(next)) return InvalidatePose(next, "captured_neighbor_pose_changed_original_target_retained");
                scores[next] = tentative; parents[next] = expanding;
                open.Enqueue(next, tentative + Estimate(next));
                continue;
            }
            if (open.Count == 0) return Finish("frontier_exhausted", "no_more_nodes_in_reported_bounds");
            Point current = open.Peek();
            // Old queue entries can remain after a cheaper route closed this point.
            // Drain them under the time allowance even at the unique-node ceiling,
            // otherwise an actually empty frontier would be called budget exhaustion.
            if (closed.Contains(current)) { open.Dequeue(); dequeued++; continue; }
            if (uniqueExpanded >= limits.MaxUniqueExpanded) return Finish("budget_exhausted", "unique_expansion_limit");
            if (expandedInSlice >= maxUniqueExpansions) return Snapshot;
            if (!StillMatches(current)) return InvalidatePose(current, "captured_stand_pose_changed_original_target_retained");
            open.Dequeue(); dequeued++; closed.Add(current);
            if (goalPredicate == null && poses.TryGetValue(current, out TileStandPose settled))
            {
                float distance = Vector2.DistanceSquared(settled.Center, requestedTarget);
                if (distance < nearestSettledDistance)
                { nearestSettledPose = settled; nearestSettledDistance = distance; }
            }
            if (current.X < leftmostExpanded) { leftmostExpanded = current.X; leftmostStand = current; }
            if (current.X > rightmostExpanded) { rightmostExpanded = current.X; rightmostStand = current; }
            if (current.Y > deepestExpanded) { deepestExpanded = current.Y; deepestStand = current; }
            uniqueExpanded++; expandedInSlice++;
            if (goalPredicate?.Invoke(current) ?? current == goal)
            {
                reachedGoal = current;
                var found = new List<Point> { current };
                while (parents.TryGetValue(current, out Point previous)) { current = previous; found.Add(current); }
                found.Reverse(); path = Array.AsReadOnly(found.ToArray());
                if (capturePose != null)
                {
                    foreach (Point node in found)
                        if (!StillMatches(node)) return InvalidatePose(node, "captured_path_pose_changed_original_target_retained");
                    pathPoses = Array.AsReadOnly(found.ConvertAll(node => poses[node]).ToArray());
                }
                return Finish("found", goalPredicate == null ? "complete_graph_path_not_executed" : "complete_attack_position_path_not_executed");
            }
            expanding = current;
            pendingNeighbors = neighbors(current).GetEnumerator();
        }
    }

    private TilePathSearchSnapshot Finish(string finalStatus, string reason)
    {
        status = finalStatus; detail = reason;
        if (finalStatus == "invalidated") { path = null; pathPoses = null; }
        pendingNeighbors?.Dispose(); pendingNeighbors = null;
        return Snapshot;
    }
    private TilePathSearchSnapshot InvalidatePose(Point? node, string reason)
    { invalidatedPoseNode = node; return Finish("invalidated", reason); }
    private static string Describe(Point? point) => point is { } value ? $"{value.X},{value.Y}" : "unresolved";
}
