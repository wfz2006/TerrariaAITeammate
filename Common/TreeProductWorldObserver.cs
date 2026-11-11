using System;
using System.Diagnostics;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

/// <summary>Game-thread identity observation; never runs drop routines, RNG, excavation or route searches.</summary>
public static class TreeProductWorldObserver
{
    public static object Observe(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 3) || !Main.tile[x, y].HasTile)
            return Unknown("sample_changed_or_outside_world");
        Tile sampled = Main.tile[x, y];
        if (sampled.TileType != TileID.Trees)
            return Unknown(sampled.TileType == TileID.LivingWood ? "living_wood_block_not_confirmed_tree_drop_path" : "unconfirmed_tree_variant");
        var timer = Stopwatch.StartNew();
        try
        {
            // Despite the XML wording, the installed native implementation returns
            // the first active non-trunk support SOIL cell; do not add one to rootY.
            WorldGen.GetTreeBottom(x, y, out int rootX, out int rootY);
            if (!WorldGen.InWorld(rootX, rootY, 3) || rootY >= Main.maxTilesY - 50 || !Main.tile[rootX, rootY].HasTile)
                return Unknown("native_root_unresolved_or_world_bottom_limit");
            Tile soil = Main.tile[rootX, rootY];
            bool baseObserved = WorldGen.InWorld(rootX, rootY - 1, 3) &&
                Main.tile[rootX, rootY - 1].HasTile && Main.tile[rootX, rootY - 1].TileType == TileID.Trees;
            // This public lookup only reads the registry. Do not invoke a ModTree's
            // DropWood or CountsAsTreeType getter to manufacture a vanilla identity.
            bool modOverride = PlantLoader.GetTree(soil.TileType) != null;
            int? itemId = baseObserved ? TreeProductObservation.VanillaWoodItemId(sampled.TileType, soil.TileType, modOverride) : null;
            return new
            {
                status = itemId.HasValue ? "known_vanilla_root_soil_identity" : modOverride ? "unknown_mod_tree_override" : baseObserved ? "unknown_soil_mapping" : "unknown_root_trunk_continuity",
                supportSoil = new { x = rootX, y = rootY, tileType = (int)soil.TileType, actuated = soil.IsActuated },
                baseTrunk = baseObserved ? new { x = rootX, y = rootY - 1 } : null,
                nativeTreeType = modOverride ? null : WorldGen.GetTreeType(soil.TileType).ToString(),
                itemId, itemName = itemId.HasValue ? Lang.GetItemNameValue(itemId.Value) : null,
                quantity = (int?)null,
                sampleProtected = AIExecutor.IsPartOfHouseOrStructure(x, y),
                sampleNativeCanKill = WorldGen.CanKillTile(x, y),
                baseProtected = baseObserved ? AIExecutor.IsPartOfHouseOrStructure(rootX, rootY - 1) : (bool?)null,
                baseNativeCanKill = baseObserved ? WorldGen.CanKillTile(rootX, rootY - 1) : (bool?)null,
                rootDownwardSpan = rootY - y,
                nativeStopY = Main.maxTilesY - 50,
                observedAtTick = Main.GameUpdateCount,
                nativeReadMilliseconds = Math.Round(timer.Elapsed.TotalMilliseconds, 3)
            };
        }
        catch (Exception exception)
        {
            return Unknown("native_read_unavailable_" + exception.GetType().Name);
        }
    }

    private static object Unknown(string reason) => new
    {
        status = "unknown", reason, itemId = (int?)null, quantity = (int?)null,
        observedAtTick = Main.GameUpdateCount
    };
}
