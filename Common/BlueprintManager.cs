using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace TerrariaAITeammate.Common;

public sealed class Blueprint
{
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public List<BlueprintTile> Tiles { get; set; } = new();
    public List<BlueprintWall> Walls { get; set; } = new();
}

public sealed class BlueprintTile
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Type { get; set; }
    public short FrameX { get; set; }
    public short FrameY { get; set; }
    public int Slope { get; set; }
    public bool HalfBlock { get; set; }
    public byte Color { get; set; }
    public int FrameNumber { get; set; }
    public int ItemType { get; set; }
    public int ItemStack { get; set; }
}

public sealed class BlueprintWall
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Type { get; set; }
    public int FrameX { get; set; }
    public int FrameY { get; set; }
    public byte Color { get; set; }
    public int FrameNumber { get; set; }
    public int ItemType { get; set; }
    public int ItemStack { get; set; }
}

public static class BlueprintManager
{
    private const int MaxWidth = 120;
    private const int MaxHeight = 90;

    public static (int X, int Y)? Corner1 { get; private set; }
    public static (int X, int Y)? Corner2 { get; private set; }
    public static bool HasSelection => Corner1.HasValue && Corner2.HasValue;

    public static string SetCorner(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 2))
            return "这个点在世界外面。";

        if (!Corner1.HasValue || Corner2.HasValue)
        {
            Corner1 = (x, y);
            Corner2 = null;
            return $"第一点 ({x},{y}) 已选，再点第二点。";
        }

        Corner2 = (x, y);
        return $"第二点 ({x},{y}) 已选。用 /ai blueprint save 名字 保存。";
    }

    public static void ClearSelection()
    {
        Corner1 = null;
        Corner2 = null;
    }

    public static bool TryGetSelection(out int x1, out int y1, out int x2, out int y2)
    {
        x1 = y1 = x2 = y2 = 0;
        if (!Corner1.HasValue || !Corner2.HasValue)
            return false;

        (x1, y1) = Corner1.Value;
        (x2, y2) = Corner2.Value;
        return true;
    }

    public static bool SaveSelection(string name, out string message)
    {
        if (!TryGetSelection(out int x1, out int y1, out int x2, out int y2))
        {
            message = "先用蓝图魔杖点两个角。";
            return false;
        }

        return Capture(name, x1, y1, x2, y2, out message);
    }

    public static bool Capture(string name, int x1, int y1, int x2, int y2, out string message)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            message = "蓝图得有个名字。";
            return false;
        }

        int minX = Math.Clamp(Math.Min(x1, x2), 5, Main.maxTilesX - 5);
        int maxX = Math.Clamp(Math.Max(x1, x2), 5, Main.maxTilesX - 5);
        int minY = Math.Clamp(Math.Min(y1, y2), 5, Main.maxTilesY - 5);
        int maxY = Math.Clamp(Math.Max(y1, y2), 5, Main.maxTilesY - 5);
        int width = maxX - minX + 1;
        int height = maxY - minY + 1;

        if (width > MaxWidth || height > MaxHeight)
        {
            message = $"选区太大了，最大 {MaxWidth}x{MaxHeight}。";
            return false;
        }

        var blueprint = new Blueprint
        {
            Name = name,
            Width = width,
            Height = height
        };

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                Tile tile = Main.tile[x, y];
                int rx = x - minX;
                int ry = y - minY;

                if (tile.HasTile)
                {
                    int style = 0;
                    int alternate = 0;
                    try
                    {
                        TileObjectData.GetTileInfo(tile, ref style, ref alternate);
                    }
                    catch
                    {
                        style = 0;
                    }

                    int item = TileLoader.GetItemDropFromTypeAndStyle(tile.TileType, style);
                    bool objectTile = false;
                    try
                    {
                        objectTile = TileObjectData.GetTileData(tile) != null;
                    }
                    catch
                    {
                        objectTile = false;
                    }

                    bool countItem = !objectTile || (tile.TileFrameX == 0 && tile.TileFrameY == 0) ||
                                     TileID.Sets.Platforms[tile.TileType];
                    blueprint.Tiles.Add(new BlueprintTile
                    {
                        X = rx,
                        Y = ry,
                        Type = tile.TileType,
                        FrameX = tile.TileFrameX,
                        FrameY = tile.TileFrameY,
                        Slope = (int)tile.Slope,
                        HalfBlock = tile.IsHalfBlock,
                        Color = tile.TileColor,
                        FrameNumber = tile.TileFrameNumber,
                        ItemType = item,
                        ItemStack = countItem && item > ItemID.None ? 1 : 0
                    });
                }

                if (tile.WallType > WallID.None && tile.WallType < WallID.Count)
                {
                    int item = WallDrop(x, y, tile.WallType);
                    blueprint.Walls.Add(new BlueprintWall
                    {
                        X = rx,
                        Y = ry,
                        Type = tile.WallType,
                        FrameX = tile.WallFrameX,
                        FrameY = tile.WallFrameY,
                        Color = tile.WallColor,
                        FrameNumber = tile.WallFrameNumber,
                        ItemType = item,
                        ItemStack = item > ItemID.None ? 1 : 0
                    });
                }
            }
        }

        if (blueprint.Tiles.Count == 0 && blueprint.Walls.Count == 0)
        {
            message = "选区里没有东西。";
            return false;
        }

        Save(blueprint);
        message = $"蓝图“{name}”已保存：{blueprint.Width}x{blueprint.Height}，方块 {blueprint.Tiles.Count}、墙 {blueprint.Walls.Count}。";
        AITeammateSystem.Instance.TestLog(message);
        return true;
    }

    public static Dictionary<int, int> RequiredItems(Blueprint blueprint)
    {
        var required = new Dictionary<int, int>();

        foreach (BlueprintTile tile in blueprint.Tiles)
        {
            if (tile.ItemType > ItemID.None && tile.ItemStack > 0)
                required[tile.ItemType] = required.GetValueOrDefault(tile.ItemType) + tile.ItemStack;
        }

        foreach (BlueprintWall wall in blueprint.Walls)
        {
            if (wall.ItemType > ItemID.None && wall.ItemStack > 0)
                required[wall.ItemType] = required.GetValueOrDefault(wall.ItemType) + wall.ItemStack;
        }

        return required;
    }

    public static bool ConsumeMaterials(Player player, CompanionLoadout loadout, Blueprint blueprint, out string message)
    {
        Dictionary<int, int> required = RequiredItems(blueprint);
        var missing = new List<string>();

        foreach ((int type, int count) in required)
        {
            int owned = loadout.CountItem(type);
            if (owned < count)
                missing.Add($"{Lang.GetItemNameValue(type)}x{count - owned}");
        }

        if (missing.Count > 0)
        {
            message = "材料不够：" + string.Join("、", missing.Take(5)) + (missing.Count > 5 ? " 等" : "") + "。";
            return false;
        }

        foreach ((int type, int count) in required)
        {
            loadout.RemoveItem(type, count);
        }

        message = $"材料齐了，共 {required.Values.Sum()} 件。";
        return true;
    }

    public static Blueprint? Load(string name)
    {
        string path = BlueprintPath(name);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Blueprint>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static bool Exists(string name) => File.Exists(BlueprintPath(name));

    public static List<string> ListNames()
    {
        string directory = BlueprintDirectory();
        if (!Directory.Exists(directory))
            return new List<string>();

        return Directory.GetFiles(directory, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OrderBy(name => name)
            .ToList();
    }

    public static bool Delete(string name, out string message)
    {
        string path = BlueprintPath(name);
        if (!File.Exists(path))
        {
            message = $"没有蓝图“{name}”。";
            return false;
        }

        File.Delete(path);
        message = $"蓝图“{name}”已删除。";
        return true;
    }

    private static void Save(Blueprint blueprint)
    {
        string directory = BlueprintDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllText(BlueprintPath(blueprint.Name), JsonSerializer.Serialize(blueprint));
    }

    private static int WallDrop(int x, int y, int wallType)
    {
        int item = 0;
        try
        {
            int drop = 0;
            if (WallLoader.Drop(x, y, wallType, ref drop))
                item = drop;
        }
        catch
        {
            item = 0;
        }

        if (item > ItemID.None)
            return item;

        return wallType switch
        {
            WallID.Wood => ItemID.WoodWall,
            WallID.Stone => ItemID.StoneWall,
            WallID.DirtUnsafe or WallID.Dirt => ItemID.DirtWall,
            WallID.Glass => ItemID.GlassWall,
            _ => ItemID.None
        };
    }

    private static int CountPlayerItems(Player player, int type)
    {
        return player.inventory.Where(item => !item.IsAir && item.type == type).Sum(item => item.stack);
    }

    private static void RemovePlayerItems(Player player, int type, int count)
    {
        int remaining = count;
        foreach (Item item in player.inventory)
        {
            if (remaining <= 0)
                break;

            if (item.IsAir || item.type != type)
                continue;

            int take = Math.Min(remaining, item.stack);
            item.stack -= take;
            remaining -= take;
            if (item.stack <= 0)
                item.SetDefaults(ItemID.None);
        }
    }

    private static string BlueprintDirectory()
    {
        string world = SafeName(Main.worldName);
        string player = SafeName(Main.player[Main.myPlayer]?.name ?? "player");
        return Path.Combine(Main.SavePath, "AITeammate", "blueprints", $"{world}_{player}");
    }

    private static string BlueprintPath(string name) => Path.Combine(BlueprintDirectory(), SafeName(name) + ".json");

    private static string SafeName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim();
    }
}
