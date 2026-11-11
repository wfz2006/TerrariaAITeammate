using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

/// <summary>A tile identity and its measured body/contact pose; a tile identity alone is not a frozen position.</summary>
public readonly record struct TileStandPose(Point Node, Vector2 Center, float Bottom);
internal sealed record PlatformRiseStep(Point Cell, TileStandPose Landing);
internal sealed record PlatformRisePreview(IReadOnlyList<PlatformRiseStep> Steps, TileStandPose? ExistingLanding);

public static partial class TilePathfinder
{
    private const int MaxNodes = 3000;
    public const float StableStandHorizontalTolerance = 8f;
    public static string LastDiagnostic { get; private set; } = "";

    public static bool HasStandableDestination(Vector2 targetWorld)
        => TryFindStandTile(targetWorld, out _);

    public static bool TryResolveStand(Vector2 targetWorld, out Point stand)
        => TryFindStandTile(targetWorld, out stand);

    public static bool TryResolveStableStand(Vector2 targetWorld, out Point stand)
        => TryFindStandTile(targetWorld, out stand, stableOnly: true);

    /// <summary>A local walk prefix from the measured20x42 grounded body, not nearest-center projection or arrival.</summary>
    public static bool TryResolveGroundedLaunch(Vector2 actualCenter, float actualBottom, float verticalSpeed, out Point stand)
    {
        stand = default;
        if (!float.IsFinite(actualCenter.X) || !float.IsFinite(actualCenter.Y) || !float.IsFinite(actualBottom) ||
            !float.IsFinite(verticalSpeed) || Math.Abs(actualCenter.Y + CompanionMovement.Height / 2f - actualBottom) > 0.1f ||
            Math.Abs(verticalSpeed) > 0.6f || !IsGroundedAt(actualCenter.X, actualBottom, verticalSpeed) ||
            !IsBodyClear(actualCenter.X, actualBottom)) return false;
        int baseX = (int)MathF.Floor(actualCenter.X / 16f), baseRow = (int)MathF.Floor((actualBottom + 1f) / 16f) - 1;
        float bestPlane = float.PositiveInfinity, bestHorizontal = float.PositiveInfinity;
        bool found = false;
        for (int x = baseX - 1; x <= baseX + 1; x++)
            for (int row = baseRow - 1; row <= baseRow + 1; row++)
            {
                if (!IsStandable(x, row)) continue;
                var candidate = new Point(x, row);
                float plane = Math.Abs(StandBottom(candidate) - actualBottom), horizontal = Math.Abs(StandCenter(candidate).X - actualCenter.X);
                if (plane > 16f || plane > bestPlane || plane == bestPlane && horizontal >= bestHorizontal ||
                    !HasWalkingConnection(actualCenter.X, actualBottom, candidate)) continue;
                stand = candidate; bestPlane = plane; bestHorizontal = horizontal; found = true;
            }
        return found;
    }

    /// <summary>Uses validated exact graph endpoints; does not project either contact onto a different surface.</summary>
    public static List<Point>? FindPathBetweenStands(Point start, Point goal, int radiusX = 64, int radiusY = 48)
    {
        TilePathSearch search = BeginSearchBetweenStands(start, goal, radiusX, radiusY, new(MaxNodes, MaxNodes * 2));
        TilePathSearchSnapshot result = search.Advance(MaxNodes, double.PositiveInfinity);
        LastDiagnostic = result.Diagnostic;
        return result.Status == "found" && result.Path != null ? new List<Point>(result.Path) : null;
    }

    internal static TilePathSearch BeginSearchBetweenStands(Point start, Point goal, int radiusX, int radiusY, TilePathSearchLimits limits)
    {
        if (radiusX < 0) throw new ArgumentOutOfRangeException(nameof(radiusX));
        if (radiusY < 0) throw new ArgumentOutOfRangeException(nameof(radiusY));
        Point? validStart = IsStandable(start.X, start.Y) ? start : null;
        Point? validGoal = IsStableStand(goal.X, goal.Y) ? goal : null;
        int minX = (int)Math.Max(5L, Math.Min(start.X, goal.X) - (long)radiusX);
        int maxX = (int)Math.Min((long)Main.maxTilesX - 5, Math.Max(start.X, goal.X) + (long)radiusX);
        int minY = (int)Math.Max(5L, Math.Min(start.Y, goal.Y) - (long)radiusY);
        int maxY = (int)Math.Min((long)Main.maxTilesY - 5, Math.Max(start.Y, goal.Y) + (long)radiusY);
        return new TilePathSearch(StandCenter(start), StandCenter(goal), validStart, validGoal,
            minX, maxX, minY, maxY, limits,
            point => Neighbors(point, minX, maxX, minY, maxY), Heuristic, null, null,
            capturePose: CaptureStandPose, poseMatches: pose => MatchesStandPose(pose) &&
                (pose.Node != goal || IsStableStand(goal.X, goal.Y)));
    }

    public static List<Point>? FindPath(Vector2 startWorld, Vector2 targetWorld, int radiusX = 64, int radiusY = 48,
        IReadOnlySet<Point>? blockedNodes = null)
    {
        TilePathSearch search = BeginSearch(startWorld, targetWorld, radiusX, radiusY, blockedNodes,
            limits: new TilePathSearchLimits(MaxNodes, MaxNodes * 2));
        TilePathSearchSnapshot result = search.Advance(MaxNodes, double.PositiveInfinity);
        LastDiagnostic = result.Diagnostic;
        return result.Status == "found" && result.Path != null ? new List<Point>(result.Path) : null;
    }

    private static IEnumerable<(Point Point, float Cost)> Neighbors(Point current, int minX, int maxX, int minY, int maxY)
    {
        bool gapLeft = false, gapRight = false;
        foreach (int dx in new[] { -1, 1 })
        {
            int nx = current.X + dx;
            if (nx < minX || nx > maxX)
                continue;

            bool walkLevel = HasWalkingConnection(current, new Point(nx, current.Y));
            if (walkLevel)
                yield return (new Point(nx, current.Y), 1f);
            if (current.Y - 1 >= minY && IsPassable(current.X, current.Y - 3) && IsStandable(nx, current.Y - 1) &&
                HasWalkingConnection(current, new Point(nx, current.Y - 1)))
                yield return (new Point(nx, current.Y - 1), 1.5f);
            bool walkDown = current.Y + 1 <= maxY && HasWalkingConnection(current, new Point(nx, current.Y + 1));
            if (!walkLevel && !walkDown)
            {
                if (dx < 0) gapLeft = true;
                else gapRight = true;
            }
            if (walkDown && IsPassable(nx, current.Y - 2))
                yield return (new Point(nx, current.Y + 1), 1.5f);
            else if (IsPassable(nx, current.Y) && IsPassable(nx, current.Y - 1) && IsPassable(nx, current.Y - 2) && !IsSolid(nx, current.Y + 1))
            {
                // A one-row lower half-block can be24px below the current contact:
                // too high for a walking step, but still an ordinary bounded fall.
                for (int ny = current.Y + 1; ny <= Math.Min(current.Y + 18, maxY); ny++)
                {
                    if (!IsBodyClear(nx * 16f + 8, StandBottom(new Point(nx, ny))))
                        break;

                    if (IsStandable(nx, ny) && HasLegacyGridCenter(new Point(nx, ny)))
                    {
                        var landing = new Point(nx, ny);
                        if (TryDropCost(current, landing, lateral: true, out float dropCost))
                            yield return (landing, dropCost);
                        break;
                    }
                }
            }
        }

        if (!IsSolid(current.X, current.Y + 1))
        {
            for (int ny = current.Y + 1; ny <= Math.Min(current.Y + 18, maxY); ny++)
            {
                if (!IsBodyClear(current.X * 16f + 8, StandBottom(new Point(current.X, ny))))
                    break;

                if (IsStandable(current.X, ny) && HasLegacyGridCenter(new Point(current.X, ny)))
                {
                    var landing = new Point(current.X, ny);
                    if (TryDropCost(current, landing, lateral: false, out float dropCost))
                        yield return (landing, dropCost);
                    break;
                }
            }
        }

        if (!TryGetStandPose(current, out TileStandPose launch)) yield break;
        bool hasGap = gapLeft || gapRight;
        int lowerLimit = hasGap ? CompanionMovement.MaximumLowerGapDropTiles : 0;
        for (int dy = lowerLimit; dy >= -CompanionMovement.MaximumJumpRiseTiles; dy--)
        {
            // A low-rise ledge across a real gap can be within horizontal flight
            // range even when it lies beyond the usual six-column neighborhood.
            int span = hasGap && dy is >= -CompanionMovement.ExtendedGapJumpRiseTiles and <= CompanionMovement.ExtendedGapJumpDropTiles
                ? CompanionMovement.ExtendedGapJumpSpanTiles : CompanionMovement.StandardJumpSpanTiles;
            for (int dx = -span; dx <= span; dx++)
            {
                int nx = current.X + dx;
                int ny = current.Y + dy;
                if (dy == 0 && Math.Abs(dx) <= 1)
                    continue;
                bool longJump = Math.Abs(dx) > CompanionMovement.StandardJumpSpanTiles;
                bool gapInDirection = dx < 0 ? gapLeft : gapRight;
                if ((dy > 0 || longJump) && !gapInDirection) continue;
                if (dy > 0 && !CompanionMovement.IsLowerGapJump(dx, dy)) continue;
                if (nx < minX || nx > maxX || ny < minY || ny > maxY ||
                    !TryGetStandPose(new Point(nx, ny), out TileStandPose landing))
                    continue;
                if (dy > 0)
                {
                    float drop = landing.Bottom - launch.Bottom;
                    float maximumDrop = dy <= 2 ? 32f : CompanionMovement.MaximumLowerGapDropTiles * 16f;
                    if (drop <= 0f || drop > maximumDrop ||
                        !IsStablePose(landing, null)) continue;
                }
                if (longJump && (launch.Bottom - landing.Bottom > CompanionMovement.ExtendedGapJumpRiseTiles * 16f ||
                    landing.Bottom - launch.Bottom > CompanionMovement.ExtendedGapJumpDropTiles * 16f || !IsStablePose(landing, null))) continue;
                if (!CanJump(launch, landing, requireClearAir: dy > 2 || longJump))
                    continue;
                // Prefer known nearby walk/drop routes when a longer jump is optional.
                float gapCost = (dy > 0 ? 4f + Math.Max(0, dy - 2) * 2.5f : 0f) + (longJump ? 10f : 0f);
                yield return (landing.Node, 3f + Math.Abs(dx) * 0.5f + Math.Abs(dy) * 0.65f + gapCost);
            }
        }
    }

    private static bool TryDropCost(Point from, Point to, bool lateral, out float cost)
    {
        float pixels = StandBottom(to) - StandBottom(from);
        cost = 0;
        // This legacy fall corridor is checked only at grid centers. A new offset
        // stand does not authorize a new fall trajectory that has not been checked.
        if (!HasLegacyGridCenter(from) || !HasLegacyGridCenter(to)) return false;
        if (pixels <= 0 || pixels > 18f * 16f) return false;
        float drop = pixels / 16f;
        cost = lateral ? drop <= 4 ? drop * 1.3f : 5.2f + (drop - 4) * 3.2f
            : drop <= 4 ? drop * 1.2f : 4.8f + (drop - 4) * 3.5f;
        return true;
    }

    public static Vector2 StandCenter(Point stand, int height = CompanionMovement.Height)
    {
        if (TryGetStandPose(stand, out TileStandPose pose))
            return new(pose.Center.X, pose.Bottom - height / 2f);
        return new(stand.X * 16f + 8, CompanionMovement.StandCenterY(stand.Y, height, UnresolvedStandBottom(stand)));
    }

    public static float StandBottom(Point stand)
        => TryGetStandPose(stand, out TileStandPose pose) ? pose.Bottom : UnresolvedStandBottom(stand);

    private static float UnresolvedStandBottom(Point stand)
        => TrySupportBottom(stand.X * 16f + 8, stand.Y, out float bottom) ? bottom : (stand.Y + 1) * 16f;

    /// <summary>Keep every legal old center. Only an invalid center may use the right tile boundary.</summary>
    public static bool TryGetStandPose(Point stand, out TileStandPose pose)
        => TryGetStandPose(stand, null, out pose);

    private static bool TryGetStandPose(Point stand, Point? extraPlatform, out TileStandPose pose)
    {
        pose = default;
        if (!InBounds(stand.X, stand.Y)) return false;
        for (int offset = 8; offset <= 16; offset += 8)
        {
            float centerX = stand.X * 16f + offset;
            if (!TrySupportBottom(centerX, stand.Y, out float bottom, extraPlatform) || !IsBodyClear(centerX, bottom)) continue;
            pose = new(stand, new Vector2(centerX, bottom - CompanionMovement.Height / 2f), bottom);
            return true;
        }
        return false;
    }

    /// <summary>Conservatively invalidate a captured pose if this anchor now resolves differently.</summary>
    public static bool MatchesStandPose(TileStandPose captured)
        => TryGetStandPose(captured.Node, out TileStandPose current) && current == captured;

    private static TileStandPose? CaptureStandPose(Point node)
        => TryGetStandPose(node, out TileStandPose pose) ? pose : null;

    private static bool HasLegacyGridCenter(Point node)
        => TryGetStandPose(node, out TileStandPose pose) && pose.Center.X == node.X * 16f + 8;

    private static bool TrySupportBottom(float centerX, int standRow, out float bottom, Point? extraPlatform = null)
    {
        float top = float.PositiveInfinity;
        for (int x = CompanionMovement.FootprintLeft(centerX); x <= CompanionMovement.FootprintRight(centerX); x++)
            if (IsSolid(x, standRow + 1) || IsPlatform(x, standRow + 1) || IsExtraPlatform(extraPlatform, x, standRow + 1))
                top = Math.Min(top, CompanionMovement.SupportTop(standRow + 1, IsHalfBlock(x, standRow + 1)));
        bottom = top;
        return !float.IsPositiveInfinity(top);
    }

    private static bool IsExtraPlatform(Point? cell, int x, int y) => cell is { } point && point.X == x && point.Y == y;

    private static bool HasPlatformSupport(TileStandPose pose, Point? extraPlatform)
    {
        int row = pose.Node.Y + 1;
        int left = CompanionMovement.FootprintLeft(pose.Center.X);
        int right = CompanionMovement.FootprintRight(pose.Center.X);
        for (int x = left; x <= right; x++)
            if ((IsPlatform(x, row) || IsExtraPlatform(extraPlatform, x, row)) &&
                CompanionMovement.SupportTop(row, IsHalfBlock(x, row)) == pose.Bottom)
                return true;
        return false;
    }

    public static bool IsBodyClear(float centerX, float bottom, int width = CompanionMovement.Width, int height = CompanionMovement.Height)
        => CompanionMovement.BodyClear(centerX, bottom, (x, y) => !IsPassable(x, y) || IsLava(x, y),
            width, height, HasEmptyHalfBlockTop);

    public static bool IsGroundedAt(float centerX, float bottom, float verticalSpeed, int width = CompanionMovement.Width)
        => IsGroundedAt(centerX, bottom, verticalSpeed, width, null);

    private static bool IsGroundedAt(float centerX, float bottom, float verticalSpeed, int width, Point? extraPlatform)
    {
        if (verticalSpeed < -0.5f) return false;
        int row = (int)MathF.Floor((bottom + 1f) / 16f);
        for (int x = CompanionMovement.FootprintLeft(centerX, width); x <= CompanionMovement.FootprintRight(centerX, width); x++)
        {
            if (IsExtraPlatform(extraPlatform, x, row) && Math.Abs(bottom - row * 16f) <= 2.5f) return true;
            if (!IsSolid(x, row) && !IsPlatform(x, row)) continue;
            Tile tile = Main.tile[x, row];
            bool onTop = Math.Abs(bottom - CompanionMovement.SupportTop(row, tile.IsHalfBlock)) <= 2.5f;
            bool onSlope = (int)tile.Slope != 0 && Math.Abs(verticalSpeed) <= 0.6f &&
                bottom >= row * 16f && bottom <= (row + 1) * 16f;
            if (onTop || onSlope) return true;
        }
        return false;
    }

    public static bool IsSafeStand(int x, int y) => IsStandable(x, y);

    /// <summary>A planned stopping pose must retain support across the controller's horizontal arrival tolerance.</summary>
    public static bool IsStableStand(int x, int y)
    {
        if (!TryGetStandPose(new Point(x, y), out TileStandPose pose)) return false;
        return IsStablePose(pose, null);
    }

    private static bool IsStablePose(TileStandPose pose, Point? extraPlatform)
        => IsGroundedAt(pose.Center.X - StableStandHorizontalTolerance, pose.Bottom, 0, CompanionMovement.Width, extraPlatform) &&
           IsGroundedAt(pose.Center.X + StableStandHorizontalTolerance, pose.Bottom, 0, CompanionMovement.Width, extraPlatform);

    private static bool CanJump(TileStandPose from, TileStandPose to, Point? extraPlatform = null, bool requireClearAir = false)
        => (from.Node.X != to.Node.X || HasPlatformSupport(to, extraPlatform)) &&
           CompanionMovement.JumpArcClear(from.Node.X, from.Node.Y, to.Node.X, to.Node.Y,
               (x, y) => !IsPassable(x, y) || IsLava(x, y) || requireClearAir && InterruptsDeepJump(x, y),
               (x, y) => HasEmptyHalfBlockTop(x, y) && (!requireClearAir || Main.tile[x, y].LiquidAmount == 0),
               from.Bottom, to.Bottom, from.Center.X, to.Center.X);

    private static bool InterruptsDeepJump(int x, int y)
    {
        Tile tile = Main.tile[x, y];
        return tile.LiquidAmount > 0 || IsPlatform(x, y) ||
               tile.HasTile && !tile.IsActuated && tile.TileType == TileID.ClosedDoor;
    }

    /// <summary>Looks for a short, local ascent without editing the live world or claiming arrival.</summary>
    internal static PlatformRisePreview? PreviewPlatformRise(Point start, Func<Point, TileStandPose, bool> allowsCell)
    {
        if (!TryGetStandPose(start, out TileStandPose launch)) return null;
        PlatformRisePreview? partial = null, best = null;
        void Consider(TileStandPose from, params PlatformRiseStep[] steps)
        {
            for (int rise = CompanionMovement.MaximumJumpRiseTiles; rise >= 1; rise--)
            for (int dx = -4; dx <= 4; dx++)
            {
                var node = new Point(from.Node.X + dx, from.Node.Y - rise);
                if (!TryGetStandPose(node, out TileStandPose ledge) ||
                    launch.Bottom - ledge.Bottom <= CompanionMovement.MaximumJumpRiseTiles * 16f ||
                    best?.ExistingLanding is { } prior && (ledge.Bottom > prior.Bottom ||
                        ledge.Bottom == prior.Bottom && steps.Length >= best.Steps.Count) ||
                    !IsStablePose(ledge, null)) continue;
                bool walking = rise == 1 && Math.Abs(dx) == 1 && IsPassable(from.Node.X, from.Node.Y - 3) &&
                    HasContinuousWalk(from.Center.X, from.Bottom, from.Node.Y, node, steps[^1].Cell);
                if (!walking && !CanJump(from, ledge)) continue;
                best = new(steps, ledge);
            }
        }

        foreach (PlatformRiseStep first in PlatformRiseSteps(launch, allowsCell).Take(8))
        {
            partial ??= new(new[] { first }, null);
            Consider(first.Landing, first);
            foreach (PlatformRiseStep second in PlatformRiseSteps(first.Landing, allowsCell).Take(8))
                Consider(second.Landing, first, second);
        }
        return best ?? partial;
    }

    private static IEnumerable<PlatformRiseStep> PlatformRiseSteps(TileStandPose from, Func<Point, TileStandPose, bool> allowsCell)
    {
        for (int rise = CompanionMovement.MaximumJumpRiseTiles; rise >= 3; rise--)
        for (int dx = -2; dx <= 2; dx++)
        {
            var cell = new Point(from.Node.X + dx, from.Node.Y + 1 - rise);
            if (!InBounds(cell.X, cell.Y) || Main.tile[cell.X, cell.Y].HasTile) continue;
            for (int x = cell.X - 1; x <= cell.X; x++)
            {
                var node = new Point(x, cell.Y - 1);
                if (!TryGetStandPose(node, cell, out TileStandPose landing) ||
                    cell.X < CompanionMovement.FootprintLeft(landing.Center.X) ||
                    cell.X > CompanionMovement.FootprintRight(landing.Center.X) || landing.Bottom != cell.Y * 16f ||
                    !IsStablePose(landing, cell) || !CanJump(from, landing, cell) || !allowsCell(cell, from)) continue;
                yield return new(cell, landing);
            }
        }
    }

    public static bool HasWalkingConnection(Point from, Point to)
    {
        if (!TryGetStandPose(from, out TileStandPose pose)) return false;
        return HasContinuousWalk(pose.Center.X, pose.Bottom, from.Y, to);
    }

    /// <summary>Tests the actual contact pose, avoiding a nearby projected stand hiding a gap under the moving body.</summary>
    public static bool HasWalkingConnection(float fromCenterX, float fromBottom, Point to)
    {
        if (!float.IsFinite(fromCenterX) || !float.IsFinite(fromBottom) || !IsGroundedAt(fromCenterX, fromBottom, 0)) return false;
        int standRow = (int)MathF.Floor((fromBottom + 1f) / 16f) - 1;
        return HasContinuousWalk(fromCenterX, fromBottom, standRow, to);
    }

    private static bool HasContinuousWalk(float fromCenterX, float fromBottom, int fromRow, Point to, Point? extraPlatform = null)
    {
        if (!TryGetStandPose(to, out TileStandPose pose)) return false;
        float targetX = pose.Center.X, targetBottom = pose.Bottom;
        int direction = Math.Sign(targetX - fromCenterX);
        if (direction == 0) return fromRow == to.Y && Math.Abs(fromBottom - targetBottom) <= 0.01f;
        int minRow = Math.Min(fromRow, to.Y) - 1, maxRow = Math.Max(fromRow, to.Y) + 1;
        var contacts = new List<(int Row, float Bottom)>(4) { (fromRow, fromBottom) };
        var nextContacts = new List<(int Row, float Bottom)>(4);
        float centerX = fromCenterX;
        while (direction * (targetX - centerX) > 0.01f)
        {
            centerX += direction * Math.Min(2f, Math.Abs(targetX - centerX));
            nextContacts.Clear();
            int lower = int.MaxValue, upper = int.MinValue;
            foreach (var contact in contacts) { lower = Math.Min(lower, contact.Row - 1); upper = Math.Max(upper, contact.Row + 1); }
            for (int row = Math.Max(minRow, lower); row <= Math.Min(maxRow, upper); row++)
            {
                if (!TrySupportBottom(centerX, row, out float bottom, extraPlatform)) continue;
                bool solidStepSupport = HasSolidSupportAt(centerX, row, bottom);
                bool reachable = false;
                foreach (var previous in contacts)
                {
                    float rise = previous.Bottom - bottom;
                    if (Math.Abs(row - previous.Row) <= 1 && Math.Abs(rise) <= 16f &&
                        (rise <= 0 || solidStepSupport))
                    { reachable = true; break; }
                }
                if (reachable && IsBodyClear(centerX, bottom) && IsGroundedAt(centerX, bottom, 0, CompanionMovement.Width, extraPlatform))
                    nextContacts.Add((row, bottom));
            }
            if (nextContacts.Count == 0) return false;
            (contacts, nextContacts) = (nextContacts, contacts);
        }
        foreach (var contact in contacts)
            if (contact.Row == to.Y && Math.Abs(contact.Bottom - targetBottom) <= 0.01f) return true;
        return false;
    }

    private static bool HasSolidSupportAt(float centerX, int standRow, float bottom)
    {
        // One-way platforms/furniture can support a landing from above, but the
        // native step-up controller cannot lift a body onto them from underneath.
        // Keep ordinary solid half/full-block steps on their actual contact plane.
        for (int x = CompanionMovement.FootprintLeft(centerX); x <= CompanionMovement.FootprintRight(centerX); x++)
            if (IsSolid(x, standRow + 1) &&
                Math.Abs(CompanionMovement.SupportTop(standRow + 1, IsHalfBlock(x, standRow + 1)) - bottom) <= 0.01f)
                return true;
        return false;
    }

    private static bool TryFindStandTile(Vector2 world, out Point tile, bool stableOnly = false)
    {
        int baseX = (int)(world.X / 16f);
        int baseY = CompanionMovement.StandRow(world.Y + CompanionMovement.Height / 2f);
        float best = float.MaxValue;
        tile = default;
        for (int dx = -3; dx <= 3; dx++)
            for (int dy = -8; dy <= 12; dy++)
            {
                int x = baseX + dx;
                int y = baseY + dy;
                if (stableOnly ? !IsStableStand(x, y) : !IsStandable(x, y)) continue;
                var point = new Point(x, y);
                float score = Vector2.DistanceSquared(StandCenter(point), world);
                if (score >= best) continue;
                best = score;
                tile = point;
            }
        return best < float.MaxValue;
    }

    private static bool IsStandable(int x, int y)
        => TryGetStandPose(new Point(x, y), out _);

    private static bool IsPassable(int x, int y)
    {
        if (!InBounds(x, y))
            return false;

        Tile tile = Main.tile[x, y];
        if (!tile.HasTile || tile.IsActuated)
            return true;

        if (tile.TileType is TileID.ClosedDoor or TileID.OpenDoor ||
            TileID.Sets.Platforms[tile.TileType] ||
            Main.tileSolidTop[tile.TileType])
            return true;

        return !Main.tileSolid[tile.TileType];
    }

    private static bool IsSolid(int x, int y)
    {
        if (!InBounds(x, y))
            return false;

        Tile tile = Main.tile[x, y];
        return tile.HasTile && !tile.IsActuated && Main.tileSolid[tile.TileType] &&
               !Main.tileSolidTop[tile.TileType] && !TileID.Sets.Platforms[tile.TileType];
    }

    private static bool IsPlatform(int x, int y)
    {
        if (!InBounds(x, y))
            return false;

        Tile tile = Main.tile[x, y];
        return tile.HasTile && !tile.IsActuated && (TileID.Sets.Platforms[tile.TileType] || Main.tileSolidTop[tile.TileType]);
    }

    private static bool IsHalfBlock(int x, int y)
        => InBounds(x, y) && Main.tile[x, y].HasTile && !Main.tile[x, y].IsActuated && Main.tile[x, y].IsHalfBlock;

    private static bool HasEmptyHalfBlockTop(int x, int y) => IsHalfBlock(x, y) && !IsLava(x, y);

    private static bool IsLava(int x, int y)
    {
        if (!InBounds(x, y))
            return false;

        Tile tile = Main.tile[x, y];
        return tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Lava;
    }

    private static bool InBounds(int x, int y)
    {
        return x > 5 && x < Main.maxTilesX - 5 && y > 5 && y < Main.maxTilesY - 5;
    }

    private static float Heuristic(Point a, Point b)
    {
        return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) * 1.5f;
    }

}
