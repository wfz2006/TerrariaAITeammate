using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace TerrariaAITeammate.Common;

public class CompanionLoadout
{
    public const int InventorySize = 50;
    public const int ArmorSize = 3;
    public const int AccessorySize = 3;

    public Item[] Inventory { get; } = CreateItems(InventorySize);
    public Item[] Armor { get; } = CreateItems(ArmorSize);
    public Item[] Accessories { get; } = CreateItems(AccessorySize);

    public int Defense =>
        Armor.Sum(item => item.IsAir ? 0 : item.defense) +
        Accessories.Sum(item => item.IsAir ? 0 : item.defense);

    public int WeaponDamage => GetBestWeapon()?.damage ?? 20;

    public int PreferredWeaponType { get; set; }
    public int Mana { get; set; } = 100;
    public int ManaMax => 100;

    public static bool IsCombatWeapon(Item item)
    {
        if (item.IsAir || item.damage <= 0)
            return false;
        if (item.pick > 0 || item.axe > 0 || item.hammer > 0)
            return false;
        if (item.ammo > 0 || item.notAmmo)
            return false;
        if (item.type is ItemID.CopperCoin or ItemID.SilverCoin or ItemID.GoldCoin or ItemID.PlatinumCoin)
            return false;
        if (item.useStyle == ItemUseStyleID.None)
            return false;
        if (item.createTile >= TileID.Dirt || item.createWall > WallID.None)
            return false;
        return true;
    }

    public Item? GetBestWeapon()
    {
        Item? preferred = Inventory.FirstOrDefault(item =>
            !item.IsAir && item.type == PreferredWeaponType && IsCombatWeapon(item));
        if (preferred != null)
            return preferred;

        Item? weapon = Inventory
            .Where(IsCombatWeapon)
            .OrderByDescending(item => item.damage)
            .FirstOrDefault();

        return weapon ?? Inventory
            .Where(item => !item.IsAir && item.damage > 0 &&
                          item.ammo == 0 && !item.notAmmo &&
                          item.type is not (ItemID.CopperCoin or ItemID.SilverCoin or ItemID.GoldCoin or ItemID.PlatinumCoin) &&
                          item.useStyle != ItemUseStyleID.None)
            .OrderByDescending(item => item.damage)
            .FirstOrDefault();
    }

    public Item? GetBestMeleeWeapon()
    {
        return Inventory
            .Where(item => IsCombatWeapon(item) &&
                          (item.DamageType == DamageClass.Melee || item.DamageType == DamageClass.MeleeNoSpeed))
            .OrderByDescending(item => item.damage)
            .FirstOrDefault();
    }

    public Item? GetBestPickaxe()
    {
        return Inventory
            .Where(item => !item.IsAir && item.pick > 0)
            .OrderByDescending(item => item.pick)
            .FirstOrDefault();
    }

    public bool HasPickaxe => GetBestPickaxe() != null;
    public bool HasAxe => Inventory.Any(item => !item.IsAir && item.axe > 0);
    public bool HasCombatWeapon => Inventory.Any(IsCombatWeapon);

    public bool EnsureStarterTools()
    {
        bool changed = false;

        if (!HasPickaxe)
            changed |= AddItem(ItemID.CopperPickaxe, 1);
        if (!HasAxe)
            changed |= AddItem(ItemID.CopperAxe, 1);
        if (!HasCombatWeapon)
            changed |= AddItem(ItemID.CopperShortsword, 1);

        return changed;
    }

    public Item? FindAmmo(int ammoType)
    {
        if (ammoType <= 0)
            return null;

        return Inventory.FirstOrDefault(item =>
            !item.IsAir && item.stack > 0 && item.ammo == ammoType && item.ammo > 0);
    }

    public string EquipmentSummary()
    {
        string[] parts =
        {
            $"头:{(Armor[0].IsAir ? "空" : Lang.GetItemNameValue(Armor[0].type))}",
            $"身:{(Armor[1].IsAir ? "空" : Lang.GetItemNameValue(Armor[1].type))}",
            $"腿:{(Armor[2].IsAir ? "空" : Lang.GetItemNameValue(Armor[2].type))}",
            $"饰1:{(Accessories[0].IsAir ? "空" : Lang.GetItemNameValue(Accessories[0].type))}",
            $"饰2:{(Accessories[1].IsAir ? "空" : Lang.GetItemNameValue(Accessories[1].type))}",
            $"饰3:{(Accessories[2].IsAir ? "空" : Lang.GetItemNameValue(Accessories[2].type))}"
        };
        return string.Join("、", parts);
    }

    public bool HasWeapon => HasCombatWeapon;

    public int FindInventoryIndex(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return -1;

        if (int.TryParse(query.Trim(), out int itemType))
        {
            for (int i = 0; i < Inventory.Length; i++)
            {
                if (!Inventory[i].IsAir && Inventory[i].type == itemType)
                    return i;
            }
        }

        string name = query.Trim();
        for (int i = 0; i < Inventory.Length; i++)
        {
            Item item = Inventory[i];
            if (item.IsAir)
                continue;

            string localized = Lang.GetItemNameValue(item.type);
            if (localized.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    public int CountItem(int itemType)
    {
        return Inventory.Where(item => !item.IsAir && item.type == itemType).Sum(item => item.stack);
    }

    public bool RemoveItem(int itemType, int count)
    {
        if (CountItem(itemType) < count)
            return false;

        int remaining = count;
        for (int i = 0; i < Inventory.Length && remaining > 0; i++)
        {
            Item item = Inventory[i];
            if (item.IsAir || item.type != itemType)
                continue;

            int take = Math.Min(item.stack, remaining);
            item.stack -= take;
            remaining -= take;
            if (item.stack <= 0)
                item.SetDefaults(ItemID.None);
        }

        return true;
    }

    public bool EquipArmorFromInventory(int inventoryIndex, int armorSlot)
    {
        if (inventoryIndex < 0 || inventoryIndex >= Inventory.Length || armorSlot < 0 || armorSlot >= Armor.Length)
            return false;

        Item item = Inventory[inventoryIndex];
        if (item.IsAir || !CanEquipArmor(item, armorSlot))
            return false;

        Item previous = Armor[armorSlot];
        Armor[armorSlot] = item;
        Inventory[inventoryIndex] = previous;
        return true;
    }

    public bool EquipAccessoryFromInventory(int inventoryIndex, int accessorySlot)
    {
        if (inventoryIndex < 0 || inventoryIndex >= Inventory.Length || accessorySlot < 0 || accessorySlot >= Accessories.Length)
            return false;

        Item item = Inventory[inventoryIndex];
        if (item.IsAir || !CanEquipAccessory(item))
            return false;

        Item previous = Accessories[accessorySlot];
        Accessories[accessorySlot] = item;
        Inventory[inventoryIndex] = previous;
        return true;
    }

    public bool AddItem(int itemType, int stack) => AddItemStack(itemType, stack) == stack;

    public int AddItemStack(int itemType, int stack)
    {
        int remaining = stack;
        int added = 0;
        Item template = new();
        template.SetDefaults(itemType);

        foreach (Item item in Inventory)
        {
            if (remaining <= 0)
                break;

            if (item.IsAir || item.type != itemType || item.stack >= item.maxStack)
                continue;

            int add = Math.Min(remaining, item.maxStack - item.stack);
            item.stack += add;
            remaining -= add;
            added += add;
        }

        foreach (Item item in Inventory)
        {
            if (remaining <= 0)
                break;

            if (!item.IsAir)
                continue;

            item.SetDefaults(itemType);
            item.stack = Math.Min(remaining, item.maxStack);
            remaining -= item.stack;
            added += item.stack;
        }

        if (added > 0 && itemType is ItemID.CopperCoin or ItemID.SilverCoin or ItemID.GoldCoin)
            ConsolidateCoins();

        return added;
    }

    private void ConsolidateCoins()
    {
        ReadOnlySpan<(int From, int To)> tiers = stackalloc (int, int)[]
        {
            (ItemID.CopperCoin, ItemID.SilverCoin),
            (ItemID.SilverCoin, ItemID.GoldCoin),
            (ItemID.GoldCoin, ItemID.PlatinumCoin)
        };

        foreach ((int fromType, int toType) in tiers)
        {
            int total = CountItem(fromType);
            if (total < 100)
                continue;

            int upgradeCount = total / 100;
            if (CountFreeCapacity(toType) < upgradeCount)
                continue;

            RemoveItem(fromType, upgradeCount * 100);
            AddItemStack(toType, upgradeCount);
        }
    }

    public int CountFreeCapacity(int itemType)
    {
        Item template = new();
        template.SetDefaults(itemType);
        int capacity = 0;

        foreach (Item item in Inventory)
        {
            if (item.IsAir)
                capacity += template.maxStack;
            else if (item.type == itemType && item.stack < item.maxStack)
                capacity += item.maxStack - item.stack;
        }

        return capacity;
    }

    public string Summary()
    {
        string[] parts = Inventory
            .Where(item => !item.IsAir)
            .Select(item => $"{Lang.GetItemNameValue(item.type)}x{item.stack}")
            .ToArray();

        return parts.Length == 0 ? "空" : string.Join("、", parts);
    }

    public void GiveAll(Player player)
    {
        foreach (Item item in Inventory)
        {
            if (item.IsAir)
                continue;

            player.QuickSpawnItem(player.GetSource_FromThis(), item.type, item.stack);
            item.SetDefaults(ItemID.None);
        }
    }

    public void Save(TagCompound tag)
    {
        tag["inventory"] = Inventory.Select(ItemIO.Save).ToList();
        tag["armor"] = Armor.Select(ItemIO.Save).ToList();
        tag["accessories"] = Accessories.Select(ItemIO.Save).ToList();
        tag["preferredWeapon"] = PreferredWeaponType;
        tag["mana"] = Mana;
    }

    public void Reset()
    {
        foreach (Item item in Inventory.Concat(Armor).Concat(Accessories))
            item.TurnToAir();
        PreferredWeaponType = ItemID.None;
        Mana = ManaMax;
    }

    public void Load(TagCompound tag)
    {
        LoadItems(tag, "inventory", Inventory);
        LoadItems(tag, "armor", Armor);
        LoadItems(tag, "accessories", Accessories);
        PreferredWeaponType = tag.ContainsKey("preferredWeapon") ? tag.GetInt("preferredWeapon") : ItemID.None;
        Mana = tag.ContainsKey("mana") ? Math.Clamp(tag.GetInt("mana"), 0, ManaMax) : ManaMax;
    }

    private static void LoadItems(TagCompound tag, string key, Item[] items)
    {
        foreach (Item item in items)
            item.TurnToAir();
        IList<TagCompound> list = tag.GetList<TagCompound>(key);
        for (int i = 0; i < items.Length && i < list.Count; i++)
            items[i] = ItemIO.Load(list[i]);
    }

    public static bool CanEquipArmor(Item item, int slot) => slot switch
    {
        0 => item.headSlot >= 0,
        1 => item.bodySlot >= 0,
        2 => item.legSlot >= 0,
        _ => false
    };

    public static bool CanEquipAccessory(Item item) => item.accessory;

    private static Item[] CreateItems(int count)
    {
        var items = new Item[count];
        for (int i = 0; i < count; i++)
            items[i] = new Item();
        return items;
    }
}
