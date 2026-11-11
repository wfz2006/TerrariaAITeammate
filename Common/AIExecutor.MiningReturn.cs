using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

public partial class AIExecutor
{
    private readonly MiningTaskReturnState miningTaskReturn = new();
    private MiningReturnCheck? miningLastReturn;
    private string miningLastReturnPhase = "not_checked";
    private int miningActualReturnChecks;

    private void ResetMiningReturn()
    { miningTaskReturn.Reset(); miningLastReturn = null; miningLastReturnPhase = "not_checked"; miningActualReturnChecks = 0; }

    private MiningReturnAnchor EnsureMiningReturnAnchor(NPC npc)
        => miningTaskReturn.CaptureOnce(currentPlan ?? (object)this, () => CaptureMiningReturnAnchor(npc));

    private static MiningReturnAnchor CaptureMiningReturnAnchor(NPC npc, bool preview = false)
    {
        bool grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
        MiningApproachStand? stand = null;
        if (grounded && TilePathfinder.TryResolveGroundedLaunch(npc.Center, npc.Bottom.Y, npc.velocity.Y, out Point resolved) &&
            TilePathfinder.TryGetStandPose(resolved, out TileStandPose pose) &&
            CompanionMovement.ReachedStand(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, grounded,
                resolved.X, resolved.Y, pose.Bottom, pose.Center.X))
            stand = new(pose.Node, pose.Center, pose.Bottom);
        return new(npc.Center, npc.Bottom.Y, grounded, stand, WorldKnowledgeObserver.LoadEpoch, Main.worldID, Main.GameUpdateCount) { IsPreview = preview };
    }

    private bool AwaitAutomaticMiningLanding(NPC npc)
    {
        if (IsDirectedMining) return false;
        bool grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
        // Support proximity can be true just before native collision lands the body.
        // Match TryResolveGroundedLaunch's existing speed prerequisite; waiting is
        // charged to the unchanged task idle budget, not treated as travel progress.
        if (grounded && Math.Abs(npc.velocity.Y) <= 0.6f) return false;
        if (!grounded && miningStand.HasValue) Navigate(npc, miningStand.Value, 4.5f);
        else npc.velocity.X *= 0.7f;
        return true;
    }

    private static MiningReturnCheck ProbeAutomaticMiningReturn(NPC npc, Vector2 from, MiningReturnAnchor anchor, MiningRouteProbeCache budget)
    {
        MiningReturnCheck Result(string status, string diagnostic, int? count = null, MiningApproachStand? end = null)
            => new(status, diagnostic, count, end) { ObservedAtTick = Main.GameUpdateCount };
        if (anchor.WorldId != Main.worldID || anchor.Epoch != WorldKnowledgeObserver.LoadEpoch || anchor.Epoch.Length == 0)
            return Result("origin_world_invalidated", "Task origin belongs to an earlier world load; it was not moved to the current pose.");
        bool actualPose = from == npc.Center;
        bool grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
        if (actualPose && grounded && anchor.Grounded && Vector2.DistanceSquared(from, anchor.Center) <= 0.01f && Math.Abs(npc.Bottom.Y - anchor.FeetY) <= 0.01f)
            return Result("already_at_task_origin", "Actual grounded pose is still the captured task origin; no return travel is necessary.", 0, anchor.Stand);
        if (anchor.Stand == null) return Result("origin_stand_unresolved", "No physically matching origin stand was captured; return remains unknown.");
        var originPose = new TileStandPose(anchor.Stand.Node, anchor.Stand.Center, anchor.Stand.FeetY);
        if (!TilePathfinder.MatchesStandPose(originPose))
            return Result("origin_stand_pose_changed", "The fixed origin's captured body/contact pose changed; its tile identity was not moved to a new center.");
        if (actualPose && !grounded) return Result("actual_pose_not_grounded", "An airborne projection is not a proven physical return launch point.");
        Point launch;
        if (actualPose)
        {
            if (!TilePathfinder.TryResolveGroundedLaunch(from, npc.Bottom.Y, npc.velocity.Y, out launch))
            {
                string nearest = TilePathfinder.TryResolveStand(from, out Point projected) ?
                    $"{projected.X},{projected.Y} feet={TilePathfinder.StandBottom(projected) / 16f}" : "unresolved";
                return Result("actual_return_start_mismatch", $"No nearby graph launch has a validated continuous walk from the actual grounded body/contact; return remains unknown. actualCenter={from.X / 16f},{from.Y / 16f} actualFeet={npc.Bottom.Y / 16f} vy={npc.velocity.Y} grounded={grounded} bodyClear={TilePathfinder.IsBodyClear(from.X, npc.Bottom.Y)} nearestProjection={nearest}");
            }
        }
        else if (!TilePathfinder.TryResolveStand(from, out launch) || !TilePathfinder.TryGetStandPose(launch, out TileStandPose plannedLaunch) ||
            Vector2.DistanceSquared(plannedLaunch.Center, from) > 0.01f)
            return Result("planned_return_start_mismatch", "The measured outbound endpoint is no longer the same exact graph stand; return remains unknown.");
        if (!TilePathfinder.TryGetStandPose(launch, out TileStandPose launchPose))
            return Result("return_start_pose_unresolved", "No exact body/contact pose remains at the measured return launch.");
        return budget.GetOrFindReturn(from, anchor.Center, start =>
        {
            var route = TilePathfinder.FindPathBetweenStands(launch, anchor.Stand.Node);
            string diagnostic = TilePathfinder.LastDiagnostic + (actualPose ?
                $" actual_grounded_walk_prefix={from.X / 16f},{npc.Bottom.Y / 16f}->stand{launch.X},{launch.Y} centerX={launchPose.Center.X / 16f} feet={launchPose.Bottom / 16f}" : " exact_outbound_endpoint_launch");
            if (route == null) return Result(diagnostic.Split(' ', 2)[0], diagnostic);
            Point endpoint;
            if (route.Count > 0) endpoint = route[^1];
            else endpoint = launch;
            if (!TilePathfinder.MatchesStandPose(launchPose) || !TilePathfinder.MatchesStandPose(originPose))
                return Result("return_pose_changed", "A captured launch/origin pose changed during the bounded return query; no new center replaced it.");
            if (endpoint != originPose.Node)
                return Result("return_endpoint_mismatch", "Returned graph endpoint is not the fixed captured origin. " + diagnostic, route.Count);
            var end = new MiningApproachStand(endpoint, originPose.Center, originPose.Bottom);
            float startFeet = actualPose ? npc.Bottom.Y : start.Y + npc.height / 2f;
            if (Math.Abs(end.FeetY - anchor.FeetY) > 5f ||
                route.Count == 0 && Math.Abs(startFeet - end.FeetY) > 5f)
                return Result("return_endpoint_mismatch", "Projected return endpoint/contact does not match the fixed task origin. " + diagnostic, route.Count, end);
            return Result("found", diagnostic, route.Count, end);
        });
    }

    private bool VerifyAutomaticMiningReturnFromActualPose(NPC npc, string phase)
    {
        if (IsDirectedMining) return true;
        // Fresh local budget: no proof survives a previous excavation or is borrowed
        // from the planned work stand while the actual actor has landed elsewhere.
        var budget = new MiningRouteProbeCache();
        miningLastReturn = ProbeAutomaticMiningReturn(npc, npc.Center, EnsureMiningReturnAnchor(npc), budget);
        miningLastReturnPhase = phase; miningActualReturnChecks++;
        if (miningLastReturn.ProvenInCurrentGraph) return true;
        miningRouteFailure = new { reason = "return_to_task_origin_not_proven", phase, returnSafety = DescribeMiningReturnSafety() };
        FinishMiningResult(npc, false, "自动采集尚未证明能从当前真实站位回到本次任务起点；已保留实际产物，不继续挖掘或偷偷更换起点。详见returnSafety；这不代表整个目标不可完成。");
        return false;
    }

    private object DescribeMiningReturnSafety()
    {
        MiningReturnAnchor? anchor = currentPlan != null && miningTaskReturn.IsOwner(currentPlan) ? miningTaskReturn.Anchor : null;
        return DescribeMiningReturnSafety(IsDirectedMining ? null : anchor, IsDirectedMining ? null : miningLastReturn,
            IsDirectedMining ? "explicit_selection_not_restricted" : miningLastReturnPhase, miningActualReturnChecks, IsDirectedMining);
    }

    private static object DescribeMiningReturnSafety(MiningReturnAnchor? anchor, MiningReturnCheck? check, string phase,
        int actualPoseChecks = 0, bool explicitSelection = false) => new
    {
        requiredForAutomaticSelection = !explicitSelection,
        returnStatus = explicitSelection ? "not_checked_explicit_selection" : check?.Status ?? "not_checked",
        returnProvenInCurrentBoundedGraph = !explicitSelection && check?.ProvenInCurrentGraph == true,
        anchor = anchor == null ? null : new
        {
            worldId = anchor.WorldId, epoch = anchor.Epoch, capturedAtTick = anchor.ObservedAtTick,
            centerTileX = anchor.Center.X / 16f, centerTileY = anchor.Center.Y / 16f, feetTileY = anchor.FeetY / 16f,
            grounded = anchor.Grounded, standX = anchor.Stand?.Node.X, standRow = anchor.Stand?.Node.Y,
            standCenterTileX = anchor.Stand?.Center.X / 16f, standCenterTileY = anchor.Stand?.Center.Y / 16f,
            standFeetTileY = anchor.Stand?.FeetY / 16f, meaning = anchor.IsPreview ? "current_readonly_preview_origin_not_a_future_task_anchor"
                : "fixed_beginning_of_this_mine_action_not_current_pose_or_player"
        },
        checkedAtTick = check?.ObservedAtTick, phase, actualPoseChecks, pathWaypointCount = check?.WaypointCount, diagnostic = check?.Diagnostic,
        note = explicitSelection ? "Explicit coordinates remain the model's selected source; no automatic return filter or source substitution was applied. Return was not checked."
            : "Only a current bounded graph return to this Mine action's fixed origin is checked. Forward and return probes share32 searches per selection. Unknown/exhausted is not safe or globally unreachable. Actual pose is rechecked with a fresh budget before excavation; removing supports or later world changes may still alter returnability. This does not execute a return or plan the next goal."
    };
}
