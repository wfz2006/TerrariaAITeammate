using System;

namespace TerrariaAITeammate.Common;

/// <summary>Shared physical dimensions and movement decisions; independent of game globals.</summary>
public static class CompanionMovement
{
    public const int Width = 20;
    public const int Height = 42;
    public const float Gravity = 0.35f;
    public const float JumpVelocity = -8.6f;
    public const float MaximumFallVelocity = 10f;
    public const int MaximumJumpRiseTiles = 6;
    internal const int StandardJumpSpanTiles = 6;
    internal const int MaximumLowerGapDropTiles = 10;
    internal const int ExtendedGapJumpSpanTiles = 10;
    internal const int ExtendedGapJumpRiseTiles = 3;
    internal const int ExtendedGapJumpDropTiles = 5;

    public static float StandCenterY(int standRow, int height = Height, float? actualBottom = null)
        => (actualBottom ?? (standRow + 1) * 16f) - height / 2f;
    public static int FootprintLeft(float centerX, int width = Width)
        => (int)MathF.Floor((centerX - width / 2f + 0.01f) / 16f);
    public static int FootprintRight(float centerX, int width = Width)
        => (int)MathF.Floor((centerX + width / 2f - 0.01f) / 16f);
    public static float SupportTop(int row, bool halfBlock) => row * 16f + (halfBlock ? 8f : 0f);
    public static float StepRise(float bottom, int frontRow, bool halfBlock)
    {
        float rise = bottom - SupportTop(frontRow, halfBlock);
        return rise > 0f && rise <= 16f ? rise : 0f;
    }
    public static int StandRow(float bottom) => (int)MathF.Floor((bottom - 0.01f) / 16f);
    public static int Direction(float distance, float deadZone = 5f)
        => Math.Abs(distance) <= deadZone ? 0 : Math.Sign(distance);
    public static bool InAttackPosition(float dx, float dy, float attackRange, bool lineOfSight)
        => lineOfSight && float.IsFinite(attackRange) && attackRange > 0f &&
           float.IsFinite(dx) && float.IsFinite(dy) && dx * dx + dy * dy <= attackRange * attackRange;
    // Horizontal rest is valid only after the landing plane has been reached.
    // A two-pixel alignment may be necessary to get the full body onto a step.
    public static int WaypointDirection(float dx, float footDelta)
        => Direction(dx, Math.Abs(footDelta) <= 3f ? 5f : 0f);
    public static float WaypointSpeed(float dx, float footDelta, float speed)
        => Math.Abs(dx) <= 3f && Math.Abs(footDelta) <= 3f ? 0f : Math.Clamp(dx / 6f, -speed, speed);

    public static bool ShouldPauseFollow(bool paused, float dx, float dy, bool directPassage = true)
        => directPassage && (paused ? Math.Abs(dx) <= 112f && Math.Abs(dy) <= 40f
            : Math.Abs(dx) <= 68f && Math.Abs(dy) <= 24f);

    public static bool ShouldRunIdleFollow(bool held, bool hasTask, bool explicitWait, bool hasGoal, bool hasPendingTasks)
        => !held && !hasTask && !explicitWait && !hasGoal && !hasPendingTasks;

    public static bool ReachedStand(float centerX, float bottom, float verticalSpeed, bool grounded,
        int standX, int standRow, float? actualBottom = null, float? actualCenterX = null)
        => Math.Abs(centerX - (actualCenterX ?? (standX * 16f + 8))) <= 16f &&
           Math.Abs(bottom - (actualBottom ?? (standRow + 1) * 16f)) <= 5f && Math.Abs(verticalSpeed) <= 0.6f && grounded;

    public static bool ReachedWaypoint(float horizontalError, float footError, bool grounded, bool onSlope)
        => grounded && Math.Abs(horizontalError) <= 8f &&
           (Math.Abs(footError) <= 3f || onSlope && Math.Abs(footError) <= 16f);

    public static bool ReachedTerminalWaypoint(float horizontalError, float footError, float verticalSpeed,
        bool grounded, bool onSlope)
        => grounded && InTerminalWaypointWindow(horizontalError, footError, onSlope) &&
           Math.Abs(verticalSpeed) <= 0.6f;

    public static bool InTerminalWaypointWindow(float horizontalError, float footError, bool onSlope)
        => Math.Abs(horizontalError) <= 8f && Math.Abs(footError) <= (onSlope ? 5f : 3f);

    public static bool ValidDirectionalExploreEndpoint(float originX, float requestedX, float endpointX)
        => Math.Abs(endpointX - requestedX) <= 4f &&
            (requestedX < originX && endpointX <= originX - 1f || requestedX > originX && endpointX >= originX + 1f);

    internal static bool IsLowerGapJump(int columns, int rows)
    {
        int span = Math.Abs(columns);
        int maximumSpan = rows <= ExtendedGapJumpDropTiles ? ExtendedGapJumpSpanTiles : StandardJumpSpanTiles;
        return span >= 2 && span <= maximumSpan && rows is >= 1 and <= MaximumLowerGapDropTiles;
    }

    public static float BrakeAtTerminalWaypoint(float horizontalError, float footError,
        float horizontalVelocity, bool onSlope)
        => InTerminalWaypointWindow(horizontalError, footError, onSlope)
            ? 0f : horizontalVelocity;

    public static bool DropSourceCleared(float sourceBottom, float currentBottom)
        => currentBottom > sourceBottom + 2f;

    public static float DropClearPlane(float contactBottom, int supportTileRow, bool shapedSupport)
        => shapedSupport ? Math.Max(contactBottom, (supportTileRow + 1) * 16f) : contactBottom;

    public static float JumpFlightTicks(float heightDifference)
    {
        float discriminant = JumpVelocity * JumpVelocity + 2f * Gravity * heightDifference;
        if (discriminant < 0f) return float.NaN;
        float descentVelocity = MathF.Sqrt(discriminant);
        return descentVelocity <= MaximumFallVelocity ? (descentVelocity - JumpVelocity) / Gravity
            : FullFallSpeedTick + (heightDifference - FullFallSpeedHeight) / MaximumFallVelocity;
    }

    private const float FullFallSpeedTick = (MaximumFallVelocity - JumpVelocity) / Gravity;
    private const float FullFallSpeedHeight = JumpVelocity * FullFallSpeedTick + 0.5f * Gravity * FullFallSpeedTick * FullFallSpeedTick;

    public static bool BodyClear(float centerX, float bottom, Func<int, int, bool> blocked,
        int width = Width, int height = Height, Func<int, int, bool>? halfBlock = null)
    {
        int left = FootprintLeft(centerX, width);
        int right = FootprintRight(centerX, width);
        int top = (int)MathF.Floor((bottom - height + 0.01f) / 16f);
        int last = (int)MathF.Floor((bottom - 0.01f) / 16f);
        for (int x = left; x <= right; x++)
            for (int y = top; y <= last; y++)
                if (blocked(x, y) && !(halfBlock?.Invoke(x, y) == true && bottom <= SupportTop(y, true) + 0.01f))
                    return false;
        return true;
    }

    public static bool JumpArcClear(int fromX, int fromRow, int toX, int toRow,
        Func<int, int, bool> blocked, Func<int, int, bool>? halfBlock = null,
        float? fromBottom = null, float? toBottom = null,
        float? fromCenterX = null, float? toCenterX = null)
    {
        float startBottom = fromBottom ?? (fromRow + 1) * 16f;
        float endBottom = toBottom ?? (toRow + 1) * 16f;
        float startX = fromCenterX ?? (fromX * 16f + 8);
        float endX = toCenterX ?? (toX * 16f + 8);
        float duration = JumpFlightTicks(endBottom - startBottom);
        if (!float.IsFinite(startX) || !float.IsFinite(endX) || float.IsNaN(duration) ||
            duration <= 0 || Math.Abs(endX - startX) > (duration - 4f) * 4.4f)
            return false;
        int samples = (int)Math.Ceiling(duration);
        for (int sample = 0; sample <= samples; sample++)
        {
            float time = duration * sample / samples;
            float x = startX + (endX - startX) * time / duration;
            float height = time <= FullFallSpeedTick ? JumpVelocity * time + 0.5f * Gravity * time * time
                : FullFallSpeedHeight + (time - FullFallSpeedTick) * MaximumFallVelocity;
            float bottom = startBottom + height;
            if (!BodyClear(x, bottom, blocked, halfBlock: halfBlock)) return false;
        }
        return true;
    }
}
