using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public class PerceptionPoint
{
    public string Kind { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Distance { get; set; }
}

public static class AIPerception
{
    private const int RadiusX = 28;
    private const int RadiusY = 18;

    public static object Build(Player player, NPC? companion)
    {
        Vector2 self = companion?.Center ?? player.Center;
        return new
        {
            self = BuildAround(self),
            player = BuildAround(player.Center)
        };
    }

    public static string Summary(Player player, NPC? companion)
    {
        return JsonSerializer.Serialize(Build(player, companion));
    }

    private static object BuildAround(Vector2 worldCenter)
    {
        int centerX = (int)(worldCenter.X / 16f);
        int centerY = (int)(worldCenter.Y / 16f);

        var ores = new List<PerceptionPoint>();
        var chests = new List<PerceptionPoint>();
        var crystals = new List<PerceptionPoint>();
        var hazards = new List<PerceptionPoint>();
        var trees = new List<PerceptionPoint>();
        var stone = new List<PerceptionPoint>();
        var stations = new List<PerceptionPoint>();
        var structureAnchors = new List<PerceptionPoint>();
        var stationTypes = Main.recipe.Take(Math.Min(Recipe.numRecipes, Main.recipe.Length))
            .Where(recipe => recipe != null && !recipe.Disabled)
            .SelectMany(recipe => recipe.requiredTile).ToHashSet();

        for (int x = centerX - RadiusX; x <= centerX + RadiusX; x++)
        {
            for (int y = centerY - RadiusY; y <= centerY + RadiusY; y++)
            {
                if (!WorldGen.InWorld(x, y, 2))
                    continue;

                Tile tile = Main.tile[x, y];
                int distance = Math.Abs(x - centerX) + Math.Abs(y - centerY);

                if (tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Lava)
                    hazards.Add(new PerceptionPoint { Kind = "熔岩", X = x, Y = y, Distance = distance });

                if (tile.WallType > WallID.None && tile.WallType < Main.wallHouse.Length && Main.wallHouse[tile.WallType])
                    structureAnchors.Add(new PerceptionPoint { Kind = "observed_safe_wall", X = x, Y = y, Distance = distance });

                if (!tile.HasTile)
                    continue;

                if (AIExecutor.IsValidTreeTrunkOrWood(x, y))
                    trees.Add(new PerceptionPoint { Kind = "wood", X = x, Y = y, Distance = distance });
                if (tile.TileType == TileID.Stone && !AIExecutor.IsPartOfHouseOrStructure(x, y))
                    stone.Add(new PerceptionPoint { Kind = "stone", X = x, Y = y, Distance = distance });
                if (stationTypes.Contains(tile.TileType))
                    stations.Add(new PerceptionPoint
                    {
                        Kind = TileID.Search.GetName(tile.TileType) ?? tile.TileType.ToString(),
                        X = x, Y = y, Distance = distance
                    });

                string? ore = OreName(tile.TileType);
                if (ore != null)
                {
                    ores.Add(new PerceptionPoint { Kind = ore, X = x, Y = y, Distance = distance });
                    continue;
                }

                if (tile.TileType is TileID.Containers or TileID.Containers2)
                    chests.Add(new PerceptionPoint { Kind = "宝箱", X = x, Y = y, Distance = distance });
                else if (tile.TileType == TileID.Heart)
                    crystals.Add(new PerceptionPoint { Kind = "生命水晶", X = x, Y = y, Distance = distance });
            }
        }

        return new
        {
            center = new { x = centerX, y = centerY },
            radius = new { x = RadiusX, y = RadiusY },
            layer = centerY > Main.rockLayer ? "洞穴" : centerY > Main.worldSurface ? "地下" : "地表",
            ores = ores.OrderBy(point => point.Distance).Take(8).ToArray(),
            chests = chests.OrderBy(point => point.Distance).Take(5).ToArray(),
            crystals = crystals.OrderBy(point => point.Distance).Take(5).ToArray(),
            hazards = hazards.OrderBy(point => point.Distance).Take(4).ToArray(),
            trees = trees.OrderBy(point => point.Distance).GroupBy(point => point.X)
                .Select(group => group.First()).Take(6).ToArray(),
            stone = stone.OrderBy(point => point.Distance).Take(6).ToArray(),
            stations = stations.OrderBy(point => point.Distance).GroupBy(point => point.Kind)
                .Select(group => group.First()).Take(12).ToArray(),
            structureAnchors = structureAnchors.OrderBy(point => point.Distance)
                .GroupBy(point => (point.X / 8, point.Y / 8)).Select(group => group.First()).Take(8).ToArray(),
            structureNote = "这些仅是实际观察到的建筑墙锚点；完整范围、楼层、门家具和可站位置必须inspect structure，不能由锚点猜测尺寸。"
        };
    }

    private static string? OreName(int tileType) => tileType switch
    {
        TileID.Copper => "铜矿",
        TileID.Iron => "铁矿",
        TileID.Silver => "银矿",
        TileID.Gold => "金矿",
        TileID.Lead => "铅矿",
        TileID.Tin => "锡矿",
        TileID.Tungsten => "钨矿",
        TileID.Platinum => "铂矿",
        TileID.Meteorite => "陨石",
        _ => null
    };
}
