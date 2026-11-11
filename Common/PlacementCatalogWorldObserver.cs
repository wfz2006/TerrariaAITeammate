using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ObjectData;

namespace TerrariaAITeammate.Common;

/// <summary>Current native item/type/style facts only; never selects materials or edits the world.</summary>
public static class PlacementCatalogWorldObserver
{
    public static PlacementCatalogResult Read(int left, int top, int width, int height)
    {
        if (width < 1 || width > StructureObservation.MaxWidth || height < 1 || height > StructureObservation.MaxHeight ||
            !WorldGen.InWorld(left, top, 2) || !WorldGen.InWorld(left + width - 1, top + height - 1, 2))
            throw new ArgumentException("Placement catalog requires the bounded actual observation rectangle.");
        CatalogItem[] items = ContentSamples.ItemsByType.Values.Where(item => !item.IsAir && (item.createTile >= TileID.Dirt || item.createWall > WallID.None))
            .Select(item => new CatalogItem(item.type, item.Name, item.createTile, item.createWall, item.placeStyle)).ToArray();
        var styles = items.Where(item => item.tileType >= 0).GroupBy(item => item.tileType)
            .ToDictionary(group => group.Key, group => group.Select(item => item.placeStyle).Distinct().ToArray());
        var observed = new HashSet<CatalogTile>();
        var walls = new HashSet<CatalogWall>();
        for (int y = top; y < top + height; y++)
        for (int x = left; x < left + width; x++)
        {
            Tile tile = Main.tile[x, y];
            if (tile.WallType > WallID.None) walls.Add(new CatalogWall(tile.WallType, WallID.Search.GetName(tile.WallType) ?? tile.WallType.ToString()));
            if (!tile.HasTile) continue;
            bool hasObjectData = TileObjectData.GetTileData(tile) != null;
            int? nativeStyle = null;
            if (hasObjectData)
            {
                int style = 0, alternate = 0;
                try { TileObjectData.GetTileInfo(tile, ref style, ref alternate); nativeStyle = style; }
                catch { /* A failed native style read remains unknown, never silently style zero. */ }
            }
            CatalogStyle resolved = PlacementCatalog.ResolveStyle(hasObjectData, nativeStyle, styles.GetValueOrDefault(tile.TileType) ?? Array.Empty<int>());
            observed.Add(new CatalogTile(tile.TileType, TileID.Search.GetName(tile.TileType) ?? tile.TileType.ToString(),
                resolved.observedStyle, resolved.styleKnown, resolved.styleSource));
        }
        return PlacementCatalog.Build(new CatalogScan(observed.ToArray(), walls.ToArray(), items));
    }
}
