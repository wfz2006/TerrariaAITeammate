using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ObjectData;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private sealed class AgentAcceptanceRun
    {
        public string SavePath = "";
        public int WorldId;
        public int CenterX;
        public int FloorY;
        public int TreesGrown;
        public int InitialGelDrops;
        public DateTime StartedAt;
        public bool Finished;
        public bool ConstructionOnly;
        public string Status = "preparing";
        public string Error = "";
        public readonly List<AgentOutcome> Steps = new();
        public readonly HashSet<long> SeenSteps = new();
    }

    // Destructive fixture preparation is permitted only in an explicitly isolated test save.
    private static bool AgentAcceptanceAllowed(string expectedSavePath, int expectedWorldId)
        => Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") is "agent" or "construction" &&
           Main.netMode == NetmodeID.SinglePlayer && !Main.gameMenu &&
           Main.SavePath.Contains("agent-acceptance", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(Main.SavePath, expectedSavePath, StringComparison.OrdinalIgnoreCase) &&
           Main.worldID == expectedWorldId;

    private async Task RunAgentAcceptanceAsync()
    {
        var run = new AgentAcceptanceRun { SavePath = Main.SavePath, WorldId = Main.worldID,
            ConstructionOnly = Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") == "construction" };
        if (Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") is not ("agent" or "construction") ||
            Main.netMode != NetmodeID.SinglePlayer ||
            !run.SavePath.Contains("agent-acceptance", StringComparison.OrdinalIgnoreCase))
        {
            TestLog("agent acceptance refused: isolated agent-acceptance save is required");
            return;
        }
        await Task.Delay(5000).ConfigureAwait(false);
        var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            try
            {
                if (!AgentAcceptanceAllowed(run.SavePath, run.WorldId))
                {
                    prepared.TrySetResult(false);
                    return;
                }
                PrepareAgentAcceptance(run);
                run.StartedAt = DateTime.UtcNow;
                run.Status = "running";
                SaveAgentAcceptance(run, false, null);
                if (run.ConstructionOnly)
                {
                    foreach (var material in CompanionConstruction.ShelterMaterials("wood"))
                        Loadout.AddItemStack(material.Key, material.Value);
                    ApplyLocalTask(new AIPlan { Action = AIActionType.Build, Blueprint = "shelter", Target = "wood" });
                }
                else
                    HandlePlayerMessage(Main.LocalPlayer,
                        "小星，请自己收集和制作所需材料，在这里建成一间有门、完整墙、桌椅和光源的可用庇护所。遇到缺料或条件不满足自己解决，完成后告诉我。");
                prepared.TrySetResult(true);
            }
            catch (Exception exception)
            {
                run.Status = "setup_failed";
                run.Error = exception.Message;
                run.Finished = true;
                SaveAgentAcceptance(run, false, null);
                TestLog("agent acceptance setup failed: " + exception.Message);
                prepared.TrySetResult(false);
            }
        });
        if (await Task.WhenAny(prepared.Task, Task.Delay(30000)).ConfigureAwait(false) != prepared.Task ||
            !await prepared.Task.ConfigureAwait(false))
            return;

        for (int poll = 0; poll <= 240 && !run.Finished; poll++)
        {
            await Task.Delay(2000).ConfigureAwait(false);
            Enqueue(() => PollAgentAcceptance(run));
        }
    }

    private void PrepareAgentAcceptance(AgentAcceptanceRun run)
    {
        run.CenterX = Math.Clamp(Main.spawnTileX, 150, Main.maxTilesX - 150);
        run.FloorY = Math.Clamp(Main.spawnTileY + 8, 90, Main.maxTilesY - 120);
        Main.autoPause = false;
        Main.dayTime = true;
        Main.time = 1000;
        autoMode = false;
        CombatPlannerEnabled = false;
        Executor.Clear();
        Executor.ResetExplorationMemory();
        Loadout.Reset();
        Loadout.EnsureStarterTools();
        Memory.Clear();
        agentJournal.Clear();

        for (int x = run.CenterX - 110; x <= run.CenterX + 110; x++)
        {
            for (int y = run.FloorY - 45; y < run.FloorY; y++)
            {
                WorldGen.KillTile(x, y, false, false, true);
                Main.tile[x, y].ClearEverything();
            }
            for (int depth = 0; depth <= 5; depth++)
            {
                Tile tile = Main.tile[x, run.FloorY + depth];
                tile.ClearEverything();
                tile.HasTile = true;
                tile.TileType = depth == 0 ? TileID.Grass : TileID.Dirt;
            }
        }
        foreach (Item item in Main.item)
            if (item.active && Math.Abs(item.Center.X / 16f - run.CenterX) < 115 &&
                Math.Abs(item.Center.Y / 16f - run.FloorY) < 50)
                item.active = false;

        Player player = Main.LocalPlayer;
        foreach (Item item in player.inventory)
        {
            bool gear = item.damage > 0 || item.pick > 0 || item.axe > 0 || item.hammer > 0 ||
                        item.headSlot >= 0 || item.bodySlot >= 0 || item.legSlot >= 0 || item.accessory;
            if (!gear) item.TurnToAir();
        }
        player.Center = new Vector2(run.CenterX * 16f + 8, run.FloorY * 16f - player.height / 2f);
        player.velocity = Vector2.Zero;
        player.direction = 1;
        player.immune = true;
        player.immuneTime = 600;
        SpawnCompanion(player);

        for (int tree = 0; tree < 8; tree++)
        {
            int x = run.CenterX + 12 + tree * 10;
            bool grown = false;
            for (int attempt = 0; attempt < 25 && !grown; attempt++)
                grown = WorldGen.GrowTree(x, run.FloorY);
            if (grown && Main.tile[x, run.FloorY - 1].HasTile &&
                Main.tile[x, run.FloorY - 1].TileType == TileID.Trees)
                run.TreesGrown++;
        }
        if (run.TreesGrown != 8)
            throw new InvalidOperationException($"Expected eight real forest trees, grew {run.TreesGrown}.");

        // A real enemy death provides the source of gel; no materials or furniture enter the backpack.
        for (int slime = 0; slime < 3; slime++)
        {
            int index = NPC.NewNPC(new EntitySource_Misc("AgentAcceptanceGelSource"),
                (run.CenterX + 8) * 16, (run.FloorY - 2) * 16, NPCID.BlueSlime);
            if (index >= 0 && index < Main.maxNPCs)
            {
                Main.npc[index].life = 1;
                Main.npc[index].StrikeInstantKill();
            }
        }
        run.InitialGelDrops = Main.item.Where(item => item.active && item.type == ItemID.Gel &&
            Math.Abs(item.Center.X / 16f - run.CenterX) < 110).Sum(item => item.stack);
        if (run.InitialGelDrops == 0)
            throw new InvalidOperationException("Real slime kills produced no gel drops.");

        // Optional stone is visible and mineable; it is never injected into an inventory.
        for (int x = run.CenterX - 40; x < run.CenterX - 25; x++)
            for (int y = run.FloorY + 1; y <= run.FloorY + 3; y++)
                Main.tile[x, y].TileType = TileID.Stone;
        TestLog($"agent acceptance fixture ready trees={run.TreesGrown} gelDrops={run.InitialGelDrops} center={run.CenterX},{run.FloorY}");
    }

    private void PollAgentAcceptance(AgentAcceptanceRun run)
    {
        if (run.Finished || !AgentAcceptanceAllowed(run.SavePath, run.WorldId))
            return;
        Player player = Main.LocalPlayer;
        player.Center = new Vector2(run.CenterX * 16f + 8, run.FloorY * 16f - player.height / 2f);
        player.velocity = Vector2.Zero;
        player.immune = true;
        player.immuneTime = 240;
        player.statLife = player.statLifeMax2;
        Main.dayTime = true;
        Main.time = 1000;
        foreach (NPC npc in Main.npc)
            if (npc.active && !npc.friendly && Math.Abs(npc.Center.X / 16f - run.CenterX) < 115)
                npc.active = false;
        foreach (AgentOutcome outcome in agentJournal.Outcomes)
            if (run.SeenSteps.Add(outcome.Sequence))
                run.Steps.Add(outcome);

        (int Left, int Top, string Material, bool RoomValid)? building = FindAgentAcceptanceBuilding(run);
        bool harvested = run.Steps.Any(step => step.Task.Action == AIActionType.Mine &&
            step.Task.Target == "wood" && step.Status == "succeeded");
        bool crafted = run.Steps.Any(step => step.Task.Action == AIActionType.Craft && step.Status == "succeeded");
        bool physicallyComplete = building is { RoomValid: true } && (run.ConstructionOnly || harvested && crafted);
        bool timedOut = DateTime.UtcNow - run.StartedAt >= TimeSpan.FromMinutes(8);
        bool declaredDone = goalStatus == "completed" || run.ConstructionOnly &&
            run.Steps.Any(step => step.Task.Action == AIActionType.Build && step.Status == "succeeded");
        if (run.ConstructionOnly && run.Steps.Any(step => step.Task.Action == AIActionType.Build && step.Status == "blocked"))
        {
            run.Status = "failed_execution";
            run.Error = run.Steps.Last().Result;
            run.Finished = true;
        }
        if (physicallyComplete && declaredDone)
        {
            run.Status = "passed";
            run.Finished = true;
        }
        else if (declaredDone && !physicallyComplete)
        {
            run.Status = "failed_false_completion";
            run.Error = "LLM declared completion without a verified real house plus harvest/craft evidence.";
            run.Finished = true;
        }
        else if (timedOut)
        {
            run.Status = "failed_timeout";
            run.Error = "Eight-minute real LLM acceptance deadline exceeded.";
            run.Finished = true;
            Stop();
        }
        SaveAgentAcceptance(run, physicallyComplete, building);
        if (run.Finished)
            TestLog($"agent acceptance result={run.Status} steps={run.Steps.Count} house={physicallyComplete} error={run.Error}");
    }

    private static (int Left, int Top, string Material, bool RoomValid)? FindAgentAcceptanceBuilding(AgentAcceptanceRun run)
    {
        int top = run.FloorY - CompanionConstruction.Height;
        foreach (string material in new[] { "wood", "stone" })
        {
            for (int left = run.CenterX - 108; left <= run.CenterX + 98; left++)
            {
                bool complete = true;
                foreach (ConstructionStep step in CompanionConstruction.ShelterLayout(left, top, material))
                {
                    if (step.Wall > 0)
                    {
                        if (Main.tile[step.X, step.Y].WallType != step.Wall) { complete = false; break; }
                        continue;
                    }
                    if (step.Tile == TileID.ClosedDoor)
                    {
                        bool door = true;
                        for (int dy = 0; dy < 3; dy++)
                            door &= Enumerable.Range(step.X - 1, 3).Any(x =>
                                Main.tile[x, step.Y + dy].HasTile &&
                                Main.tile[x, step.Y + dy].TileType is TileID.ClosedDoor or TileID.OpenDoor);
                        if (!door) { complete = false; break; }
                        continue;
                    }
                    TileObjectData? data = TileObjectData.GetTileData(step.Tile, step.Style);
                    for (int x = step.X; x < step.X + (data?.Width ?? 1); x++)
                        for (int y = step.Y; y < step.Y + (data?.Height ?? 1); y++)
                            complete &= Main.tile[x, y].HasTile && Main.tile[x, y].TileType == step.Tile;
                    if (!complete) break;
                }
                if (complete)
                {
                    bool roomValid = WorldGen.StartRoomCheck(left + 5, top + 3) && WorldGen.RoomNeeds(-1);
                    return (left, top, material, roomValid);
                }
            }
        }
        return null;
    }

    private void SaveAgentAcceptance(AgentAcceptanceRun run, bool physicallyComplete,
        (int Left, int Top, string Material, bool RoomValid)? building)
    {
        string directory = Path.Combine(run.SavePath, "AITeammate");
        Directory.CreateDirectory(directory);
        int harvestedWood = run.Steps.Where(step => step.Task.Action == AIActionType.Mine && step.Task.Target == "wood")
            .Select(step => Regex.Match(step.Result, @"实际新增木材\s*(\d+)"))
            .Where(match => match.Success).Sum(match => int.Parse(match.Groups[1].Value));
        var result = new
        {
            scenario = run.ConstructionOnly ? "construction-engine-only" : "real-llm-self-supplied-shelter", status = run.Status, error = run.Error,
            startedUtc = run.StartedAt, updatedUtc = DateTime.UtcNow,
            elapsedSeconds = run.StartedAt == default ? 0 : (DateTime.UtcNow - run.StartedAt).TotalSeconds,
            fixture = new { run.CenterX, run.FloorY, run.TreesGrown, run.InitialGelDrops,
                startingMaterials = run.ConstructionOnly ? "exact shelter materials supplied by execution-only fixture" : "none; only copper pickaxe, copper axe and copper shortsword", preplacedStations = 0 },
            physicallyComplete, goalStatus, activeGoal, lastReply = LastReply,
            house = building == null ? null : new { x = building.Value.Left, y = building.Value.Top,
                material = building.Value.Material, width = 10, height = 7, roomValid = building.Value.RoomValid },
            harvestedWood, executionSteps = run.Steps.Count,
            actions = run.Steps.GroupBy(step => step.Task.Action.ToString().ToLowerInvariant())
                .ToDictionary(group => group.Key, group => group.Count()),
            outcomes = run.Steps.Select(step => new { step.Sequence, action = step.Task.Action.ToString(),
                step.Task.Target, step.Task.Item, step.Task.Count, step.Status, step.Result }).ToArray(),
            currentInventory = Loadout.Summary(), currentTask = Executor.StatusText()
        };
        File.WriteAllText(Path.Combine(directory, "acceptance-result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
