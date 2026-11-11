using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrariaAITeammate.Common;

public sealed record ShelterProbePoint(int x, int y);
public sealed record ShelterProbeBounds(int left, int top, int width, int height);
public sealed record ShelterGroundScan(int fromY, int toY);
public sealed record ShelterProbeOrigin(int x, int feetY, int direction);
public sealed record ShelterSiteCandidate(int centerX, int feetReferenceY, string status, int? groundY,
    ShelterProbePoint? resolvedTopLeft, ShelterProbePoint? blockingCell, int? blockingTileType, bool? actorIntersects);
public sealed record ShelterSiteScan(string mode, ShelterProbePoint? requestedTopLeft, ShelterProbeOrigin origin,
    ShelterGroundScan groundScan, ShelterProbeBounds requestedReadBounds, int candidateCount, int acceptedCandidateCount,
    IReadOnlyList<ShelterSiteCandidate> candidates, string scope);

/// <summary>
/// Exact existing shelter geometry predicates, shared by inspection and execution.
/// Its SiteCell.Solid input is the legacy raw tileSolid flag, including whatever
/// types the native table marks solid; this is not a safe-standing/path test.
/// Delegates only read. No placement, route, material or native housing check occurs.
/// </summary>
public sealed class ShelterSiteProbe
{
    public static bool AllowsConstruction(bool required, bool available, bool covered, bool? allowed)
        => !required || available && covered && allowed == true;

    private readonly int width, height;
    private readonly Func<int, int, bool> inWorld, replaceablePlant;
    private readonly Func<int, int, SiteCell> read;
    private readonly Func<int, int, int, int, bool> actorIntersects;

    public ShelterSiteProbe(int width, int height, Func<int, int, bool> inWorld, Func<int, int, SiteCell> read,
        Func<int, int, bool> replaceablePlant, Func<int, int, int, int, bool> actorIntersects)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        this.width = width; this.height = height; this.inWorld = inWorld; this.read = read;
        this.replaceablePlant = replaceablePlant; this.actorIntersects = actorIntersects;
    }

    public static IEnumerable<int> AutomaticCenters(int actorX, int direction)
    {
        int facing = direction == 0 ? 1 : Math.Sign(direction);
        int[] distances = { 10, 12, 14, 16, 18, 20, 24, 28, 8, 6 };
        foreach (int sign in new[] { facing, -facing })
            foreach (int distance in distances) yield return actorX + sign * distance;
    }

    public ShelterSiteScan Inspect(int actorX, int feetY, int direction, int? requestedLeft = null, int? requestedTop = null)
    {
        if (requestedLeft.HasValue != requestedTop.HasValue) throw new ArgumentException("Shelter x/y must be paired.");
        int referenceFeet = requestedTop.HasValue ? requestedTop.Value + height : feetY;
        int[] centers = requestedLeft.HasValue ? new[] { requestedLeft.Value + width / 2 } : AutomaticCenters(actorX, direction).ToArray();
        ShelterSiteCandidate[] results = centers.Select(center => ProbeAt(center, referenceFeet)).ToArray();
        int left = centers.Min() - width / 2, right = centers.Max() - width / 2 + width - 1;
        int scanTop = referenceFeet - 3, scanBottom = scanTop + 16;
        return new ShelterSiteScan(requestedLeft.HasValue ? "explicit_ground_projection" : "automatic_current_actor",
            requestedLeft.HasValue ? new ShelterProbePoint(requestedLeft.Value, requestedTop!.Value) : null,
            new ShelterProbeOrigin(actorX, feetY, direction), new ShelterGroundScan(scanTop, scanBottom),
            new ShelterProbeBounds(left, scanTop - height, right - left + 1, scanBottom - (scanTop - height) + 1),
            results.Length, results.Count(result => result.status == "geometry_passed"), Array.AsReadOnly(results),
            "Requested read envelope, not every cell read: first ground is sought inclusively from groundScan.fromY through toY in each candidate column; only world-valid cells are read. Candidate order and first failing predicate match the existing build skill. Geometry passed does not prove a reachable work stand, safe surrounding travel, native object placement or valid housing. Outside this bounded search is unknown.");
    }

    public ShelterSiteCandidate ProbeAt(int centerX, int feetY)
    {
        int startY = feetY - 3;
        if (!TryGround(centerX, startY, out int groundY))
            return new(centerX, feetY, "ground_not_found_at_center", null, null, null, null, null);
        int left = centerX - width / 2, top = groundY - height;
        var resolved = new ShelterProbePoint(left, top);
        if (actorIntersects(left, top, width, height))
            return new(centerX, feetY, "actor_intersection", groundY, resolved, null, null, true);
        for (int x = left; x < left + width; x++)
        {
            if (!TryGround(x, startY, out int columnGround))
                return new(centerX, feetY, "ground_column_not_found", groundY, resolved, new(x, startY), null, false);
            if (Math.Abs(columnGround - groundY) > 1)
                return new(centerX, feetY, "ground_height_mismatch", groundY, resolved, new(x, columnGround), null, false);
            for (int y = top; y < top + height; y++)
            {
                if (!inWorld(x, y)) return new(centerX, feetY, "outside_world_bounds", groundY, resolved, new(x, y), null, false);
                SiteCell cell = read(x, y);
                if (cell.HasTile && !replaceablePlant(x, y))
                    return new(centerX, feetY, "occupied_tile", groundY, resolved, new(x, y), cell.TileType, false);
                if (cell.LiquidAmount > 0)
                    return new(centerX, feetY, "liquid_in_footprint", groundY, resolved, new(x, y), cell.HasTile ? cell.TileType : null, false);
            }
        }
        return new(centerX, feetY, "geometry_passed", groundY, resolved, null, null, false);
    }

    private bool TryGround(int x, int startY, out int groundY)
    {
        for (int y = startY; y <= startY + 16; y++)
        {
            if (!inWorld(x, y)) continue;
            SiteCell cell = read(x, y);
            if (cell.HasTile && cell.Solid) { groundY = y; return true; }
        }
        groundY = -1;
        return false;
    }
}
