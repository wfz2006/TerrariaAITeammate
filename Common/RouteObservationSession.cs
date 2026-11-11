using System;
using Microsoft.Xna.Framework;

namespace TerrariaAITeammate.Common;

public sealed record RouteObservationFrame(string Epoch, int WorldId, ulong Tick, object ActionToken,
    Vector2 ObserverCenter, float ObserverBottom, bool ObserverGrounded, float ObserverVelocityY,
    Vector2 TargetCenter, bool TargetActive = true);

/// <summary>One readonly action owns a forward search and, if requested, a bounded return search.</summary>
public sealed class RouteObservationSession
{
    private readonly TilePathSearch search;
    private readonly bool tracksPlayer;
    private readonly bool checkReturn;
    private TilePathSearch? returnSearch;
    private string? returnTermination;
    private TileStandPose? returnAnchor;
    private ulong? lastAdvancedTick;
    private string? terminalStatus;
    public RouteObservationFrame Initial { get; }
    public RouteObservationFrame Latest { get; private set; }
    public int SliceCount { get; private set; }
    public string InvalidationReason { get; private set; } = "";
    public bool ObservationValid => InvalidationReason.Length == 0 && search.Snapshot.Status != "invalidated";
    public bool IsComplete => Snapshot.Status != "searching" && ReturnStatus != "searching";
    public TilePathSearchSnapshot? ReturnSnapshot => Snapshot.Status == "found" ? returnSearch?.Snapshot : null;
    public TileStandPose? ReturnAnchor => Snapshot.Status == "found" ? returnAnchor : null;
    public string? ReturnStatus => !checkReturn ? null : Snapshot.Status switch
    {
        "searching" => "pending_outbound",
        "found" => returnTermination ?? returnSearch?.Snapshot.Status ?? "searching",
        _ => "not_checked_outbound_not_found"
    };
    public TilePathSearchSnapshot Snapshot => terminalStatus == null ? search.Snapshot : search.Snapshot with
    { Status = terminalStatus, Path = null, PathPoses = null, FrontierPose = null, Diagnostic = $"{terminalStatus}: {(terminalStatus == "time_budget_exhausted" ? "600_game_tick_limit_not_no_route_proof" : InvalidationReason)}; {search.Snapshot.Diagnostic}" };

    public RouteObservationSession(TilePathSearch search, RouteObservationFrame initial, bool tracksPlayer, bool checkReturn = false)
    { this.search = search; Initial = Latest = initial; this.tracksPlayer = tracksPlayer; this.checkReturn = checkReturn; }

    public TilePathSearchSnapshot Advance(RouteObservationFrame current)
    {
        Latest = current;
        if (terminalStatus == "cancelled") return Snapshot;
        string reason = current.Epoch != Initial.Epoch || current.WorldId != Initial.WorldId || current.Epoch.Length == 0 ? "world_epoch_changed" :
            !ReferenceEquals(current.ActionToken, Initial.ActionToken) ? "action_replaced" :
            current.Tick < Initial.Tick ? "world_tick_rewound" :
            Vector2.DistanceSquared(current.ObserverCenter, Initial.ObserverCenter) > 16f * 16f ||
                Math.Abs(current.ObserverBottom - Initial.ObserverBottom) > 5f ? "observer_pose_changed" :
            tracksPlayer && (!current.TargetActive || Vector2.DistanceSquared(current.TargetCenter, Initial.TargetCenter) > 32f * 32f) ? "player_target_changed" : "";
        if (reason.Length > 0) { terminalStatus = "invalidated"; InvalidationReason = reason; return Snapshot; }
        if (terminalStatus != null) return Snapshot;
        if (search.Snapshot.Status != "searching")
        {
            if (ReturnStatus == "searching") AdvanceReturn(current);
            // Revalidate the completed forward poses once when publishing both
            // directions, without walking its full path on every return slice.
            if (IsComplete)
            {
                ObserveSearchStatus(search.Advance(NavigationRouteObservation.QueryUniqueExpansions, NavigationRouteObservation.QueryMilliseconds));
                if (ReturnStatus == "found" && returnAnchor is { } anchor && search.Snapshot.StartPose is { } origin &&
                    anchor.Node != origin.Node && !NearWalkConnectedOrigin(anchor, origin))
                    returnTermination = "return_anchor_connection_changed";
            }
            return Snapshot;
        }
        if (current.Tick - Initial.Tick >= NavigationRouteObservation.MaxSessionTicks)
        { terminalStatus = "time_budget_exhausted"; return Snapshot; }
        if (lastAdvancedTick == current.Tick) return Snapshot;
        lastAdvancedTick = current.Tick; SliceCount++;
        ObserveSearchStatus(search.Advance(NavigationRouteObservation.QueryUniqueExpansions, NavigationRouteObservation.QueryMilliseconds));
        return Snapshot;
    }

    private void AdvanceReturn(RouteObservationFrame current)
    {
        if (current.Tick - Initial.Tick >= NavigationRouteObservation.MaxSessionTicks)
        { returnTermination = "time_budget_exhausted"; return; }
        if (lastAdvancedTick == current.Tick) return;
        if (returnSearch == null)
        {
            TilePathSearchSnapshot outbound = search.Snapshot;
            if (outbound.StartPose is not { } origin || outbound.GoalPose is not { } destination ||
                !Initial.ObserverGrounded ||
                !TilePathfinder.TryResolveGroundedLaunch(Initial.ObserverCenter, Initial.ObserverBottom, Initial.ObserverVelocityY, out Point launch) ||
                launch != origin.Node ||
                !TilePathfinder.MatchesStandPose(origin) || !TilePathfinder.MatchesStandPose(destination))
            { returnTermination = "origin_or_destination_not_verified"; return; }
            returnAnchor = StableReturnAnchor(origin);
            if (returnAnchor is not { } anchor)
            { returnTermination = "return_anchor_not_verified"; return; }
            int expanded = outbound.Limits.MaxUniqueExpanded - outbound.UniqueExpanded;
            int discovered = outbound.Limits.MaxDiscovered - outbound.Discovered;
            if (expanded <= 0 || discovered <= 0)
            { returnTermination = "shared_budget_exhausted"; return; }
            returnSearch = TilePathfinder.BeginSearchBetweenStands(destination.Node, anchor.Node,
                NavigationRouteObservation.PaddingX, NavigationRouteObservation.PaddingY, new(expanded, discovered));
        }
        lastAdvancedTick = current.Tick; SliceCount++;
        returnSearch.Advance(NavigationRouteObservation.QueryUniqueExpansions, NavigationRouteObservation.QueryMilliseconds);
    }

    private TileStandPose? StableReturnAnchor(TileStandPose origin)
    {
        if (TilePathfinder.IsStableStand(origin.Node.X, origin.Node.Y) && AtOriginPose(origin)) return origin;
        if (TilePathfinder.TryResolveStableStand(Initial.ObserverCenter, out Point nearby) &&
            TilePathfinder.TryGetStandPose(nearby, out TileStandPose pose) && NearWalkConnectedOrigin(pose, origin)) return pose;
        return null;
    }

    private bool AtOriginPose(TileStandPose pose) => CompanionMovement.ReachedStand(Initial.ObserverCenter.X, Initial.ObserverBottom,
        Initial.ObserverVelocityY, Initial.ObserverGrounded, pose.Node.X, pose.Node.Y, pose.Bottom, pose.Center.X);

    private bool NearWalkConnectedOrigin(TileStandPose pose, TileStandPose origin) => AtOriginPose(pose) &&
        TilePathfinder.IsStableStand(pose.Node.X, pose.Node.Y) && TilePathfinder.MatchesStandPose(pose) &&
        TilePathfinder.HasWalkingConnection(Initial.ObserverCenter.X, Initial.ObserverBottom, pose.Node) &&
        TilePathfinder.HasWalkingConnection(pose.Node, origin.Node);

    private void ObserveSearchStatus(TilePathSearchSnapshot snapshot)
    {
        if (snapshot.Status != "invalidated") return;
        terminalStatus = "invalidated";
        InvalidationReason = "captured_stand_pose_changed";
    }

    public void Cancel(string reason = "executor_cancelled")
    { terminalStatus = "cancelled"; InvalidationReason = reason; }
}
