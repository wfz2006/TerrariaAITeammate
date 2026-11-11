using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AIExecutor
{
    private TilePathSearch? routeSearch;
    private TilePathSearchSnapshot? routeSnapshot;
    private readonly Dictionary<TileTransition, uint> failedTransitions = new();
    private readonly NavigationEdgeProgress edgeProgress = new();
    private string routeEpoch = "", routeFailureCode = "";
    private bool navHasTarget;
    private bool routeDefence, routeWidenedHorizontally;
    private bool routeAttackApproach;
    private float routeAttackRange;
    private uint searchStartedAt;
    private uint? attackApproachStartedAt;
    private int routeReplans, routeSearchCount, endpointSettleTicks;
    private bool routeRecoveryRequested;
    private int travelTickBudget = 180 * 60;
    private Vector2 requestedNavGoal;
    private IReadOnlyList<TileStandPose>? navPathPoses;
    private TileStandPose? navGoalPose;
    private readonly Dictionary<Point, TileStandPose> failedStandPoses = new();
    private readonly HashSet<Point> unsettledFiringStands = new();
    private TileStandPose? CurrentFrozenWaypoint => navPathPoses != null && navIndex >= 0 && navIndex < navPathPoses.Count
        ? navPathPoses[navIndex] : null;
    private TileStandPose? ReportedFrozenGoal => navGoalPose ?? routeSnapshot?.GoalPose;

    private static object? DescribeFrozenStand(TileStandPose? pose) => pose is { } stand ? new
    {
        x = stand.Node.X, standRow = stand.Node.Y, centerTileX = stand.Center.X / 16f,
        centerTileY = stand.Center.Y / 16f, feetTileY = stand.Bottom / 16f
    } : null;

    public object NavigationState => new
    {
        status = routeSearch != null ? "searching" : routeFailureCode.Length > 0 ? "blocked" : navPath != null ? "following_graph_route" : "idle",
        intent = routeDefence ? "temporary_defence" : "current_task",
        goalKind = routeAttackApproach ? "visible_attack_position" : "exact_destination",
        requestedTarget = navHasTarget ? new { x = requestedNavGoal.X / 16f, y = requestedNavGoal.Y / 16f } : null,
        plannedTarget = navHasTarget && ReportedFrozenGoal is { } planned
            ? new { x = planned.Center.X / 16f, y = planned.Center.Y / 16f } : null,
        currentWaypoint = DescribeFrozenStand(CurrentFrozenWaypoint),
        currentWaypointPoseMatchesCurrentWorld = CurrentFrozenWaypoint is { } waypointPose
            ? TilePathfinder.MatchesStandPose(waypointPose) : (bool?)null,
        goalStandPose = navHasTarget ? DescribeFrozenStand(ReportedFrozenGoal) : null,
        goalStandPoseMatchesCurrentWorld = navHasTarget && ReportedFrozenGoal is { } goalPose
            ? TilePathfinder.MatchesStandPose(goalPose) : (bool?)null,
        selectedAttackStand = routeAttackApproach && routeSnapshot?.Goal is { } attackStand
            ? new { x = attackStand.X, standRow = attackStand.Y } : null,
        searchStatus = routeSnapshot?.Status, searchUniqueExpanded = routeSnapshot?.UniqueExpanded,
        searchDiagnostic = routeSnapshot?.Diagnostic,
        frontier = routeSnapshot is { Status: "frontier_exhausted", FrontierPose: { } frontier } && TilePathfinder.MatchesStandPose(frontier)
            ? NavigationRouteObservation.DescribeFrontier(NavigationWorldObserver.DescribeRouteStand(frontier),
                routeSnapshot.RequestedTarget.X / 16f, routeSnapshot.RequestedTarget.Y / 16f) : null,
        invalidatedPoseNode = routeSnapshot?.InvalidatedPoseNode is { } invalidatedPose
            ? new { x = invalidatedPose.X, standRow = invalidatedPose.Y } : null,
        searchDequeued = routeSnapshot?.Dequeued, searchDiscovered = routeSnapshot?.Discovered,
        searchNeighborPending = routeSnapshot?.NeighborExpansionPending,
        waypointIndex = navIndex, waypointCount = navPath?.Count ?? 0,
        failedTransitions = failedTransitions.Count, replans = routeReplans, searches = routeSearchCount,
        widenedHorizontalEnvelope = routeWidenedHorizontally,
        recentBlockedApproaches = failedTransitions.OrderByDescending(p => p.Value).Take(3).Select(p => new
        {
            from = new { x = p.Key.From.X, standRow = p.Key.From.Y },
            to = new { x = p.Key.To.X, standRow = p.Key.To.Y }, observedAtTick = p.Value,
            meaning = "this_directed_attempt_stalled_not_global_unreachability"
        }).ToArray(),
        noProgressTicks = navigationNoProgressTicks, edgeIdleTicks = edgeProgress.NoProgressTicks,
        totalTaskTickLimit = travelTickBudget, failureCode = routeFailureCode, failure = NavigationFailureReason,
        note = "Search slices retain the final target; found/empty graph routes are not physical arrival. No automatic terrain edits or teleport."
    };

    private void ResetRouteNavigation()
    {
        routeSearch = null; routeSnapshot = null; routeEpoch = ""; navHasTarget = false;
        navPathPoses = null; navGoalPose = null;
        failedTransitions.Clear(); edgeProgress.Reset(); routeReplans = routeSearchCount = endpointSettleTicks = 0;
        failedStandPoses.Clear(); unsettledFiringStands.Clear();
        routeRecoveryRequested = routeWidenedHorizontally = false; routeFailureCode = "";
        routeDefence = false; routeAttackApproach = false; routeAttackRange = 0f;
        attackApproachStartedAt = null;
    }

    private void CancelRouteSearch()
    { routeSearch = null; endpointSettleTicks = 0; }

    private void InvalidateRoutePath()
    { navPath = null; navPathPoses = null; CancelRouteSearch(); }

    private void ClearChangedPoseFailures(IReadOnlyCollection<Point> changed)
    {
        foreach (Point point in changed)
        {
            blockedWaypoints.Remove(point);
            failedStandPoses.Remove(point);
            unsettledFiringStands.Remove(point);
            edgeProgress.ForgetPoint(point.X, point.Y);
        }
        foreach (TileTransition edge in failedTransitions.Keys.Where(edge => changed.Contains(edge.From) || changed.Contains(edge.To)).ToArray())
            failedTransitions.Remove(edge);
    }

    private void RememberFailedStandPose(int index)
    {
        if (navPathPoses == null || index < 0 || index >= navPathPoses.Count) return;
        TileStandPose pose = navPathPoses[index];
        if (!failedStandPoses.ContainsKey(pose.Node) && failedStandPoses.Count >= 512) failedStandPoses.Remove(failedStandPoses.First().Key);
        failedStandPoses[pose.Node] = pose;
    }

    private void ClearStaleStandFailures()
    {
        Point[] changed = failedStandPoses.Values.Where(pose => !TilePathfinder.MatchesStandPose(pose)).Select(pose => pose.Node).ToArray();
        ClearChangedPoseFailures(changed);
    }

    private void ReplanChangedRoutePose(NPC npc, IReadOnlyCollection<Point> changed, string reason)
    {
        ClearChangedPoseFailures(changed);
        if (routeSnapshot != null)
            routeSnapshot = routeSnapshot with { Status = "invalidated", Path = null, PathPoses = null,
                InvalidatedPoseNode = changed.Count > 0 ? changed.First() : routeSnapshot.InvalidatedPoseNode,
                Diagnostic = reason + "; requested_target_retained; " + routeSnapshot.Diagnostic };
        InvalidateRoutePath(); routeRecoveryRequested = true; navigationRetryAt = Main.GameUpdateCount + 1;
        navGoalPose = null;
        pathBlockedTicks = 0; npc.velocity.X *= 0.65f;
        AITeammateSystem.Instance.TestLog($"navigation pose invalidated: {reason}; requested={requestedNavGoal.X / 16f},{requestedNavGoal.Y / 16f}; retry budget retained");
    }

    private bool RevalidateFrozenRoute(NPC npc)
    {
        if (navPath == null) return true;
        if (navPathPoses == null || navPathPoses.Count != navPath.Count || navGoalPose is not { } goal)
        {
            FailNavigation(npc, "path_pose_missing", "路径缺少冻结身体站姿；没有用新的实时中心替代旧路径。");
            return false;
        }
        var changed = new HashSet<Point>();
        if (!TilePathfinder.MatchesStandPose(goal) || !routeAttackApproach && !TilePathfinder.IsStableStand(goal.Node.X, goal.Node.Y))
            changed.Add(goal.Node);
        for (int index = Math.Max(0, navIndex - 1); index <= navIndex && index < navPath.Count; index++)
        {
            TileStandPose pose = navPathPoses[index];
            if (pose.Node != navPath[index] || !TilePathfinder.MatchesStandPose(pose)) changed.Add(navPath[index]);
        }
        if (changed.Count == 0) return true;
        ReplanChangedRoutePose(npc, changed, "captured_stand_pose_changed");
        return false;
    }

    private void FailNavigation(NPC npc, string code, string detail)
    {
        InvalidateRoutePath(); routeFailureCode = code;
        AITeammateSystem.Instance.RecordTestNavigationFailure(action, code);
        NavigationFailureReason = detail;
        navigationRetryAt = Main.GameUpdateCount + 120;
        npc.velocity.X *= 0.5f;
        if (currentPlan == null || action == AIActionType.Mine || autoDefend) return;
        if (action == AIActionType.Attack) BeginBossReview(npc);
        if (action == AIActionType.Explore) FinishExplore(npc, Main.LocalPlayer, false, detail);
        else if (action == AIActionType.Come && PlatformRecoveryAfterRouteFailure(npc) is { } platformRecovery)
            FinishCurrentStep(JsonSerializer.Serialize(new { kind = "navigation_result", success = false, reason = detail, platformRecovery }), false);
        else FinishCurrentStep(detail, false);
    }

    private object? PlatformRecoveryAfterRouteFailure(NPC npc)
    {
        AITeammateSystem system = AITeammateSystem.Instance;
        return routeFailureCode == "frontier_exhausted" && routeSnapshot?.UniqueExpanded <= 64 && system.MayPreviewTemporaryPlatforms
            ? InspectPlatformRecovery(npc, (x, y) => system.InspectConstructionProtection(x, y, 1, 1)) : null;
    }

    internal object? InspectPlatformRecovery(NPC npc, Func<int, int, GoalConstructionProtection?> inspectProtection)
    {
        if (!TilePathfinder.TryResolveGroundedLaunch(npc.Center, npc.Bottom.Y, npc.velocity.Y, out Point start) ||
            !TilePathfinder.TryGetStandPose(start, out TileStandPose actualLaunch)) return null;
        bool AllowsCell(Point cell, TileStandPose from)
        {
            if (!WorldGen.InWorld(cell.X, cell.Y, 3)) return false;
            Tile tile = Main.tile[cell.X, cell.Y];
            if (tile.HasTile || tile.IsActuated || tile.LiquidAmount != 0 || !HasAdjacentSupport(cell.X, cell.Y)) return false;
            var footprint = new Rectangle(cell.X * 16, cell.Y * 16, 16, 16);
            Vector2 center = new(footprint.Center.X, footprint.Center.Y);
            if (Vector2.DistanceSquared(from == actualLaunch ? npc.Center : from.Center, center) > 112f * 112f ||
                Main.npc.Any(actor => actor.active && actor.Hitbox.Intersects(footprint)) ||
                Main.player.Any(actor => actor.active && !actor.dead && actor.Hitbox.Intersects(footprint))) return false;
            if (inspectProtection(cell.X, cell.Y) is not { } protection) return false;
            // Removing an object's support and adding a platform to a dry, clear
            // cell are different operations. Prefer the same mask used by Place.
            return protection.Available || protection.Required
                ? protection is { Available: true, Covered: true, Allowed: true }
                : !IsPartOfHouseOrStructure(cell.X, cell.Y);
        }

        PlatformRisePreview? preview = TilePathfinder.PreviewPlatformRise(start, AllowsCell);
        if (preview == null) return null;
        int owned = Loadout.CountItem(ItemID.WoodPlatform);
        int wood = Loadout.CountItem(ItemID.Wood);
        Recipe? platformRecipe = CompanionCrafting.FindRecipes(ItemID.WoodPlatform.ToString()).FirstOrDefault(recipe =>
            recipe.createItem.stack == 2 && !recipe.requiredTile.Any(tile => tile >= 0) && CompanionCrafting.ConditionsMet(recipe) &&
            CompanionCrafting.Requirements(recipe) is { Count: 1 } ingredients &&
            ingredients[0].Count == 1 && ingredients[0].AcceptedItems.Contains(ItemID.Wood));
        object Stand(TileStandPose pose) => new { x = pose.Node.X, y = pose.Node.Y, feetTileY = pose.Bottom / 16f };
        return new
        {
            kind = "temporary_platform_rise_preview", observedAtTick = Main.GameUpdateCount,
            material = new { itemId = ItemID.WoodPlatform, owned, ordinaryWoodOwned = wood, handCraftRecipeId = platformRecipe?.RecipeIndex },
            steps = preview.Steps.Select(step => new { place = new { x = step.Cell.X, y = step.Cell.Y }, explore = Stand(step.Landing) }).ToArray(),
            existingLanding = preview.ExistingLanding is { } landing ? Stand(landing) : null,
            note = "仅是从当前接地站位的通行预检。按需合成木平台，先放一块再走到对应站位；后续放置须从上一站位重新核对。若有现成上沿，可从最后一块平台走或跳上去。地形和原屋保护已按本帧检查，原生放置与抵达仍由执行核验。通行耗材须从成品携带量另计。未检查上沿到远目标的全程。"
        };
    }

    private void NavigateAttackApproach(NPC npc, Vector2 targetPosition, float attackRange, int aimWidth, float speed)
        => NavigateRoute(npc, targetPosition, speed, attackRange, aimWidth);

    private static bool AttackStandValid(TileStandPose stand, Vector2 targetPosition, float attackRange, int aimWidth)
    {
        if (!TilePathfinder.MatchesStandPose(stand)) return false;
        Vector2 center = stand.Center;
        return CompanionMovement.InAttackPosition(center.X - targetPosition.X, center.Y - targetPosition.Y,
            attackRange, CompanionAim.TryFindVisiblePoint(center, targetPosition, aimWidth, out _));
    }

    private void NavigateRoute(NPC npc, Vector2 targetPosition, float speed, float? attackRange = null, int aimWidth = 0)
    {
        string epoch = WorldKnowledgeObserver.LoadEpoch;
        if (routeEpoch != epoch)
        {
            ResetRouteNavigation(); navPath = null; navBuiltAt = 0;
            routeEpoch = epoch;
        }
        bool attackApproach = attackRange.HasValue;
        if (routeDefence != autoDefend || routeAttackApproach != attackApproach ||
            attackApproach && Math.Abs(routeAttackRange - attackRange!.Value) > 0.5f)
        {
            InvalidateRoutePath(); navHasTarget = false; routeSnapshot = null;
            routeDefence = autoDefend; routeFailureCode = "";
            routeAttackApproach = attackApproach; routeAttackRange = attackRange ?? 0f;
            navigationNoProgressTicks = 0; navigationBestDistance = float.MaxValue;
            attackApproachStartedAt = null;
        }
        if (attackApproach && !autoDefend)
        {
            attackApproachStartedAt ??= Main.GameUpdateCount;
            if (Main.GameUpdateCount - attackApproachStartedAt.Value > 20 * 60)
            {
                FailNavigation(npc, "attack_approach_timeout", "连续20秒未取得实际射程和视线；目标未被击败，需要重新选择接近方式。");
                return;
            }
        }
        requestedNavGoal = targetPosition;
        if (!RevalidateFrozenRoute(npc)) return;
        bool routeRemaining = navPath != null && navIndex < navPath.Count;
        float targetShift = Vector2.Distance(targetPosition, navGoal);
        bool targetMoved = !navHasTarget || NavigationTravelBudget.TargetNeedsRefresh(targetShift, routeRemaining);
        bool shiftedFar = targetShift > 128f;
        // Keep a useful route during small owner movements. This avoids restarting a
        // distant follow search every few frames while the player is walking.
        bool mustRetarget = targetMoved && (!routeRemaining || shiftedFar);
        if (routeAttackApproach)
        {
            TileStandPose? selected = routeSnapshot?.Status == "found" ? navGoalPose : null;
            bool selectedValid = selected.HasValue && AttackStandValid(selected.Value, targetPosition, routeAttackRange, aimWidth);
            bool closeToSelected = selected.HasValue && Vector2.DistanceSquared(npc.Center, selected.Value.Center) <= 24f * 24f;
            // A short enemy move can invalidate the final aim without invalidating
            // the stairs or doors on the way there. Finish that usable approach.
            bool refreshAim = routeRemaining ? !selectedValid && shiftedFar : !selectedValid || !closeToSelected;
            mustRetarget = !navHasTarget || Main.GameUpdateCount >= navigationRetryAt &&
                selected.HasValue && refreshAim;
        }
        if (routeSearch == null && (mustRetarget || navPath == null && Main.GameUpdateCount >= navigationRetryAt))
        {
            // A falling actor may be within the support tolerance of a stair
            // before collision settles it. Keep the current approach until then.
            if (!IsGrounded(npc) || attackApproach && Math.Abs(npc.velocity.Y) > 0.6f)
            {
                if (routeRemaining) FollowPath(npc, speed);
                else npc.velocity.X *= 0.65f;
                return;
            }
            if (routeRecoveryRequested && ++routeReplans > 24)
            {
                FailNavigation(npc, "route_retry_budget", "本次行走的有限重规划预算已用完；原目标未改变，需要重新判断通路。");
                return;
            }
            foreach (TileTransition old in failedTransitions.Where(p => Main.GameUpdateCount - p.Value > 1800).Select(p => p.Key).ToArray())
                failedTransitions.Remove(old);
            foreach (Point old in blockedWaypoints.Where(p => Main.GameUpdateCount - p.Value > 1800).Select(p => p.Key).ToArray())
            {
                blockedWaypoints.Remove(old);
                unsettledFiringStands.Remove(old);
            }
            ClearStaleStandFailures();
            routeRecoveryRequested = false; routeSearchCount++;
            IReadOnlySet<Point>? blockedNodes = blockedWaypoints.Count == 0 ? null : new HashSet<Point>(blockedWaypoints.Keys);
            IReadOnlySet<TileTransition>? blockedEdges = failedTransitions.Count == 0 ? null : new HashSet<TileTransition>(failedTransitions.Keys);
            Vector2 frozenTarget = targetPosition;
            // Starting a replacement search retires the old path and its poses
            // together. A multi-tick search must not expose half of that route.
            if (navPath != null) InvalidateRoutePath();
            navGoalPose = null;
            routeSearch = routeAttackApproach
                ? TilePathfinder.BeginSearchForApproach(npc.Center, frozenTarget, routeAttackRange,
                    center => CompanionAim.TryFindVisiblePoint(center, frozenTarget, aimWidth, out _), blockedNodes, blockedEdges,
                    rejectedFiringStands: unsettledFiringStands)
                : TilePathfinder.BeginSearch(npc.Center, targetPosition,
                    radiusX: action == AIActionType.Explore && routeWidenedHorizontally ? TilePathfinder.WideRoutePaddingX : 64,
                    blockedNodes: blockedNodes, blockedTransitions: blockedEdges);
            searchStartedAt = Main.GameUpdateCount; navGoal = targetPosition; navHasTarget = true;
            routeEpoch = epoch; endpointSettleTicks = 0; routeFailureCode = "";
        }
        if (routeSearch != null)
        {
            TilePathSearch search = routeSearch;
            routeSnapshot = search.Advance(64, 2);
            if (routeSnapshot.Status == "searching")
            {
                npc.velocity.X *= 0.65f;
                if (Main.GameUpdateCount - searchStartedAt > 600)
                    FailNavigation(npc, "search_time_budget", "本次搜索达到十秒计算时限，尚未证明到目标有路；目标与地形没有被改写。");
                return;
            }
            routeSearch = null;
            if (routeSnapshot.Status == "invalidated")
            {
                var changed = new HashSet<Point>();
                if (routeSnapshot.InvalidatedPoseNode is { } invalidated) changed.Add(invalidated);
                ReplanChangedRoutePose(npc, changed, "search_stand_pose_changed");
                return;
            }
            if (routeSnapshot.Status != "found")
            {
                AITeammateSystem.Instance.TestLog("navigation search ended: " + routeSnapshot.Diagnostic);
                if (!routeWidenedHorizontally && action == AIActionType.Explore && exploreHasCoordinates &&
                    !routeAttackApproach && !autoDefend && search.ShouldWidenHorizontalEnvelope)
                {
                    // Try one wider corridor around exactly the same requested
                    // launch and destination. Current physical edges stay excluded.
                    routeWidenedHorizontally = true;
                    routeSearchCount++;
                    routeSearch = TilePathfinder.BeginSearch(routeSnapshot.RequestedStart, routeSnapshot.RequestedTarget,
                        radiusX: TilePathfinder.WideRoutePaddingX,
                        blockedNodes: blockedWaypoints.Count == 0 ? null : new HashSet<Point>(blockedWaypoints.Keys),
                        blockedTransitions: failedTransitions.Count == 0 ? null : new HashSet<TileTransition>(failedTransitions.Keys));
                    searchStartedAt = Main.GameUpdateCount;
                    npc.velocity.X *= 0.65f;
                    AITeammateSystem.Instance.TestLog($"navigation widened horizontally to padding={TilePathfinder.WideRoutePaddingX} at same destination");
                    return;
                }
                navigationFailures++;
                FailNavigation(npc, routeSnapshot.Status,
                    $"本次有界寻路未给出完整路线（{routeSnapshot.Status}，展开{routeSnapshot.UniqueExpanded}）；这不证明整个世界不可达。");
                return;
            }
            if (!autoDefend && action == AIActionType.Explore && exploreStandDestination.HasValue &&
                (routeSnapshot.Goal != exploreStandDestination || routeSnapshot.GoalPose != exploreStandPose))
            {
                FailNavigation(npc, "destination_projection_changed", "路径的投影端点与原先选定站位不符；没有把另一个地点当成目标抵达。");
                return;
            }
            if (routeSnapshot.Path is not { } path || routeSnapshot.PathPoses is not { } poses || poses.Count != path.Count ||
                routeSnapshot.GoalPose is not { } frozenGoal || routeSnapshot.Goal != frozenGoal.Node ||
                path.Where((node, index) => poses[index].Node != node).Any())
            {
                FailNavigation(npc, "path_pose_missing", "原生路径没有完整、对应的冻结站姿序列；没有重新解析来填补旧结果。");
                return;
            }
            navPath = path.ToList(); navPathPoses = poses.ToArray(); navGoalPose = frozenGoal; navIndex = 0;
            navBuiltAt = Main.GameUpdateCount; navigationRetryAt = Main.GameUpdateCount + 30;
            pathBlockedTicks = 0; lastPathPosition = npc.Center;
            AITeammateSystem.Instance.TestLog($"path found tiles={navPath.Count} unique={routeSnapshot.UniqueExpanded} replans={routeReplans}");
        }
        if (!RevalidateFrozenRoute(npc)) return;
        if (navPath != null && navIndex < navPath.Count)
        {
            endpointSettleTicks = 0; FollowPath(npc, speed); return;
        }
        if (navPath == null) { npc.velocity.X *= 0.65f; return; }

        TileStandPose? goal = navGoalPose;
        bool attackSight = routeAttackApproach && CompanionAim.TryFindVisiblePoint(npc.Center, targetPosition, aimWidth, out _);
        bool actualArrival = routeAttackApproach
            ? CompanionMovement.InAttackPosition(npc.Center.X - targetPosition.X, npc.Center.Y - targetPosition.Y,
                routeAttackRange, attackSight)
            : goal.HasValue && CompanionMovement.ReachedTerminalWaypoint(goal.Value.Center.X - npc.Center.X,
                    goal.Value.Bottom - npc.Bottom.Y, npc.velocity.Y, IsGrounded(npc), HasSlopedSupport(npc));
        if (actualArrival) { endpointSettleTicks = 0; npc.velocity.X = routeAttackApproach ? npc.velocity.X * 0.65f : 0f; return; }
        if (routeAttackApproach && goal is { } firingStand && !AttackStandValid(firingStand, targetPosition, routeAttackRange, aimWidth))
        {
            // The enemy moved away from this stand. Wait for the next aim refresh
            // instead of reporting an actual landing failure against a stale target.
            endpointSettleTicks = 0;
            npc.velocity.X *= 0.65f;
            return;
        }
        // A completed projection is never arrival evidence. Briefly settle an actual
        // landing / align on a shaped floor, then report the mismatch explicitly.
        if (++endpointSettleTicks > 30)
        {
            if (routeAttackApproach && goal is { } unsettled)
            {
                RejectUnsettledFiringStand(npc, unsettled, targetPosition, attackSight);
                return;
            }
            FailNavigation(npc, "endpoint_not_physically_reached", "图路径已经耗尽，但人物没有真实落到目标站位；不能当作已到达。");
            return;
        }
        float dx = goal.HasValue ? goal.Value.Center.X - npc.Center.X : 0f;
        if (IsGrounded(npc) && Math.Abs(dx) > 0.5f && Math.Abs(dx) < 24)
        { npc.velocity.X = Math.Clamp(dx / 6, -1.5f, 1.5f); FaceNavigation(npc, Math.Sign(dx)); }
        else npc.velocity.X *= 0.65f;
        if (!routeAttackApproach && goal.HasValue)
            npc.velocity.X = CompanionMovement.BrakeAtTerminalWaypoint(dx,
                goal.Value.Bottom - npc.Bottom.Y, npc.velocity.X, HasSlopedSupport(npc));
    }

    private void RejectUnsettledFiringStand(NPC npc, TileStandPose unsettled, Vector2 targetPosition, bool attackSight)
    {
        // This projected firing position did not work for the actual body.
        // Keep it traversable but seek another endpoint under the recovery budget.
        unsettledFiringStands.Add(unsettled.Node);
        blockedWaypoints[unsettled.Node] = Main.GameUpdateCount;
        RememberFailedStandPose(navPath!.Count - 1);
        InvalidateRoutePath(); navGoalPose = null;
        routeRecoveryRequested = true; navigationRetryAt = Main.GameUpdateCount + 1;
        npc.velocity.X *= 0.65f;
        AITeammateSystem.Instance.TestLog($"firing stand not physically reached: tile={unsettled.Node.X},{unsettled.Node.Y} actor={npc.Center.X:0.##},{npc.Center.Y:0.##} " +
            $"planned={unsettled.Center.X:0.##},{unsettled.Center.Y:0.##} range={routeAttackRange:0.##} distance={Vector2.Distance(npc.Center, targetPosition):0.##} sight={attackSight}; try another stand");
    }

    private void RejectCurrentTransition(NPC npc, Point waypoint, Point standing)
    {
        Point from = navPath != null && navIndex > 0 ? navPath[navIndex - 1] : standing;
        var edge = new TileTransition(from, waypoint);
        if (failedTransitions.Count >= 256) failedTransitions.Remove(failedTransitions.MinBy(p => p.Value).Key);
        failedTransitions[edge] = Main.GameUpdateCount;
        RememberFailedStandPose(Math.Max(0, navIndex - 1));
        RememberFailedStandPose(navIndex);
        routeRecoveryRequested = true;
        blockedWaypoints[waypoint] = Main.GameUpdateCount;
        AITeammateSystem.Instance.TestLog($"path transition stalled from={from.X},{from.Y} to={waypoint.X},{waypoint.Y}; retain goal and replan");
        InvalidateRoutePath(); navigationRetryAt = Main.GameUpdateCount + 1;
        pathBlockedTicks = 0; npc.velocity.X *= 0.5f;
    }
}
