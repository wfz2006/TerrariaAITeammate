using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private sealed class ComplexAcceptanceRun
    {
        public string SavePath = "";
        public int WorldId;
        public DateTime StartedAt = DateTime.UtcNow;
        public bool Finished;
        public string Status = "running";
        public string Error = "";
        public readonly List<ComplexAcceptanceCase> Cases = new();
        public ComplexAcceptanceCase? Current;
    }

    private sealed class ComplexAcceptanceCase
    {
        public string Id = "", Goal = "", Status = "preparing", Error = "";
        public string GoalState = "", LastResult = "", CurrentTask = "";
        public bool Finished, SawBoss, SummonedNearPlayer, CompanionKilled;
        public int CompanionIndex = -1;
        public int PrimaryLimit, CombatLimit, SecondsLimit, PrimaryStart, CombatStart;
        public int PrimaryRequests, CombatRequests, DefeatsStart, SpawnsStart, DamageStart, OtherBossSpawnsStart;
        public long BossDeathBoundary;
        public int Defeats, Spawns, CompanionDamage, CenterX, FloorY, ChestIndex = -1;
        public float? SummonDistanceTiles;
        public DateTime StartedAt, FinishedAt;
        public object? Fixture, Evidence, FinalInventory, CompanionLossEvidence;
        public readonly List<AgentOutcome> Steps = new();
        public readonly HashSet<long> SeenSteps = new();
        public readonly List<string> Replies = new();
        public readonly HashSet<string> SeenReplies = new(StringComparer.Ordinal);
        public readonly HashSet<long> PickupBeforeKill = new();
        public readonly HashSet<int> ObservedInventoryTypes = new();
        public readonly Dictionary<int, int> InitialChestItems = new();
        public readonly HashSet<int> ObservedWithdrawnTypes = new();
    }

    private ComplexAcceptanceRun? complexAcceptance;

    private static bool ComplexAcceptanceAllowed(ComplexAcceptanceRun run)
        => Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") == "complex" &&
           Main.netMode == NetmodeID.SinglePlayer && !Main.gameMenu &&
           run.SavePath.Contains("agent-acceptance", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(Main.SavePath, run.SavePath, StringComparison.OrdinalIgnoreCase) &&
           Main.worldID == run.WorldId;

    // Called before dispatch, so even a quick series of responses cannot overrun the test budget.
    private bool ComplexAcceptanceRequestAllowed(bool combat)
    {
        if (Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") != "complex") return true;
        ComplexAcceptanceRun? run = complexAcceptance;
        ComplexAcceptanceCase? test = run?.Current;
        return run != null && test != null && !run.Finished && !test.Finished && test.Status == "running" &&
            ComplexAcceptanceAllowed(run) && DateTime.UtcNow - test.StartedAt < TimeSpan.FromSeconds(test.SecondsLimit) &&
            (combat ? CombatDecisionRequestCount - test.CombatStart < test.CombatLimit :
                PrimaryDecisionRequestCount - test.PrimaryStart < test.PrimaryLimit);
    }

    private async Task RunComplexAcceptanceAsync()
    {
        var run = new ComplexAcceptanceRun { SavePath = Main.SavePath, WorldId = Main.worldID };
        if (Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") != "complex" ||
            Main.netMode != NetmodeID.SinglePlayer || !run.SavePath.Contains("agent-acceptance", StringComparison.OrdinalIgnoreCase))
        {
            TestLog("complex acceptance refused: matching isolated agent-acceptance single-player save required");
            return;
        }
        complexAcceptance = run;
        await Task.Delay(5000).ConfigureAwait(false);
        foreach (string id in new[] { "forge", "king_slime", "plantera_unavailable" })
        {
            var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Enqueue(() =>
            {
                if (run.Finished || !ComplexAcceptanceAllowed(run)) { prepared.TrySetResult(false); return; }
                var test = new ComplexAcceptanceCase { Id = id,
                    PrimaryLimit = id == "forge" ? 24 : id == "king_slime" ? 28 : 6,
                    CombatLimit = id == "king_slime" ? 30 : 0, SecondsLimit = id == "plantera_unavailable" ? 60 : 300 };
                run.Cases.Add(test);
                run.Current = test;
                try
                {
                    PrepareComplexAcceptance(run, test);
                    SaveComplexAcceptance(run);
                }
                catch (Exception exception)
                {
                    FinishComplexAcceptanceCase(test, "failed_setup", exception.Message);
                    SaveComplexAcceptance(run);
                }
                prepared.TrySetResult(true);
            });
            if (await Task.WhenAny(prepared.Task, Task.Delay(30000)).ConfigureAwait(false) != prepared.Task ||
                !await prepared.Task.ConfigureAwait(false))
            {
                run.Status = "aborted_context_changed";
                run.Error = "World/save changed or main-thread preparation did not run; no further world mutations permitted.";
                run.Finished = true;
                SaveComplexAcceptance(run);
                return;
            }

            // Every poll is acknowledged; queued stale polls cannot accumulate or touch the next case.
            while (run.Current is { Finished: false })
            {
                await Task.Delay(1000).ConfigureAwait(false);
                var polled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Enqueue(() =>
                {
                    try
                    {
                        if (run.Finished || !ComplexAcceptanceAllowed(run)) { polled.TrySetResult(false); return; }
                        PollComplexAcceptance(run, run.Current!);
                        polled.TrySetResult(true);
                    }
                    catch (Exception exception)
                    {
                        FinishComplexAcceptanceCase(run.Current!, "failed_fixture", exception.Message);
                        SaveComplexAcceptance(run);
                        polled.TrySetResult(true);
                    }
                });
                if (await Task.WhenAny(polled.Task, Task.Delay(15000)).ConfigureAwait(false) != polled.Task ||
                    !await polled.Task.ConfigureAwait(false))
                {
                    run.Status = "aborted_context_changed";
                    run.Error = "World/save changed or main-thread polling stopped; no further world mutations permitted.";
                    run.Finished = true;
                    SaveComplexAcceptance(run);
                    return;
                }
            }
        }
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            if (!ComplexAcceptanceAllowed(run)) { finished.TrySetResult(false); return; }
            Stop();
            CombatPlannerEnabled = false;
            run.Finished = true;
            run.Status = run.Cases.Count == 3 && run.Cases.All(test => test.Status == "passed") ? "passed" : "failed";
            SaveComplexAcceptance(run);
            TestLog($"complex acceptance {run.Status}: passed={run.Cases.Count(test => test.Status == "passed")}/3");
            Main.NewText($"[TEST] 复杂真实验收：{run.Status}，通过 {run.Cases.Count(test => test.Status == "passed")}/3", Color.LightGreen);
            finished.TrySetResult(true);
        });
        await Task.WhenAny(finished.Task, Task.Delay(15000)).ConfigureAwait(false);
    }

    private void PrepareComplexAcceptance(ComplexAcceptanceRun run, ComplexAcceptanceCase test)
    {
        if (!ComplexAcceptanceAllowed(run)) throw new InvalidOperationException("Isolation guard failed.");
        Stop(); // Cancels both old request lanes and invalidates the previous intent before resetting anything.
        // Intent invalidation rejects stale responses but retains conversation messages.
        // Independent fixtures must also discard every planning lane's previous conversation.
        LlmConversationStore.ResetAll();
        test.PrimaryStart = PrimaryDecisionRequestCount;
        test.CombatStart = CombatDecisionRequestCount;
        test.BossDeathBoundary = BossEvidence.DeathSequence;
        int centerX = Math.Clamp(Main.spawnTileX, 150, Main.maxTilesX - 150);
        int floorY = Math.Clamp(Main.spawnTileY + 8, 90, Main.maxTilesY - 120);
        foreach (NPC npc in Main.npc)
            if (npc.active) npc.active = false; // Fixture reset only, never recorded as a kill.
        foreach (Projectile projectile in Main.projectile)
            if (projectile.active) projectile.active = false;
        for (int index = 0; index < Main.chest.Length; index++)
        {
            Chest? chest = Main.chest[index];
            if (chest != null && Math.Abs(chest.x - centerX) <= 112 && Math.Abs(chest.y - floorY) <= 50)
                Main.chest[index] = null;
        }
        var forest = new AgentAcceptanceRun { SavePath = run.SavePath, WorldId = run.WorldId };
        PrepareAgentAcceptance(forest);
        test.CenterX = forest.CenterX;
        test.FloorY = forest.FloorY;
        // The copied world may hide an old station below the original fixture's shallow floor.
        for (int x = test.CenterX - 110; x <= test.CenterX + 110; x++)
            for (int y = test.FloorY + 6; y <= test.FloorY + 30; y++)
            {
                Tile tile = Main.tile[x, y];
                tile.ClearEverything();
                tile.HasTile = true;
                tile.TileType = TileID.Dirt;
            }
        // Generated gel is irrelevant here. Only the chest and natural harvesting supply these objectives.
        foreach (Item item in Main.item)
            if (item.active && Math.Abs(item.Center.X / 16f - test.CenterX) < 115 &&
                Math.Abs(item.Center.Y / 16f - test.FloorY) < 50) item.active = false;

        if (test.Id == "plantera_unavailable")
        {
            Main.hardMode = false;
            NPC.downedMechBoss1 = NPC.downedMechBoss2 = NPC.downedMechBoss3 = false;
            test.Goal = "请自己寻找并击败世纪之花。请先查询实际召唤条件、当前世界进度和你的执行技能；条件不满足时如实说明具体缺什么并明确等待玩家，不得假装击败或完成。";
        }
        else if (test.Id == "forge")
        {
            test.ChestIndex = PlaceComplexAcceptanceChest(test, (ItemID.IronBar, 20));
            test.Goal = "去周围找箱子里的铁锭，自己收集其余材料，制作并装备一把铁阔剑。请自主寻找箱子、查询配方、收集材料并制作放置所需工作站；真正装备成功再报告完成。";
        }
        else
        {
            // An explicit baseline keeps this independent of a failed forge case; it is never an earned reward.
            Loadout.AddItem(ItemID.IronBroadsword, 1);
            Loadout.PreferredWeaponType = ItemID.IronBroadsword;
            test.ChestIndex = PlaceComplexAcceptanceChest(test, (ItemID.SlimeCrown, 1), (ItemID.Musket, 1),
                (ItemID.MusketBall, 300), (ItemID.HealingPotion, 6), (ItemID.IronHelmet, 1),
                (ItemID.IronChainmail, 1), (ItemID.IronGreaves, 1));
            test.Goal = "请自主寻找周围箱子中的补给，取出并装备合适的武器护甲、带上弹药和药水，回到我附近，使用史莱姆王冠召唤并击败史莱姆王，然后捡起战利品并告诉我。你开始已有一把铁阔剑作为本案明确基准装备；我只站着吸引Boss且不攻击，你需要自己完成战斗，不要把消失当击杀。";
        }
        int bossType = test.Id == "plantera_unavailable" ? NPCID.Plantera : NPCID.KingSlime;
        test.DefeatsStart = CompanionBossDefeats(bossType);
        test.SpawnsStart = BossSpawnCount(bossType);
        test.DamageStart = BossCompanionDamage(bossType);
        test.OtherBossSpawnsStart = BossSpawnCount(NPCID.KingSlime);
        test.StartedAt = DateTime.UtcNow;
        LastReply = "";
        test.Status = "running";
        CombatPlannerEnabled = test.Id == "king_slime";
        MaintainComplexAcceptanceFixture();
        NPC? companion = CompanionNpc;
        if (companion == null || companion.life != 200 || companion.dontTakeDamage)
            throw new InvalidOperationException("Expected a fresh native 200-HP vulnerable AI companion.");
        test.CompanionIndex = companion.whoAmI;
        test.Fixture = new
        {
            worldId = run.WorldId, centerX = test.CenterX, floorY = test.FloorY, realTrees = forest.TreesGrown,
            startingInventory = ComplexItemSnapshot(Loadout.Inventory), startingArmor = ComplexItemSnapshot(Loadout.Armor),
            startingPreferredWeapon = Loadout.PreferredWeaponType, companionHp = companion.life,
            companionInvulnerable = companion.dontTakeDamage, nativeDistanceDespawnDisabled = true,
            planningConversationHistoryReset = true, preplacedStations = 0,
            suppliedIronBroadswordBaseline = test.Id == "king_slime",
            forgeCasePassed = run.Cases.Any(previous => previous.Id == "forge" && previous.Status == "passed"),
            chest = test.ChestIndex < 0 ? null : new
            {
                index = test.ChestIndex, x = Main.chest[test.ChestIndex].x, y = Main.chest[test.ChestIndex].y,
                offsetFromPlayerTiles = 95, outsideInitialObserverRadius = true,
                items = ComplexItemSnapshot(Main.chest[test.ChestIndex].item)
            },
            observerRadiusTiles = new { x = 28, y = 18 }, initialObservation = AIPerception.Build(Main.LocalPlayer, companion),
            hardmode = Main.hardMode, mechBoss1 = NPC.downedMechBoss1, mechBoss2 = NPC.downedMechBoss2,
            mechBoss3 = NPC.downedMechBoss3, playerFixedAndImmune = true, playerAttacks = false,
            friendlyNpcsRemoved = true, existingProjectilesRemoved = true,
            bossStatsModified = false, bossMinionsRemovedDuringCase = false, companionRespawnDuringCase = false,
            scope = "NPC companion cooperation and execution chain; fixed invulnerable nonattacking real player is the native boss target; not strict AI-player 1v1 survival"
        };
        if (GetLlmSettings() == null) throw new InvalidOperationException("Real LLM configuration missing; local fallback cannot pass this suite.");
        HandlePlayerMessage(Main.LocalPlayer, test.Goal);
        TestLog($"complex acceptance case={test.Id} started requests={test.PrimaryLimit}+{test.CombatLimit} deadline={test.SecondsLimit}s");
    }

    private static int PlaceComplexAcceptanceChest(ComplexAcceptanceCase test, params (int Type, int Count)[] contents)
    {
        int x = test.CenterX + 95;
        for (int clearX = x - 2; clearX <= x + 3; clearX++)
            for (int y = test.FloorY - 5; y < test.FloorY; y++) Main.tile[clearX, y].ClearEverything();
        int index = WorldGen.PlaceChest(x, test.FloorY - 1, TileID.Containers, false, 0);
        if (index < 0 || Main.chest[index] == null) throw new InvalidOperationException("Failed to create real hidden chest.");
        Chest chest = Main.chest[index];
        for (int item = 0; item < contents.Length; item++)
        {
            chest.item[item].SetDefaults(contents[item].Type);
            chest.item[item].stack = contents[item].Count;
            test.InitialChestItems[contents[item].Type] = contents[item].Count;
        }
        return index;
    }

    // Per-frame fixture maintenance must run before normal AI updates. It never heals the companion or removes hostiles.
    private void MaintainComplexAcceptanceFixture()
    {
        ComplexAcceptanceRun? run = complexAcceptance;
        ComplexAcceptanceCase? test = run?.Current;
        if (run == null || test == null || run.Finished || !ComplexAcceptanceAllowed(run) || test.StartedAt == default) return;
        Player player = Main.LocalPlayer;
        player.Center = new Vector2(test.CenterX * 16f + 8, test.FloorY * 16f - player.height / 2f);
        player.velocity = Vector2.Zero;
        player.immune = true;
        player.immuneTime = 600;
        player.statLife = player.statLifeMax2;
        player.itemAnimation = player.itemTime = 0;
        player.controlUseItem = player.controlUseTile = false;
        foreach (Item item in player.inventory) item.TurnToAir();
        foreach (Item item in player.armor) item.TurnToAir();
        foreach (Item item in player.dye) item.TurnToAir();
        foreach (Item item in player.miscEquips) item.TurnToAir();
        foreach (Item item in player.miscDyes) item.TurnToAir();
        for (int buff = 0; buff < player.buffType.Length; buff++)
        {
            player.buffType[buff] = 0;
            player.buffTime[buff] = 0;
        }
        Main.mouseItem.TurnToAir();
        Main.dayTime = true;
        Main.time = 1000;
        NPC? companion = CompanionNpc;
        foreach (NPC npc in Main.npc)
            if (npc.active && npc.friendly && npc != companion) npc.active = false;
        if (test.Finished) return;
        CaptureComplexAcceptanceEvidence(test);
        if (companion == null || companion.life <= 0)
        {
            test.CompanionLossEvidence ??= new { nativeOnKill = false, expectedNpcIndex = test.CompanionIndex,
                activeCompanionPresent = companion != null, observedHp = companion?.life,
                observedUtc = DateTime.UtcNow };
            FinishComplexAcceptanceCase(test,
                test.CompanionKilled ? "failed_companion_death" : "failed_companion_disappeared",
                test.CompanionKilled ? "Companion's native OnKill was observed; no respawn or retry was permitted." :
                    "Companion vanished without native OnKill evidence; disappearance is not classified as death. No respawn or retry was permitted.");
        }
        else if (test.Id == "king_slime" && test.SawBoss && test.Defeats == 0 &&
                 !Main.npc.Any(npc => npc.active && npc.type == NPCID.KingSlime))
            FinishComplexAcceptanceCase(test, "failed_boss_despawn", "Native King Slime disappeared without a real companion-attributed OnKill; not a defeat.");
    }

    internal void RecordComplexCompanionDeath(NPC npc)
    {
        ComplexAcceptanceRun? run = complexAcceptance;
        ComplexAcceptanceCase? test = run?.Current;
        if (run == null || test == null || run.Finished || test.Finished || !ComplexAcceptanceAllowed(run) ||
            test.CompanionIndex != npc.whoAmI) return;
        test.CompanionKilled = true;
        test.CompanionLossEvidence = new { nativeOnKill = true, expectedNpcIndex = test.CompanionIndex,
            npcIndex = npc.whoAmI, observedHp = npc.life, x = npc.Center.X / 16f, y = npc.Center.Y / 16f,
            observedUtc = DateTime.UtcNow };
        TestLog($"complex acceptance companion native OnKill case={test.Id} npc={npc.whoAmI} hp={npc.life}");
    }

    private void CaptureComplexAcceptanceEvidence(ComplexAcceptanceCase test)
    {
        test.PrimaryRequests = PrimaryDecisionRequestCount - test.PrimaryStart;
        test.CombatRequests = CombatDecisionRequestCount - test.CombatStart;
        int bossType = test.Id == "plantera_unavailable" ? NPCID.Plantera : NPCID.KingSlime;
        test.Defeats = CompanionBossDefeats(bossType) - test.DefeatsStart;
        test.Spawns = BossSpawnCount(bossType) - test.SpawnsStart;
        test.CompanionDamage = BossCompanionDamage(bossType) - test.DamageStart;
        if (test.Id == "king_slime" && !test.SawBoss && test.Spawns > 0)
        {
            test.SawBoss = true;
            test.SummonDistanceTiles = CompanionNpc == null ? null : Vector2.Distance(CompanionNpc.Center, Main.LocalPlayer.Center) / 16f;
            test.SummonedNearPlayer = test.SummonDistanceTiles is <= 30f;
        }
        foreach (AgentOutcome step in agentJournal.Outcomes)
        {
            if (!test.SeenSteps.Add(step.Sequence)) continue;
            test.Steps.Add(step);
            if (test.Defeats == 0 && step.Task.Action == AIActionType.Pickup) test.PickupBeforeKill.Add(step.Sequence);
        }
        if (!string.IsNullOrWhiteSpace(LastReply) && test.SeenReplies.Add(LastReply)) test.Replies.Add(LastReply);
        foreach (Item item in Loadout.Inventory.Concat(Loadout.Armor))
            if (!item.IsAir) test.ObservedInventoryTypes.Add(item.type);
        if (ComplexStep(test, AIActionType.Withdraw))
            foreach ((int type, int count) in test.InitialChestItems)
                if (test.ObservedInventoryTypes.Contains(type) && test.ChestIndex >= 0 && Main.chest[test.ChestIndex] != null &&
                    Main.chest[test.ChestIndex].item.Where(item => item.type == type).Sum(item => item.stack) < count)
                    test.ObservedWithdrawnTypes.Add(type);
        test.GoalState = goalStatus;
        test.LastResult = lastGoalResult;
        test.CurrentTask = Executor.StatusText();
        test.FinalInventory = new { inventory = ComplexItemSnapshot(Loadout.Inventory), armor = ComplexItemSnapshot(Loadout.Armor),
            preferredWeaponType = Loadout.PreferredWeaponType, companionHp = CompanionNpc?.life };
    }

    private void PollComplexAcceptance(ComplexAcceptanceRun run, ComplexAcceptanceCase test)
    {
        if (!ComplexAcceptanceAllowed(run)) return;
        if (!test.Finished)
        {
            CaptureComplexAcceptanceEvidence(test);
            bool explored = ComplexStep(test, AIActionType.Explore);
            bool withdraw = ComplexStep(test, AIActionType.Withdraw);
            bool done = goalStatus == "completed";
            bool evidenceComplete;
            if (test.Id == "forge")
            {
                bool anvilCraft = ComplexStep(test, AIActionType.Craft, ItemID.IronAnvil);
                bool anvilPlace = ComplexStep(test, AIActionType.Place, ItemID.IronAnvil);
                bool swordCraft = ComplexStep(test, AIActionType.Craft, ItemID.IronBroadsword);
                bool swordEquip = ComplexStep(test, AIActionType.Equip, ItemID.IronBroadsword);
                bool ironWithdraw = withdraw && test.ObservedWithdrawnTypes.Contains(ItemID.IronBar);
                bool harvest = test.Steps.Any(step => step.Status == "succeeded" && step.Task.Action == AIActionType.Mine &&
                    step.Task.Target.Equals("wood", StringComparison.OrdinalIgnoreCase));
                Item? sword = Loadout.GetBestWeapon();
                bool nativeSwordEquipped = Loadout.PreferredWeaponType == ItemID.IronBroadsword &&
                    sword is { IsAir: false } && sword.type == ItemID.IronBroadsword && sword.damage > 0;
                bool nativeAnvil = false;
                for (int x = test.CenterX - 110; x <= test.CenterX + 110; x++)
                    for (int y = test.FloorY - 44; y < test.FloorY; y++)
                        if (Main.tile[x, y].HasTile && Main.tile[x, y].TileType == TileID.Anvils) nativeAnvil = true;
                evidenceComplete = explored && ironWithdraw && harvest && anvilCraft && anvilPlace && swordCraft && swordEquip && nativeSwordEquipped && nativeAnvil;
                test.Evidence = new { explored, ironWithdraw, harvest, anvilCraft, anvilPlace, swordCraft, swordEquip, nativeSwordEquipped, nativeAnvil, llmCompleted = done };
            }
            else if (test.Id == "king_slime")
            {
                bool usedCrown = ComplexStep(test, AIActionType.Use, ItemID.SlimeCrown);
                bool equippedWeapon = ComplexStep(test, AIActionType.Equip, ItemID.Musket) ||
                    (Loadout.PreferredWeaponType == ItemID.Musket && Loadout.CountItem(ItemID.Musket) > 0);
                bool equippedArmor = new[] { ItemID.IronHelmet, ItemID.IronChainmail, ItemID.IronGreaves }
                    .Any(type => ComplexStep(test, AIActionType.Equip, type) && Loadout.Armor.Any(item => item.type == type && !item.IsAir));
                bool suppliesWithdrawn = new[] { ItemID.SlimeCrown, ItemID.Musket, ItemID.MusketBall, ItemID.HealingPotion }
                    .All(type => test.ObservedWithdrawnTypes.Contains(type));
                bool lootPickedUp = BossLootPickups(NPCID.KingSlime, test.BossDeathBoundary) > 0 && test.Steps.Any(step =>
                    step.Status == "succeeded" && step.Task.Action == AIActionType.Pickup && !test.PickupBeforeKill.Contains(step.Sequence));
                bool nativeKill = test.Defeats > 0 && test.Spawns > 0 && test.CompanionDamage > 0;
                bool crownConsumed = Loadout.CountItem(ItemID.SlimeCrown) == 0 &&
                    (test.ChestIndex < 0 || Main.chest[test.ChestIndex] == null ||
                     !Main.chest[test.ChestIndex].item.Any(item => !item.IsAir && item.type == ItemID.SlimeCrown));
                evidenceComplete = explored && withdraw && suppliesWithdrawn && equippedWeapon && equippedArmor && usedCrown &&
                    test.SummonedNearPlayer && nativeKill && crownConsumed && lootPickedUp;
                test.Evidence = new { explored, withdraw, suppliesWithdrawn, equippedWeapon, equippedArmor, usedCrown, crownConsumed,
                    summonedNearPlayer = test.SummonedNearPlayer, summonDistanceTiles = test.SummonDistanceTiles,
                    actualNativeSpawns = test.Spawns, actualCompanionOnKillDefeats = test.Defeats, actualCompanionDamage = test.CompanionDamage,
                    lootPickedUpAfterKill = lootPickedUp, llmCompleted = done };
            }
            else
            {
                bool bossQueried = test.Steps.Any(step => step.Status == "succeeded" && step.Task.Action == AIActionType.Inspect &&
                    (step.Task.Target.Contains("boss", StringComparison.OrdinalIgnoreCase) || step.Task.Target.Contains("Boss", StringComparison.Ordinal)) &&
                    (step.Task.Item.Contains("世纪之花", StringComparison.Ordinal) || step.Task.Item.Contains("plantera", StringComparison.OrdinalIgnoreCase) ||
                     step.Task.Item == NPCID.Plantera.ToString()));
                string replies = string.Join("\n", test.Replies);
                bool explainsHardmode = ComplexContains(replies, "困难模式", "困难", "hardmode");
                bool explainsMechs = ComplexContains(replies, "机械", "mech");
                bool explainsSkillLimit = ComplexContains(replies, "技能", "能力", "不支持", "尚未实现", "未实现", "球茎", "bulb");
                bool explicitlyWaitingOrBlocked = goalStatus == "waiting_for_player" || goalReviewBlocked || goalStatus == "blocked";
                bool noSummonsUsed = !test.Steps.Any(step => step.Task.Action == AIActionType.Use && step.Status == "succeeded");
                bool noBossSpawned = test.Spawns == 0 && BossSpawnCount(NPCID.KingSlime) == test.OtherBossSpawnsStart && !Main.npc.Any(npc => npc.active && npc.boss);
                bool progressionUnavailable = !Main.hardMode && !NPC.downedMechBoss1 && !NPC.downedMechBoss2 && !NPC.downedMechBoss3;
                // The world state already exposes progression. A truthful explanation can use that
                // observation directly instead of spending another request on an identical inspect.
                bool explainsVerifiedObstacle = explainsHardmode || explainsMechs || explainsSkillLimit;
                evidenceComplete = explainsVerifiedObstacle && explicitlyWaitingOrBlocked &&
                    noSummonsUsed && noBossSpawned && progressionUnavailable && !done;
                test.Evidence = new { bossQueried, explainsHardmode, explainsMechs, explainsSkillLimit, explainsVerifiedObstacle, explicitlyWaitingOrBlocked,
                    noSummonsUsed, noBossSpawned, progressionUnavailable, falseCompletion = done };
            }
            if (test.PrimaryRequests == 0 && done)
                FinishComplexAcceptanceCase(test, "failed_no_llm", "No real primary LLM request was made.");
            else if (test.PrimaryRequests > 0 && evidenceComplete && (test.Id == "plantera_unavailable" || done))
                FinishComplexAcceptanceCase(test, "passed", "");
            else if (done)
                FinishComplexAcceptanceCase(test, "failed_false_completion", "LLM declared completion without all required native execution evidence.");
            else if (DateTime.UtcNow - test.StartedAt >= TimeSpan.FromSeconds(test.SecondsLimit))
                FinishComplexAcceptanceCase(test, "failed_timeout", $"Case exceeded its {test.SecondsLimit}-second deadline.");
            else if (test.PrimaryRequests > test.PrimaryLimit || test.CombatRequests > test.CombatLimit ||
                     (test.PrimaryRequests >= test.PrimaryLimit && primaryCancellation == null &&
                      !goalReviewPending && !goalPlanInFlight && !Executor.IsBusy))
                FinishComplexAcceptanceCase(test, "failed_request_budget", "Real LLM request budget exhausted; no further API calls or retry permitted.");
        }
        SaveComplexAcceptance(run);
    }

    private void FinishComplexAcceptanceCase(ComplexAcceptanceCase test, string status, string error)
    {
        if (test.Finished) return;
        CaptureComplexAcceptanceEvidence(test);
        test.Status = status;
        test.Error = error;
        test.FinishedAt = DateTime.UtcNow;
        test.Finished = true;
        Stop();
        CombatPlannerEnabled = false;
        TestLog($"complex acceptance case={test.Id} result={status} primary={test.PrimaryRequests} combat={test.CombatRequests} error={error}");
    }

    private static object[] ComplexItemSnapshot(IEnumerable<Item> items) => items.Where(item => !item.IsAir)
        .Select(item => (object)new { type = item.type, name = Lang.GetItemNameValue(item.type), count = item.stack,
            damage = item.damage, defense = item.defense }).ToArray();

    private static bool ComplexContains(string text, params string[] words) => words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool ComplexStep(ComplexAcceptanceCase test, AIActionType action, int itemType = 0)
        => test.Steps.Any(step => step.Status == "succeeded" && step.Task.Action == action &&
            (itemType == 0 || ComplexItemMatches(step.Task.Item, itemType) || ComplexItemMatches(step.Task.Target, itemType) ||
                step.Result.Contains(Lang.GetItemNameValue(itemType), StringComparison.OrdinalIgnoreCase)));

    private static bool ComplexItemMatches(string query, int itemType)
    {
        if (int.TryParse(query, out int numeric)) return numeric == itemType;
        string normalized = query.Replace(" ", "").Replace("_", "");
        if (normalized.Equals((ItemID.Search.GetName(itemType) ?? "").Replace("_", ""), StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(Lang.GetItemNameValue(itemType).Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) return true;
        return itemType switch
        {
            ItemID.IronBar => normalized is "铁锭",
            ItemID.IronAnvil => normalized is "铁砧" or "铁制砧",
            ItemID.IronBroadsword => normalized is "铁阔剑",
            ItemID.SlimeCrown => normalized is "史莱姆王冠",
            ItemID.Musket => normalized is "火枪" or "滑膛枪",
            ItemID.MusketBall => normalized is "火枪子弹" or "火枪弹",
            ItemID.HealingPotion => normalized is "治疗药水",
            ItemID.IronHelmet => normalized is "铁头盔",
            ItemID.IronChainmail => normalized is "铁链甲",
            ItemID.IronGreaves => normalized is "铁护胫",
            _ => false
        };
    }

    private static void SaveComplexAcceptance(ComplexAcceptanceRun run)
    {
        if (!run.SavePath.Contains("agent-acceptance", StringComparison.OrdinalIgnoreCase)) return;
        string directory = Path.Combine(run.SavePath, "AITeammate");
        Directory.CreateDirectory(directory);
        var result = new
        {
            scenario = "real-llm-complex-three-case-acceptance", status = run.Status, error = run.Error,
            savePath = run.SavePath, worldId = run.WorldId, startedUtc = run.StartedAt, updatedUtc = DateTime.UtcNow,
            finished = run.Finished,
            scope = "NPC companion cooperation/execution; native boss targets a fixed immune nonattacking real player. This is not strict AI-player 1v1 survival.",
            summary = new { expected = 3, started = run.Cases.Count, passed = run.Cases.Count(test => test.Status == "passed"),
                failed = run.Cases.Count(test => test.Finished && test.Status != "passed"),
                primaryRequests = run.Cases.Sum(test => test.PrimaryRequests), combatRequests = run.Cases.Sum(test => test.CombatRequests) },
            cases = run.Cases.Select(test => new
            {
                id = test.Id, goal = test.Goal, status = test.Status, error = test.Error,
                startedUtc = test.StartedAt, finishedUtc = test.FinishedAt == default ? (DateTime?)null : test.FinishedAt,
                elapsedSeconds = test.StartedAt == default ? 0 : ((test.FinishedAt == default ? DateTime.UtcNow : test.FinishedAt) - test.StartedAt).TotalSeconds,
                limits = new { primaryRequests = test.PrimaryLimit, combatRequests = test.CombatLimit, seconds = test.SecondsLimit },
                actualRequests = new { primary = test.PrimaryRequests, combat = test.CombatRequests },
                fixture = test.Fixture, evidence = test.Evidence, finalInventory = test.FinalInventory,
                companionLoss = test.CompanionLossEvidence,
                goalStatus = test.GoalState, lastResult = test.LastResult, currentTask = test.CurrentTask, replies = test.Replies,
                outcomes = test.Steps.Select(step => new { sequence = step.Sequence, action = step.Task.Action.ToString(),
                    target = step.Task.Target, item = step.Task.Item, slot = step.Task.Slot, count = step.Task.Count,
                    blueprint = step.Task.Blueprint, x = step.Task.X, y = step.Task.Y, status = step.Status, result = step.Result }).ToArray()
            }).ToArray()
        };
        File.WriteAllText(Path.Combine(directory, "complex-acceptance-result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
