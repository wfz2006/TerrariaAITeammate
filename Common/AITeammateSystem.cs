using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.UI;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem : ModSystem
{
    public static AITeammateSystem Instance => ModContent.GetInstance<AITeammateSystem>();

    private readonly ConcurrentQueue<Action> mainThreadQueue = new();
    private bool autoTestStarted;
    private bool autoMode;
    private bool autoPlanInFlight;
    private DateTime lastCommandAt = DateTime.MinValue;
    private DateTime lastAutoPlanAt = DateTime.MinValue;
    private DateTime lastExploreAt = DateTime.MinValue;
    private string activeGoal = "";
    private string lastGoalResult = "";
    private string goalStepLog = "";
    private bool goalReviewPending;
    private bool goalReviewBlocked;
    private bool goalPlanInFlight;
    private bool goalWaitingForPlayer;
    private DateTime lastGoalPlanAt = DateTime.MinValue;
    private int goalGeneration;
    public string ActiveGoal => activeGoal;
    public string LastGoalResult => lastGoalResult;
    public bool GoalBlocked => goalReviewBlocked;
    private int doorTestX;
    private int doorTestY;
    private int testChestIndex = -1;
    private int blueprintTestFloorY;
    private int blueprintTestSourceX;
    private string blueprintTestName = "";
    private int followTestCenterX;
    private int followTestFloorY;
    private int defendTestSlime = -1;
    private bool combatPlanInFlight;
    private DateTime lastCombatPlanAt = DateTime.MinValue;
    private int combatLatencyMs;
    private string lastCombatSignature = "";
    private int combatCallId;
    // Session-only control choice. Basic commands remain usable without any LLM
    // requests; an ordinary model request or explicit auto-on restores guidance.
    private bool manualControlActive = true;

    public bool AutoMode => autoMode;
    public bool ManualControlActive => manualControlActive;
    public bool CombatPlannerEnabled { get; set; } = true;
    public string LastReply { get; private set; } = "";
    public string LastAutoPlan { get; private set; } = "";
    public string LastCombatDecision { get; private set; } = "";
    public int LastCombatIntervalMs { get; private set; }

    public AIExecutor Executor { get; } = new();
    public AIMemory Memory { get; } = new();
    public CompanionLoadout Loadout { get; } = new();
    public CompanionInventoryUI InventoryUI { get; } = new();

    public NPC? CompanionNpc => Main.npc.FirstOrDefault(n => n.active && n.type == ModContent.NPCType<Content.NPCs.AICompanion>());

    public override void Load()
    {
        if (!Main.dedServ)
        {
            PanelKeybind = KeybindLoader.RegisterKeybind(Mod, "CompanionPanel", Keys.O);
            ChatHook.Load();
        }
    }

    public override void PostSetupContent()
    {
        if (Main.dedServ ||
            Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") is not ("agent" or "construction" or "complex" or "house") ||
            !Main.SavePath.Contains("agent-acceptance", StringComparison.OrdinalIgnoreCase))
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(2500).ConfigureAwait(false);
            for (int attempt = 0; attempt < 6 && Main.gameMenu; attempt++)
            {
                Main.QueueMainThreadAction(() =>
                {
                    if (!Main.gameMenu || !Main.SavePath.Contains("agent-acceptance", StringComparison.OrdinalIgnoreCase))
                        return;
                    try
                    {
                        WorldGen.clearWorld();
                        Main.LoadWorlds();
                        Main.LoadPlayers();
                        if (Main.WorldList.Count > 0 && Main.PlayerList.Count > 0)
                        {
                            Main.WorldList[0].SetAsActive();
                            Main.PlayerList[0].SetAsActive();
                            TestLog($"autotest auto-entering world={Main.WorldList[0].Name} player={Main.PlayerList[0].Name}");
                            WorldGen.playWorld();
                        }
                    }
                    catch (Exception exception)
                    {
                        TestLog("autotest auto-enter failed: " + exception.Message);
                    }
                });
                await Task.Delay(6000).ConfigureAwait(false);
            }
        });
    }

    public override void Unload()
    {
        StopTestRecording();
        if (!Main.dedServ) ChatHook.Unload();
        PanelKeybind = null;
        CloseCompanionPanel();
    }

    public override void OnWorldLoad()
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
            return;
        worldReady = true;
        WorldKnowledgeObserver.Reset();
        Player player = Main.player[Main.myPlayer];
        BlueprintManager.ClearSelection();
        LlmConversationStore.ResetAll();
        goalPlanningState.Clear();
        activeGoal = "";
        lastGoalResult = "";
        goalStepLog = "";
        goalReviewPending = false;
        goalReviewBlocked = false;
        goalWaitingForPlayer = false;
        goalPlanInFlight = false;
        goalGeneration++;
        Memory.Initialize(Main.worldName, player.name);
        TestLog($"memory loaded: facts={Memory.FactsSnapshot().Replace("\n", " | ")} file={Memory.FilePath}");

        string mode = Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") ?? "";
        if (!string.IsNullOrEmpty(mode))
        {
            Main.autoPause = false;
            TestLog("autotest auto-pause disabled");
        }
        CombatPlannerEnabled = string.IsNullOrEmpty(mode) || mode == "combatbrain";
        if (autoTestStarted)
            return;

        if (mode == "house")
        {
            autoTestStarted = true;
            if (!GoalResumeNativeSelected) _ = RunHouseAcceptanceAsync();
        }
        else if (mode == "complex")
        {
            autoTestStarted = true;
            _ = RunComplexAcceptanceAsync();
        }
        else if (mode is "agent" or "construction")
        {
            autoTestStarted = true;
            _ = RunAgentAcceptanceAsync();
        }
        else if (mode == "1")
        {
            autoTestStarted = true;
            _ = RunAutoTestAsync();
        }
        else if (mode == "build")
        {
            autoTestStarted = true;
            _ = RunBuildTestAsync();
        }
        else if (mode == "brain")
        {
            autoTestStarted = true;
            _ = RunBrainTestAsync();
        }
        else if (mode == "recall")
        {
            autoTestStarted = true;
            _ = RunRecallTestAsync();
        }
        else if (mode == "final")
        {
            autoTestStarted = true;
            _ = RunFinalTestAsync();
        }
        else if (mode == "door")
        {
            autoTestStarted = true;
            _ = RunDoorTestAsync();
        }
        else if (mode == "explore")
        {
            autoTestStarted = true;
            _ = RunExploreTestAsync();
        }
        else if (mode == "inventory")
        {
            autoTestStarted = true;
            _ = RunInventoryTestAsync();
        }
        else if (mode == "actions")
        {
            autoTestStarted = true;
            _ = RunActionTestAsync();
        }
        else if (mode == "chat")
        {
            autoTestStarted = true;
            _ = RunChatTestAsync();
        }
        else if (mode == "multi")
        {
            autoTestStarted = true;
            _ = RunMultiTestAsync();
        }
        else if (mode == "dynamic")
        {
            autoTestStarted = true;
            _ = RunDynamicTestAsync();
        }
        else if (mode == "combat")
        {
            autoTestStarted = true;
            _ = RunCombatTestAsync();
        }
        else if (mode == "mining")
        {
            autoTestStarted = true;
            _ = RunMiningTestAsync();
        }
        else if (mode == "craft")
        {
            autoTestStarted = true;
            _ = RunCraftTestAsync();
        }
        else if (mode == "chest")
        {
            autoTestStarted = true;
            _ = RunChestTestAsync();
        }
        else if (mode == "features")
        {
            autoTestStarted = true;
            _ = RunFeatureSuiteAsync();
        }
        else if (mode == "blueprint")
        {
            autoTestStarted = true;
            _ = RunBlueprintTestAsync();
        }
        else if (mode == "combatbrain")
        {
            autoTestStarted = true;
            _ = RunCombatBrainTestAsync();
        }
        else if (mode == "buildrecover")
        {
            autoTestStarted = true;
            _ = RunBuildRecoverTestAsync();
        }
        else if (mode == "follow")
        {
            autoTestStarted = true;
            _ = RunFollowTestAsync();
        }
        else if (mode == "defend")
        {
            autoTestStarted = true;
            _ = RunDefendTestAsync();
        }
        else if (mode == "goal")
        {
            autoTestStarted = true;
            _ = RunGoalTestAsync();
        }
    }

    private async Task RunBlueprintTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("blueprint test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            ClearLoadout();
            DebugArena(player, 40);

            int px = (int)(player.Center.X / 16f);
            int py = (int)(player.Center.Y / 16f);
            blueprintTestFloorY = (int)(player.Bottom.Y / 16f);
            blueprintTestSourceX = px - 30;
            blueprintTestName = "测试小屋";

            for (int x = blueprintTestSourceX; x <= blueprintTestSourceX + 6; x++)
            {
                WorldGen.PlaceTile(x, blueprintTestFloorY, TileID.Stone, true, true);
                WorldGen.PlaceTile(x, blueprintTestFloorY - 4, TileID.Stone, true, true);
            }

            for (int y = blueprintTestFloorY - 3; y <= blueprintTestFloorY - 1; y++)
            {
                WorldGen.PlaceTile(blueprintTestSourceX, y, TileID.Stone, true, true);
                WorldGen.PlaceTile(blueprintTestSourceX + 6, y, TileID.Stone, true, true);
            }

            for (int x = blueprintTestSourceX + 1; x <= blueprintTestSourceX + 5; x++)
            {
                for (int y = blueprintTestFloorY - 3; y <= blueprintTestFloorY - 1; y++)
                    WorldGen.PlaceWall(x, y, WallID.Stone, true);
            }

            WorldGen.PlaceObject(blueprintTestSourceX + 1, blueprintTestFloorY - 1, TileID.WorkBenches, true, 0, 0, 0, 1);
            WorldGen.PlaceObject(blueprintTestSourceX + 4, blueprintTestFloorY - 1, TileID.ClosedDoor, true, 0, 0, 0, 1);
            WorldGen.PlaceTile(blueprintTestSourceX + 3, blueprintTestFloorY - 1, TileID.Torches, true, true);
            WorldGen.PlaceTile(blueprintTestSourceX + 5, blueprintTestFloorY - 2, TileID.Dirt, true, true);
            int sourceChest = WorldGen.PlaceChest(blueprintTestSourceX - 2, blueprintTestFloorY - 1, TileID.Containers, false, 0);
            TestLog($"source chest index={sourceChest}");

            TestLog($"source objects floor={blueprintTestFloorY} wb={Main.tile[blueprintTestSourceX + 1, blueprintTestFloorY - 1].TileType} door={Main.tile[blueprintTestSourceX + 4, blueprintTestFloorY - 1].TileType} torch={Main.tile[blueprintTestSourceX + 3, blueprintTestFloorY - 1].TileType}");

            if (!BlueprintManager.Capture(blueprintTestName, blueprintTestSourceX - 2, blueprintTestFloorY - 4,
                    blueprintTestSourceX + 6, blueprintTestFloorY, out string capture))
            {
                TestLog("blueprint capture failed: " + capture);
                return;
            }

            Blueprint? blueprint = BlueprintManager.Load(blueprintTestName);
            if (blueprint == null)
            {
                TestLog("blueprint load failed");
                return;
            }

            Dictionary<int, int> required = BlueprintManager.RequiredItems(blueprint);
            foreach ((int type, int count) in required)
                Loadout.AddItem(type, count + 20);

            player.position = new Vector2((px + 20) * 16f - player.width / 2f, blueprintTestFloorY * 16f - player.height);
            player.velocity = Vector2.Zero;
            NPC? companion = CompanionNpc;
            if (companion != null)
            {
                companion.Center = new Vector2((px + 18) * 16f + 8f, blueprintTestFloorY * 16f - companion.height / 2f);
                companion.velocity = Vector2.Zero;
            }

            TestLog($"blueprint capture ok: {capture} required={string.Join(",", required.Select(kv => $"{Lang.GetItemNameValue(kv.Key)}x{kv.Value}"))} source={blueprintTestSourceX},{blueprintTestFloorY}");
        });

        await Task.Delay(1000);
        Enqueue(() => ApplyLocalTask(new AIPlan
        {
            Action = AIActionType.Build,
            Blueprint = blueprintTestName
        }));
        await Task.Delay(30000);
        Enqueue(() =>
        {
            Blueprint? blueprint = BlueprintManager.Load(blueprintTestName);
            if (blueprint == null)
            {
                TestLog("blueprint result failed: blueprint missing");
                return;
            }

            int originX = Executor.LastBlueprintOriginX;
            int originY = Executor.LastBlueprintOriginY;
            int tileMismatch = 0;
            int wallMismatch = 0;

            foreach (BlueprintTile source in blueprint.Tiles)
            {
                int x = originX + source.X;
                int y = originY + source.Y;
                if (!WorldGen.InWorld(x, y, 2))
                {
                    tileMismatch++;
                    continue;
                }

                Tile target = Main.tile[x, y];
                if (!target.HasTile || target.TileType != source.Type ||
                    target.TileFrameX != source.FrameX || target.TileFrameY != source.FrameY)
                    tileMismatch++;
            }

            foreach (BlueprintWall source in blueprint.Walls)
            {
                int x = originX + source.X;
                int y = originY + source.Y;
                if (!WorldGen.InWorld(x, y, 2) || Main.tile[x, y].WallType != source.Type)
                    wallMismatch++;
            }

            TestLog($"blueprint result: status={StatusText()} origin={originX},{originY} tileMismatch={tileMismatch} wallMismatch={wallMismatch} tiles={blueprint.Tiles.Count} walls={blueprint.Walls.Count}");
        });
        Enqueue(() => TestLog("blueprint test done"));
    }

    private async Task RunBuildRecoverTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("buildrecover test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            ClearLoadout();
            DebugArena(player);
            RemovePlayerItem(player, ItemID.Wood);
            RemovePlayerItem(player, ItemID.StoneBlock);
            TestLog($"buildrecover no materials: blocked={(Executor.BlockedPlan != null)} tasks={TaskText()}");
            ApplyLocalTask(new AIPlan { Action = AIActionType.Build });
        });

        await Task.Delay(3000);
        Enqueue(() =>
        {
            TestLog($"buildrecover after fail: blocked={(Executor.BlockedPlan != null)} reason={Executor.BlockedReason} tasks={TaskText()}");
            Loadout.AddItem(ItemID.StoneBlock, 25);
            ApplyLocalTask(new AIPlan { Action = AIActionType.Build });
        });
        await Task.Delay(10000);
        Enqueue(() =>
        {
            TestLog($"buildrecover result: blocked={(Executor.BlockedPlan != null)} tasks={TaskText()} inv={Loadout.Summary()} status={StatusText()}");
            Executor.Clear();
        });
        Enqueue(() => TestLog("buildrecover test done"));
    }

    private static void RemovePlayerItem(Player player, int type)
    {
        foreach (Item item in player.inventory)
            if (!item.IsAir && item.type == type)
                item.SetDefaults(ItemID.None);
    }

    private async Task RunFollowTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("follow test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            DebugArena(player, 45);

            followTestCenterX = (int)(player.Center.X / 16f);
            followTestFloorY = (int)(player.Bottom.Y / 16f);
            SetFollowTestPositions(-30, -35);
            TestLog($"follow slow setup center={followTestCenterX} floor={followTestFloorY} dist={FollowTestDistance():0.0}");
        });

        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(250);
            Enqueue(() =>
            {
                Player player = Main.player[Main.myPlayer];
                player.position.X += 3f * 16f;
                player.velocity.X = 3f;
            });
        }

        await Task.Delay(1500);
        Enqueue(() => LogFollowTest("walk30"));

        Enqueue(() =>
        {
            PlaceFollowTestWall(1);
            SetFollowTestPositions(10, -10);
            TestLog("follow step phase");
        });
        await Task.Delay(8000);
        Enqueue(() => LogFollowTest("step1"));

        Enqueue(() =>
        {
            PlaceFollowTestWall(3);
            SetFollowTestPositions(10, -10);
            TestLog("follow wall3 phase");
        });
        await Task.Delay(10000);
        Enqueue(() => LogFollowTest("wall3"));

        Enqueue(() =>
        {
            PlaceFollowTestWall(6);
            SetFollowTestPositions(10, -10);
            TestLog("follow wall6 phase");
        });
        await Task.Delay(10000);
        Enqueue(() => LogFollowTest("wall6"));

        Enqueue(() =>
        {
            ClearFollowTestWall();
            Executor.Clear();
            TestLog("follow test done");
        });
    }

    private void SetFollowTestPositions(int playerOffset, int companionOffset)
    {
        Player player = Main.player[Main.myPlayer];
        player.position = new Vector2((followTestCenterX + playerOffset) * 16f - player.width / 2f,
            followTestFloorY * 16f - player.height);
        player.velocity = Vector2.Zero;

        NPC? companion = CompanionNpc;
        if (companion != null)
        {
            companion.Center = new Vector2((followTestCenterX + companionOffset) * 16f + 8f,
                followTestFloorY * 16f - companion.height / 2f);
            companion.velocity = Vector2.Zero;
        }

        Executor.Apply(new AIPlan { Action = AIActionType.Follow });
    }

    private void PlaceFollowTestWall(int height)
    {
        ClearFollowTestWall();
        for (int y = followTestFloorY - height; y <= followTestFloorY - 1; y++)
            WorldGen.PlaceTile(followTestCenterX, y, TileID.Stone, true, true);

        var tiles = new List<string>();
        for (int y = followTestFloorY - 6; y <= followTestFloorY; y++)
        {
            Tile tile = Main.tile[followTestCenterX, y];
            tiles.Add($"{y}:{(tile.HasTile ? tile.TileType.ToString() : "-")}");
        }

        TestLog($"follow wall placed h={height} x={followTestCenterX} tiles={string.Join(",", tiles)}");
    }

    private void ClearFollowTestWall()
    {
        for (int y = followTestFloorY - 10; y <= followTestFloorY - 1; y++)
            WorldGen.KillTile(followTestCenterX, y, false, false, false);
    }

    private float FollowTestDistance()
    {
        NPC? companion = CompanionNpc;
        Player player = Main.player[Main.myPlayer];
        return companion == null ? -1f : Vector2.Distance(companion.Center, player.Center) / 16f;
    }

    private void LogFollowTest(string phase)
    {
        NPC? companion = CompanionNpc;
        Player player = Main.player[Main.myPlayer];
        string npc = companion == null
            ? "none"
            : $"{(int)(companion.Center.X / 16f)},{(int)(companion.Center.Y / 16f)} v={companion.velocity.X:0.0}";
        TestLog($"follow {phase}: dist={FollowTestDistance():0.0} npc={npc} " +
                $"player={(int)(player.Center.X / 16f)},{(int)(player.Center.Y / 16f)} " +
                $"action={Executor.CurrentAction} status={StatusText()}");
    }

    private async Task RunDefendTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("defend test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            ClearLoadout();
            DebugArena(player, 35);
            ClearEnemies();
            Loadout.Inventory[0].SetDefaults(ItemID.IronBroadsword);

            NPC? companion = CompanionNpc;
            if (companion != null)
            {
                companion.Center = player.Center + new Vector2(-5f * 16f, 0f);
                companion.velocity = Vector2.Zero;
            }

            player.statLife = player.statLifeMax2;
            Executor.Apply(new AIPlan { Action = AIActionType.Follow });
            EnsureDefendSlime(player);
            TestLog($"defend setup auto={Executor.IsAutoDefending} hold={Executor.HoldPosition} action={Executor.CurrentAction} slime={DefendSlimeHp()}");
        });

        await Task.Delay(1000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            player.statLife = player.statLifeMax2;
            player.Hurt(PlayerDeathReason.ByNPC(defendTestSlime), 5, 0);
            TestLog($"defend hurt1 auto={Executor.IsAutoDefending} action={Executor.CurrentAction} hp={player.statLife}");
        });

        await Task.Delay(6000);
        Enqueue(() => TestLog($"defend result1 auto={Executor.IsAutoDefending} hold={Executor.HoldPosition} action={Executor.CurrentAction} slime={DefendSlimeHp()} tasks={TaskText()}"));

        Enqueue(() =>
        {
            ClearEnemies();
            TestLog("defend threat cleared");
        });
        await Task.Delay(3500);
        Enqueue(() => TestLog($"defend endResult auto={Executor.IsAutoDefending} hold={Executor.HoldPosition} action={Executor.CurrentAction} tasks={TaskText()}"));

        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            EnsureDefendSlime(player);
            Executor.SetHold(true);
            TestLog($"defend hold setup hold={Executor.HoldPosition} auto={Executor.IsAutoDefending} action={Executor.CurrentAction} slime={DefendSlimeHp()}");
        });

        await Task.Delay(1000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            player.statLife = player.statLifeMax2;
            player.Hurt(PlayerDeathReason.ByNPC(defendTestSlime), 5, 0);
            TestLog($"defend hurt2 hold auto={Executor.IsAutoDefending} hold={Executor.HoldPosition} action={Executor.CurrentAction} slime={DefendSlimeHp()}");
        });

        await Task.Delay(4000);
        Enqueue(() => TestLog($"defend holdResult auto={Executor.IsAutoDefending} hold={Executor.HoldPosition} action={Executor.CurrentAction} slime={DefendSlimeHp()}"));

        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            Executor.SetHold(false);
            Executor.Apply(new AIPlan { Action = AIActionType.Follow });
            EnsureDefendSlime(player);
            player.statLife = player.statLifeMax2;
            player.Hurt(PlayerDeathReason.ByNPC(defendTestSlime), 5, 0);
            TestLog($"defend hurt3 auto={Executor.IsAutoDefending} hold={Executor.HoldPosition} action={Executor.CurrentAction} slime={DefendSlimeHp()}");
        });

        await Task.Delay(5000);
        Enqueue(() =>
        {
            TestLog($"defend result3 auto={Executor.IsAutoDefending} hold={Executor.HoldPosition} action={Executor.CurrentAction} slime={DefendSlimeHp()} tasks={TaskText()}");
            ClearEnemies();
            Executor.Clear();
            defendTestSlime = -1;
        });

        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            player.statLife = player.statLifeMax2;
            Executor.SetHold(false);
            Executor.Apply(new AIPlan { Action = AIActionType.Follow });
            TestLog($"defend llm hold request start hold={Executor.HoldPosition} action={Executor.CurrentAction}");
        });
        await Task.Delay(500);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "别动，站在原地，别护主"));
        await Task.Delay(5000);
        Enqueue(() => TestLog($"defend llm hold: hold={Executor.HoldPosition} action={Executor.CurrentAction} tasks={TaskText()}"));

        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "跟着我"));
        await Task.Delay(5000);
        Enqueue(() => TestLog($"defend llm unhold: hold={Executor.HoldPosition} action={Executor.CurrentAction} tasks={TaskText()}"));

        Enqueue(() =>
        {
            Executor.Clear();
            TestLog("defend test done");
        });
    }

    private void EnsureDefendSlime(Player player)
    {
        if (defendTestSlime >= 0 && defendTestSlime < Main.maxNPCs)
        {
            NPC existing = Main.npc[defendTestSlime];
            if (existing.active && existing.life > 0)
                return;
        }

        defendTestSlime = DebugSpawnEnemyAt(player.Center + new Vector2(8f * 16f, 0f), NPCID.BlueSlime, 8000);
        TestLog($"defend slime spawned index={defendTestSlime}");
    }

    private string DefendSlimeHp()
    {
        if (defendTestSlime < 0 || defendTestSlime >= Main.maxNPCs)
            return "none";

        NPC npc = Main.npc[defendTestSlime];
        return npc.active ? $"{npc.life}/{npc.lifeMax}" : "dead";
    }

    private async Task RunGoalTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("goal test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            ClearLoadout();
            DebugArena(player, 30);
            Loadout.EnsureStarterTools();

            NPC? companion = CompanionNpc;
            if (companion != null)
            {
                int x = (int)(companion.Center.X / 16f) + 4;
                int floorY = (int)(companion.Bottom.Y / 16f);
                for (int i = 1; i <= 3; i++)
                    WorldGen.PlaceTile(x, floorY - i, TileID.Copper, true, true);
                TestLog($"goal test copper x={x} floor={floorY}");
            }

            ApplyLocalTask(new AIPlan
            {
                Reply = "收到，我去挖铜矿。",
                Goal = "挖 3 个铜矿",
                Action = AIActionType.Mine,
                Target = "copper",
                Count = 3
            });
            TestLog($"goal test mining start goal={ActiveGoal} pick={Loadout.GetBestPickaxe()?.Name}");
        });

        await Task.Delay(18000);
        Enqueue(() => TestLog(
            $"goal test after mine: goal={ActiveGoal} blocked={GoalBlocked} last={LastGoalResult} " +
            $"action={Executor.CurrentAction} inv={Loadout.Summary()} tasks={TaskText()}"));

        Enqueue(() =>
        {
            ApplyPlan(new AIPlan { Reply = "先重置目标。", GoalDone = true }, "（测试）");
            Item? pickaxe = Loadout.GetBestPickaxe();
            if (pickaxe != null)
                Loadout.RemoveItem(pickaxe.type, 1);
            ApplyPlan(new AIPlan
            {
                Reply = "我再试试挖铁矿。",
                Goal = "挖 2 个铁矿",
                Action = AIActionType.Mine,
                Target = "iron",
                Count = 2
            }, "（测试）");
            TestLog($"goal test blocked start goal={ActiveGoal} pick={Loadout.HasPickaxe}");
        });

        await Task.Delay(7000);
        Enqueue(() => TestLog(
            $"goal test blocked result: goal={ActiveGoal} blocked={GoalBlocked} last={LastGoalResult} " +
            $"action={Executor.CurrentAction} tasks={TaskText()}"));

        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            ApplyPlan(new AIPlan { Reply = "测试下一阶段。", GoalDone = true }, "（测试）");
            Executor.Clear();
            ClearLoadout();
            Loadout.EnsureStarterTools();
            DebugArena(player, 30);

            NPC? companion = CompanionNpc;
            if (companion != null)
            {
                int x = (int)(companion.Center.X / 16f) + 4;
                int floorY = (int)(companion.Bottom.Y / 16f);
                for (int i = 1; i <= 3; i++)
                    WorldGen.PlaceTile(x, floorY - i, TileID.Copper, true, true);
            }

            TestLog("goalchat request: 去挖 3 个铜矿");
            HandlePlayerMessage(player, "去挖 3 个铜矿");
        });

        await Task.Delay(6000);
        Enqueue(() => TestLog($"goalchat plan: goal={ActiveGoal} action={Executor.CurrentAction} tasks={TaskText()}"));

        await Task.Delay(20000);
        Enqueue(() => TestLog(
            $"goalchat result: goal={ActiveGoal} blocked={GoalBlocked} last={LastGoalResult} " +
            $"action={Executor.CurrentAction} inv={Loadout.Summary()} tasks={TaskText()}"));

        Enqueue(() =>
        {
            ApplyPlan(new AIPlan { Reply = "测试结束。", GoalDone = true }, "（测试）");
            Executor.Clear();
            TestLog("goal test done");
        });
    }

    private async Task RunCombatBrainTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("combatbrain test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            ClearLoadout();
            DebugArena(player);

            Loadout.Inventory[0].SetDefaults(ItemID.IronBroadsword);
            Loadout.Inventory[1].SetDefaults(ItemID.WoodenBow);
            Loadout.Inventory[2].SetDefaults(ItemID.WoodenArrow);
            Loadout.Inventory[2].stack = 40;
            Loadout.Inventory[3].SetDefaults(ItemID.HealingPotion);
            Loadout.Inventory[3].stack = 3;
            Loadout.PreferredWeaponType = ItemID.WoodenBow;

            NPC? companion = CompanionNpc;
            if (companion != null)
            {
                companion.life = 50;
                DebugSpawnEnemyAt(companion.Center + new Vector2(4f * 16f, 0f), NPCID.BlueSlime, 800);
            }

            TestLog($"combatbrain setup hp={companion?.life} weapons={Loadout.Summary()}");
        });

        await Task.Delay(1000);
        Enqueue(() => ApplyLocalTask(new AIPlan { Action = AIActionType.Attack }));
        await Task.Delay(15000);
        Enqueue(() =>
        {
            NPC? companion = CompanionNpc;
            TestLog($"combatbrain result: decision={LastCombatDecision} interval={LastCombatIntervalMs} latency={combatLatencyMs} action={Executor.CurrentAction} hp={companion?.life} potions={Loadout.CountItem(ItemID.HealingPotion)} ammo={Loadout.CountItem(ItemID.WoodenArrow)} status={StatusText()}");
        });
        Enqueue(() =>
        {
            ClearEnemies();
            foreach (Item item in Loadout.Inventory)
            {
                if (item.type == ItemID.HealingPotion)
                    item.SetDefaults(ItemID.None);
            }

            NPC? companion = CompanionNpc;
            if (companion != null)
            {
                companion.life = 20;
                DebugSpawnEnemyAt(companion.Center + new Vector2(3f * 16f, 0f), NPCID.BlueSlime, 800);
                DebugSpawnEnemyAt(companion.Center + new Vector2(4f * 16f, -16f), NPCID.BlueSlime, 800);
                DebugSpawnEnemyAt(companion.Center + new Vector2(-3f * 16f, 0f), NPCID.BlueSlime, 800);
            }

            ApplyLocalTask(new AIPlan { Action = AIActionType.Attack });
            TestLog($"combatbrain phase2 setup hp={companion?.life} potions={Loadout.CountItem(ItemID.HealingPotion)}");
        });
        await Task.Delay(12000);
        Enqueue(() => TestLog($"combatbrain phase2 result: decision={LastCombatDecision} action={Executor.CurrentAction} hp={CompanionNpc?.life}"));
        Enqueue(() =>
        {
            CombatPlannerEnabled = false;
            Executor.Clear();
        });
        Enqueue(() => TestLog("combatbrain test done"));
    }

    private async Task RunFeatureSuiteAsync()
    {
        await RunCombatTestAsync();
        await RunMiningTestAsync();
        await RunCraftTestAsync();
        await RunChestTestAsync();
        TestLog("feature suite done");
    }

    private async Task RunDynamicTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("dynamic test start");
            SpawnCompanion(player);
            DebugOre(player);
        });

        await Task.Delay(2000);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "小星，挖 3 个铁矿，然后跟着我"));
        await Task.Delay(6000);
        Enqueue(() => TestLog($"dynamic after add: tasks={TaskText()} | status={StatusText()}"));

        Enqueue(() =>
        {
            DebugSpawnEnemy(Main.player[Main.myPlayer]);
            HandlePlayerMessage(Main.player[Main.myPlayer], "先别挖了，来帮我打那只史莱姆");
        });
        await Task.Delay(6000);
        Enqueue(() => TestLog($"dynamic after replace: tasks={TaskText()} | status={StatusText()}"));

        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "现在继续跟着我"));
        await Task.Delay(6000);
        Enqueue(() => TestLog($"dynamic after follow: tasks={TaskText()} | status={StatusText()}"));

        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "继续"));
        await Task.Delay(5000);
        Enqueue(() => TestLog($"dynamic after continue: tasks={TaskText()} | status={StatusText()}"));
        Enqueue(() => TestLog("dynamic test done"));
    }

    private async Task RunCombatTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("combat test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            ClearLoadout();
            DebugArena(player);
            Loadout.Inventory[0].SetDefaults(ItemID.IronBroadsword);
            Loadout.PreferredWeaponType = ItemID.IronBroadsword;

            NPC? companion = CompanionNpc;
            if (companion != null)
                DebugSpawnEnemyAt(companion.Center + new Vector2(3f * 16f, 0f), NPCID.BlueSlime, 500);

            TestLog($"combat melee setup weapon={Loadout.GetBestWeapon()?.Name} range={CompanionWeapons.AttackRange(Loadout)}");
        });

        await Task.Delay(1200);
        Enqueue(() =>
        {
            TestLog($"combat before melee: slimes={CountSlimes()} animation={Executor.Animation.Active} kind={Executor.Animation.Kind} visual={Executor.VisualItem?.Name}");
            ApplyLocalTask(new AIPlan { Action = AIActionType.Attack });
        });
        await Task.Delay(3000);
        Enqueue(() =>
        {
            NPC? slime = NearestSlime();
            TestLog($"combat melee result: status={StatusText()} slimeLife={slime?.life} animation={Executor.Animation.Active} kind={Executor.Animation.Kind} visual={Executor.VisualItem?.Name}");
        });

        Enqueue(() =>
        {
            ClearEnemies();
            Loadout.Inventory[1].SetDefaults(ItemID.WoodenBow);
            Loadout.Inventory[2].SetDefaults(ItemID.WoodenArrow);
            Loadout.Inventory[2].stack = 40;
            Loadout.PreferredWeaponType = ItemID.WoodenBow;
            NPC? companion = CompanionNpc;
            if (companion != null)
                DebugSpawnEnemyAt(companion.Center + new Vector2(10f * 16f, 0f), NPCID.BlueSlime, 800);

            TestLog($"combat ranged setup ammo={Loadout.CountItem(ItemID.WoodenArrow)} weapon={Loadout.GetBestWeapon()?.Name} range={CompanionWeapons.AttackRange(Loadout)}");
        });

        await Task.Delay(1000);
        Enqueue(() => ApplyLocalTask(new AIPlan { Action = AIActionType.Attack }));
        await Task.Delay(5000);
        Enqueue(() =>
        {
            NPC? slime = NearestSlime();
            int arrows = Main.projectile.Count(p => p.active && p.type == ProjectileID.WoodenArrowFriendly);
            TestLog($"combat ranged result: status={StatusText()} ammo={Loadout.CountItem(ItemID.WoodenArrow)} arrows={arrows} slimeLife={slime?.life} animation={Executor.Animation.Active} kind={Executor.Animation.Kind} visual={Executor.VisualItem?.Name}");
        });
        Enqueue(() => TestLog("combat test done"));
    }

    private async Task RunMiningTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("mining test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            ClearLoadout();
            DebugArena(player);
            bool starter = Loadout.EnsureStarterTools();
            TestLog($"mining starter tools granted={starter} pick={Loadout.GetBestPickaxe()?.Name} axe={Loadout.HasAxe} weapon={Loadout.GetBestWeapon()?.Name}");

            NPC? companion = CompanionNpc;
            if (companion != null)
            {
                int x = (int)(companion.Center.X / 16f) + 4;
                int floorY = (int)(companion.Bottom.Y / 16f);
                for (int i = 1; i <= 3; i++)
                    WorldGen.PlaceTile(x, floorY - i, TileID.Iron, true, true);

                TestLog($"mining setup x={x} floorY={floorY} pick={Loadout.GetBestPickaxe()?.pick}");
            }
        });

        await Task.Delay(1000);
        Enqueue(() => ApplyLocalTask(new AIPlan
        {
            Action = AIActionType.Mine,
            Target = "iron",
            Count = 3
        }));
        await Task.Delay(20000);
        Enqueue(() =>
        {
            NPC? companion = CompanionNpc;
            int tilesLeft = 0;
            if (companion != null)
            {
                int x = (int)(companion.Center.X / 16f) + 4;
                int floorY = (int)(companion.Bottom.Y / 16f);
                for (int i = 1; i <= 3; i++)
                {
                    if (IsIronTile(x, floorY - i))
                        tilesLeft++;
                }
            }

            TestLog($"mining result: status={StatusText()} ironOre={Loadout.CountItem(ItemID.IronOre)} tilesLeft={tilesLeft} pick={Loadout.GetBestPickaxe()?.Name} animation={Executor.Animation.Active} kind={Executor.Animation.Kind}");
        });

        Enqueue(() =>
        {
            Item? pickaxe = Loadout.GetBestPickaxe();
            if (pickaxe != null)
                Loadout.RemoveItem(pickaxe.type, 1);
            ApplyLocalTask(new AIPlan { Action = AIActionType.Mine, Target = "iron", Count = 1 });
        });
        await Task.Delay(2500);
        Enqueue(() => TestLog($"mining no-pickaxe result: blocked={Executor.BlockedPlan != null} reason={Executor.BlockedReason} tasks={TaskText()}"));
        Enqueue(() => TestLog("mining test done"));
    }

    private async Task RunCraftTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("craft test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            ClearLoadout();
            DebugArena(player);
            Loadout.AddItem(ItemID.Wood, 20);

            NPC? companion = CompanionNpc;
            if (companion != null)
            {
                int x = (int)(companion.Center.X / 16f) + 3;
                int floorY = (int)(companion.Bottom.Y / 16f);
                WorldGen.PlaceTile(x, floorY - 1, TileID.WorkBenches, true, true);
                companion.Center = new Vector2((x - 1) * 16f + 8f, floorY * 16f - companion.height / 2f);
                companion.velocity = Vector2.Zero;
                TestLog($"craft setup workbench={HasWorkbenchNear(companion)} wood={Loadout.CountItem(ItemID.Wood)}");
            }
        });

        await Task.Delay(1000);
        Enqueue(() => ApplyLocalTask(new AIPlan
        {
            Action = AIActionType.Craft,
            Item = ItemID.WoodenSword.ToString(),
            Count = 1
        }));
        await Task.Delay(15000);
        Enqueue(() => TestLog(
            $"craft result: status={StatusText()} wood={Loadout.CountItem(ItemID.Wood)} sword={Loadout.CountItem(ItemID.WoodenSword)} inv={Loadout.Summary()}"));
        Enqueue(() => TestLog("craft test done"));
    }

    private async Task RunChestTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("chest test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            ClearLoadout();
            DebugArena(player);

            NPC? companion = CompanionNpc;
            if (companion != null)
            {
                int x = (int)(companion.Center.X / 16f) + 4;
                int floorY = (int)(companion.Bottom.Y / 16f);
                int chestIndex = WorldGen.PlaceChest(x, floorY - 1, TileID.Containers, false, 0);
                testChestIndex = chestIndex;
                if (chestIndex >= 0 && chestIndex < Main.chest.Length)
                {
                    Chest chest = Main.chest[chestIndex];
                    chest.item[0].SetDefaults(ItemID.IronOre);
                    chest.item[0].stack = 10;
                    companion.Center = new Vector2((x - 2) * 16f + 8f, floorY * 16f - companion.height / 2f);
                    companion.velocity = Vector2.Zero;
                    TestLog($"chest setup index={chestIndex} at={chest.x},{chest.y} iron={ChestIron(chest)}");
                }
                else
                {
                    TestLog($"chest setup failed index={chestIndex}");
                }
            }
        });

        await Task.Delay(1000);
        Enqueue(() => ApplyLocalTask(new AIPlan
        {
            Action = AIActionType.Withdraw,
            Item = ItemID.IronOre.ToString(),
            Count = 4
        }));
        await Task.Delay(10000);
        Enqueue(() =>
        {
            Chest? chest = FindTestChest();
            TestLog($"chest withdraw result: status={StatusText()} invIron={Loadout.CountItem(ItemID.IronOre)} chestIron={ChestIron(chest)}");
        });

        Enqueue(() => ApplyLocalTask(new AIPlan
        {
            Action = AIActionType.Deposit,
            Item = ItemID.IronOre.ToString(),
            Count = 2
        }));
        await Task.Delay(10000);
        Enqueue(() =>
        {
            Chest? chest = FindTestChest();
            TestLog($"chest deposit result: status={StatusText()} invIron={Loadout.CountItem(ItemID.IronOre)} chestIron={ChestIron(chest)}");
        });
        Enqueue(() => TestLog("chest test done"));
    }

    private void ClearLoadout()
    {
        foreach (Item item in Loadout.Inventory)
            item.SetDefaults(ItemID.None);
        foreach (Item item in Loadout.Armor)
            item.SetDefaults(ItemID.None);
        foreach (Item item in Loadout.Accessories)
            item.SetDefaults(ItemID.None);

        Loadout.PreferredWeaponType = ItemID.None;
    }

    public void DebugArena(Player player, int halfWidth = 20)
    {
        PrepareTestWorld();

        int px = (int)(player.Center.X / 16f);
        int py = (int)(player.Center.Y / 16f);
        int floorY = py + 3;

        for (int x = px - halfWidth; x <= px + halfWidth; x++)
        {
            for (int y = floorY - 8; y <= floorY - 1; y++)
            {
                WorldGen.KillTile(x, y, false, false, false);
                WorldGen.KillWall(x, y, false);
            }

            WorldGen.PlaceTile(x, floorY, TileID.Stone, true, true);
        }

        player.position = new Vector2(px * 16f - player.width / 2f, floorY * 16f - player.height);
        player.velocity = Vector2.Zero;

        NPC? companion = CompanionNpc;
        if (companion != null)
        {
            companion.Center = new Vector2((px + 2) * 16f + 8f, floorY * 16f - companion.height / 2f);
            companion.velocity = Vector2.Zero;
        }

        TestLog($"arena prepared px={px} floorY={floorY} companion={(companion != null)}");
    }

    private void ClearEnemies()
    {
        foreach (NPC npc in Main.npc)
        {
            if (npc.active && !npc.friendly && npc.type != NPCID.Guide)
                npc.active = false;
        }
    }

    public int DebugSpawnEnemyAt(Vector2 center, int type = NPCID.BlueSlime, int hp = 500)
    {
        int index = NPC.NewNPC(new EntitySource_SpawnNPC(), (int)center.X, (int)center.Y, type);
        if (index < 0 || index >= Main.maxNPCs)
            return index;

        NPC npc = Main.npc[index];
        npc.Center = center;
        npc.velocity = Vector2.Zero;
        npc.lifeMax = hp;
        npc.life = hp;
        npc.damage = 0;
        return index;
    }

    private static int CountSlimes() =>
        Main.npc.Count(n => n.active && n.type == NPCID.BlueSlime);

    private static NPC? NearestSlime() =>
        Main.npc.Where(n => n.active && n.type == NPCID.BlueSlime)
            .OrderByDescending(n => n.life)
            .FirstOrDefault();

    private static bool IsIronTile(int x, int y) =>
        WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile && Main.tile[x, y].TileType == TileID.Iron;

    private static bool HasWorkbenchNear(NPC npc)
    {
        int centerX = (int)(npc.Center.X / 16f);
        int centerY = (int)(npc.Center.Y / 16f);

        for (int x = centerX - 5; x <= centerX + 5; x++)
        {
            for (int y = centerY - 4; y <= centerY + 4; y++)
            {
                if (WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile &&
                    Main.tile[x, y].TileType == TileID.WorkBenches)
                    return true;
            }
        }

        return false;
    }

    private Chest? FindTestChest()
    {
        if (testChestIndex < 0 || testChestIndex >= Main.chest.Length)
            return null;

        return Main.chest[testChestIndex];
    }

    private static int ChestIron(Chest? chest)
    {
        if (chest == null)
            return 0;

        return chest.item.Where(item => !item.IsAir && item.type == ItemID.IronOre).Sum(item => item.stack);
    }

    private async Task RunMultiTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("multi test start");
            SpawnCompanion(player);
            DebugCompanionGear();
            Loadout.Inventory[4].SetDefaults(ItemID.HealingPotion);
            Loadout.Inventory[5].SetDefaults(ItemID.Wood);
            Loadout.Inventory[5].stack = 20;
            Loadout.Inventory[6].SetDefaults(ItemID.GoldHelmet);
            NPC? npc = CompanionNpc;
            if (npc != null)
                npc.life = 50;
            Item.NewItem(new EntitySource_Misc("AITeammateTest"), player.Center + new Vector2(80f, 0f), ItemID.GoldCoin, 5);
        });

        await Task.Delay(2000);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer],
            "小星，把地上的金币捡起来，喝一瓶治疗药水，把金头盔戴上，再把木材丢掉"));
        await Task.Delay(35000);
        Enqueue(() => TestLog($"multi final: summary={Loadout.Summary()} | life={CompanionNpc?.life} | equipment={Loadout.EquipmentSummary()} | groundWood={Main.item.Any(i => i.active && i.type == ItemID.Wood)} | status={StatusText()}"));
        Enqueue(() => TestLog("multi test done"));
    }

    private async Task RunChatTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("chat test start");
            SpawnCompanion(player);
            DebugCompanionGear();
            Item.NewItem(new EntitySource_Misc("AITeammateTest"), player.Center + new Vector2(80f, 0f), ItemID.GoldCoin, 5);
        });

        await Task.Delay(1000);
        Enqueue(() => Main.chatMonitor.NewText("小星，今天过得怎么样？", 255));
        await Task.Delay(12000);
        Enqueue(() => TestLog($"chat casual: {LastReply}"));

        Enqueue(() => Main.chatMonitor.NewText("小星，把地上的金币捡起来", 255));
        await Task.Delay(12000);
        Enqueue(() => TestLog($"chat command: {LastReply} | summary={Loadout.Summary()}"));

        Enqueue(() => Main.chatMonitor.NewText("小星，跟着我", 255));
        await Task.Delay(10000);
        Enqueue(() => TestLog($"chat follow: {LastReply} | task={Executor.StatusText()}"));

        Enqueue(() => Main.chatMonitor.NewText("小星，你周围有什么资源？", 255));
        await Task.Delay(12000);
        Enqueue(() => TestLog($"chat perception: {LastReply}"));
        Enqueue(() => TestLog("chat test done"));
    }

    private async Task RunActionTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("actions test start");
            SpawnCompanion(player);
            DebugCompanionGear();
            Loadout.Inventory[4].SetDefaults(ItemID.HealingPotion);
            Loadout.Inventory[5].SetDefaults(ItemID.Wood);
            Loadout.Inventory[5].stack = 20;
            Loadout.Inventory[6].SetDefaults(ItemID.GoldHelmet);
            NPC? npc = CompanionNpc;
            if (npc != null)
                npc.life = 50;
            Item.NewItem(new EntitySource_Misc("AITeammateTest"), player.Center + new Vector2(80f, 0f), ItemID.GoldCoin, 5);
            TestLog($"actions setup summary={Loadout.Summary()} life={CompanionNpc?.life} defense={Loadout.Defense}");
        });

        await Task.Delay(2000);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "小星，把地上的金币捡起来"));
        await Task.Delay(12000);
        Enqueue(() => TestLog($"after pickup: summary={Loadout.Summary()} life={CompanionNpc?.life}"));

        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "喝一瓶治疗药水"));
        await Task.Delay(12000);
        Enqueue(() => TestLog($"after use: summary={Loadout.Summary()} life={CompanionNpc?.life}"));

        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "把金头盔戴上"));
        await Task.Delay(12000);
        Enqueue(() => TestLog($"after equip: equipment={Loadout.EquipmentSummary()} defense={Loadout.Defense}"));

        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "把木材丢掉"));
        await Task.Delay(12000);
        Enqueue(() => TestLog($"after drop: summary={Loadout.Summary()} groundWood={Main.item.Any(i => i.active && i.type == ItemID.Wood)}"));
        Enqueue(() => TestLog("actions test done"));
    }

    private async Task RunInventoryTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("inventory test start");
            SpawnCompanion(player);
            DebugCompanionGear();
            ToggleInventory();
            TestLog($"inventory ui={InventoryUI.Visible} defense={Loadout.Defense} weapon={Loadout.WeaponDamage} summary={Loadout.Summary()}");
        });

        await Task.Delay(20000);
        Enqueue(() => TestLog($"inventory after: defense={Loadout.Defense} weapon={Loadout.WeaponDamage} summary={Loadout.Summary()}"));
        Enqueue(() => ToggleInventory());
        Enqueue(() => TestLog("inventory test done"));
    }

    public override void UpdateUI(GameTime gameTime)
    {
        UpdateCompanionLauncher(gameTime);
        InventoryUI.Update();
    }

    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        AddCompanionLauncherLayer(layers);
        layers.Add(new LegacyGameInterfaceLayer("TerrariaAITeammate: Companion Inventory",
            () =>
            {
                if (InventoryUI.Visible)
                    InventoryUI.Draw(Main.spriteBatch);
                return true;
            },
            InterfaceScaleType.None));
    }

    public override void SaveWorldData(TagCompound tag)
    {
        Loadout.Save(tag);
        SaveAgentGoal(tag);
    }

    public override void LoadWorldData(TagCompound tag)
    {
        Loadout.Load(tag);
        if (Loadout.EnsureStarterTools())
            TestLog("starter tools granted: " + Loadout.Summary());
        LoadAgentGoal(tag);
    }

    public void ToggleInventory() => InventoryUI.Toggle();

    public void DebugCompanionGear()
    {
        Loadout.Inventory[0].SetDefaults(ItemID.IronBroadsword);
        Loadout.Armor[0].SetDefaults(ItemID.IronHelmet);
        Loadout.Armor[1].SetDefaults(ItemID.IronChainmail);
        Loadout.Armor[2].SetDefaults(ItemID.IronGreaves);
        Loadout.Accessories[0].SetDefaults(ItemID.Shackle);
        TestLog($"companion gear: defense={Loadout.Defense} weapon={Loadout.WeaponDamage}");
    }

    private async Task RunDoorTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("door test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            DebugDoorScenario(player);
        });

        await Task.Delay(1500);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "跟着我，穿过那扇门"));
        await Task.Delay(22000);
        Enqueue(() =>
        {
            NPC? npc = CompanionNpc;
            bool passed = npc != null && npc.Center.X > doorTestX * 16f;
            TestLog($"door result: {StatusText()} | doorOpen={IsDoorOpen(doorTestX, doorTestY)} | passed={passed}");
        });
        Enqueue(() => TestLog("door test done"));
    }

    private async Task RunExploreTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("explore test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            PrepareTestWorld();
            TeleportToSpawn(player);
            NPC? npc = CompanionNpc;
            if (npc != null)
            {
                npc.Center = player.Center + new Vector2(-80f, -32f);
                npc.velocity = Vector2.Zero;
            }
            DebugOre(player);
        });

        await Task.Delay(2000);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "去周围探索一下，看看有什么"));
        await Task.Delay(32000);
        Enqueue(() => TestLog($"explore result: {StatusText()} | facts={Memory.FactsSnapshot().Replace("\n", " | ")}"));

        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "你刚才探索到了什么？"));
        await Task.Delay(14000);
        Enqueue(() => TestLog($"explore recall: {LastReply}"));

        Enqueue(() =>
        {
            PrepareTestWorld();
            SetAutoMode(true);
        });
        await Task.Delay(30000);
        Enqueue(() => TestLog($"auto explore: lastAuto={LastAutoPlan} | status={StatusText()}"));
        Enqueue(() => TestLog("explore test done"));
    }

    private void TeleportToSpawn(Player player)
    {
        int sx = Main.spawnTileX;
        int sy = Main.spawnTileY;
        int groundY = sy;

        for (int y = sy - 5; y <= sy + 20; y++)
        {
            if (!WorldGen.InWorld(sx, y, 3) || !Main.tile[sx, y].HasTile || !Main.tileSolid[Main.tile[sx, y].TileType])
                continue;

            groundY = y;
            break;
        }

        for (int x = sx - 1; x <= sx + 1; x++)
        {
            for (int y = groundY - 4; y <= groundY - 1; y++)
                WorldGen.KillTile(x, y, false, false, false);
        }

        player.position = new Vector2(sx * 16 - player.width / 2f, groundY * 16 - player.height);
        player.velocity = Vector2.Zero;
        TestLog($"teleported to spawn {sx},{groundY}");
    }

    private async Task RunFinalTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("final test start");
            SpawnCompanion(player);
            SetAutoMode(false);
            DebugDoorScenario(player);
        });

        await Task.Delay(1500);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "跟着我，穿过那扇门"));
        await Task.Delay(18000);
        Enqueue(() => TestLog($"door test: {StatusText()} | doorOpenAtEnd={IsDoorOpen(doorTestX, doorTestY)}"));

        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "记住：我叫老张，我家在森林左边"));
        await Task.Delay(12000);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "我叫什么？我家在哪？"));
        await Task.Delay(14000);
        Enqueue(() => TestLog($"memory: reply={LastReply} | facts={Memory.FactsSnapshot().Replace("\n", " | ")}"));

        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "去周围探索一下，看看有什么"));
        await Task.Delay(30000);
        Enqueue(() => TestLog($"explore: {StatusText()} | facts={Memory.FactsSnapshot().Replace("\n", " | ")}"));

        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "你刚才探索到了什么？"));
        await Task.Delay(14000);
        Enqueue(() => TestLog($"explore recall: reply={LastReply}"));

        Enqueue(() => DebugSpawnEnemy(Main.player[Main.myPlayer]));
        await Task.Delay(1000);
        Enqueue(() => SetAutoMode(true));
        await Task.Delay(30000);
        Enqueue(() =>
        {
            int slimes = Main.npc.Count(n => n.active && n.type == NPCID.BlueSlime);
            TestLog($"auto: {StatusText()} | slimes={slimes} | lastAuto={LastAutoPlan}");
        });
        Enqueue(() => TestLog("final test done"));
    }

    public void DebugDoorScenario(Player player)
    {
        int px = (int)(player.Center.X / 16f);
        int py = (int)(player.Center.Y / 16f);
        int doorX = px + 14;
        int groundY = py + 3;
        int halfWidth = 12;
        doorTestX = doorX;
        doorTestY = groundY;

        PrepareTestWorld();

        for (int x = doorX - halfWidth; x <= doorX + halfWidth; x++)
        {
            for (int y = groundY - 9; y <= groundY; y++)
            {
                WorldGen.KillTile(x, y, false, false, false);
                WorldGen.KillWall(x, y, false);
            }

            WorldGen.PlaceTile(x, groundY + 1, TileID.Stone, true, true);
            WorldGen.PlaceTile(x, groundY - 8, TileID.Stone, true, true);
        }

        for (int y = groundY - 7; y <= groundY; y++)
        {
            WorldGen.PlaceTile(doorX - halfWidth, y, TileID.Stone, true, true);
            WorldGen.PlaceTile(doorX + halfWidth, y, TileID.Stone, true, true);

            if (y < groundY - 2)
                WorldGen.PlaceTile(doorX, y, TileID.Stone, true, true);
        }

        WorldGen.PlaceTile(doorX, groundY, TileID.ClosedDoor, true, true);
        TestLog($"door tile initial hasTile={Main.tile[doorX, groundY].HasTile} type={Main.tile[doorX, groundY].TileType}");

        int playerX = doorX + 10;
        for (int x = playerX - 1; x <= playerX + 1; x++)
        {
            for (int y = groundY - 3; y <= groundY; y++)
                WorldGen.KillTile(x, y, false, false, false);
        }

        player.position = new Vector2(playerX * 16 - player.width / 2f, (groundY + 1) * 16 - player.height);
        player.velocity = Vector2.Zero;

        NPC? npc = CompanionNpc;
        if (npc != null)
        {
            npc.Center = new Vector2((doorX - 6) * 16 + 8, (groundY + 1) * 16 - npc.height / 2f);
            npc.velocity = Vector2.Zero;
        }

        TestLog($"door scenario doorX={doorX} groundY={groundY} playerX={playerX}");
    }

    private static void PrepareTestWorld()
    {
        Main.dayTime = true;
        Main.time = 0;
        Main.bloodMoon = false;
        Main.eclipse = false;

        foreach (NPC npc in Main.npc)
        {
            if (npc.active && !npc.friendly && npc.type != NPCID.Guide)
                npc.active = false;
        }
    }

    public bool IsDoorOpen(int x, int y)
    {
        return WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile && Main.tile[x, y].TileType == TileID.OpenDoor;
    }

    public override void OnWorldUnload()
    {
        StopTestRecording();
        WorldKnowledgeObserver.Clear();
        CloseCompanionPanel();
        InventoryUI.Close();
        BlueprintManager.ClearSelection();
        Memory.Save();
        ResetAgentRuntime();
    }

    private async Task RunBrainTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("brain test start");
            SpawnCompanion(player);
            SetAutoMode(false);
        });

        await Task.Delay(2000);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "记住：我叫老张，我家在森林左边"));
        await Task.Delay(12000);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "我叫什么？我家在哪？"));
        await Task.Delay(14000);
        Enqueue(() =>
        {
            bool pass = LastReply.Contains("老张") &&
                        (LastReply.Contains("森林") || LastReply.Contains("左边"));
            TestLog($"memory recall: {(pass ? "PASS" : "FAIL")} | reply={LastReply} | facts={Memory.FactsSnapshot().Replace("\n", " | ")}");
            DebugSpawnEnemy(Main.player[Main.myPlayer]);
        });

        await Task.Delay(1500);
        Enqueue(() => SetAutoMode(true));
        await Task.Delay(30000);
        Enqueue(() =>
        {
            int slimes = Main.npc.Count(n => n.active && n.type == NPCID.BlueSlime);
            TestLog($"auto: {StatusText()} | slimes={slimes} | lastAuto={LastAutoPlan}");
        });
        Enqueue(() => TestLog("brain test done"));
    }

    private async Task RunRecallTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("recall test start");
            SpawnCompanion(player);
            HandlePlayerMessage(player, "你还记得我叫什么、我家在哪吗？");
        });

        await Task.Delay(14000);
        Enqueue(() =>
        {
            bool pass = LastReply.Contains("老张") &&
                        (LastReply.Contains("森林") || LastReply.Contains("左边"));
            TestLog($"cross-session recall: {(pass ? "PASS" : "FAIL")} | reply={LastReply} | facts={Memory.FactsSnapshot().Replace("\n", " | ")}");
            TestLog("recall test done");
        });
    }

    private async Task RunBuildTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("buildtest start");
            SpawnCompanion(player);
            DebugKit(player);
            DebugPrepareBuildSite(player);
        });

        await Task.Delay(2000);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "建一个小屋"));
        await Task.Delay(40000);
        Enqueue(() => TestLog("after build: " + StatusText()));
        Enqueue(() => TestLog("buildtest done"));
    }

    private async Task RunAutoTestAsync()
    {
        await Task.Delay(5000);
        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            TestLog("autotest start");
            SpawnCompanion(player);
            TestLog($"companion={(CompanionNpc != null)}");
        });

        await Task.Delay(3000);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "跟着我，别乱跑"));
        await Task.Delay(9000);
        Enqueue(() => TestLog("after follow: " + StatusText()));

        Enqueue(() =>
        {
            Player player = Main.player[Main.myPlayer];
            DebugSpawnEnemy(player);
        });
        await Task.Delay(1500);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "打那只史莱姆"));
        await Task.Delay(14000);
        Enqueue(() =>
        {
            int slimes = Main.npc.Count(n => n.active && n.type == NPCID.BlueSlime);
            TestLog($"after attack: {StatusText()} | nearbySlimes={slimes}");
        });

        Enqueue(() => DebugOre(Main.player[Main.myPlayer]));
        await Task.Delay(1500);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "挖 3 个铁矿"));
        await Task.Delay(32000);
        Enqueue(() => TestLog($"after mine: {StatusText()} | inv={Executor.InventorySummary()}"));

        Enqueue(() => DebugKit(Main.player[Main.myPlayer]));
        await Task.Delay(1500);
        Enqueue(() => HandlePlayerMessage(Main.player[Main.myPlayer], "建一个小屋"));
        await Task.Delay(32000);
        Enqueue(() => TestLog($"after build: {StatusText()}"));
        Enqueue(() => TestLog("autotest done"));
    }

    public void TestLog(string message) => Mod.Logger.Info("[AITEST] " + message);

    public override void PostUpdateEverything()
    {
        if (!worldReady || Main.netMode != NetmodeID.SinglePlayer)
            return;
        WorldKnowledgeObserver.Tick();
        while (mainThreadQueue.TryDequeue(out Action? action))
            action();
        MaintainComplexAcceptanceFixture();
        TickHouseAcceptance();
        TickHousePlanningAcceptance();
        if (GoalResumeNativeSelected) TickGoalResumeNative();

        TickCombatDirector();
        TickAgentPlanning();
    }

    private void TickCombatDirector()
    {
        if (NaturalFlashSelected) return;
        if (Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") == "complex" && !ComplexAcceptanceRequestAllowed(true)) return;
        if (manualControlActive || !CombatPlannerEnabled || combatPlanInFlight || primaryCancellation != null || CompanionNpc == null || !Executor.IsInCombat)
            return;
        if (!ModContent.GetInstance<AIConfig>().UseCombatLlm)
            return;

        Player player = Main.player[Main.myPlayer];
        if (!player.active || player.dead)
            return;

        LlmSettings? settings = GetLlmSettings();
        if (settings == null)
            return;

        NPC? target = NearestHostile(CompanionNpc);
        if (target == null)
            return;

        int enemies = CountHostiles(CompanionNpc, 30f * 16f);
        int nearestTiles = (int)(Vector2.Distance(CompanionNpc.Center, target.Center) / 16f);
        bool anyBoss = target.boss || Main.npc.Any(n =>
            n.active && n.boss && Vector2.Distance(n.Center, CompanionNpc.Center) < 160f * 16f);
        int interval = ComputeCombatInterval(CompanionNpc, enemies, nearestTiles, anyBoss);
        LastCombatIntervalMs = interval;

        int hpBucket = CompanionNpc.life / 35;
        int distBucket = nearestTiles / 10;
        string signature = $"{hpBucket}/{enemies > 3}/{distBucket}/{(anyBoss ? "boss" : target.whoAmI.ToString())}";
        DateTime now = DateTime.UtcNow;
        double elapsed = (now - lastCombatPlanAt).TotalMilliseconds;
        bool changed = signature != lastCombatSignature && elapsed > 1800;
        int due = Math.Max(interval, combatLatencyMs + 350);

        if (!changed && elapsed < due)
            return;

        lastCombatSignature = signature;
        lastCombatPlanAt = now;
        int callId = ++combatCallId;
        string state = AIPlanner.BuildCombatState(player, CompanionNpc, Executor);
        bool debug = ModContent.GetInstance<AIConfig>().DebugLog;
        combatPlanInFlight = true;
        CombatDecisionRequestCount++;
        TestModelRequest? testRequest = BeginTestModelRequest("combat", callId);
        Action<LlmUsage>? testUsage = testRequest == null ? null : testRequest.CaptureUsage;
        var combatRequestCancellation = new System.Threading.CancellationTokenSource();
        combatCancellation = combatRequestCancellation;
        System.Threading.CancellationToken combatToken = combatRequestCancellation.Token;

        _ = Task.Run(async () =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                CombatDecision decision = await new AIPlanner()
                    .PlanCombatAsync(settings, state, debug, raw => Mod.Logger.Info(raw), cancellationToken: combatToken, onUsage: testUsage)
                    .ConfigureAwait(false);
                watch.Stop();

                Enqueue(() =>
                {
                    FinishTestModelRequest(testRequest);
                    if (!ReleaseCombatDecision(combatRequestCancellation, callId)) return;
                    combatLatencyMs = (int)watch.ElapsedMilliseconds;
                    lastCombatPlanAt = DateTime.UtcNow;
                    if (manualControlActive || Executor.HoldPosition || !Executor.IsInCombat)
                        return;

                    ApplyCombatDecision(decision);
                });
            }
            catch (OperationCanceledException) when (combatToken.IsCancellationRequested)
            {
                Enqueue(() =>
                {
                    FinishTestModelRequest(testRequest, cancelled: true);
                    ReleaseCombatDecision(combatRequestCancellation, callId);
                });
            }
            catch (Exception exception)
            {
                watch.Stop();
                Enqueue(() =>
                {
                    FinishTestModelRequest(testRequest, exception);
                    if (!ReleaseCombatDecision(combatRequestCancellation, callId)) return;
                    combatLatencyMs = (int)watch.ElapsedMilliseconds;
                    lastCombatPlanAt = DateTime.UtcNow;
                    Mod.Logger.Warn("[AITEST] combat plan failed: " + exception.Message);
                });
            }
        });
    }

    private static int ComputeCombatInterval(NPC companion, int enemies, int nearestTiles, bool boss)
    {
        int hpPercent = (int)(companion.life * 100f / Math.Max(1, companion.lifeMax));
        if (hpPercent <= 30 || nearestTiles <= 5)
            return 900;
        if (hpPercent <= 55 || nearestTiles <= 10 || enemies >= 4)
            return 1600;
        if (boss)
            return 2400;
        return 2200;
    }

    private static NPC? NearestHostile(NPC companion)
    {
        return Main.npc
            .Where(n => n.active && !n.friendly && n.life > 0 && n.CanBeChasedBy() &&
                        Vector2.Distance(n.Center, companion.Center) < (n.boss ? 160f * 16f : 45f * 16f))
            .OrderBy(n => Vector2.Distance(n.Center, companion.Center))
            .FirstOrDefault();
    }

    private static int CountHostiles(NPC companion, float range)
    {
        return Main.npc.Count(n => n.active && !n.friendly && n.life > 0 && n.CanBeChasedBy() &&
                                    Vector2.Distance(n.Center, companion.Center) < range);
    }

    private void ApplyCombatDecision(CombatDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.Action))
            return;

        LastCombatDecision = $"{decision.Action}|{decision.Weapon}|{decision.Item}|{decision.Reason}";
        Mod.Logger.Info("[AITEST] combat decision: " + LastCombatDecision);
        if (IsTestRecording)
            DiagnosticsRecorder.Record("combat_decision", new { action = decision.Action });

        if (decision.Action == "retreat" && Executor.CurrentAction == AIActionType.Retreat)
            return;

        switch (decision.Action)
        {
            case "fight":
                if (!string.IsNullOrWhiteSpace(decision.Weapon))
                    Executor.TrySwitchWeapon(decision.Weapon, out _);
                Executor.ResumeCombat();
                break;
            case "switch":
                if (!string.IsNullOrWhiteSpace(decision.Weapon) &&
                    !Executor.TrySwitchWeapon(decision.Weapon, out string switchMessage))
                    Mod.Logger.Info("[AITEST] combat switch failed: " + switchMessage);
                Executor.ResumeCombat();
                break;
            case "use":
                if (!string.IsNullOrWhiteSpace(decision.Item) &&
                    !Executor.TryUseCombatItem(decision.Item, out string useMessage))
                    Mod.Logger.Info("[AITEST] combat use failed: " + useMessage);
                break;
            case "retreat":
                if (!Executor.BeginRetreat(out string retreatMessage))
                    Mod.Logger.Info("[AITEST] combat retreat failed: " + retreatMessage);
                break;
        }
    }

    internal bool HandleChatMessage(string message)
    {
        if (!CanUseCompanionControls) return false;

        bool explicitResume = (restoredGoalAwaitingContinue || (Executor.HoldPosition && activeGoal.Length > 0)) &&
            GoalResumeCheckpoint.IsExplicitContinue(message);
        AIConfig config = ModContent.GetInstance<AIConfig>();
        string? body = PlayerChatInput.Route(message, config.ChatMode, explicitResume);
        if (body == null) return false;

        SendCompanionChat(Main.LocalPlayer, body);
        return true;
    }

    internal void SendCompanionChat(Player player, string message)
    {
        if (!CanUseCompanionControls) return;
        if (!PlayerChatInput.IsPlayerMessage(message))
        {
            Main.NewText(CompanionControlsUI.Text("ChatUsage"), Color.Yellow);
            return;
        }

        message = message.Trim();
        Main.NewText(CompanionControlsUI.Text("ChatSent", message), new Color(174, 218, 252));
        HandlePlayerMessage(player, message, fromChat: true);
    }

    public void Enqueue(Action action) => mainThreadQueue.Enqueue(action);

    public void SetAutoMode(bool enabled)
    {
        if (!enabled && primaryCancellation != null && primaryTicket.Kind == "auto")
            InvalidateAgentIntent();
        autoMode = enabled;
        if (enabled)
        {
            manualControlActive = false;
            // Stop clears its queue. Auto-on may leave that empty basic wait,
            // but does not resume a separately paused retained task/old goal.
            if (Executor.HoldPosition && !Executor.HasCurrentPlan && Executor.SnapshotTasks().Count == 0 &&
                string.IsNullOrWhiteSpace(activeGoal)) Executor.SetHold(false);
        }
        lastCommandAt = DateTime.MinValue;
        lastAutoPlanAt = DateTime.MinValue;
        Main.NewText(enabled ? "小星：自主模式开了。" : "小星：自主模式关了。", Color.LightGreen);
        TestLog("auto mode " + enabled);
    }

    private static LlmSettings? GetLlmSettings()
    {
        AIConfig config = ModContent.GetInstance<AIConfig>();
        if (!config.UseLlm)
            return null;

        string apiKey = config.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("AITEAMMATE_API_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            string keyPath = Path.Combine(Main.SavePath, "AITeammate", "api_key.txt");
            try
            {
                if (File.Exists(keyPath))
                    apiKey = File.ReadAllText(keyPath).Trim();
            }
            catch
            {
                // Ignore a missing or unreadable local key file.
            }
        }
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        string baseUrl = config.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = Environment.GetEnvironmentVariable("AITEAMMATE_BASE_URL") ?? "https://api.deepseek.com";

        string model = config.Model;
        if (string.IsNullOrWhiteSpace(model))
            model = Environment.GetEnvironmentVariable("AITEAMMATE_MODEL") ?? "";
        if (string.IsNullOrWhiteSpace(model))
            model = "deepseek-flash";

        return config.CreateSettings(apiKey, baseUrl, model);
    }

    public string MemoryText()
    {
        string facts = string.IsNullOrWhiteSpace(Memory.FactsSnapshot()) ? "空" : Memory.FactsSnapshot();
        string recent = string.IsNullOrWhiteSpace(Memory.Snapshot()) ? "空" : Memory.Snapshot();
        return $"长期记忆：\n{facts}\n最近：\n{recent}";
    }

    public void SpawnCompanion(Player player)
    {
        if (Loadout.EnsureStarterTools())
            TestLog("starter tools granted on spawn: " + Loadout.Summary());

        int type = ModContent.NPCType<Content.NPCs.AICompanion>();
        NPC? existing = Main.npc.FirstOrDefault(n => n.active && n.type == type);

        if (existing != null)
        {
            existing.Center = player.Center + new Vector2(-80f * player.direction, 0f);
            existing.velocity = Vector2.Zero;
            Main.NewText("小星已经在了。", Color.LightGreen);
            return;
        }

        SpawnNewCompanion(player.Center, player.Center + new Vector2(-80f * player.direction, 0f), type);
    }

    private NPC? SpawnNewCompanion(Vector2 origin, Vector2 center, int type)
    {
        int index = NPC.NewNPC(new Terraria.DataStructures.EntitySource_SpawnNPC(), (int)origin.X, (int)origin.Y, type);
        if (index < 0 || index >= Main.maxNPCs)
            return null;

        NPC npc = Main.npc[index];
        npc.GivenName = "小星";
        npc.Center = center;
        npc.velocity = Vector2.Zero;
        Memory.Add("你召唤了小星。");
        Main.NewText("小星来了。", Color.LightGreen);
        return npc;
    }

    public void Stop()
    {
        InvalidateAgentIntent();
        manualControlActive = true;
        autoMode = false;
        ClearActiveGoal();
        Executor.Clear(true);
        Main.NewText("小星：好，停了。", Color.LightGreen);
    }

    public void OnPlayerHurt(PlayerDeathReason source)
    {
        if (source.SourceNPCIndex < 0 && source.SourceProjectileLocalIndex < 0 && source.SourceProjectileType <= 0)
            return;

        Executor.NotifyPlayerAttacked(source.SourceNPCIndex);
    }

    public string TaskText() => Executor.TaskSummary();

    public void ApplyLocalTask(AIPlan plan)
    {
        if (plan.Action == AIActionType.None)
            return;

        InvalidateAgentIntent();
        manualControlActive = true;
        autoMode = false;
        ClearActiveGoal();
        ApplyPlan(plan, "（直接任务）");
    }

    internal void ExecuteBasicCommand(Player player, AIPlan plan)
    {
        if (IsTestRecording)
            DiagnosticsRecorder.Record("local_command", new { action = plan.Action.ToString() });
        if (plan.Action == AIActionType.Wait)
        {
            Stop();
            return;
        }

        if (CompanionNpc == null) SpawnCompanion(player);
        if (CompanionNpc != null) ApplyLocalTask(plan);
    }

    public string StatusText()
    {
        NPC? npc = CompanionNpc;
        AIConfig config = ModContent.GetInstance<AIConfig>();
        string who = npc == null ? "未召唤" : $"HP {npc.life}/{npc.lifeMax}";
        string localKeyPath = Path.Combine(Main.SavePath, "AITeammate", "api_key.txt");
        string api = string.IsNullOrWhiteSpace(config.ApiKey) &&
                     string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AITEAMMATE_API_KEY")) &&
                     !File.Exists(localKeyPath)
            ? "未配置" : "已配置";
        string auto = autoMode ? "自主" : "手动";
        string facts = string.IsNullOrWhiteSpace(Memory.FactsSnapshot()) ? "0条" : $"{Memory.FactsSnapshot().Split('\n').Length}条";
        string resume = restoredGoalAwaitingContinue ? $" · 已恢复目标：{activeGoal}（暂停，输入“继续”重新核对现场）" : "";
        return $"小星：{who} · 任务：{Executor.StatusText()} · 背包：{Executor.InventorySummary()} · 记忆：{facts} · 模式：{auto} · API：{api}{resume}";
    }

    public void GiveInventory(Player player) => Executor.GiveInventory(player);

    public void GiveBlueprintWand(Player player)
    {
        GiveItem(player, ModContent.ItemType<Content.Items.BlueprintWand>(), 1);
        Main.NewText("蓝图魔杖已发放：左键点两个角，再用 /ai blueprint save 名字 保存。", Color.LightBlue);
    }

    public void DebugKit(Player player)
    {
        GiveItem(player, ItemID.Wood, 120);
        GiveItem(player, ItemID.IronOre, 30);
        GiveItem(player, ItemID.StoneBlock, 60);
        Main.NewText("测试材料已发放。", Color.LightBlue);
    }

    private static void GiveItem(Player player, int itemType, int stack)
    {
        foreach (Item item in player.inventory)
        {
            if (!item.IsAir)
                continue;

            item.SetDefaults(itemType);
            item.stack = stack;
            return;
        }

        player.QuickSpawnItem(player.GetSource_FromThis(), itemType, stack);
    }

    public void DebugPrepareBuildSite(Player player)
    {
        int px = (int)(player.Center.X / 16f);
        int py = (int)(player.Center.Y / 16f);
        int centerX = px + 15;
        int groundY = py + 4;

        for (int x = centerX - 6; x <= centerX + 6; x++)
        {
            for (int y = groundY - 7; y <= groundY; y++)
            {
                WorldGen.KillTile(x, y, false, false, false);
                WorldGen.KillWall(x, y, false);
            }

            WorldGen.PlaceTile(x, groundY + 1, TileID.Stone, true, true);
        }

        Main.NewText("测试建造区已清理。", Color.LightBlue);
        TestLog("build site prepared");
    }

    public void DebugSpawnEnemy(Player player)
    {
        int index = NPC.NewNPC(new Terraria.DataStructures.EntitySource_SpawnNPC(),
            (int)player.Center.X + 180, (int)player.Center.Y - 40, NPCID.BlueSlime);
        if (index >= 0 && index < Main.maxNPCs)
            Main.NewText("测试史莱姆已生成。", Color.LightBlue);
    }

    public void DebugOre(Player player)
    {
        int px = (int)(player.Center.X / 16f);
        int py = (int)(player.Center.Y / 16f);

        for (int i = 0; i < 5; i++)
            WorldGen.PlaceTile(px + 5 + i, py + 2, TileID.Iron, true, true);

        Main.NewText("测试铁矿已放置。", Color.LightBlue);
    }
}
