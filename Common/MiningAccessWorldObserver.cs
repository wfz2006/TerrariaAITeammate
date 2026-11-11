using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

/// <summary>Sample-cell access facts only; never invokes graph search, tool use or source selection.</summary>
public static class MiningAccessWorldObserver
{
    public static object Observe(NPC observer, int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 3) || !observer.active || !float.IsFinite(observer.Center.X) || !float.IsFinite(observer.Center.Y))
            return new { geometryStatus = "not_tested", geometricWorkStandCount = (int?)null,
                reason = "sample_outside_world_or_actual_actor_unavailable" };
        var timer = Stopwatch.StartNew();
        try
        {
            MiningTerrainCell cell = NavigationWorldObserver.ReadMiningCell(x, y);
            Vector2 target = new(x * 16f + 8, y * 16f + 8);
            MiningApproachGeometry geometry = MiningApproachProbe.EnumerateGeometry(observer.Center, observer.height, target,
                TilePathfinder.IsStableStand, (node, height) => new MiningApproachStand(node,
                    TilePathfinder.StandCenter(node, height), TilePathfinder.StandBottom(node)));
            var stands = geometry.Candidates.Take(3).Select(stand => new
            {
                x = stand.Node.X, standRow = stand.Node.Y, centerTileX = stand.Center.X / 16f,
                centerTileY = stand.Center.Y / 16f, feetTileY = stand.FeetY / 16f
            }).ToArray();
            return new
            {
                geometryStatus = "measured_current_window",
                geometryFinding = geometry.Candidates.Count == 0 ? "no_current_geometry" : "geometry_exists_route_not_tested",
                geometricWorkStandCount = geometry.Candidates.Count, geometry.GridChecks,
                standStabilityHorizontalMarginPixels = TilePathfinder.StableStandHorizontalTolerance,
                enumerationComplete = geometry.GridChecks == MiningApproachProbe.MaximumGeometryChecks,
                actualObserver = new { centerTileX = observer.Center.X / 16f, centerTileY = observer.Center.Y / 16f },
                actualDistancePixels = geometry.ActualDistancePixels,
                actualWithinToolRangeByDistance = geometry.ActualInToolReach,
                maximumToolRangePixels = MiningApproachProbe.ToolReachPixels,
                structureProtected = cell.StructureProtected, nativeCanKill = cell.NativeCanKill, actuated = cell.Actuated,
                standSamples = stands, standSamplesOmitted = Math.Max(0, geometry.Candidates.Count - stands.Length),
                route = "not_tested", tool = "not_tested", returnToOrigin = "not_tested", cascade = "not_tested",
                observedAtTick = Main.GameUpdateCount, elapsedMilliseconds = Math.Round(timer.Elapsed.TotalMilliseconds, 3)
            };
        }
        catch (Exception exception)
        {
            return new { geometryStatus = "not_tested", geometricWorkStandCount = (int?)null,
                reason = "native_read_unavailable_" + exception.GetType().Name,
                observedAtTick = Main.GameUpdateCount, elapsedMilliseconds = Math.Round(timer.Elapsed.TotalMilliseconds, 3) };
        }
    }
}
