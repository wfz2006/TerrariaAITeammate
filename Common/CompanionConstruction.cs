using System;
using System.Collections.Generic;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public readonly record struct ConstructionStep(int X, int Y, int Tile, int Wall, int ItemType, int Style = 0);

/// <summary>Geometry and costs share the same placement list, so advertised materials cannot drift.</summary>
public static class CompanionConstruction
{
    public const int Width = 10;
    public const int Height = 7;

    public static (int X, int Y) BuilderStand(int left, int top) => (left - 2, top + Height);

    public static IReadOnlyDictionary<int, int> ShelterMaterials(string material)
    {
        var result = new Dictionary<int, int>();
        foreach (ConstructionStep step in ShelterLayout(0, 0, material))
            result[step.ItemType] = result.GetValueOrDefault(step.ItemType) + 1;
        return result;
    }

    // Coordinates for furniture identify its top-left corner, not its placement origin.
    // Resolve native createTile/placeStyle from each ItemType before execution;
    // species furniture shares tile types but has different native object styles.
    public static List<ConstructionStep> ShelterLayout(int left, int top, string material)
    {
        bool stone = material.Equals("stone", StringComparison.OrdinalIgnoreCase);
        bool ebonwood = material.Equals("ebonwood", StringComparison.OrdinalIgnoreCase);
        if (!stone && !ebonwood && !string.IsNullOrWhiteSpace(material) && !material.Equals("wood", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Shelter material must be wood, stone or ebonwood.", nameof(material));

        int block = stone ? TileID.Stone : ebonwood ? TileID.Ebonwood : TileID.WoodBlock;
        int wall = stone ? WallID.Stone : ebonwood ? WallID.Ebonwood : WallID.Wood;
        int blockItem = stone ? ItemID.StoneBlock : ebonwood ? ItemID.Ebonwood : ItemID.Wood;
        int wallItem = stone ? ItemID.StoneWall : ebonwood ? ItemID.EbonwoodWall : ItemID.WoodWall;
        int doorItem = ebonwood ? ItemID.EbonwoodDoor : ItemID.WoodenDoor;
        int workbenchItem = ebonwood ? ItemID.EbonwoodWorkBench : ItemID.WorkBench;
        int chairItem = ebonwood ? ItemID.EbonwoodChair : ItemID.WoodenChair;
        var steps = new List<ConstructionStep>();
        for (int x = 0; x < Width; x++)
            steps.Add(new(left + x, top + Height - 1, block, 0, blockItem));
        for (int y = Height - 2; y >= 1; y--)
        {
            if (y < Height - 4)
            {
                steps.Add(new(left, top + y, block, 0, blockItem));
                steps.Add(new(left + Width - 1, top + y, block, 0, blockItem));
            }
        }
        for (int x = 0; x < Width; x++)
            steps.Add(new(left + x, top, block, 0, blockItem));
        for (int x = 1; x < Width - 1; x++)
            for (int y = 1; y < Height - 1; y++)
                steps.Add(new(left + x, top + y, 0, wall, wallItem));

        steps.Add(new(left, top + Height - 4, TileID.ClosedDoor, 0, doorItem));
        // Keep a surface shelter traversable from either side instead of sealing a travel route.
        steps.Add(new(left + Width - 1, top + Height - 4, TileID.ClosedDoor, 0, doorItem));
        steps.Add(new(left + 3, top + Height - 2, TileID.WorkBenches, 0, workbenchItem));
        steps.Add(new(left + 6, top + Height - 3, TileID.Chairs, 0, chairItem));
        steps.Add(new(left + 2, top + 2, TileID.Torches, 0, ItemID.Torch));
        return steps;
    }
}
