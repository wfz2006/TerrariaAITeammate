using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

// All entry points run on the game thread. The planner receives serialized snapshots only.
public static class AgentKnowledge
{
    public static object Snapshot(Player player, NPC? companion, CompanionLoadout loadout) => new
    {
        queries = new[] { "world", "sources", "observation", "recipe", "build", "item", "stations", "nearby", "boss", "chest", "structure", "house", "structure_detail", "site", "terrain", "route", "mine", "design" },
        worldKnowledge = WorldKnowledgeObserver.Status(),
        sourceInspect = PlanningStaticKnowledge.Get("sourceInspect"),
        observationRecall = PlanningStaticKnowledge.Get("observationRecall"),
        worldInspect = PlanningStaticKnowledge.Get("worldInspect"),
        bosses = AITeammateSystem.Instance.BossWorldState,
        inspectUsage = PlanningStaticKnowledge.Get("inspectUsage"),
        inventory = loadout.Inventory.Where(item => !item.IsAir).Select(ItemSummary).ToArray(),
        playerInventory = player.inventory.Where(item => !item.IsAir).Select(ItemSummary).ToArray(),
        equipment = loadout.Armor.Concat(loadout.Accessories).Where(item => !item.IsAir).Select(ItemSummary).ToArray(),
        craftingInventory = PlanningStaticKnowledge.Get("craftingInventory"),
        craftingSelection = PlanningStaticKnowledge.Get("craftingSelection"),
        conditionContext = PlanningStaticKnowledge.Get("conditionContext"),
        movementLimits = NavigationRouteObservation.MovementLimits(),
        navigationInspect = PlanningStaticKnowledge.Get("navigationInspect"),
        miningSelection = PlanningStaticKnowledge.Get("miningSelection"),
        miningInspect = PlanningStaticKnowledge.Get("miningInspect"),
        designInspect = PlanningStaticKnowledge.Get("designInspect"),
        stations = companion == null ? Array.Empty<object>() : StationSnapshot(companion),
        constructionInProgress = AITeammateSystem.Instance.Executor.ConstructionProgress,
        selectedStructure = StructureWorldObserver.RefreshLatest(companion),
        knownConstruction = new { name = "shelter", source = "predefined_template", observedFromWorld = false,
            width = CompanionConstruction.Width, height = CompanionConstruction.Height,
            materials = ShelterOptions(player, loadout), note = "默认10x7模板左右各有三格高门洞，可从两侧出入；实际外部通路仍取决于场地。不能用作原屋尺寸或样式证据。" },
        note = PlanningStaticKnowledge.Get("note")
    };

    public static string Query(string query, string target, NPC npc, Player player, AIExecutor executor,
        int? x = null, int? y = null, JsonElement? design = null)
    {
        string topic = (target ?? "").Trim().ToLowerInvariant();
        query = (query ?? "").Trim();
        int count = 1;
        Match quantity = Regex.Match(query, @"^(.*?)\s+[x×]\s*(\d+)$", RegexOptions.IgnoreCase);
        if (quantity.Success && int.TryParse(quantity.Groups[2].Value, out int parsed))
        {
            query = quantity.Groups[1].Value.Trim();
            count = Math.Clamp(parsed, 1, 999);
        }

        CompanionLoadout loadout = AITeammateSystem.Instance.Loadout;
        object result = topic switch
        {
            "world" => WorldKnowledgeObserver.Query(query, npc, x, y),
            "sources" => executor.InspectMiningSources(npc, query, x, y),
            "observation" => AITeammateSystem.Instance.ReadWorldObservation(query),
            "boss" => BossKnowledge.Query(query, npc, player),
            "chest" => ChestQuery(npc, x, y),
            "structure" or "house" => StructureWorldObserver.Query(query, npc, x, y),
            "structure_detail" => StructureWorldObserver.QueryDetail(query, npc, x, y),
            "site" => ConstructionSiteWorldObserver.Query(query, npc, x, y),
            "terrain" => NavigationWorldObserver.Terrain(query, npc, x, y),
            "route" => NavigationWorldObserver.Route(query, npc, player, x, y),
            "mine" => x.HasValue && y.HasValue
                ? executor.InspectMiningInteraction(npc, query, x.Value, y.Value)
                : !x.HasValue && !y.HasValue ? executor.InspectMiningOptions(npc, query) : InvalidMiningCoordinates(query),
            "design" => ConstructionDesignWorldObserver.Query(design, npc, player, loadout),
            "build" or "blueprint" => BuildQuery(query, npc, player, loadout, executor, x, y),
            "stations" => new { topic = "stations", stations = StationSnapshot(npc, 40, 25) },
            "nearby" or "resources" => AIPerception.Build(player, npc),
            "item" or "inventory" => ItemQuery(query, player, loadout),
            _ => RecipeQuery(query, count, npc, player, loadout)
        };
        return JsonSerializer.Serialize(result);
    }

    private static object InvalidMiningCoordinates(string resource) => new
    {
        topic = "mine", source = "live_world", available = false, callSucceeded = true, inputValid = false,
        resource,
        issues = new[] { new { code = "incomplete_seed", severity = "error",
            message = "指定种子时x/y必须成对；不填两者才是自动候选查询，不能用缺一项坐标自动换点。" } },
        miningExecuted = false, worldModified = false, inventoryModified = false, actorsMoved = false
    };

    private static object ChestQuery(NPC npc, int? x = null, int? y = null)
    {
        bool explicitTarget = x.HasValue || y.HasValue;
        int index = explicitTarget
            ? CompanionChests.FindTargetChest(npc, false, "", x, y, range: 5 * 16f)
            : CompanionChests.FindNearestChest(npc, 5 * 16f);
        Chest? chest = CompanionChests.GetChest(index);
        if (chest == null) return new { found = false, error = explicitTarget
            ? "指定x/y没有命中5格内真实箱子的2×2足迹；没有改读其它箱子。先靠近选定箱子或核对坐标。"
            : "先靠近真实箱子5格内，不能查看未接近箱子的内容。" };
        bool locked = Chest.IsLocked(chest.x, chest.y);
        var nearbyChests = CompanionChests.FindNearbyChests(npc, 24f * 16f, 5)
            .Select(entry => new
            {
                index = entry.Index,
                x = entry.Chest.x,
                y = entry.Chest.y,
                distance = (int)Math.Round(entry.DistanceTiles),
                locked = Chest.IsLocked(entry.Chest.x, entry.Chest.y),
                items = Chest.IsLocked(entry.Chest.x, entry.Chest.y)
                    ? Array.Empty<object>()
                    : entry.Chest.item.Where(item => !item.IsAir).Select(ItemSummary).ToArray()
            }).ToArray();
        return new { found = true, index, x = chest.x, y = chest.y, locked,
            items = locked ? Array.Empty<object>() : chest.item.Where(item => !item.IsAir).Select(ItemSummary).ToArray(),
            nearbyChests,
            rule = "箱内取物必须使用 type=\"withdraw\"（pickup只捡地面物品）。带x/y严格选择命中其2×2足迹的箱子，缺失/超选择范围/目标不符不改选别箱；不带坐标才按名称或ID自动选箱。item=\"all\",count=0取可装下的全部补给；锁箱当前不能解锁。" };
    }

    private static object ItemSummary(Item item) => new
    {
        id = item.type, name = Lang.GetItemNameValue(item.type), count = item.stack,
        pick = item.pick, axe = item.axe, damage = item.damage, defense = item.defense, useAmmo = item.useAmmo,
        placeTile = item.createTile, placeWall = item.createWall,
        ammo = item.ammo, heal = item.healLife,
        equipSlot = item.headSlot >= 0 ? "head" : item.bodySlot >= 0 ? "body" : item.legSlot >= 0 ? "legs" : item.accessory ? "accessory" : CompanionLoadout.IsCombatWeapon(item) ? "weapon" : ""
    };

    internal static object RecipeQuery(string query, int count, NPC npc, Player player, CompanionLoadout loadout,
        bool includeSourceRecipes = true)
    {
        Recipe[] candidates = CompanionCrafting.FindRecipes(query).ToArray();
        Recipe? preferred = CompanionCrafting.FindRecipe(query, loadout, npc, out string error);
        return new
        {
            topic = "recipe", query, requestedOutput = count,
            found = candidates.Length > 0, error,
            preferredRecipe = preferred?.RecipeIndex,
            maxCraftsPerAction = CompanionCrafting.MaxCrafts,
            conditionContext = "local_player",
            recipes = candidates.OrderBy(recipe => recipe == preferred ? 0 : 1).Take(4)
                .Select(recipe => DescribeRecipe(recipe, count, npc, player, loadout, includeSourceRecipes)).ToArray(),
            candidateCount = candidates.Length,
            note = "requiredTile全部需要；ownedPlayer不能用于craft。选择某条配方时把其recipeId传入craft.recipe_id，不填则自动选择；显式ID失败不会换配方。先合成/放置工作站，再制作需要它的物品。"
        };
    }

    private static object DescribeRecipe(Recipe recipe, int count, NPC npc, Player player, CompanionLoadout loadout,
        bool includeSourceRecipes = true)
    {
        int perCraft = Math.Max(1, recipe.createItem.stack);
        int crafts = Math.Clamp((int)Math.Ceiling(count / (double)perCraft), 1, CompanionCrafting.MaxCrafts);
        IReadOnlyList<MaterialRequirement> requirements = CompanionCrafting.Requirements(recipe, crafts);
        Dictionary<int, int> stock = CompanionCrafting.InventoryCounts(loadout);
        var allStations = CompanionCrafting.NearbyStations(npc, 40, 25);
        bool materialsAvailable = MaterialAllocator.TryAllocate(requirements, stock, out _);
        return new
        {
            recipeId = recipe.RecipeIndex,
            output = new { id = recipe.createItem.type, name = Lang.GetItemNameValue(recipe.createItem.type), perCraft },
            crafts, actualOutput = crafts * perCraft,
            canCraftNow = materialsAvailable && CompanionCrafting.HasStationNear(npc, recipe) && CompanionCrafting.ConditionsMet(recipe),
            materialsAvailable,
            ingredients = requirements.Select(requirement => new
            {
                id = requirement.Id,
                groupId = requirement.Id >= CompanionCrafting.RecipeGroupOffset
                    ? requirement.Id - CompanionCrafting.RecipeGroupOffset : -1,
                name = CompanionCrafting.RequirementName(requirement.Id), required = requirement.Count,
                ownedSelf = requirement.AcceptedItems.Sum(type => stock.GetValueOrDefault(type)),
                ownedPlayer = player.inventory.Where(item => !item.IsAir && requirement.AcceptedItems.Contains(item.type)).Sum(item => item.stack),
                missing = Math.Max(0, requirement.Count - requirement.AcceptedItems.Sum(type => stock.GetValueOrDefault(type))),
                acceptedItemCount = requirement.AcceptedItems.Length,
                acceptedItems = requirement.AcceptedItems.Take(12).Select(type => new { id = type, name = Lang.GetItemNameValue(type) }).ToArray(),
                sourceRecipes = requirement.AcceptedItems.Where(_ => includeSourceRecipes).Take(3).SelectMany(type =>
                    CompanionCrafting.FindRecipes(type.ToString()).Take(2)).Take(3).Select(source => new
                    {
                        recipeId = source.RecipeIndex,
                        output = Lang.GetItemNameValue(source.createItem.type), count = source.createItem.stack,
                        ingredients = CompanionCrafting.Requirements(source).Select(row => new
                        {
                            id = row.Id, name = CompanionCrafting.RequirementName(row.Id), count = row.Count
                        }).ToArray(),
                        stations = source.requiredTile.Select(TileName).ToArray()
                    }).ToArray()
            }).ToArray(),
            stations = recipe.requiredTile.Select(type => new
            {
                tileId = type, name = TileName(type),
                nearSelf = CompanionCrafting.NearbyStations(npc).Any(station => CompanionCrafting.StationMatches(station.Type, type)),
                observed = allStations.Where(station => CompanionCrafting.StationMatches(station.Type, type))
                    .Select(station => new { x = station.X, y = station.Y }).Take(3).ToArray(),
                placeableItems = ContentSamples.ItemsByType.Values.Where(item => item.createTile == type)
                    .Take(3).Select(item => new { id = item.type, name = Lang.GetItemNameValue(item.type) }).ToArray()
            }).ToArray(),
            conditionsMet = CompanionCrafting.ConditionsMet(recipe),
            conditions = recipe.Conditions.Select(condition => condition.Description.Value).ToArray()
        };
    }

    private static object BuildQuery(string query, NPC observer, Player player, CompanionLoadout loadout, AIExecutor executor, int? x, int? y)
    {
        if (string.IsNullOrWhiteSpace(query) || query is "shelter" or "wood" or "stone" or "ebonwood" or "庇护所" or "小屋")
            return executor.InspectShelterBuild(observer, query, x, y);
        Blueprint? blueprint = BlueprintManager.Load(query);
        return new
        {
            topic = "build", name = query, found = blueprint != null,
            materials = blueprint == null ? Array.Empty<object>() : DescribeCosts(BlueprintManager.RequiredItems(blueprint), player, loadout)
        };
    }

    private static object[] ShelterOptions(Player player, CompanionLoadout loadout) => new[] { "wood", "stone", "ebonwood" }
        .Select(material => (object)new
        {
            material,
            items = DescribeCosts(CompanionConstruction.ShelterMaterials(material), player, loadout)
        }).ToArray();

    private static object[] DescribeCosts(IReadOnlyDictionary<int, int> materials, Player player, CompanionLoadout loadout) =>
        materials.Select(pair => (object)new
        {
            id = pair.Key, name = Lang.GetItemNameValue(pair.Key), required = pair.Value,
            ownedSelf = loadout.CountItem(pair.Key),
            ownedPlayer = player.inventory.Where(item => !item.IsAir && item.type == pair.Key).Sum(item => item.stack),
            missing = Math.Max(0, pair.Value - loadout.CountItem(pair.Key))
        }).ToArray();

    private static object ItemQuery(string query, Player player, CompanionLoadout loadout) => new
    {
        topic = "item", query,
        items = ContentSamples.ItemsByType.Values.Where(item => item.type > ItemID.None && CompanionCrafting.MatchesItem(item, query))
            .OrderBy(item => Lang.GetItemNameValue(item.type).Length).Take(8).Select(item => new
            {
                details = ItemSummary(item), ownedSelf = loadout.CountItem(item.type),
                ownedPlayer = player.inventory.Where(held => !held.IsAir && held.type == item.type).Sum(held => held.stack)
            }).ToArray()
    };

    private static object[] StationSnapshot(NPC npc, int radiusX = 5, int radiusY = 4)
    {
        var needed = Main.recipe.Take(Math.Min(Recipe.numRecipes, Main.recipe.Length))
            .Where(recipe => recipe != null && !recipe.Disabled).SelectMany(recipe => recipe.requiredTile).ToHashSet();
        return CompanionCrafting.NearbyStations(npc, radiusX, radiusY)
            .Where(station => needed.Any(required => CompanionCrafting.StationMatches(station.Type, required)))
            .OrderBy(station => Math.Abs(station.X - npc.Center.X / 16f) + Math.Abs(station.Y - npc.Center.Y / 16f))
            .Take(16).Select(station => (object)new
            {
                tileId = station.Type, name = TileName(station.Type), x = station.X, y = station.Y,
                satisfies = needed.Where(required => CompanionCrafting.StationMatches(station.Type, required)).Take(8).ToArray()
            }).ToArray();
    }

    private static string TileName(int type) => TileID.Search.GetName(type) ?? type.ToString();
}
