namespace TerrariaAITeammate.Common;

public readonly record struct ShelterHerbState(bool HasTile, int TileType, int FrameX, int FrameY,
    bool Solid, bool Platform, bool Door, bool HalfBlock, int Slope, bool Actuated);

/// <summary>Only native transitions verified against the installed game; not a general vegetation whitelist.</summary>
public static class ShelterVegetationRules
{
    public static bool IsKnownHerbBloomTransition(ShelterHerbState before, ShelterHerbState after)
    {
        // Installed WorldGen.GrowAlch: frame36 toggles83/84, frame108 only83->84.
        // CheckAlch's frameX/18 case5 (frame90) also toggles83/84. Other herb
        // species/stages are not inferred from this evidence. See the native-IL fixture.
        bool Normal(ShelterHerbState cell) => cell.HasTile && !cell.Solid && !cell.Platform && !cell.Door &&
            !cell.HalfBlock && cell.Slope == 0 && !cell.Actuated && cell.FrameY == 0;
        if (!Normal(before) || !Normal(after) || before.FrameX != after.FrameX) return false;
        bool blooms = before.TileType == 83 && after.TileType == 84;
        bool ceasesBlooming = before.TileType == 84 && after.TileType == 83;
        return before.FrameX switch
        {
            36 or 90 => blooms || ceasesBlooming,
            108 => blooms,
            _ => false
        };
    }
}
