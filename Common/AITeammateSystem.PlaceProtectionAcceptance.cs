using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ObjectData;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string PlaceProtectionNativeCase = "place_protection_native";
    private static readonly GoalCondition[] PlaceProtectionConditions = { new() { Type = "structure_preserved" } };

    private sealed record PlaceProtectionStep(string Id, AIPlan Task, GoalProtectionRegion Witness,
        GoalConstructionProtection? Protection, int? ExpectedWallType = null);
    private sealed record PlaceProtectionCell(int X, int Y, bool HasTile, int TileType, int WallType,
        int TileFrameX, int TileFrameY, int WallFrameX, int WallFrameY, int TileColor, int WallColor,
        int Slope, bool HalfBlock, bool Actuated, int LiquidAmount, int LiquidType);
    private sealed record PlaceProtectionStock(int Slot, int Type, int Stack, int Prefix);

    private bool placeProtectionStarted, placeProtectionFinished, placeProtectionLlmDisabled;
    private int placeProtectionIndex, placeProtectionFrames, placeProtectionDispatchFrame;
    private long placeProtectionSequence;
    private DateTime placeProtectionStartedUtc;
    private ShelterWorldAudit? placeProtectionAudit;
    private PlaceProtectionStep[] placeProtectionSteps = Array.Empty<PlaceProtectionStep>();
    private PlaceProtectionCell[] placeProtectionCellsBefore = Array.Empty<PlaceProtectionCell>();
    private PlaceProtectionStock[] placeProtectionStockBefore = Array.Empty<PlaceProtectionStock>();
    private readonly List<object> placeProtectionEvents = new();

    private void BeginPlaceProtectionNative()
    {
        placeProtectionStarted = true;
        placeProtectionStartedUtc = DateTime.UtcNow;
        placeProtectionLlmDisabled = GetLlmSettings() == null;
        if (!placeProtectionLlmDisabled || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0)
        {
            FinishPlaceProtectionNative("failed", "The controlled case requires disabled LLM settings and zero prior requests.");
            return;
        }

        Stop();
        Executor.ResetNavigationTelemetry();
        NPC npc = CompanionNpc!;
        npc.Center = new Vector2(3096.339f * 16, 398.6875f * 16);
        npc.velocity = Vector2.Zero;
        Main.LocalPlayer.Center = Stand(3173, 395);
        Main.LocalPlayer.velocity = Vector2.Zero;

        const int wallStock = 8, chairStock = 2;
        if (!Loadout.AddItem(ItemID.WoodWall, wallStock) || !Loadout.AddItem(ItemID.WoodenChair, chairStock))
        {
            FinishPlaceProtectionNative("failed", "Controlled placement materials do not fit.");
            return;
        }

        bool goalAccepted = ApplyPlan(new AIPlan
        {
            Goal = "受控原屋保护放置诊断",
            CompletionConditions = PlaceProtectionConditions.ToList()
        }, null, sourcePlayerRequest: "零 API 受控 Place 保护合同诊断");
        placeProtectionAudit = ShelterWorldAudit.Capture(3220, 390);
        GoalNativeConditionResult? native = ObservePlaceProtection();
        ShelterPreservationReport fixture = placeProtectionAudit.CheckPreservation();

        Item wall = Loadout.Inventory[Loadout.FindInventoryIndex(ItemID.WoodWall.ToString())];
        Item chair = Loadout.Inventory[Loadout.FindInventoryIndex(ItemID.WoodenChair.ToString())];
        TileObjectData? chairData = TileObjectData.GetTileData(chair.createTile, chair.placeStyle);
        var protectedAir = new GoalProtectionRegion(3098, 399, 1, 1);
        var area = new GoalProtectionRegion(3098, 399, 2, 2);
        var chairFootprint = new GoalProtectionRegion(3098, 398, chairData?.Width ?? 1, chairData?.Height ?? 1);
        var allowedAir = new GoalProtectionRegion(3099, 400, 1, 1);

        GoalConstructionProtection Inspect(GoalProtectionRegion region) =>
            InspectConstructionProtection(region.X, region.Y, region.Width, region.Height);

        PlaceProtectionStep Wall(string id, GoalProtectionRegion region, int? expectedWall = null) => new(id,
            new AIPlan { Action = AIActionType.Place, Item = ItemID.WoodWall.ToString(), Count = region.Width * region.Height,
                X = region.X, Y = region.Y, Width = region.Width, Height = region.Height }, region, Inspect(region), expectedWall);

        PlaceProtectionStep single = Wall("protected_air_wall", protectedAir);
        PlaceProtectionStep rectangle = Wall("mixed_rectangle", area);
        PlaceProtectionStep furniture = new("protected_furniture_footprint", new AIPlan
        {
            Action = AIActionType.Place, Item = ItemID.WoodenChair.ToString(), Count = 1, X = chairFootprint.X, Y = chairFootprint.Y
        }, chairFootprint, Inspect(chairFootprint));
        var missingCoordinate = new PlaceProtectionStep("missing_coordinate", new AIPlan
        {
            Action = AIActionType.Place, Item = ItemID.WoodWall.ToString(), Count = 1, X = protectedAir.X
        }, protectedAir, null);
        PlaceProtectionStep allowed = Wall("unprotected_wall", allowedAir, wall.createWall);
        GoalConstructionProtection chairTopLeft = InspectConstructionProtection(chairFootprint.X, chairFootprint.Y, 1, 1);
        // PlaceArea visits the bottom row first, from left to right.
        GoalConstructionProtection rectangleFirstCell = InspectConstructionProtection(area.X, area.Y + area.Height - 1, 1, 1);
        GoalProtectionRegion? sample = single.Protection?.SampleBounds;
        var outside = new GoalProtectionRegion(sample == null ? 0 : sample.X + sample.Width, protectedAir.Y, 1, 1);
        PlaceProtectionStep uncovered = Wall("outside_original_sample", outside);
        placeProtectionSteps = new[] { single, rectangle, furniture, missingCoordinate, uncovered, allowed };

        PlaceProtectionCell[] protectedBefore = ReadPlaceProtectionCells(protectedAir);
        PlaceProtectionCell[] rectangleBefore = ReadPlaceProtectionCells(area);
        PlaceProtectionCell[] furnitureBefore = ReadPlaceProtectionCells(chairFootprint);
        PlaceProtectionCell[] allowedBefore = ReadPlaceProtectionCells(allowedAir);
        bool IsProtectedAir(PlaceProtectionStep step) => step.Protection is { Required: true, Available: true, Covered: true, Allowed: false } p &&
            p.ProtectedHits.Any(hit => hit.X == protectedAir.X && hit.Y == protectedAir.Y);
        bool preflight = goalAccepted && native?.Met == true && fixture is { preserved: true, coverageComplete: true, diffCount: 0 } &&
            wall.createWall > WallID.None && wall.createTile < TileID.Dirt &&
            !protectedBefore[0].HasTile && protectedBefore[0].WallType == WallID.None && IsProtectedAir(single) &&
            rectangleBefore.All(c => c.WallType == WallID.None) && IsProtectedAir(rectangle) &&
            rectangleFirstCell is { Covered: true, Allowed: true } &&
            chairData is { Width: 1, Height: 2 } && furnitureBefore.All(c => !c.HasTile && c.LiquidAmount == 0) &&
            chairTopLeft is { Covered: true, Allowed: true } && IsProtectedAir(furniture) &&
            sample != null && WorldGen.InWorld(outside.X, outside.Y, 3) &&
            uncovered.Protection is { Required: true, Available: true, Covered: false, Allowed: null } &&
            !allowedBefore[0].HasTile && allowedBefore[0].WallType == WallID.None && allowedBefore[0].LiquidAmount == 0 &&
            allowed.Protection is { Covered: true, Allowed: true };

        placeProtectionEvents.Add(new
        {
            phase = "baseline", tick = Main.GameUpdateCount, preflight, goalAccepted, nativeCondition = native,
            actor = new { x = npc.Center.X / 16, y = npc.Center.Y / 16, feetY = npc.Bottom.Y / 16 },
            supplied = new { wallStock, chairStock, wallType = wall.createWall, chairTileType = chair.createTile, chair.placeStyle },
            inventory = ReadPlaceProtectionStock(), protectedBefore, rectangleBefore, furnitureBefore, allowedBefore,
            chairTopLeft, rectangleFirstCell, steps = placeProtectionSteps, fixture,
            production = PreservationEvidence.DebugSnapshot()
        });
        if (!preflight)
        {
            FinishPlaceProtectionNative("failed", "Original native mask or placement geometry preflight failed.");
            return;
        }
        DispatchPlaceProtectionNative();
    }

    private void DispatchPlaceProtectionNative()
    {
        PlaceProtectionStep step = placeProtectionSteps[placeProtectionIndex];
        placeProtectionCellsBefore = ReadPlaceProtectionCells(step.Witness);
        placeProtectionStockBefore = ReadPlaceProtectionStock();
        placeProtectionSequence = agentJournal.LastSequence;
        placeProtectionDispatchFrame = placeProtectionFrames;
        placeProtectionEvents.Add(new
        {
            phase = "dispatch", step.Id, step.Task, step.Witness, tick = Main.GameUpdateCount,
            cells = placeProtectionCellsBefore, inventory = placeProtectionStockBefore
        });
        Executor.Apply(step.Task);
    }

    private void TickPlaceProtectionNative()
    {
        if (Main.gameMenu) return;
        placeProtectionFrames++;
        if (CompanionNpc is not { active: true } || GetLlmSettings() != null ||
            PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 || Executor.NavigationTeleportCount != 0)
        {
            FinishPlaceProtectionNative("failed", "Companion missing, LLM enabled, API request observed, or teleport observed.");
            return;
        }

        Player player = Main.LocalPlayer;
        player.immune = true;
        player.immuneTime = 600;
        player.statLife = player.statLifeMax2;
        foreach (NPC hostile in Main.npc)
            if (hostile.active && !hostile.friendly) hostile.active = false;

        PlaceProtectionStep step = placeProtectionSteps[placeProtectionIndex];
        PlaceProtectionCell[] cells = ReadPlaceProtectionCells(step.Witness);
        PlaceProtectionStock[] inventory = ReadPlaceProtectionStock();
        AgentOutcome[] outcomes = agentJournal.Outcomes.Where(outcome => outcome.Sequence > placeProtectionSequence).ToArray();
        bool denied = !step.ExpectedWallType.HasValue;
        bool cellsUnchanged = cells.SequenceEqual(placeProtectionCellsBefore);
        bool inventoryUnchanged = inventory.SequenceEqual(placeProtectionStockBefore);
        bool unexpectedWrite = denied && (!cellsUnchanged || !inventoryUnchanged);
        if (outcomes.Length == 0 && !unexpectedWrite && placeProtectionFrames - placeProtectionDispatchFrame <= 30)
            return;

        GoalNativeConditionResult? native = ObservePlaceProtection();
        ShelterPreservationReport fixture = placeProtectionAudit!.CheckPreservation();
        bool sameTask = outcomes.Length == 1 && PlanTaskIdentity.AreEquivalent(step.Task, outcomes[0].Task);
        bool protectionBlocked = sameTask && outcomes[0].Status == "blocked" && outcomes[0].Result.Contains("原屋保护", StringComparison.Ordinal);
        PlaceProtectionStock[] expectedStock = placeProtectionStockBefore.Select(s =>
            s.Type == ItemID.WoodWall ? s with { Stack = s.Stack - 1 } : s).ToArray();
        bool nativeWallAdded = !denied && cells[0].WallType == step.ExpectedWallType &&
            placeProtectionCellsBefore[0].WallType == WallID.None && !cells[0].HasTile && inventory.SequenceEqual(expectedStock);
        bool passed = native?.Met == true && fixture is { preserved: true, coverageComplete: true, diffCount: 0 } &&
            (denied ? protectionBlocked && cellsUnchanged && inventoryUnchanged : sameTask && outcomes[0].Status == "succeeded" && nativeWallAdded);
        placeProtectionEvents.Add(new
        {
            phase = "outcome", step.Id, tick = Main.GameUpdateCount, passed, outcomes, cells, inventory,
            cellsUnchanged, inventoryUnchanged, protectionBlocked, nativeWallAdded, nativeCondition = native, fixture
        });
        if (!passed)
        {
            FinishPlaceProtectionNative("failed", $"{step.Id}: unexpected outcome, world/inventory change, preservation failure, or step deadline.");
            return;
        }
        if (++placeProtectionIndex == placeProtectionSteps.Length)
            FinishPlaceProtectionNative("passed", "");
        else
            DispatchPlaceProtectionNative();
    }

    private GoalNativeConditionResult? ObservePlaceProtection() =>
        GoalWorldObserver.Capture(this, PlaceProtectionConditions, goalBossDeathBoundary).NativeConditions.GetValueOrDefault(0);

    private static PlaceProtectionCell[] ReadPlaceProtectionCells(GoalProtectionRegion region)
    {
        var cells = new List<PlaceProtectionCell>();
        for (int y = region.Y; y < region.Y + region.Height; y++)
            for (int x = region.X; x < region.X + region.Width; x++)
            {
                Tile tile = Main.tile[x, y];
                cells.Add(new(x, y, tile.HasTile, tile.TileType, tile.WallType, tile.TileFrameX, tile.TileFrameY,
                    tile.WallFrameX, tile.WallFrameY, tile.TileColor, tile.WallColor, (int)tile.Slope,
                    tile.IsHalfBlock, tile.IsActuated, tile.LiquidAmount, tile.LiquidType));
            }
        return cells.ToArray();
    }

    private PlaceProtectionStock[] ReadPlaceProtectionStock() => Loadout.Inventory
        .Select((item, slot) => new PlaceProtectionStock(slot, item.type, item.stack, item.prefix))
        .Where(stock => stock.Type > ItemID.None && stock.Stack > 0).ToArray();

    private void FinishPlaceProtectionNative(string status, string error)
    {
        placeProtectionFinished = true;
        try
        {
            string directory = Path.Combine(Main.SavePath, "AITeammate");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "place-protection-native-result.json"), JsonSerializer.Serialize(new
            {
                status, error, world = Main.worldName, worldId = Main.worldID, epoch = WorldKnowledgeObserver.LoadEpoch,
                startedUtc = placeProtectionStartedUtc, finishedUtc = DateTime.UtcNow, frames = placeProtectionFrames,
                llmSettingsNullAtSetup = placeProtectionLlmDisabled, llmSettingsNullAtFinish = GetLlmSettings() == null,
                primaryRequests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount,
                warps = Executor.NavigationTeleportCount, completedSteps = placeProtectionIndex,
                fixtureBaseline = placeProtectionAudit?.BaselineReport, production = PreservationEvidence.DebugSnapshot(),
                events = placeProtectionEvents,
                scope = "Controlled zero-API production Place on an isolated original-house copy. Setup positions and inventory are supplied once. " +
                    "Player protected and hostiles removed. Terrain and building cells come from the original copy. " +
                    "Only the six scripted Executor.Apply actions are exercised."
            }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        }
        finally
        {
            Stop();
        }
        TestLog($"{PlaceProtectionNativeCase} {status}: {error}");
    }
}
