using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public static class CompanionChests
{
    public static int FindNearestChest(NPC npc, float range = 60f * 16f)
    {
        int best = -1;
        float bestDistance = range;

        for (int i = 0; i < Main.chest.Length; i++)
        {
            Chest? chest = Main.chest[i];
            if (chest == null || chest.x <= 0 || chest.y <= 0 || !IsChestBuilt(chest))
                continue;

            float distance = Vector2.Distance(Center(chest), npc.Center);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    public static int FindTargetChest(NPC npc, bool deposit, string query, int? explicitX = null, int? explicitY = null,
        int preferredIndex = -1, float range = 60f * 16f)
    {
        if (explicitX.HasValue || explicitY.HasValue)
        {
            return ExplicitTaskSelection.ChestByCoordinates(explicitX, explicitY, range,
                Main.chest.Select((chest, index) => chest == null
                    ? new ObservedChestTarget(index, 0, 0, float.PositiveInfinity, false)
                    : new ObservedChestTarget(index, chest.x, chest.y,
                        Vector2.Distance(Center(chest), npc.Center), IsChestBuilt(chest))));
        }

        string trimmed = (query ?? "").Trim();
        bool all = string.IsNullOrWhiteSpace(trimmed) ||
                   trimmed.ToLowerInvariant() is "all" or "全部" or "所有" or "everything";

        if (!deposit)
        {
            if (preferredIndex >= 0 && GetChest(preferredIndex) is { } preferred &&
                !Chest.IsLocked(preferred.x, preferred.y) &&
                Vector2.Distance(Center(preferred), npc.Center) <= 20f * 16f &&
                preferred.item.Any(item => !item.IsAir && item.stack > 0 && (all || Matches(item, trimmed))))
            {
                return preferredIndex;
            }

            if (!all)
            {
                int bestMatch = -1;
                float bestMatchDist = range;
                for (int i = 0; i < Main.chest.Length; i++)
                {
                    Chest? chest = Main.chest[i];
                    if (chest == null || chest.x <= 0 || chest.y <= 0 || !IsChestBuilt(chest) || Chest.IsLocked(chest.x, chest.y))
                        continue;

                    float distance = Vector2.Distance(Center(chest), npc.Center);
                    if (distance >= bestMatchDist)
                        continue;

                    if (chest.item.Any(item => !item.IsAir && item.stack > 0 && Matches(item, trimmed)))
                    {
                        bestMatchDist = distance;
                        bestMatch = i;
                    }
                }
                if (bestMatch >= 0)
                    return bestMatch;
            }
        }
        else if (preferredIndex >= 0 && GetChest(preferredIndex) is { } preferredDeposit &&
                 !Chest.IsLocked(preferredDeposit.x, preferredDeposit.y) &&
                 Vector2.Distance(Center(preferredDeposit), npc.Center) <= 16f * 16f)
        {
            return preferredIndex;
        }

        return FindNearestChest(npc, range);
    }

    public static List<(int Index, Chest Chest, float DistanceTiles)> FindNearbyChests(NPC npc, float range = 30f * 16f, int maxCount = 5)
    {
        var list = new List<(int Index, Chest Chest, float DistanceTiles)>();
        for (int i = 0; i < Main.chest.Length; i++)
        {
            Chest? chest = Main.chest[i];
            if (chest == null || chest.x <= 0 || chest.y <= 0 || !IsChestBuilt(chest))
                continue;
            float dist = Vector2.Distance(Center(chest), npc.Center);
            if (dist <= range)
                list.Add((i, chest, dist / 16f));
        }
        return list.OrderBy(entry => entry.DistanceTiles).Take(maxCount).ToList();
    }

    public static Chest? GetChest(int index)
    {
        if (index < 0 || index >= Main.chest.Length)
            return null;

        Chest? chest = Main.chest[index];
        return chest != null && IsChestBuilt(chest) ? chest : null;
    }

    public static Vector2 Center(Chest chest) => new(chest.x * 16f + 16f, chest.y * 16f + 16f);

    public static bool MatchesExplicitTarget(Chest chest, int? x, int? y)
        => !x.HasValue && !y.HasValue ||
            IsChestBuilt(chest) && ExplicitTaskSelection.ChestContains(chest.x, chest.y, x, y);

    public static bool IsChestBuilt(Chest chest)
    {
        if (chest.x <= 0 || chest.y <= 0 || !WorldGen.InWorld(chest.x, chest.y, 2))
            return false;

        Tile tile = Main.tile[chest.x, chest.y];
        return tile.HasTile && (tile.TileType == TileID.Containers || tile.TileType == TileID.Containers2);
    }

    public static bool Deposit(CompanionLoadout loadout, Chest chest, string query, int count, out string message)
    {
        if (Chest.IsLocked(chest.x, chest.y)) { message = "箱子上锁，当前技能不能解锁。"; return false; }
        bool all = string.IsNullOrWhiteSpace(query) || query is "all" or "全部" or "所有" or "everything";
        int remaining = count <= 0 ? int.MaxValue : count;
        int moved = 0;

        foreach (Item item in loadout.Inventory)
        {
            if (remaining <= 0)
                break;

            if (item.IsAir || item.stack <= 0)
                continue;

            if (all)
            {
                if (!CanStore(item))
                    continue;
            }
            else if (!Matches(item, query))
            {
                continue;
            }

            int amount = Math.Min(item.stack, remaining);
            int actual = MoveToChest(chest, item, amount);
            moved += actual;
            remaining -= actual;
        }

        if (moved <= 0)
        {
            message = all ? "背包里没有适合存箱子的东西。" : $"背包里没有{query}。";
            return false;
        }

        OpenFeedback(chest);
        message = $"往箱子里存了 {moved} 件东西。";
        AITeammateSystem.Instance.TestLog($"chest deposit moved={moved} chest={chest.x},{chest.y}");
        return true;
    }

    public static bool Withdraw(CompanionLoadout loadout, Chest chest, string query, int count, out string message)
    {
        if (Chest.IsLocked(chest.x, chest.y)) { message = "箱子上锁，当前技能不能解锁。"; return false; }
        if (string.IsNullOrWhiteSpace(query))
        {
            message = "要我从箱子里取什么？";
            return false;
        }

        bool all = query.Trim().ToLowerInvariant() is "all" or "全部" or "所有" or "everything";
        int remaining = count <= 0 ? int.MaxValue : count;
        int moved = 0;

        IEnumerable<Item> orderedSlots = all
            ? chest.item.OrderBy(item => item != null && !item.IsAir &&
                item.type is ItemID.CopperCoin or ItemID.SilverCoin or ItemID.GoldCoin or ItemID.PlatinumCoin ? 1 : 0)
            : chest.item;

        foreach (Item item in orderedSlots)
        {
            if (remaining <= 0)
                break;

            if (item == null || item.IsAir || item.stack <= 0 || (!all && !Matches(item, query)))
                continue;

            int amount = Math.Min(item.stack, remaining);
            int added = loadout.AddItemStack(item.type, amount);
            if (added <= 0)
                continue;

            item.stack -= added;
            if (item.stack <= 0)
                item.SetDefaults(ItemID.None);

            moved += added;
            remaining -= added;
        }

        if (moved <= 0)
        {
            message = $"箱子里没有取到{query}。";
            return false;
        }

        OpenFeedback(chest);
        message = $"从箱子里取了 {moved} 件{query}。";
        AITeammateSystem.Instance.TestLog($"chest withdraw moved={moved} chest={chest.x},{chest.y}");
        return true;
    }

    private static int MoveToChest(Chest chest, Item source, int amount)
    {
        int remaining = amount;

        foreach (Item target in chest.item)
        {
            if (remaining <= 0)
                break;

            if (target.IsAir || target.type != source.type || target.stack >= target.maxStack)
                continue;

            int add = Math.Min(remaining, target.maxStack - target.stack);
            target.stack += add;
            remaining -= add;
        }

        for (int i = 0; i < chest.item.Length && remaining > 0; i++)
        {
            Item target = chest.item[i];
            if (!target.IsAir)
                continue;

            int stack = Math.Min(remaining, source.maxStack);
            target.SetDefaults(source.type);
            target.stack = stack;
            target.prefix = source.prefix;
            remaining -= stack;
        }

        int moved = amount - remaining;
        if (moved <= 0)
            return 0;

        source.stack -= moved;
        if (source.stack <= 0)
            source.SetDefaults(ItemID.None);

        return moved;
    }

    private static bool CanStore(Item item)
    {
        if (CompanionLoadout.IsCombatWeapon(item) || item.pick > 0 || item.axe > 0 || item.hammer > 0)
            return false;

        if (item.headSlot >= 0 || item.bodySlot >= 0 || item.legSlot >= 0 || item.accessory)
            return false;

        return true;
    }

    public static bool Matches(Item item, string query)
    {
        if (int.TryParse(query, out int itemType))
            return item.type == itemType;

        string localized = Lang.GetItemNameValue(item.type);
        string english = item.Name ?? "";
        return localized.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               english.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenFeedback(Chest chest)
    {
        SoundEngine.PlaySound(SoundID.Item1, Center(chest));
    }
}
