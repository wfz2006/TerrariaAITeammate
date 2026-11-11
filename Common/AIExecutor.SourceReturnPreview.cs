using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

public partial class AIExecutor
{
    // Independent observation only: preserve the explicit source's forward
    // admission and do not attach a preview anchor to a future Mine action.
    private JsonObject InspectSourceWithReturnPreview(NPC observer, string resource, int x, int y,
        MiningReturnAnchor observerStart)
    {
        JsonObject forward = JsonSerializer.SerializeToNode(InspectMiningInteraction(observer, resource, x, y))!.AsObject();
        var origin = new JsonObject
        {
            ["worldId"] = observerStart.WorldId, ["epoch"] = observerStart.Epoch,
            ["capturedAtTick"] = observerStart.ObservedAtTick,
            ["centerTileX"] = observerStart.Center.X / 16f, ["centerTileY"] = observerStart.Center.Y / 16f,
            ["feetTileY"] = observerStart.FeetY / 16f, ["grounded"] = observerStart.Grounded,
            ["standX"] = observerStart.Stand?.Node.X, ["standRow"] = observerStart.Stand?.Node.Y,
            ["standFeetTileY"] = observerStart.Stand?.FeetY / 16f,
            ["meaning"] = "actual_pose_at_start_of_this_readonly_sources_query_not_player_or_future_mine_anchor"
        };
        forward["returnToObserverStart"] = MiningSourceReturnPreview.Observe(forward, origin, selectedStand =>
        {
            Vector2 from = selectedStand == null ? observer.Center : TilePathfinder.StandCenter(
                new Point(selectedStand["x"]!.GetValue<int>(), selectedStand["standRow"]!.GetValue<int>()), observer.height);
            // This native helper performs at most one exact-stand reverse graph
            // search. Observe reserves that slot within the original16 forward
            // plus reverse probes; an exhausted forward budget never calls here.
            var budget = new MiningRouteProbeCache();
            MiningReturnCheck check = ProbeAutomaticMiningReturn(observer, from, observerStart, budget);
            return JsonSerializer.SerializeToNode(new
            {
                status = check.Status, returnProvenInCurrentBoundedGraph = check.ProvenInCurrentGraph,
                worldId = Main.worldID, epoch = WorldKnowledgeObserver.LoadEpoch,
                observedAtTick = check.ObservedAtTick, routeProbeCount = budget.Count,
                pathWaypointCount = check.WaypointCount, diagnostic = check.Diagnostic,
                fromActualPose = selectedStand == null,
                endpoint = check.Endpoint == null ? null : new
                {
                    x = check.Endpoint.Node.X, standRow = check.Endpoint.Node.Y,
                    centerTileX = check.Endpoint.Center.X / 16f, centerTileY = check.Endpoint.Center.Y / 16f,
                    feetTileY = check.Endpoint.FeetY / 16f
                },
                worldModified = false, actorsMoved = false, inventoryModified = false, miningExecuted = false
            })!.AsObject();
        });
        return forward;
    }
}
