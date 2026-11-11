using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

// Isolated acceptance only. The operator selects each step from observed facts.
public partial class AITeammateSystem
{
    private const int ClosurePlatformWoodPhase = 7, ClosurePlatformCraftPhase = 8;
    private const int ClosurePlatformPlacePhase = 9, ClosurePlatformStandPhase = 10;
    private const int ClosurePlatformLimit = 4;
    private bool closurePlatformMode, closurePlatformWoodTried, closurePlatformCraftTried;
    private int closurePlatformPlayer, closurePlatformWorld, closurePlatformEarnedWood, closurePlatformCrafted;
    private int closurePlatformMinedStone, closurePlatformSpawnStart, closurePlatformCraftCount, closurePlatformWoodBefore;
    private Point closurePlatformTarget;
    private float closurePlatformStandStartFeet;
    private Vector2 closurePlatformReturnStart;
    private ulong closurePlatformTerrainTick;
    private readonly HashSet<Point> closurePlatformObservedAir = new();
    private readonly HashSet<Point> closurePlatformPlaced = new();
    private readonly HashSet<Point> closurePlatformStandAttempts = new();
    private readonly HashSet<Point> closurePlatformContacts = new();

    private static bool ClosurePlatformConfigSafe()
    {
        AIConfig config = ModContent.GetInstance<AIConfig>();
        return HouseMode && !config.UseLlm && !config.AllowTeleportRecovery;
    }

    private bool BeginClosurePlatform(Player player)
    {
        string directive = ClosureFile("platform.txt");
        if (File.Exists(directive)) File.Delete(directive);
        closureAutomaticPages = 1;
        closurePlatformPlayer = player.whoAmI;
        closurePlatformWorld = Main.worldID;
        var inventory = ShelterCounts(Loadout.Inventory);
        bool zeroMaterials = inventory.GetValueOrDefault(ItemID.Wood) == 0 &&
            inventory.GetValueOrDefault(ItemID.WoodPlatform) == 0 && inventory.GetValueOrDefault(ItemID.StoneBlock) == 0;
        bool looseMaterial = Main.item.Any(item => item.active && item.type is ItemID.Wood or ItemID.WoodPlatform);
        closureEvents.Add(new { kind = "platform_fixture_setup", inventory, zeroMaterials, looseMaterial,
            playerId = closurePlatformPlayer, worldId = closurePlatformWorld, platformLimit = ClosurePlatformLimit,
            scope = "one setup, native Starter tools, no supplied resources, no autonomous-plan claim" });
        if (zeroMaterials && !looseMaterial && ClosurePlatformPreserved()) return true;
        FinishAutonomousClosureSources("uncovered", "platform baseline, zero materials or fresh loose-drop precondition failed");
        return false;
    }

    private void ObserveClosurePlatformAir(int x, int y, int width, int height, ulong tick)
    {
        closurePlatformTerrainTick = tick;
        for (int tx = x; tx < x + width; tx++)
            for (int ty = y; ty < y + height; ty++)
                if (ClosurePlatformDryAir(new Point(tx, ty))) closurePlatformObservedAir.Add(new Point(tx, ty));
    }

    private static bool ClosurePlatformDryAir(Point cell)
    {
        Tile tile = Main.tile[cell.X, cell.Y];
        return !tile.HasTile && !tile.IsActuated && tile.LiquidAmount == 0;
    }

    private bool ClosurePlatformPreserved()
        => closureAudit?.CheckPreservation() is { preserved: true, coverageComplete: true };

    private int ClosurePlatformStock => Loadout.CountItem(ItemID.WoodPlatform);
    private bool ClosurePlatformLedgerMatches => ClosurePlatformStock == closurePlatformCrafted - closurePlatformPlaced.Count;

    private void ReviewClosurePlatform(string detail)
    {
        closurePhase = 2;
        Executor.SetHold(true);
        SaveAutonomousClosureSources("awaiting_visual_review", detail);
        File.WriteAllText(ClosureFile("ready.txt"), "awaiting_visual_review");
    }

    private void RejectClosurePlatform(string directive, string reason)
    {
        closureEvents.Add(new { kind = "platform_directive_rejected", directive, reason });
        ReviewClosurePlatform(reason);
    }

    private void ApplyClosurePlatform(AIPlan plan, int phase)
    {
        closurePhase = phase;
        File.WriteAllText(ClosureFile("ready.txt"), "running");
        Executor.Apply(plan);
    }

    private void DispatchClosurePlatformWood(int x, int y, int count, JsonElement fresh)
    {
        bool ordinaryTree = fresh.GetProperty("seed").GetProperty("actualTileType").GetInt32() == TileID.Trees &&
            fresh.TryGetProperty("treeProduct", out var tree) && tree.ValueKind == JsonValueKind.Object &&
            tree.TryGetProperty("itemId", out var product) && product.ValueKind == JsonValueKind.Number &&
            product.GetInt32() == ItemID.Wood;
        if (!ordinaryTree || Loadout.CountItem(ItemID.Wood) != 0 ||
            Main.item.Any(item => item.active && item.type == ItemID.Wood) || !ClosurePlatformPreserved())
        {
            RejectClosurePlatform("wood", "requires fresh ordinary Wood tree, no prior Wood/loose Wood, and original preservation");
            return;
        }
        closurePlatformWoodTried = true;
        closureWorkCount++;
        closurePlatformSpawnStart = LongGoalDropWitness.Spawns.Count;
        closureEvents.Add(new { kind = "platform_wood_dispatch", x, y, count, fresh,
            inventory = ShelterCounts(Loadout.Inventory) });
        ApplyClosurePlatform(new AIPlan { Action = AIActionType.Mine, Target = "wood", X = x, Y = y, Count = count },
            ClosurePlatformWoodPhase);
    }

    private static bool ClosurePlatformRecipe(Recipe recipe)
    {
        if (recipe.createItem.type != ItemID.WoodPlatform || recipe.createItem.stack != 2) return false;
        var ingredients = CompanionCrafting.Requirements(recipe);
        return ingredients.Count == 1 && ingredients[0].Count == 1 && ingredients[0].AcceptedItems.Contains(ItemID.Wood);
    }

    private void DispatchClosurePlatformCraft(NPC npc, string argument)
    {
        int wood = Loadout.CountItem(ItemID.Wood);
        if (!int.TryParse(argument, out int count) || count is not (2 or 4) || closurePlatformCraftTried ||
            closurePlatformEarnedWood < count / 2 || wood != closurePlatformEarnedWood || ClosurePlatformStock != 0 ||
            !ClosurePlatformPreserved())
        {
            RejectClosurePlatform("craft", "craft 2 or craft 4 once, using only attributable ordinary Wood");
            return;
        }
        string item = ItemID.WoodPlatform.ToString();
        Recipe? recipe = CompanionCrafting.FindRecipes(item).FirstOrDefault(candidate =>
            ClosurePlatformRecipe(candidate) && CompanionCrafting.ConditionsMet(candidate) &&
            CompanionCrafting.HasStationNear(npc, candidate));
        if (recipe == null)
        {
            RejectClosurePlatform("craft", "no native two-platform recipe with a one-Wood ingredient is available here");
            return;
        }
        closurePlatformCraftTried = true;
        closurePlatformCraftCount = count;
        closurePlatformWoodBefore = wood;
        closureEvents.Add(new { kind = "platform_craft_dispatch", itemId = ItemID.WoodPlatform, count, wood,
            recipeId = recipe.RecipeIndex, inventory = ShelterCounts(Loadout.Inventory) });
        ApplyClosurePlatform(new AIPlan { Action = AIActionType.Craft, Item = item, Count = count, RecipeId = recipe.RecipeIndex },
            ClosurePlatformCraftPhase);
    }

    private static bool TryReadClosurePlatformPoint(string argument, out Point cell)
    {
        cell = default;
        string[] pair = argument.Split(',');
        if (pair.Length != 2 || !int.TryParse(pair[0], out int x) || !int.TryParse(pair[1], out int y) ||
            !WorldGen.InWorld(x, y, 10)) return false;
        cell = new Point(x, y);
        return true;
    }

    private void DispatchClosurePlatformPlace(NPC npc, Point cell)
    {
        var protection = closureAudit?.ReadConstructionProtection(cell.X, cell.Y, 1, 1);
        var footprint = new Rectangle(cell.X * 16, cell.Y * 16, 16, 16);
        float distance = Vector2.Distance(npc.Center, new Vector2(footprint.Center.X, footprint.Center.Y));
        bool actorOverlap = Main.npc.Any(actor => actor.active && actor.Hitbox.Intersects(footprint)) ||
            Main.player.Any(actor => actor.active && !actor.dead && actor.Hitbox.Intersects(footprint));
        bool observedDryAir = closurePlatformObservedAir.Contains(cell);
        bool currentDryAir = ClosurePlatformDryAir(cell);
        closureEvents.Add(new { kind = "platform_place_preflight", x = cell.X, y = cell.Y,
            tick = Main.GameUpdateCount, terrainTick = closurePlatformTerrainTick, observedDryAir, currentDryAir,
            distancePixels = distance, actorOverlap, protection, stock = ClosurePlatformStock });
        if (closurePlatformPlaced.Count >= ClosurePlatformLimit || !ClosurePlatformLedgerMatches || ClosurePlatformStock <= 0 ||
            distance > 112f || actorOverlap || !observedDryAir || !currentDryAir ||
            protection is not { Available: true, Covered: true, Allowed: true } || !ClosurePlatformPreserved())
        {
            RejectClosurePlatform("place", "requires owned crafted platform, seven-tile reach, observed and current dry air, and a clear immutable protection check");
            return;
        }
        closurePlatformTarget = cell;
        closurePlatformObservedAir.Remove(cell);
        ApplyClosurePlatform(new AIPlan { Action = AIActionType.Place, Item = ItemID.WoodPlatform.ToString(), Count = 1,
            X = cell.X, Y = cell.Y }, ClosurePlatformPlacePhase);
    }

    private static bool IsClosurePlacedPlatform(Point cell)
    {
        Tile tile = Main.tile[cell.X, cell.Y];
        return tile.HasTile && !tile.IsActuated && tile.TileType == TileID.Platforms &&
            TileID.Sets.Platforms[tile.TileType] && Main.tileSolidTop[tile.TileType];
    }

    private IEnumerable<Point> ClosurePlatformUnderFeet(float x, float bottom, int width)
    {
        int left = CompanionMovement.FootprintLeft(x, width), right = CompanionMovement.FootprintRight(x, width);
        return closurePlatformPlaced.Where(cell => cell.X >= left && cell.X <= right &&
            Math.Abs(cell.Y * 16f - bottom) <= 2.5f && IsClosurePlacedPlatform(cell));
    }

    private void RecordClosurePlatformContact(NPC npc)
    {
        if (Math.Abs(npc.velocity.Y) > 0.6f ||
            !TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width)) return;
        foreach (Point support in ClosurePlatformUnderFeet(npc.Center.X, npc.Bottom.Y, npc.width))
            if (closurePlatformContacts.Add(support))
                closureEvents.Add(new { kind = "platform_physical_contact", x = support.X, y = support.Y,
                    tick = Main.GameUpdateCount, phase = closurePhase, center = new { x = npc.Center.X, y = npc.Center.Y },
                    feet = npc.Bottom.Y, vy = npc.velocity.Y, grounded = true });
    }

    private void DispatchClosurePlatformStand(NPC npc, Point requested)
    {
        // Match the public Explore coordinate resolution before the task is sent.
        Vector2 target = new(requested.X * 16f + 8, requested.Y * 16f + 8);
        if (!TilePathfinder.TryResolveStableStand(target, out Point resolved) || resolved != requested ||
            !TilePathfinder.TryGetStandPose(resolved, out TileStandPose pose) ||
            npc.Bottom.Y - pose.Bottom < 4f || !ClosurePlatformUnderFeet(pose.Center.X, pose.Bottom, npc.width).Any() ||
            !ClosurePlatformPreserved() || !closurePlatformStandAttempts.Add(requested))
        {
            RejectClosurePlatform("stand", "requires an untried higher resolved Explore stance on a platform placed by this fixture");
            return;
        }
        closurePlatformTarget = requested;
        closurePlatformStandStartFeet = npc.Bottom.Y;
        closureEvents.Add(new { kind = "platform_stand_dispatch", x = resolved.X, standRow = resolved.Y,
            center = new { x = pose.Center.X, y = pose.Center.Y }, targetFeet = pose.Bottom, startFeet = npc.Bottom.Y });
        ApplyClosurePlatform(new AIPlan { Action = AIActionType.Explore, X = requested.X, Y = requested.Y },
            ClosurePlatformStandPhase);
    }

    private bool DispatchClosurePlatformDirective(NPC npc, Player player)
    {
        string file = ClosureFile("platform.txt");
        if (!File.Exists(file)) return false;
        string[] parts = File.ReadAllText(file).Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        File.Delete(file);
        string command = parts.Length > 0 ? parts[0] : "";
        string argument = parts.Length == 2 ? parts[1] : "";
        if (command == "craft")
            DispatchClosurePlatformCraft(npc, argument);
        else if (closurePlatformMinedStone < 5 || Loadout.CountItem(ItemID.StoneBlock) != closurePlatformMinedStone)
            RejectClosurePlatform(command, "requires five attributable stone still carried by the companion");
        else if (command == "preview" && argument.Length == 0)
        {
            var before = ShelterCounts(Loadout.Inventory);
            Vector2 position = npc.Center;
            object? preview = Executor.InspectPlatformRecovery(npc, (x, y) => closureAudit?.ReadConstructionProtection(x, y, 1, 1));
            bool unchanged = npc.Center == position && before.OrderBy(item => item.Key)
                .SequenceEqual(ShelterCounts(Loadout.Inventory).OrderBy(item => item.Key)) && ClosurePlatformPreserved();
            closureEvents.Add(new { kind = "native_platform_recovery_preview", tick = Main.GameUpdateCount,
                position = new { x = position.X, y = position.Y }, preview, unchanged });
            ReviewClosurePlatform(preview != null && unchanged ? "native platform rise preview recorded" : "native platform rise preview unavailable");
        }
        else if (command == "place" && TryReadClosurePlatformPoint(argument, out Point cell))
            DispatchClosurePlatformPlace(npc, cell);
        else if (command == "stand" && TryReadClosurePlatformPoint(argument, out Point stand))
            DispatchClosurePlatformStand(npc, stand);
        else if (command == "come" && argument.Length == 0 && closurePlatformPlaced.Count > 0 &&
            ClosurePlatformLedgerMatches && ClosurePlatformPreserved() && player.active && !player.dead &&
            player.whoAmI == closurePlatformPlayer && Main.worldID == closurePlatformWorld &&
            (Math.Abs(npc.Center.X - player.Center.X) > 48f || Math.Abs(npc.Bottom.Y - player.Bottom.Y) > 24f))
        {
            closurePlatformReturnStart = npc.Center;
            closureEvents.Add(new { kind = "platform_return_dispatch", playerId = player.whoAmI,
                start = new { x = npc.Center.X, y = npc.Center.Y, feet = npc.Bottom.Y },
                player = new { x = player.Center.X, y = player.Center.Y, feet = player.Bottom.Y },
                inventory = ShelterCounts(Loadout.Inventory) });
            ApplyClosurePlatform(new AIPlan { Action = AIActionType.Come }, 4);
        }
        else
            RejectClosurePlatform(command, "expected craft 2|4, preview, place x,y, stand x,standRow, or one come with placed platforms and intact evidence");
        return true;
    }

    private bool ReadClosurePlatformHarvest(AgentOutcome outcome, int itemType, int minimum)
    {
        int stock = Loadout.CountItem(itemType);
        var drops = LongGoalDropWitness.Spawns.Skip(closurePlatformSpawnStart).Where(drop => drop.type == itemType).ToArray();
        var removed = new HashSet<(int x, int y)>();
        bool complete = false;
        int yielded = 0;
        try
        {
            using var doc = JsonDocument.Parse(outcome.Result);
            JsonElement result = doc.RootElement;
            complete = result.GetProperty("removedTilesTruncated").ValueKind == JsonValueKind.False;
            foreach (var tile in result.GetProperty("removedTiles").EnumerateArray())
                removed.Add((tile.GetProperty("x").GetInt32(), tile.GetProperty("y").GetInt32()));
            yielded = result.GetProperty("actualYields").EnumerateArray()
                .Where(item => item.GetProperty("itemType").GetInt32() == itemType)
                .Sum(item => item.GetProperty("amount").GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            complete = false;
        }
        closureRemovedTiles.AddRange(removed);
        int spawned = drops.Sum(drop => drop.stack);
        bool attributed = drops.Length > 0 && drops.All(drop => drop.stack > 0 && drop.action == "Mine" &&
            drop.sourceType == typeof(EntitySource_TileBreak).FullName && drop.tileX is int x && drop.tileY is int y && removed.Contains((x, y)));
        bool accepted = outcome.Status == "succeeded" && complete && attributed && removed.Count > 0 &&
            yielded == stock && stock >= minimum && spawned >= stock && ClosurePlatformPreserved();
        closureEvents.Add(new { kind = "platform_harvest_evidence", itemType, stock, minimum, yielded, spawned, complete,
            attributed, accepted, removed = removed.Select(tile => new { tile.x, tile.y }), drops });
        return accepted;
    }

    private bool CompleteClosurePlatformAction(AgentOutcome outcome, NPC npc, Player player)
    {
        switch (closurePhase)
        {
            case ClosurePlatformWoodPhase:
                if (!ReadClosurePlatformHarvest(outcome, ItemID.Wood, outcome.Task.Count))
                    FinishAutonomousClosureSources("failed_or_uncovered", "Wood pickup lacks matching native removal/drop evidence");
                else
                {
                    closurePlatformEarnedWood = Loadout.CountItem(ItemID.Wood);
                    ReviewClosurePlatform("ordinary Wood verified; platform.txt craft 2 or craft 4");
                }
                return true;
            case ClosurePlatformCraftPhase:
                int woodAfter = Loadout.CountItem(ItemID.Wood);
                bool crafted = outcome.Status == "succeeded" && ClosurePlatformStock == closurePlatformCraftCount &&
                    closurePlatformWoodBefore - woodAfter == closurePlatformCraftCount / 2 && ClosurePlatformPreserved();
                closureEvents.Add(new { kind = "platform_craft_evidence", accepted = crafted, woodBefore = closurePlatformWoodBefore,
                    woodAfter, platformStock = ClosurePlatformStock, inventory = ShelterCounts(Loadout.Inventory) });
                if (!crafted) FinishAutonomousClosureSources("failed_or_uncovered", "native crafting material or platform delta did not match");
                else
                {
                    closurePlatformCrafted = ClosurePlatformStock;
                    ReviewClosurePlatform("wood platforms crafted; review and mine a fresh observed five-stone source");
                }
                return true;
            case 3:
                if (!ReadClosurePlatformHarvest(outcome, ItemID.StoneBlock, 5))
                    FinishAutonomousClosureSources("failed_or_uncovered", "did not carry five attributable native stone");
                else
                {
                    closurePlatformMinedStone = Loadout.CountItem(ItemID.StoneBlock);
                    closurePlatformObservedAir.Clear();
                    ReviewClosurePlatform("five stone verified; inspect fresh terrain, then place one platform at a time");
                }
                return true;
            case ClosurePlatformPlacePhase:
                Point cell = closurePlatformTarget;
                bool placed = outcome.Status == "succeeded" && IsClosurePlacedPlatform(cell) &&
                    ClosurePlatformStock == closurePlatformCrafted - closurePlatformPlaced.Count - 1 && ClosurePlatformPreserved();
                closureEvents.Add(new { kind = "platform_place_evidence", x = cell.X, y = cell.Y, accepted = placed,
                    itemId = ItemID.WoodPlatform, nativeTile = (int)Main.tile[cell.X, cell.Y].TileType,
                    platformStock = ClosurePlatformStock, inventory = ShelterCounts(Loadout.Inventory) });
                if (!placed) FinishAutonomousClosureSources("failed_or_uncovered", "native platform or one-item consumption was not verified");
                else
                {
                    closurePlatformPlaced.Add(cell);
                    ReviewClosurePlatform("platform placed; use stand x,standRow to climb or continue reviewing return");
                }
                return true;
            case ClosurePlatformStandPhase:
                var support = ClosurePlatformUnderFeet(npc.Center.X, npc.Bottom.Y, npc.width).ToArray();
                bool landed = outcome.Status == "succeeded" && closurePlatformStandStartFeet - npc.Bottom.Y >= 4f &&
                    support.Length > 0 && Math.Abs(npc.velocity.Y) <= 0.6f &&
                    TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
                closureEvents.Add(new { kind = "platform_stand_evidence", accepted = landed, startFeet = closurePlatformStandStartFeet,
                    actual = new { x = npc.Center.X, y = npc.Center.Y, feet = npc.Bottom.Y },
                    support = support.Select(point => new { x = point.X, y = point.Y }) });
                if (!landed || !ClosurePlatformPreserved())
                    FinishAutonomousClosureSources("failed_or_uncovered", "Explore did not physically ascend and land on placed support");
                else
                    ReviewClosurePlatform("actual platform landing verified; inspect the next step or send come");
                return true;
            case 4:
                CompleteClosurePlatformReturn(outcome, npc, player);
                return true;
            default:
                return false;
        }
    }

    private void CompleteClosurePlatformReturn(AgentOutcome outcome, NPC npc, Player player)
    {
        float horizontal = Math.Abs(npc.Center.X - player.Center.X);
        float vertical = Math.Abs(npc.Bottom.Y - player.Bottom.Y);
        bool samePlayer = player.active && !player.dead && player.whoAmI == closurePlatformPlayer && Main.worldID == closurePlatformWorld;
        bool grounded = Math.Abs(npc.velocity.Y) <= 0.6f &&
            TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
        bool sameFloor = vertical <= 8f && grounded &&
            TilePathfinder.IsGroundedAt(player.Center.X, player.Bottom.Y, player.velocity.Y, player.width);
        int stone = Loadout.CountItem(ItemID.StoneBlock);
        bool carriedStone = stone >= 5 && stone == closurePlatformMinedStone;
        var preservation = closureAudit?.CheckPreservation();
        bool physicallyMoved = Vector2.Distance(npc.Center, closurePlatformReturnStart) >= 16f;
        bool passed = outcome.Status == "succeeded" && samePlayer && sameFloor && horizontal <= 48f && carriedStone && physicallyMoved &&
            ClosurePlatformLedgerMatches && closurePlatformContacts.Count > 0 &&
            preservation is { preserved: true, coverageComplete: true };
        closureEvents.Add(new { kind = "platform_return_evidence", passed, samePlayer, sameFloor, grounded, physicallyMoved,
            horizontalPixels = horizontal, verticalFeetPixels = vertical, stone, carriedStone,
            placed = closurePlatformPlaced.Select(point => new { x = point.X, y = point.Y }),
            contacted = closurePlatformContacts.Select(point => new { x = point.X, y = point.Y }),
            inventory = ShelterCounts(Loadout.Inventory), preservation });
        FinishAutonomousClosureSources(passed ? "passed_controlled_platform_return" : "failed_or_uncovered",
            passed ? "" : "same-player grounded return, carried stone, actual platform contact or protection evidence was incomplete");
    }
}
