using Terraria;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace TerrariaAITeammate.Common;

public sealed class WorldKnowledgeTileHooks : GlobalTile
{
    public override void KillTile(int i, int j, int type, ref bool fail, ref bool effectOnly, ref bool noItem)
    {
        if (!effectOnly) WorldKnowledgeObserver.Invalidate(i, j);
    }

    public override void PlaceInWorld(int i, int j, int type, Item item)
    {
        TileObjectData? data = TileObjectData.GetTileData(type, item.placeStyle);
        int width = data?.Width ?? 1, height = data?.Height ?? 1;
        // The player hook's coordinate may be an object-origin cell. Include its
        // complete bounded footprint even when it crosses an index chunk edge.
        WorldKnowledgeObserver.InvalidateArea(i - width, j - height, width * 2 + 1, height * 2 + 1);
    }
}
