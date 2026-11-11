using System;
using System.Linq;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

public sealed record ObservedRouteStand(int X, int Row, float CenterX, float CenterY, float Bottom);
public sealed record ObservedRouteBounds(int MinX, int MaxX, int MinY, int MaxY);
public sealed record ObservedRouteReturn(string Status, int UniqueExpanded, int Discovered, int? WaypointCount, string? Diagnostic,
    ObservedRouteStand? Anchor);
public sealed record ObservedRouteSample(float RequestedX, float RequestedY, float ActualStartX, float ActualStartY,
    ObservedRouteStand? ResolvedStart, ObservedRouteStand? ResolvedDestination, ObservedRouteStand[]? Path, string Diagnostic)
{
    // Position/feet use tile units; vertical velocity retains native pixels per tick.
    public float? ActualBottom { get; init; }
    public bool? ActualGrounded { get; init; }
    public float? ActualVelocityY { get; init; }
    public string? SearchStatus { get; init; }
    public ObservedRouteBounds? Bounds { get; init; }
    public int UniqueExpanded { get; init; }
    public int Dequeued { get; init; }
    public int Discovered { get; init; }
    public int OpenCount { get; init; }
    public bool NeighborExpansionPending { get; init; }
    public ObservedRouteStand? FrontierStand { get; init; }
    public ObservedRouteReturn? ReturnCheck { get; init; }
    public bool ObservationValid { get; init; } = true;
    public bool ContinuationRetained { get; init; }
    public ulong? StartedAtTick { get; init; }
    public ulong? CompletedAtTick { get; init; }
    public int SliceCount { get; init; }
}

public static class NavigationRouteObservation
{
    public const int QueryUniqueExpansions = 64;
    public const double QueryMilliseconds = 2;
    public const int MaxUniqueExpanded = 30000;
    public const int MaxDiscovered = 60000;
    public const ulong MaxSessionTicks = 600;
    public const int PaddingX = 64;
    public const int PaddingY = 48;
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    public static object QueryLimits() => new
    {
        perQueryUniqueExpansions = QueryUniqueExpansions, perQueryMilliseconds = QueryMilliseconds,
        perFrameUniqueExpansions = QueryUniqueExpansions, perFrameMilliseconds = QueryMilliseconds,
        maxUniqueExpanded = MaxUniqueExpanded, maxDiscovered = MaxDiscovered,
        maximumTicksForRetainedAction = MaxSessionTicks,
        paddingX = PaddingX, paddingY = PaddingY,
        boundsMeaning = "resolved_endpoint_envelope_plus_padding_clipped_to_world",
        timeBudget = "soft_limit_checked_between_search_operations_native_predicates_not_preempted"
    };
    public static object MovementLimits() => new
    {
        bodyWidthPixels = CompanionMovement.Width, bodyHeightPixels = CompanionMovement.Height,
        maximumJumpRiseTiles = CompanionMovement.MaximumJumpRiseTiles, walkingStepPixels = 16,
        comeArrival = new { horizontalPixels = 48, verticalPixels = 24 },
        followPause = new { horizontalPixels = 68, verticalPixels = 24 },
        followResume = new { horizontalPixels = 112, verticalPixels = 40 },
        standPoseSampling = new { maximumCenterAttemptsPerAnchor = 2, existingLegalGridCenterKept = true,
            fallbackCenterOffsetPixelsFromOldCenter = 8, fallbackOnlyWhenOldCenterInvalid = true,
            scope = "one_canonical_pose_per_anchor_not_all_subtile_positions_or_a_vertical_shaft_trajectory_proof" },
        note = "这些是当前真实运动约束。不能提高跳高或传送绕过地形；路线查询不会执行行走或造出支撑。"
    };
    public static string AnalyzeJson(string sampleJson) => JsonSerializer.Serialize(Analyze(
        JsonSerializer.Deserialize<ObservedRouteSample>(sampleJson, ReadOptions) ?? throw new ArgumentException("Missing route sample.")));

    public static object Analyze(ObservedRouteSample sample)
    {
        string status = sample.SearchStatus ?? (sample.Path != null ? "found" : "unknown");
        bool found = sample.ObservationValid && status == "found" && sample.ResolvedStart != null && sample.ResolvedDestination != null && sample.Path != null;
        ObservedRouteStand[]? path = found ? sample.Path : null;
        bool sameProjectedStand = sample.ResolvedStart != null && sample.ResolvedDestination != null &&
            sample.ResolvedStart.X == sample.ResolvedDestination.X && sample.ResolvedStart.Row == sample.ResolvedDestination.Row;
        bool hasArrivalEvidence = sample.ObservationValid && sample.ActualBottom.HasValue && sample.ActualGrounded.HasValue && sample.ActualVelocityY.HasValue;
        bool alreadyAt = hasArrivalEvidence && sample.ResolvedDestination != null && CompanionMovement.ReachedStand(
            sample.ActualStartX * 16f, sample.ActualBottom!.Value * 16f, sample.ActualVelocityY!.Value,
            sample.ActualGrounded!.Value, sample.ResolvedDestination.X, sample.ResolvedDestination.Row,
            sample.ResolvedDestination.Bottom * 16f, sample.ResolvedDestination.CenterX * 16f);
        float? snap = sample.ResolvedDestination == null ? null : Distance(sample.RequestedX, sample.RequestedY,
            sample.ResolvedDestination.CenterX, sample.ResolvedDestination.CenterY);
        float actualDistance = Distance(sample.ActualStartX, sample.ActualStartY, sample.RequestedX, sample.RequestedY);
        object? Stand(ObservedRouteStand? point, string? reachability = null) => point == null ? null : new
        {
            x = point.X, standRow = point.Row, centerTileX = point.CenterX, centerTileY = point.CenterY,
            bottomTileY = point.Bottom, reachability = reachability ?? (found ? "on_current_found_graph_path" : "geometric_candidate_only")
        };
        return new
        {
            kind = "navigation_route_observation",
            requestedDestination = new { x = sample.RequestedX, y = sample.RequestedY },
            actualStart = new { x = sample.ActualStartX, y = sample.ActualStartY,
                bottomTileY = sample.ActualBottom, grounded = sample.ActualGrounded, verticalVelocityPixelsPerTick = sample.ActualVelocityY },
            resolvedStart = Stand(sample.ResolvedStart), resolvedDestination = Stand(sample.ResolvedDestination),
            destinationStandable = sample.ResolvedDestination != null,
            pathFound = found,
            searchStatus = status,
            searchIncomplete = status is "searching" or "budget_exhausted" or "unknown" or "invalidated" or "cancelled" or "time_budget_exhausted",
            observationValid = sample.ObservationValid, continuationRetained = sample.ContinuationRetained,
            readWindow = new { startedAtTick = sample.StartedAtTick, completedAtTick = sample.CompletedAtTick, slices = sample.SliceCount,
                mixedTick = sample.StartedAtTick.HasValue && sample.CompletedAtTick.HasValue && sample.StartedAtTick != sample.CompletedAtTick },
            limits = QueryLimits(),
            bounds = sample.Bounds == null ? null : new { minX = sample.Bounds.MinX, maxX = sample.Bounds.MaxX,
                minY = sample.Bounds.MinY, maxY = sample.Bounds.MaxY },
            expanded = new { unique = sample.UniqueExpanded, dequeued = sample.Dequeued,
                discovered = sample.Discovered, open = sample.OpenCount, neighborExpansionPending = sample.NeighborExpansionPending },
            destinationReachability = found ? "path_found_in_current_bounded_graph" : "not_proven_by_this_bounded_graph_search",
            returnCheck = sample.ReturnCheck is { } returnCheck ? new
            {
                status = returnCheck.Status, roundTripPathFound = found && returnCheck.Status == "found" && returnCheck.Anchor != null,
                uniqueExpanded = returnCheck.UniqueExpanded, discovered = returnCheck.Discovered,
                waypointCount = returnCheck.WaypointCount, diagnostic = returnCheck.Diagnostic,
                anchor = Stand(returnCheck.Anchor, found && returnCheck.Status == "found"
                    ? "on_current_found_return_graph_path" : "verified_origin_connection_only"),
                originRelation = returnCheck.Anchor == null ? "unknown" :
                    sample.ResolvedStart is { } start && returnCheck.Anchor.X == start.X && returnCheck.Anchor.Row == start.Row
                        ? "exact_origin_stand" : "nearby_walk_connected_origin",
                scope = "从本次目的站位查回原始站位或与其双向走路相通的邻近稳定落脚点。两向共用节点和游戏帧预算；尚未移动或检查采挖后的变化。"
            } : null,
            frontier = sample.ObservationValid && status == "frontier_exhausted" && sample.FrontierStand is { } frontier
                ? DescribeFrontier(frontier, sample.RequestedX, sample.RequestedY) : null,
            sameProjectedStand,
            alreadyAtResolvedStand = alreadyAt,
            arrivalEvidence = hasArrivalEvidence ? "actual_feet_grounded_and_vertical_velocity" : "unknown",
            goalSnapDistanceTiles = snap,
            actualDistanceToTargetTiles = actualDistance,
            actualDistanceToTargetPixels = actualDistance * 16f,
            withinMiningDistanceByDistanceOnly = actualDistance <= 5.5f,
            resolvedStandToTargetDistanceTiles = snap,
            resolvedStandToTargetDistancePixels = snap * 16f,
            resolvedStandWithinMiningDistanceByDistanceOnly = snap.HasValue && snap.Value <= 5.5f,
            waypointColumns = new[] { "standX", "standRow", "centerTileX", "centerTileY", "feetTileY" },
            totalWaypointCount = path?.Length ?? 0,
            pathTruncated = path?.Length > 96,
            waypointWindow = "prefix",
            waypoints = path?.Take(96).Select(point => new[] { point.X, (float)point.Row, point.CenterX, point.CenterY, point.Bottom }).ToArray(),
            diagnostic = sample.Diagnostic,
            travelExecuted = false, worldModified = false,
            scope = "current_bounded_search_and_resolved_stands_only",
            note = "站位存在不等于有路；pathFound只在searchStatus=found且本轮有完整路径时为真，未执行行走。searching/budget_exhausted只是有界工作尚未证明路线，不能当作无路；frontier_exhausted也只限报告的bounds。搜索框是解析后两端的包络加横64/纵48格padding并裁到世界，远目标不因距离被拒绝。sameProjectedStand只表示两端投影相同；alreadyAtResolvedStand须实际脚底、接地、竖速及水平位置均满足站位到达条件，arrivalEvidence=unknown不能当到达。actualDistanceToTarget与withinMiningDistanceByDistanceOnly从队友本轮真实中心actualStart算到请求目标；resolvedStandToTarget与resolvedStandWithinMiningDistanceByDistanceOnly仅描述候选端点到目标，goalSnapDistanceTiles是后者的兼容字段，不代表队友已经在候选工位。距离在5.5格内也不等于已验证工具、视线、材料保护或开采成功。未找到当前图路径不代表整片区域永久不可达；可由你观察地形、显式挖自然块、放置支撑或另选来源后重新查询。"
        };
    }
    internal static object DescribeFrontier(ObservedRouteStand stand, float requestedX, float requestedY) => new
    {
        stand = new { x = stand.X, standRow = stand.Row, centerTileX = stand.CenterX, bottomTileY = stand.Bottom },
        distanceToRequestedTiles = Distance(stand.CenterX, stand.CenterY, requestedX, requestedY),
        terrainQuery = new { type = "inspect", target = "terrain", x = stand.X, y = stand.Row },
        note = "本次图搜索中距请求最近的已展开站位。需要看阻断时可读此处小窗，实际通行、返程和采掘仍须核对。"
    };
    private static float Distance(float x1, float y1, float x2, float y2)
        => MathF.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
}
