namespace TerrariaAITeammate.Common;

/// <summary>Visual state only. Timing is measured in game ticks, never draw-call count.</summary>
public sealed class CompanionVisualPose
{
    public const int FrameWidth = 40, FrameHeight = 56, FrameCount = 12, LandingTicks = 7;
    public bool Grounded { get; private set; }
    public ulong? LandingUntilTick { get; private set; }
    public int PoseIndex { get; private set; }
    private ulong? sampledTick;

    public int Update(ulong tick, bool grounded, float velocityY, int groundPose, bool melee, bool shoot)
    {
        if (sampledTick == tick) return PoseIndex;
        if (sampledTick.HasValue && tick < sampledTick.Value)
        { sampledTick = null; LandingUntilTick = null; }
        if (!grounded) LandingUntilTick = null;
        else if (sampledTick.HasValue && !Grounded)
            LandingUntilTick = tick > ulong.MaxValue - LandingTicks ? ulong.MaxValue : tick + LandingTicks;
        Grounded = grounded; sampledTick = tick;
        PoseIndex = melee ? 10 : shoot ? 11 : !grounded
            ? !float.IsFinite(velocityY) ? 5 : velocityY < -.6f ? 6 : velocityY > .6f ? 8 : 7
            : LandingUntilTick.HasValue && tick < LandingUntilTick.Value ? 9
            : groundPose is >= 0 and <= 4 ? groundPose : 0;
        return PoseIndex;
    }

    public static int ResolveAtlasFrame(int pose, int frameCount)
        => pose is < 0 or >= FrameCount ? 0 : frameCount == FrameCount ? pose
            : frameCount == 6 ? pose < 6 ? pose : pose <= 8 ? 5 : 0 : 0;
}
