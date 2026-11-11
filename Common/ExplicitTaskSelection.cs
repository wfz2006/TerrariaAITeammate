using System;
using System.Collections.Generic;

namespace TerrariaAITeammate.Common;

public readonly record struct ObservedChestTarget(int Index, int X, int Y, float DistancePixels, bool Built);

/// <summary>Strict identities shared by native adapters and pure checks; no fallback or target generation.</summary>
public static class ExplicitTaskSelection
{
    public static T? RecipeById<T>(int id, int recipeCount, Func<int, T?> read, Func<T, bool> usable) where T : class
    {
        if (id < 0 || id >= recipeCount) return null;
        T? recipe = read(id);
        return recipe != null && usable(recipe) ? recipe : null;
    }

    public static int ChestByCoordinates(int? x, int? y, float rangePixels, IEnumerable<ObservedChestTarget> chests)
    {
        if (!x.HasValue || !y.HasValue) return -1;
        foreach (ObservedChestTarget chest in chests)
            if (chest.Built && chest.DistancePixels <= rangePixels && ChestContains(chest.X, chest.Y, x, y))
                return chest.Index;
        return -1;
    }

    public static bool ChestContains(int chestX, int chestY, int? x, int? y)
        => chestX > 0 && chestY > 0 && x.HasValue && y.HasValue &&
            x.Value >= chestX && (long)x.Value < (long)chestX + 2 &&
            y.Value >= chestY && (long)y.Value < (long)chestY + 2;
}
