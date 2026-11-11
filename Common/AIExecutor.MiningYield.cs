using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AIExecutor
{
    private MiningYieldLedger? miningWoodYield;
    private string miningWoodFamilyDefinition = "not_captured";

    private static HashSet<int> WoodItemTypes(out string definition)
    {
        if (RecipeGroup.recipeGroups.TryGetValue(RecipeGroupID.Wood, out RecipeGroup? group) && group.ValidItems.Count > 0)
        {
            definition = "native_RecipeGroupID.Wood_members_at_action_start";
            return group.ValidItems.Where(id => id > 0 && id != ItemID.Acorn && id != ItemID.Gel).ToHashSet();
        }
        definition = "explicit_native_natural_wood_fallback_recipe_group_unavailable";
        return new() { ItemID.Wood, ItemID.Ebonwood, ItemID.RichMahogany, ItemID.Pearlwood,
            ItemID.Shadewood, ItemID.BorealWood, ItemID.PalmWood, ItemID.AshWood };
    }

    private static Dictionary<int, int> MiningInventoryCounts() => Loadout.Inventory.Where(i => !i.IsAir)
        .GroupBy(i => i.type).ToDictionary(g => g.Key, g => g.Sum(i => i.stack));

    private void BeginWoodYield()
    {
        HashSet<int> family = WoodItemTypes(out miningWoodFamilyDefinition);
        miningWoodYield = new(family, MiningInventoryCounts());
    }

    private int CurrentMiningGained() => target == "wood"
        ? miningWoodYield?.TotalGained(MiningInventoryCounts()) ?? 0
        : Math.Max(0, Loadout.CountItem(ItemForTile(mineTileType)) - mineAtTaskStart);

    private static (int Minimum, int Maximum) WoodCapacity(IEnumerable<int> family)
    {
        int[] capacities = family.Select(Loadout.CountFreeCapacity).ToArray();
        return capacities.Length == 0 ? (0, 0) : (capacities.Min(), capacities.Max());
    }

    private static int AvailableWoodCapacity() => WoodCapacity(WoodItemTypes(out _)).Maximum;
    private int CurrentMiningCapacity() => target == "wood"
        ? WoodCapacity(miningWoodYield?.AcceptedItemTypes ?? WoodItemTypes(out _)).Maximum
        : Loadout.CountFreeCapacity(ItemForTile(mineTileType));

    private static object DescribeMiningProduct(string resource, int expectedType)
    {
        if (resource != "wood")
        {
            int id = ItemForTile(expectedType);
            return new { itemId = (int?)id, identityKnown = true, name = id > 0 ? Lang.GetItemNameValue(id) : "",
                inventoryCapacity = (int?)Loadout.CountFreeCapacity(id) };
        }
        HashSet<int> family = WoodItemTypes(out string definition); var capacity = WoodCapacity(family);
        return new
        {
            itemId = (int?)null, identityScope = "wood_family_inventory_capacity_not_seed_product",
            seedIdentitySource = "sibling_treeProduct_if_present_otherwise_not_tested",
            familyDefinition = definition, inventoryCapacity = (int?)null,
            minimumCapacityAcrossFamily = capacity.Minimum, maximumCapacityAcrossFamily = capacity.Maximum,
            everyFamilyMemberHasCapacity = capacity.Minimum > 0,
            familyItems = family.OrderBy(id => id).Select(id => new { itemId = id, name = Lang.GetItemNameValue(id) }).ToArray(),
            note = "This object reports wood-family inventory capacity only, not the seed's product identity. Read sibling treeProduct when present for independently observed native root-soil identity; unknown there never defaults to Wood9. Capacity bounds do not predict quantity or guarantee a particular source fits; actual pickup determines yield. Family membership does not guarantee interchangeability in any particular recipe."
        };
    }
}
