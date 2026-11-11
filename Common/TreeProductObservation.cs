namespace TerrariaAITeammate.Common;

/// <summary>Identity only, not quantity. Verified against the installed vanilla KillTile_GetTreeDrops soil branches.</summary>
public static class TreeProductObservation
{
    public static int? VanillaWoodItemId(int trunkTileType, int soilTileType, bool modOverride = false)
    {
        // Only the ordinary native tree path is confirmed here. The private drop
        // routine also consumes RNG and invokes mod hooks; never run it to inspect.
        if (trunkTileType != 5 || modOverride) return null;
        return soilTileType switch
        {
            2 or 477 => 9,             // Grass/GolfGrass -> Wood
            23 or 661 => 619,          // CorruptGrass/CorruptJungleGrass -> Ebonwood
            199 or 662 => 911,         // CrimsonGrass/CrimsonJungleGrass -> Shadewood
            60 => 620,                // JungleGrass -> RichMahogany
            109 or 492 => 621,         // HallowedGrass/GolfGrassHallowed -> Pearlwood
            147 => 2503,              // SnowBlock -> BorealWood
            633 => 5215,              // AshGrass -> AshWood
            _ => null                 // No guessed normal-wood fallback, including mushroom/random drops.
        };
    }
}
