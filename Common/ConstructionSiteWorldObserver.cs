using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public static class ConstructionSiteWorldObserver
{
    public static object Query(string query, NPC observer, int? x = null, int? y = null)
        => QueryCore(query, observer, x, y, allowRemoteTerrain: false);

    /// <summary>Remote bounded native terrain facts only; does not change any interaction or construction permission.</summary>
    public static object QueryTerrain(string query, NPC observer, int? x = null, int? y = null)
        => QueryCore(query, observer, x, y, allowRemoteTerrain: true);

    private static object QueryCore(string query, NPC observer, int? x, int? y, bool allowRemoteTerrain)
    {
        ConstructionSiteRequest request;
        try
        {
            request = ConstructionSiteRequest.Parse(query, x, y);
            request.ValidateObservationBounds(Main.maxTilesX, Main.maxTilesY, observer.Center.X / 16d, observer.Center.Y / 16d,
                allowRemoteTerrain);
        }
        catch (ArgumentException exception) { return Error(exception.Message); }
        int right = request.X + request.Width - 1, bottom = request.Y + request.Height - 1;

        SiteCell Read(int tx, int ty)
        {
            Tile tile = Main.tile[tx, ty];
            int type = tile.HasTile ? tile.TileType : TileID.Dirt;
            bool platform = tile.HasTile && (Main.tileSolidTop[type] || TileID.Sets.Platforms[type]);
            bool solid = tile.HasTile && Main.tileSolid[type] && !platform;
            bool door = tile.HasTile && type is TileID.ClosedDoor or TileID.OpenDoor;
            bool furniture = tile.HasTile && Main.tileFrameImportant[type] && !solid && !platform &&
                type is not (TileID.Trees or TileID.PalmTree or TileID.Cactus or TileID.Vines or TileID.Plants or TileID.Plants2);
            return new SiteCell(tile.HasTile, type, tile.WallType, solid, platform, door, furniture,
                tile.LiquidAmount, tile.LiquidType, tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Lava,
                tile.IsActuated, (int)tile.Slope, tile.IsHalfBlock);
        }

        var cells = new List<SiteCell>(request.Width * request.Height);
        for (int ty = request.Y; ty <= bottom; ty++)
        for (int tx = request.X; tx <= right; tx++) cells.Add(Read(tx, ty));
        SiteCell[] below = Enumerable.Range(request.X, request.Width).Select(tx => Read(tx, bottom + 1)).ToArray();
        Rectangle area = new(request.X * 16, request.Y * 16, request.Width * 16, request.Height * 16);
        var actors = new List<SiteActor>();
        SiteActor Actor(string kind, int id, string name, Rectangle hitbox)
        {
            int left = (int)Math.Floor(hitbox.Left / 16d), top = (int)Math.Floor(hitbox.Top / 16d);
            int actorRight = (int)Math.Ceiling(hitbox.Right / 16d), actorBottom = (int)Math.Ceiling(hitbox.Bottom / 16d);
            return new SiteActor(kind, id, name, left, top, actorRight - left, actorBottom - top);
        }
        foreach (Player player in Main.player.Where(player => player.active && !player.dead && player.Hitbox.Intersects(area)))
            actors.Add(Actor("player", player.whoAmI, player.name, player.Hitbox));
        foreach (NPC npc in Main.npc.Where(npc => npc.active && npc.Hitbox.Intersects(area)))
            actors.Add(Actor("npc", npc.whoAmI, npc.FullName, npc.Hitbox));
        object measurement = ConstructionSiteObservation.Analyze(new ConstructionSiteScan(request.X, request.Y,
            request.Width, request.Height, cells.ToArray(), below, actors.ToArray(), request.ExpectedTileType, request.ExpectedWallType));
        PlacementCatalogResult catalog = PlacementCatalogWorldObserver.Read(request.X, request.Y, request.Width, request.Height);
        return new
        {
            topic = "site", source = "live_world", available = true, worldId = Main.worldID,
            observedAtTick = Main.GameUpdateCount, measurement,
            observationScope = allowRemoteTerrain ? "world_bounded_readonly_terrain" : "local_site_within_64_tiles",
            tileTypes = catalog.tileTypes, wallTypes = catalog.wallTypes,
            placementCatalogSource = catalog.source, placementCatalogNote = catalog.note,
            actorFootprints = "Only actors whose actual pixel hitboxes intersect this rectangle are listed; their reported tile bounds are rounded outward. These are current moving entities, not invented permanent blocks.",
            thisToolSupportsDemolition = false,
            note = "只报告当前原生坐标中的实际格子，不选择方案、不修改世界、不批准施工。已有相同方块/墙仍是占用，不能把底层屋顶支撑推断成整块上层空间为空。不同材料、家具、门或已建小房需要保留；当前没有安全拆迁技能时，应由LLM换范围、修改方案或明确等待，不得假装可拆。"
        };
    }

    private static object Error(string error) => new
    {
        topic = "site", source = "live_world", available = false, worldId = Main.worldID,
        observedAtTick = Main.GameUpdateCount, error,
        note = "未取得完整区域观察，不能据此断言施工空间为空。"
    };
}
