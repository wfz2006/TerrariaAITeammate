using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public static class CompanionCrafting
{
    public const int RecipeGroupOffset = 1_000_000;
    public const int MaxCrafts = 20;
    private static readonly FieldInfo? QuickLookupField = typeof(Recipe).GetField("requiredItemQuickLookup",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly Type? EntryType = QuickLookupField?.FieldType.GetElementType();
    private static readonly FieldInfo? EntryIdField = EntryType?.GetField("itemIdOrRecipeGroup",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo? EntryStackField = EntryType?.GetField("stack",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public static Recipe? FindRecipe(string query, out string error)
    {
        Recipe? recipe = FindRecipes(query).FirstOrDefault();
        error = recipe == null ? $"没找到“{query}”的可用配方。" : "";
        return recipe;
    }

    public static Recipe? FindRecipe(string query, CompanionLoadout loadout, NPC npc, out string error)
    {
        Dictionary<int, int> stock = InventoryCounts(loadout);
        Recipe? recipe = FindRecipes(query).OrderBy(candidate =>
        {
            bool materials = MaterialAllocator.TryAllocate(Requirements(candidate), stock, out _);
            bool conditions = ConditionsMet(candidate);
            bool station = HasStationNear(npc, candidate);
            return materials && conditions && station ? 0 : materials && conditions ? 1 : materials ? 2 : 3;
        }).ThenBy(candidate => Requirements(candidate).Sum(requirement =>
            Math.Max(0, requirement.Count - requirement.AcceptedItems.Sum(type => stock.GetValueOrDefault(type)))))
          .FirstOrDefault();
        error = recipe == null ? $"没找到“{query}”的可用配方。" : "";
        return recipe;
    }

    public static Recipe? FindRecipe(string query, CompanionLoadout loadout, NPC npc, int? recipeId, out string error)
    {
        if (!recipeId.HasValue) return FindRecipe(query, loadout, npc, out error);
        Recipe? selected = ExplicitTaskSelection.RecipeById(recipeId.Value,
            Math.Min(Recipe.numRecipes, Main.recipe.Length), index => Main.recipe[index],
            recipe => recipe.RecipeIndex == recipeId.Value && !recipe.Disabled &&
                !recipe.createItem.IsAir && MatchesItem(recipe.createItem, query));
        error = selected == null
            ? $"指定recipe_id={recipeId.Value}不存在、已禁用或产物不匹配{query}；没有改选其它配方。请重新inspect recipe。"
            : "";
        return selected;
    }

    public static IEnumerable<Recipe> FindRecipes(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<Recipe>();
        string normalized = query.Trim();
        var matches = Main.recipe.Take(Math.Min(Recipe.numRecipes, Main.recipe.Length))
            .Where(recipe => recipe != null && !recipe.Disabled && !recipe.createItem.IsAir)
            .Select(recipe => (Recipe: recipe, Score: MatchScore(recipe.createItem, normalized)))
            .Where(match => match.Score < int.MaxValue).ToArray();
        if (matches.Length == 0)
            return Array.Empty<Recipe>();
        int best = matches.Min(match => match.Score);
        return matches.Where(match => match.Score == best).Select(match => match.Recipe).ToArray();
    }

    public static bool MatchesItem(Item item, string query) => MatchScore(item, query.Trim()) < int.MaxValue;

    private static int MatchScore(Item item, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return int.MaxValue;
        if (int.TryParse(query, out int itemType))
            return item.type == itemType ? 0 : int.MaxValue;
        string localized = Lang.GetItemNameValue(item.type);
        string canonical = item.ModItem?.FullName ?? ItemID.Search.GetName(item.type) ?? "";
        if (localized.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            item.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            canonical.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (localized.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            canonical.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        return localized.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               canonical.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : int.MaxValue;
    }

    public static bool HasStationNear(NPC npc, Recipe recipe)
    {
        var stations = NearbyStations(npc);
        return recipe.requiredTile.All(required => stations.Any(station => StationMatches(station.Type, required)));
    }

    public static bool StationMatches(int actual, int required)
    {
        if (actual == required || (TileLoader.GetTile(actual)?.AdjTiles?.Contains(required) ?? false))
            return true;
        return required switch
        {
            TileID.Furnaces => actual is TileID.Hellforge or TileID.AdamantiteForge,
            TileID.Hellforge => actual == TileID.AdamantiteForge,
            TileID.Anvils => actual == TileID.MythrilAnvil,
            TileID.Bottles or TileID.Tables => actual == TileID.AlchemyTable,
            _ => false
        };
    }

    public static List<(int Type, int X, int Y)> NearbyStations(NPC npc, int radiusX = 5, int radiusY = 4)
    {
        int centerX = (int)(npc.Center.X / 16f);
        int centerY = (int)(npc.Center.Y / 16f);
        var stations = new List<(int, int, int)>();
        var nearestByType = new Dictionary<int, (int Type, int X, int Y)>();
        for (int x = centerX - radiusX; x <= centerX + radiusX; x++)
            for (int y = centerY - radiusY; y <= centerY + radiusY; y++)
            {
                if (!WorldGen.InWorld(x, y, 2))
                    continue;
                Tile tile = Main.tile[x, y];
                if (!tile.HasTile)
                    continue;
                if (!nearestByType.TryGetValue(tile.TileType, out var existing) ||
                    Math.Abs(x - centerX) + Math.Abs(y - centerY) <
                    Math.Abs(existing.X - centerX) + Math.Abs(existing.Y - centerY))
                    nearestByType[tile.TileType] = (tile.TileType, x, y);
            }
        stations.AddRange(nearestByType.Values);
        return stations;
    }

    public static bool FindNearestStation(NPC npc, Recipe recipe, out int x, out int y)
    {
        x = y = -1;
        if (recipe.requiredTile.Count == 0)
            return true;
        var nearby = NearbyStations(npc);
        int missing = recipe.requiredTile.FirstOrDefault(required =>
            !nearby.Any(station => StationMatches(station.Type, required)), -1);
        if (missing < 0)
            return true;
        float best = float.MaxValue;
        int cx = (int)(npc.Center.X / 16f);
        int cy = (int)(npc.Center.Y / 16f);
        for (int sx = cx - 40; sx <= cx + 40; sx++)
            for (int sy = cy - 25; sy <= cy + 25; sy++)
            {
                if (!WorldGen.InWorld(sx, sy, 2) || !Main.tile[sx, sy].HasTile ||
                    !StationMatches(Main.tile[sx, sy].TileType, missing))
                    continue;
                float distance = Vector2.DistanceSquared(npc.Center, new Vector2(sx * 16 + 8, sy * 16 + 8));
                if (distance >= best || !AllStationsAt(sx, sy, recipe))
                    continue;
                best = distance;
                x = sx;
                y = sy;
            }
        return x >= 0;
    }

    private static bool AllStationsAt(int cx, int cy, Recipe recipe)
    {
        var found = new HashSet<int>();
        for (int x = cx - 5; x <= cx + 5; x++)
            for (int y = cy - 4; y <= cy + 4; y++)
                if (WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile)
                    found.Add(Main.tile[x, y].TileType);
        return recipe.requiredTile.All(required => found.Any(actual => StationMatches(actual, required)));
    }

    public static bool ConditionsMet(Recipe recipe)
    {
        try { return !recipe.Disabled && RecipeLoader.RecipeAvailable(recipe); }
        catch { return false; }
    }

    public static Dictionary<int, int> InventoryCounts(CompanionLoadout loadout, Player? player = null)
    {
        var counts = loadout.Inventory.Where(item => !item.IsAir && item.stack > 0)
            .GroupBy(item => item.type).ToDictionary(group => group.Key, group => group.Sum(item => item.stack));
        if (player is { active: true })
        {
            foreach (Item item in player.inventory.Where(item => !item.IsAir && item.stack > 0))
                counts[item.type] = counts.GetValueOrDefault(item.type) + item.stack;
        }
        return counts;
    }

    public static IReadOnlyList<MaterialRequirement> Requirements(Recipe recipe, int crafts = 1)
    {
        var rows = new List<(int Id, int Stack)>();
        if (QuickLookupField?.GetValue(recipe) is Array lookup && lookup.Length > 0 &&
            EntryIdField != null && EntryStackField != null)
            foreach (object entry in lookup)
            {
                int id = (int)(EntryIdField.GetValue(entry) ?? 0);
                int stack = (int)(EntryStackField.GetValue(entry) ?? 0);
                if (id > 0 && stack > 0)
                    rows.Add((id, stack));
            }
        if (rows.Count == 0)
            foreach (Item item in recipe.requiredItem.Where(item => !item.IsAir))
            {
                int id = item.type;
                foreach (int groupId in recipe.acceptedGroups)
                    if (RecipeGroup.recipeGroups.TryGetValue(groupId, out RecipeGroup? group) &&
                        group.ValidItems.Contains(item.type))
                    {
                        id = RecipeGroupOffset + groupId;
                        break;
                    }
                rows.Add((id, item.stack));
            }
        return rows.Select(row => new MaterialRequirement(row.Id,
            checked(row.Stack * Math.Clamp(crafts, 1, MaxCrafts)),
            row.Id >= RecipeGroupOffset
                ? RecipeGroup.recipeGroups.TryGetValue(row.Id - RecipeGroupOffset, out RecipeGroup? group)
                    ? group.ValidItems.ToArray() : Array.Empty<int>()
                : new[] { row.Id })).ToArray();
    }

    public static string RequirementName(int id) => id >= RecipeGroupOffset
        ? RecipeGroup.recipeGroups.TryGetValue(id - RecipeGroupOffset, out RecipeGroup? group)
            ? group.GetText?.Invoke() ?? "材料组" : "未知材料组"
        : Lang.GetItemNameValue(id);

    public static bool TryCraft(CompanionLoadout loadout, Recipe recipe, int requestedCount, NPC npc, out string message)
        => TryCraft(loadout, recipe, requestedCount, npc, out message, out _);

    internal static bool TryCraft(CompanionLoadout loadout, Recipe recipe, int requestedCount, NPC npc, out string message, out int produced)
    {
        produced = 0;
        if (!ConditionsMet(recipe))
        {
            message = "配方条件未满足：" + string.Join("、", recipe.Conditions.Select(condition => condition.Description.Value));
            return false;
        }
        if (!HasStationNear(npc, recipe))
        {
            message = "缺少配方所需的全部工作站。";
            return false;
        }
        int perCraft = Math.Max(1, recipe.createItem.stack);
        int crafts = Math.Clamp((int)Math.Ceiling(Math.Max(1, requestedCount) / (double)perCraft), 1, MaxCrafts);
        var requirements = Requirements(recipe, crafts);
        Dictionary<int, int> stock = InventoryCounts(loadout);
        if (!MaterialAllocator.TryAllocate(requirements, stock, out Dictionary<int, int> consumption))
        {
            message = "材料不够：" + string.Join("、", requirements.Select(requirement =>
                $"{RequirementName(requirement.Id)}需{requirement.Count}，持有{requirement.AcceptedItems.Sum(type => stock.GetValueOrDefault(type))}"));
            return false;
        }

        // Prepare the whole transaction on clones, including slots freed by ingredient consumption.
        Item[] prepared = loadout.Inventory.Select(item => item.Clone()).ToArray();
        foreach ((int type, int amount) in consumption)
        {
            int remaining = amount;
            foreach (Item item in prepared.Where(item => !item.IsAir && item.type == type))
            {
                int take = Math.Min(remaining, item.stack);
                item.stack -= take;
                remaining -= take;
                if (item.stack <= 0)
                    item.TurnToAir();
                if (remaining <= 0)
                    break;
            }
        }
        int outputCount = checked(perCraft * crafts);
        int left = outputCount;
        foreach (Item item in prepared)
        {
            if (item.IsAir || item.type != recipe.createItem.type || item.prefix != recipe.createItem.prefix ||
                !ItemLoader.CanStack(item, recipe.createItem))
                continue;
            int added = Math.Min(left, Math.Max(0, item.maxStack - item.stack));
            item.stack += added;
            left -= added;
        }
        for (int i = 0; i < prepared.Length && left > 0; i++)
            if (prepared[i].IsAir)
            {
                prepared[i] = recipe.createItem.Clone();
                prepared[i].stack = Math.Min(left, prepared[i].maxStack);
                left -= prepared[i].stack;
            }
        if (left > 0)
        {
            message = "背包放不下做出来的东西，材料没有扣除。";
            return false;
        }
        for (int i = 0; i < prepared.Length; i++)
            loadout.Inventory[i] = prepared[i];
        produced = outputCount;
        SoundEngine.PlaySound(SoundID.Item1, npc.Center);
        string name = Lang.GetItemNameValue(recipe.createItem.type);
        AITeammateSystem.Instance.TestLog($"crafted {name} x{outputCount} crafts={crafts} recipe={recipe.RecipeIndex}");
        message = $"做好了 {name}x{outputCount}。";
        return true;
    }
}

