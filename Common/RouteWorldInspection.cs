using System;
using System.Linq;
using Terraria;

namespace TerrariaAITeammate.Common;

/// <summary>Native adapter; all world reads and advancement stay on the game thread.</summary>
public sealed class RouteWorldInspection
{
    private readonly object? error;
    private readonly NavigationRouteRequest? request;
    private readonly RouteObservationSession? session;
    private readonly ObservedRouteStand? start, destination;
    internal RouteWorldInspection(object error) => this.error = error;
    internal RouteWorldInspection(NavigationRouteRequest request, RouteObservationSession session,
        ObservedRouteStand? start, ObservedRouteStand? destination)
    { this.request = request; this.session = session; this.start = start; this.destination = destination; }

    public object? Advance(NPC observer, Player player, object actionToken)
    {
        if (error != null) return error;
        var initial = session!.Initial;
        var current = new RouteObservationFrame(WorldKnowledgeObserver.LoadEpoch, Main.worldID, Main.GameUpdateCount, actionToken,
            observer.Center, observer.Bottom.Y, TilePathfinder.IsGroundedAt(observer.Center.X, observer.Bottom.Y, observer.velocity.Y, observer.width),
            observer.velocity.Y, request!.Kind == "player" ? player.Center : initial.TargetCenter, player.active && !player.dead);
        session.Advance(current);
        return session.IsComplete ? DescribeCurrent(continuationRetained: true) : null;
    }

    public object DescribeCurrent(bool continuationRetained)
    {
        if (error != null) return error;
        var initial = session!.Initial; var current = session.Latest; var snapshot = session.Snapshot;
        TilePathSearchSnapshot? back = session.ReturnSnapshot;
        ObservedRouteStand? frozenStart = snapshot.StartPose is { } capturedStart ? NavigationWorldObserver.DescribeRouteStand(capturedStart) : start;
        ObservedRouteStand? frozenDestination = snapshot.GoalPose is { } capturedGoal ? NavigationWorldObserver.DescribeRouteStand(capturedGoal) : destination;
        var sample = new ObservedRouteSample(initial.TargetCenter.X / 16f, initial.TargetCenter.Y / 16f,
            initial.ObserverCenter.X / 16f, initial.ObserverCenter.Y / 16f, frozenStart, frozenDestination,
            session.ObservationValid ? snapshot.PathPoses?.Select(NavigationWorldObserver.DescribeRouteStand).ToArray() : null, snapshot.Diagnostic)
        {
            ActualBottom = initial.ObserverBottom / 16f, ActualGrounded = initial.ObserverGrounded, ActualVelocityY = initial.ObserverVelocityY,
            SearchStatus = snapshot.Status, ObservationValid = session.ObservationValid,
            ContinuationRetained = continuationRetained, StartedAtTick = initial.Tick, CompletedAtTick = current.Tick, SliceCount = session.SliceCount,
            Bounds = snapshot.Start.HasValue && snapshot.Goal.HasValue ? new(snapshot.MinX, snapshot.MaxX, snapshot.MinY, snapshot.MaxY) : null,
            UniqueExpanded = snapshot.UniqueExpanded, Dequeued = snapshot.Dequeued, Discovered = snapshot.Discovered,
            OpenCount = snapshot.OpenCount, NeighborExpansionPending = snapshot.NeighborExpansionPending,
            FrontierStand = snapshot.FrontierPose is { } frontier ? NavigationWorldObserver.DescribeRouteStand(frontier) : null,
            ReturnCheck = session.ReturnStatus is { } status ? new(status,
                back?.UniqueExpanded ?? 0, back?.Discovered ?? 0, back?.Path?.Count, back?.Diagnostic,
                session.ReturnAnchor is { } anchor ? NavigationWorldObserver.DescribeRouteStand(anchor) : null) : null
        };
        return new
        {
            topic = "route", source = "live_world", available = session.ObservationValid, worldId = initial.WorldId, epoch = initial.Epoch,
            currentWorldId = current.WorldId, currentEpoch = current.Epoch, observedAtTick = current.Tick,
            scanStartedAtTick = initial.Tick, scanCompletedAtTick = current.Tick,
            mixedTickObservation = initial.Tick != current.Tick,
            targetKind = request!.Kind, requestedInput = new { kind = request.Kind, x = request.X, y = request.Y, checkReturn = request.CheckReturn },
            measurement = NavigationRouteObservation.Analyze(sample), movementLimits = NavigationRouteObservation.MovementLimits(),
            search = new { algorithm = "current_TilePathfinder", bounded = true, limits = NavigationRouteObservation.QueryLimits(),
                continuationRetained, framesAdvanced = session.SliceCount, invalidationReason = session.InvalidationReason,
                executionBlockedNodeHistoryApplied = false },
            worldModified = false, inventoryModified = false, actorsMoved = false,
            note = continuationRetained
                ? "一次inspect动作跨游戏帧推进同一只读搜索，终态才反馈模型，无需重复付费轮询。每帧64唯一节点/软2ms，总30000/60000及600游戏tick上限。起点/玩家端点或世界失效时明确返回invalidated，不能当找到路径或无路。事实来自报告的跨帧读取窗口，地形并非冻结快照，行走仍须重新核对；查询不移动人物或修改世界。"
                : "兼容即时调用仅计算一片且不保留会话；searching不能当无路。正常inspect route动作会跨帧完成同一次搜索后再反馈模型。图结果不是实际行走或到达证明。"
        };
    }

    public void Cancel() => session?.Cancel();
}
