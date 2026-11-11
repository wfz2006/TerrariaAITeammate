using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Enums;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ObjectData;

namespace TerrariaAITeammate.Common;

/// <summary>Adapts actual native facts to a sparse, read-only review of the model's design.</summary>
public static class ConstructionDesignWorldObserver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static object Query(JsonElement? design, NPC observer, Player player, CompanionLoadout loadout)
    {
        try
        {
            var items = new Dictionary<int, DesignItemData>();
            foreach (int id in ConstructionDesignReview.ReadItemIds(design))
                if (ReadItem(id) is { } data) items[id] = data;

            DesignRouteProof FirstRoute(DesignWorkPose pose)
            {
                Vector2 target = new((pose.StandX + .5f) * 16f, pose.FeetY * 16f - CompanionMovement.Height / 2f);
                if (!WorldGen.InWorld(pose.StandX, (int)pose.FeetY, 3))
                    return new(false, false, "requested work pose outside live world");
                if (Vector2.DistanceSquared(observer.Center, target) > 256f * 16f * 256f * 16f)
                    return new(false, false, "requested work pose outside this bounded 256-tile native route query");
                bool resolved = TilePathfinder.TryResolveStand(target, out Point stand);
                bool exact = resolved && Vector2.DistanceSquared(TilePathfinder.StandCenter(stand), target) <= 1f &&
                    Math.Abs(TilePathfinder.StandBottom(stand) / 16f - pose.FeetY) <= .01f;
                List<Point>? path = resolved ? TilePathfinder.FindPath(observer.Center, target) : null;
                return new(path != null, exact, resolved ? TilePathfinder.LastDiagnostic : "work_pose_has_no_current_native_stand");
            }
            DesignCell Read(int x, int y)
            {
                if (!WorldGen.InWorld(x, y, 2)) throw new ArgumentException("Design observation attempted to read outside the live world; no missing cells were treated as air.");
                // Read radius bounds the native adapter independently from invalid submitted geometry.
                if (Math.Abs(x - observer.Center.X / 16f) > StructureObservation.MaxWidth + StructureWorldObserver.MaxAnchorDistance ||
                    Math.Abs(y - observer.Center.Y / 16f) > StructureObservation.MaxHeight + StructureWorldObserver.MaxAnchorDistance)
                    throw new ArgumentException("Design exceeds the current bounded native observation region; move nearer and inspect before reviewing it.");
                return ReadCell(x, y);
            }
            DesignReviewResult review = ConstructionDesignReview.Review(design, Read, items,
                new DesignActor(observer.Center.X / 16f, observer.Bottom.Y / 16f), FirstRoute,
                CompanionCrafting.InventoryCounts(loadout));

            const int recipeFactLimit = 8;
            var materials = new List<object>();
            foreach (DesignBillLine line in review.Bill.Take(recipeFactLimit))
            {
                if (!ContentSamples.ItemsByType.TryGetValue(line.ItemId, out Item? item)) continue;
                bool raw = IsRawMaterial(item);
                materials.Add(new
                {
                    itemId = line.ItemId, name = line.Name, required = line.Required, owned = line.Owned, missing = line.Missing,
                    sourceKind = line.Required == 0 ? "existing_world_reused" : raw ? "world_harvest_raw_material" : "derived_item_recipe",
                    recipe = raw || line.Required == 0 ? null : AgentKnowledge.RecipeQuery(line.ItemId.ToString(), line.Required, observer, player, loadout, includeSourceRecipes: false),
                    note = raw ? "直接核实实际采集来源；不把墙/平台回收配方当作新增原料来源。" : "真实原生配方事实，配方组/替代材料及完整原料净需求仍由你选择并核算；actualOutput受每次craft上限影响，大数量需要分批。"
                });
            }
            return new
            {
                topic = "design", source = "live_world", available = true, worldId = Main.worldID,
                callSucceeded = true, reviewStatus = review.Status, requiresRevision = !review.InputValid || !review.StaticConstraintsPassed,
                observedAtTick = Main.GameUpdateCount, review = JsonSerializer.SerializeToNode(review, JsonOptions),
                itemMetadata = JsonSerializer.SerializeToNode(items.Values.ToArray(), JsonOptions),
                materialFacts = materials, materialFactsTruncated = review.Bill.Count > recipeFactLimit,
                materialFactsTotal = review.Bill.Count,
                worldModified = false, inventoryModified = false, actorsMoved = false,
                note = "只读静态预检；没有执行放置、移动或原生住房检查。首次工位路径来自本轮真实TilePathfinder，后续overlay路径只属模型假设后的保守检查。家具native锚点/变体、未来路线及配方组选择未获实际执行证明，不能声称建成。未在结果重复整份提交设计；issues.step按提交操作定位。"
            };
        }
        catch (ArgumentException exception) { return InputIssue(exception.Message); }
        catch (JsonException exception) { return InputIssue(exception.Message); }
    }

    public static DesignItemData? ReadItem(int id)
    {
        if (!ContentSamples.ItemsByType.TryGetValue(id, out Item? item) || item.IsAir) return null;
        if (item.createWall > WallID.None && item.createTile < TileID.Dirt)
            return new DesignItemData(id, item.Name, DesignItemKind.Wall, WallType: item.createWall);
        if (item.createTile < TileID.Dirt) return null;
        int type = item.createTile;
        bool platform = Main.tileSolidTop[type] || TileID.Sets.Platforms[type];
        bool solid = Main.tileSolid[type];
        TileObjectData? data = TileObjectData.GetTileData(type, item.placeStyle);
        if (platform)
        {
            // The executor uses PlaceObject whenever data exists. Native tile19's 1x1 data has no anchors;
            // TileObject.CanPlace explicitly accepts an empty anchor set. Do not substitute player PlaceTile
            // or ordinary block adjacency rules. Unknown native metadata and modded platform types stay unsupported.
            bool reviewed = type == TileID.Platforms && solid && Main.tileSolidTop[type] && TileID.Sets.Platforms[type] &&
                data != null && data.Width == 1 && data.Height == 1 &&
                data.CoordinateFullWidth > 0 && data.CoordinateFullHeight > 0 &&
                data.Origin.X == 0 && data.Origin.Y == 0 && !data.UsesCustomCanPlace &&
                data.AnchorTop.tileCount == 0 && data.AnchorBottom.tileCount == 0 &&
                data.AnchorLeft.tileCount == 0 && data.AnchorRight.tileCount == 0 && !data.AnchorWall &&
                !(data.AnchorValidTiles?.Length > 0) && !(data.AnchorInvalidTiles?.Length > 0) &&
                !(data.AnchorAlternateTiles?.Length > 0) && !(data.AnchorValidWalls?.Length > 0) &&
                data.HookCheckIfCanPlace.hook == null && data.HookPlaceOverride.hook == null &&
                data.HookPostPlaceEveryone.hook == null && data.HookPostPlaceMyPlayer.hook == null &&
                data.AlternatesCount == 0 && data.RandomStyleRange == 0 && !(data.SpecificRandomStyles?.Length > 0) &&
                data.Direction == TileObjectDirection.None && !data.FlattenAnchors && !data.WaterDeath &&
                data.WaterPlacement == LiquidPlacement.Allowed &&
                (data.LavaPlacement == LiquidPlacement.Allowed || data.LavaPlacement == LiquidPlacement.NotAllowed);
            string platformDetail = JsonSerializer.Serialize(new
            {
                reason = reviewed ? "single_native_object_zero_anchors" : "platform_variant_not_reviewed",
                singlePlacementSupportedByExecutor = true, areaPlacementSupportedByExecutor = false,
                nativePlatformPlacementValidated = false, tileObjectDataPresent = data != null,
                metadataContractReviewed = reviewed,
                executionWhenObjectDataPresent = "WorldGen.PlaceObject_then_TileObject.CanPlace",
                executionWhenNoObjectData = "WorldGen.PlaceTile_single_cell",
                note = reviewed
                    ? "仅按原生单格零anchor元数据作条件式预检；实际放置、framing及潜在全局hooks未验证。区域平台仍不支持。"
                    : "真实执行有单格平台路径，但此类型/足迹/anchor/液体或metadata hooks变体未映射，设计预检仍unsupported。"
            });
            return new DesignItemData(id, item.Name, reviewed ? DesignItemKind.Platform : DesignItemKind.Unsupported, type,
                Width: data?.Width ?? 1, Height: data?.Height ?? 1,
                OriginX: data?.Origin.X ?? 0, OriginY: data?.Origin.Y ?? 0,
                Style: item.placeStyle, Solid: solid, Platform: true,
                Anchor: reviewed ? DesignAnchorRule.NativeObjectNoAnchors : DesignAnchorRule.Unknown, AnchorDetail: platformDetail);
        }
        if (data == null && solid && !platform)
            return new DesignItemData(id, item.Name, DesignItemKind.Block, type, Style: item.placeStyle, Solid: solid);
        if (data == null) return null; // No invented footprint or anchor for unsupported native objects.
        bool special = data.AnchorValidTiles?.Length > 0 || data.AnchorInvalidTiles?.Length > 0 ||
            data.AnchorAlternateTiles?.Length > 0 || data.AnchorValidWalls?.Length > 0;
        DesignAnchorRule anchor = special ? DesignAnchorRule.Unknown :
            data.AnchorLeft.tileCount > 0 || data.AnchorRight.tileCount > 0 || data.AnchorWall ? DesignAnchorRule.SideOrWall :
            data.AnchorBottom.tileCount > 0 && data.AnchorTop.tileCount > 0 ? DesignAnchorRule.FloorAndCeiling :
            data.AnchorBottom.tileCount > 0 ? DesignAnchorRule.Floor :
            data.AnchorTop.tileCount > 0 ? DesignAnchorRule.Ceiling : DesignAnchorRule.Unknown;
        string detail = JsonSerializer.Serialize(new
        {
            coordinateConvention = "place_left_top_then_native_origin_offset", origin = new[] { data.Origin.X, data.Origin.Y },
            bottom = new { type = data.AnchorBottom.type.ToString(), count = data.AnchorBottom.tileCount, start = data.AnchorBottom.checkStart },
            top = new { type = data.AnchorTop.type.ToString(), count = data.AnchorTop.tileCount, start = data.AnchorTop.checkStart },
            left = new { type = data.AnchorLeft.type.ToString(), count = data.AnchorLeft.tileCount, start = data.AnchorLeft.checkStart },
            right = new { type = data.AnchorRight.type.ToString(), count = data.AnchorRight.tileCount, start = data.AnchorRight.checkStart },
            wall = data.AnchorWall, specialAnchorRestrictions = special,
            nativePlacementValidated = false, door = type is TileID.ClosedDoor or TileID.OpenDoor
        });
        return new DesignItemData(id, item.Name, DesignItemKind.Furniture, type, Width: data.Width, Height: data.Height,
            OriginX: data.Origin.X, OriginY: data.Origin.Y, Style: item.placeStyle, Solid: solid,
            Platform: platform, Anchor: anchor, AnchorDetail: detail);
    }

    public static DesignCell ReadCell(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 2)) throw new ArgumentException("Cell outside the live world.");
        Tile tile = Main.tile[x, y];
        int type = tile.HasTile ? tile.TileType : -1;
        bool platform = tile.HasTile && (Main.tileSolidTop[type] || TileID.Sets.Platforms[type]);
        TileObjectData? data = tile.HasTile ? TileObjectData.GetTileData(tile) : null;
        bool furniture = tile.HasTile && data != null && !platform;
        int left = int.MinValue, top = int.MinValue, style = 0, alternate = 0;
        if ((furniture || platform) && data != null && data.CoordinateFullWidth > 0 && data.CoordinateFullHeight > 0)
            TileObjectData.GetTileInfo(tile, ref style, ref alternate);
        if (furniture && data!.CoordinateFullWidth > 0 && data.CoordinateFullHeight > 0)
        {
            left = x - tile.TileFrameX % data.CoordinateFullWidth / Math.Max(1, data.CoordinateWidth + data.CoordinatePadding);
            int frameY = tile.TileFrameY % data.CoordinateFullHeight, localRow = 0;
            while (localRow < data.Height - 1 && localRow < data.CoordinateHeights.Length && frameY >= data.CoordinateHeights[localRow] + data.CoordinatePadding)
                frameY -= data.CoordinateHeights[localRow++] + data.CoordinatePadding;
            top = y - localRow;
        }
        return new DesignCell(tile.HasTile, type, tile.WallType, tile.HasTile && Main.tileSolid[type], platform,
            tile.IsActuated, tile.IsHalfBlock, (int)tile.Slope, furniture, left, top, style,
            tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Lava);
    }

    private static bool IsRawMaterial(Item item) => item.type is ItemID.Wood or ItemID.StoneBlock or ItemID.DirtBlock or
        ItemID.CopperOre or ItemID.IronOre or ItemID.SilverOre or ItemID.GoldOre or ItemID.LeadOre or
        ItemID.TinOre or ItemID.TungstenOre or ItemID.PlatinumOre or ItemID.Meteorite;

    private static object InputIssue(string message) => new
    {
        topic = "design", source = "live_world", available = false, worldId = Main.worldID,
        callSucceeded = true, reviewStatus = "invalid_native_query", requiresRevision = true,
        observedAtTick = Main.GameUpdateCount,
        review = new { inputValid = false, staticConstraintsPassed = false, status = "invalid_native_query",
            issues = new[] { new { code = "native_observation_scope", severity = "error", message } },
            worldModified = false, constructionCompleted = false, nativeHousingValidated = false },
        worldModified = false, inventoryModified = false, actorsMoved = false
    };
}
