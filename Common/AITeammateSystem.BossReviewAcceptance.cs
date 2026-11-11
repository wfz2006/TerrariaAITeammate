using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string BossReviewNativeCase = "boss_review_native";
    private const string BossPursuitTimeoutCase = "boss_pursuit_timeout_native";
    private const string BossReviewTerminalApiCase = "boss_review_terminal_api";
    private const string BossReviewTerminalOutputCase = "boss_review_terminal_output";
    private const string BossReviewGoal = "受控史莱姆王复核期间防守诊断";
    private const string BossReviewFailure = "受控验收：已取得指定 Boss 后触发真实导航失败处理。";
    private const float BossReviewRetreatPixels = 8f;
    private const int BossReviewIdleTicks = 30, BossReviewHoldTicks = 72;
    private static readonly GoalCondition[] BossReviewConditions = { new() { Type = "structure_preserved" } };

    private enum BossReviewPhase { Summon, Acquire, Block, Review, Hold, Terminal, Identity, PursuitTimeout }
    private enum BossReviewTerminalGate { None, Api, Output }
    private sealed record BossReviewRunway(int ActorX, int ObserverX, int BossX, int FloorY, int RetreatTiles);
    private sealed record BossReviewTerminalBaseline(int Ammo, int Shots, string GoalId, long GoalStart,
        long OutcomeSequence, ModNPC Companion, int RequiredFiringFrames);
    private sealed record BossReviewShot(int Owner, int Identity, int Type, ulong Tick, BossReviewPhase Phase,
        AIActionType Action, bool HasPlan, int AmmoBefore, float X, float Y);
    private sealed record BossReviewHit(int Owner, int Identity, ulong Tick, long SpawnId, int Damage);
    private sealed record BossReviewFrame(ulong Tick, BossReviewPhase Phase, float X, float Y, float FeetY, float Vx, float Vy,
        bool Grounded, bool JustHit, int Life, float ObserverX, float ObserverY, int ObserverLife,
        float? BossX, float? BossY, int? BossLife, bool BossChaseable, bool BossOverlapsActor, float? Distance, bool LineOfSight,
        int Ammo, int Shots, int BossDamage, AIActionType Action, bool HasPlan, int Queue, bool ReviewPending, bool Hold, bool AutoDefend);

    private bool bossReviewStarted, bossReviewFinished, bossReviewBlockObserved, bossReviewIdleObserved;
    private bool bossPursuitTimeout;
    private ulong bossPursuitStartedAt;
    private int bossReviewFrame, bossReviewPhaseFrame, bossReviewAwaySign, bossReviewIdleFrames, bossReviewHoldAmmo, bossReviewHoldShotCount;
    private long bossReviewSpawnBoundary, bossReviewSpawnId, bossReviewSequence;
    private ulong bossReviewRetreatTick, bossReviewHoldTick, bossReviewKnockbackUntil;
    private float bossReviewGroundedAway, bossReviewHoldKnockback;
    private int bossReviewBlockAmmo, bossReviewBlockShots, bossReviewBlockDamage;
    private BossReviewPhase bossReviewPhase;
    private BossReviewTerminalGate bossReviewTerminalGate;
    private BossReviewTerminalBaseline? bossReviewTerminalBaseline;
    private int bossReviewTerminalFiringFrames;
    private BossReviewRunway? bossReviewRunway;
    private BossReviewFrame? bossReviewPrevious;
    private ShelterWorldAudit? bossReviewAudit;
    private Dictionary<string, string> bossReviewHashesBefore = new();
    private object? bossReviewProductionBeforeHold;
    private AIPlan? bossReviewAttack;
    private readonly List<object> bossReviewEvents = new();
    private readonly List<BossReviewFrame> bossReviewTrace = new();
    private readonly List<BossReviewShot> bossReviewShots = new();
    private readonly List<BossReviewHit> bossReviewHits = new();

    internal bool BossReviewObserving => bossReviewStarted && !bossReviewFinished;

    private void BeginBossReviewNative()
    {
        string? testCase = Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE");
        bossPursuitTimeout = testCase == BossPursuitTimeoutCase;
        bossReviewCapturedWait = testCase == BossReviewCapturedWaitCase;
        bossReviewIdentityVariant = testCase switch
        {
            BossReviewCompanionRespawnCase => BossReviewIdentityVariant.CompanionRespawn,
            BossReviewReplacedBossCase => BossReviewIdentityVariant.ReplacedBoss,
            _ => BossReviewIdentityVariant.None
        };
        bossReviewTerminalGate = testCase switch
        {
            BossReviewTerminalApiCase => BossReviewTerminalGate.Api,
            BossReviewTerminalOutputCase => BossReviewTerminalGate.Output,
            _ => BossReviewTerminalGate.None
        };
        bossReviewStarted = true;
        Stop();
        Executor.ResetNavigationTelemetry();
        if (GetLlmSettings() != null || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 ||
            CompanionNpc is not { active: true } npc || Main.LocalPlayer.dead || Main.npc.Any(n => n.active && n.boss))
        {
            FinishBossReviewNative("failed_setup", "Requires a live companion and observer, no existing Boss, and disabled LLM.");
            return;
        }

        bossReviewHashesBefore = ReadBossReviewHashes();
        Player observer = Main.LocalPlayer;
        if (bossReviewCapturedWait)
        {
            if (!PrepareBossReviewCapturedWait(npc, observer))
            { FinishBossReviewNative("uncovered_setup", "The measured envelope approximations could not be placed legally on the original terrain."); return; }
        }
        else
        {
            bossReviewRunway = FindBossReviewRunway(npc, observer);
            if (bossReviewRunway is not { } runway)
            { FinishBossReviewNative("failed_setup", "No dry original-terrain runway met the bounded stand checks."); return; }
            npc.Bottom = new Vector2(runway.ActorX * 16f + 8, runway.FloorY * 16f);
            observer.Bottom = new Vector2(runway.ObserverX * 16f + 8, runway.FloorY * 16f);
            if (bossReviewIdentityVariant == BossReviewIdentityVariant.CompanionRespawn)
                observer.direction = Math.Sign(runway.ActorX - runway.ObserverX);
        }
        npc.velocity = observer.velocity = Vector2.Zero;
        foreach (Item item in observer.inventory.Concat(observer.armor).Concat(observer.miscEquips)) item.TurnToAir();
        for (int i = 0; i < observer.buffType.Length; i++) { observer.buffType[i] = 0; observer.buffTime[i] = 0; }
        Main.mouseItem.TurnToAir();
        foreach (Projectile projectile in Main.projectile)
            if (projectile.active && projectile.friendly) projectile.active = false;
        MaintainBossReviewObserver(observer);

        var supplies = new Dictionary<int, int>
        {
            [ItemID.PhoenixBlaster] = 1, [ItemID.MusketBall] = bossReviewCapturedWait ? 400 : 100,
            [ItemID.SlimeCrown] = bossReviewIdentityVariant == BossReviewIdentityVariant.ReplacedBoss ? 2 : 1,
            [ItemID.MoltenHelmet] = 1, [ItemID.MoltenBreastplate] = 1, [ItemID.MoltenGreaves] = 1
        };
        foreach (var supply in supplies)
            if (!Loadout.AddItem(supply.Key, supply.Value))
            { FinishBossReviewNative("failed_setup", "The controlled loadout does not fit."); return; }

        bossReviewAudit = ShelterWorldAudit.Capture(3220, 390);
        ShelterPreservationReport original = bossReviewAudit.CheckPreservation();
        if (original is not { preserved: true, coverageComplete: true, diffCount: 0 })
        { FinishBossReviewNative("failed_setup", "Original-house protection baseline is incomplete."); return; }

        bossReviewSpawnBoundary = BossEvidence.Snapshot().Select(encounter => encounter.SpawnId).DefaultIfEmpty().Max();
        bossReviewSequence = agentJournal.LastSequence;
        manualControlActive = false;
        bool accepted = ApplyPlan(new AIPlan
        {
            Goal = BossReviewGoal, CompletionConditions = BossReviewConditions.ToList(), ReplaceTasks = true,
            Steps = new List<AIPlan>
            {
                new() { Action = AIActionType.Equip, Item = ItemID.PhoenixBlaster.ToString() },
                new() { Action = AIActionType.Equip, Item = ItemID.MoltenHelmet.ToString() },
                new() { Action = AIActionType.Equip, Item = ItemID.MoltenBreastplate.ToString() },
                new() { Action = AIActionType.Equip, Item = ItemID.MoltenGreaves.ToString() },
                new() { Action = AIActionType.Use, Item = ItemID.SlimeCrown.ToString(), Count = 1 }
            }
        }, null, sourcePlayerRequest: "零 API 受控 Boss 复核防守诊断");
        GoalNativeConditionResult? native = ReadBossReviewPreservation();
        bossReviewEvents.Add(new { kind = "setup", runway = bossReviewRunway, supplies, accepted, native, original,
            production = PreservationEvidence.DebugSnapshot(), frame = CaptureBossReviewFrame(npc, observer, null) });
        if (!accepted || native?.Met != true)
            FinishBossReviewNative("failed_setup", "The native goal or preservation contract was unavailable before summon.");
    }

    private void TickBossReviewNative()
    {
        bossReviewFrame++;
        Player observer = Main.LocalPlayer;
        if (GetLlmSettings() != null || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 || Executor.NavigationTeleportCount != 0 ||
            CompanionNpc is not { active: true } npc || observer.dead)
        {
            if (bossReviewCapturedWait) RecordBossReviewCapturedInterruption();
            FinishBossReviewNative("failed", "Companion or observer lost, LLM enabled, API call or companion warp.");
            return;
        }
        MaintainBossReviewObserver(observer);

        NPC? boss = FindBossReviewEncounter();
        BossReviewFrame sample = CaptureBossReviewFrame(npc, observer, boss);
        BossReviewFrame previous = bossReviewPrevious ?? sample;
        bossReviewPrevious = sample;
        bossReviewTrace.Add(sample);
        if (bossReviewCapturedWait) RecordBossReviewCapturedFrame(npc, boss, sample);

        if (bossReviewPhase == BossReviewPhase.Summon)
        {
            CompleteBossReviewSummon(npc, observer);
            return;
        }
        bool capturedKill = bossReviewCapturedWait && (bossReviewPhase is BossReviewPhase.Review or BossReviewPhase.Hold) &&
            bossReviewCapturedDeathSequence > 0;
        if (boss == null && !capturedKill)
        { FinishBossReviewNative("failed", "The same live King Slime encounter was lost."); return; }

        switch (bossReviewPhase)
        {
            case BossReviewPhase.Acquire:
                InjectBossReviewNavigationFailure(npc, boss!, sample);
                break;
            case BossReviewPhase.Block:
                VerifyBossReviewBlock(sample);
                break;
            case BossReviewPhase.Review:
                if (bossReviewCapturedWait) WatchBossReviewCapturedWait(npc, observer, sample);
                else WatchBossReviewResponse(observer, previous, sample);
                break;
            case BossReviewPhase.Hold:
                WatchBossReviewHold(previous, sample);
                break;
            case BossReviewPhase.Terminal:
                WatchBossReviewTerminal(npc, sample);
                break;
            case BossReviewPhase.Identity:
                WatchBossReviewIdentity(npc, boss!, previous, sample);
                break;
            case BossReviewPhase.PursuitTimeout:
                VerifyBossPursuitTimeout(boss!, sample);
                break;
        }
    }

    private void CompleteBossReviewSummon(NPC npc, Player observer)
    {
        AgentOutcome[] outcomes = agentJournal.Outcomes.Where(outcome => outcome.Sequence > bossReviewSequence).ToArray();
        AgentOutcome? use = outcomes.LastOrDefault(outcome => outcome.Task.Action == AIActionType.Use);
        if (use == null && outcomes.All(outcome => outcome.Status != "blocked") && bossReviewFrame <= 120) return;
        BossEncounter[] spawns = BossEvidence.Snapshot().Where(encounter => encounter.SpawnId > bossReviewSpawnBoundary).ToArray();
        bool equipped = Loadout.GetBestWeapon()?.type == ItemID.PhoenixBlaster &&
            outcomes.Count(outcome => outcome.Task.Action == AIActionType.Equip && outcome.Status == "succeeded") == 4;
        bossReviewEvents.Add(new { kind = "summon_outcomes", outcomes, spawns, equipped,
            crown = Loadout.CountItem(ItemID.SlimeCrown), ammo = Loadout.CountItem(ItemID.MusketBall) });
        int expectedCrowns = bossReviewIdentityVariant == BossReviewIdentityVariant.ReplacedBoss ? 1 : 0;
        if (use?.Status != "succeeded" || !equipped || Loadout.CountItem(ItemID.SlimeCrown) != expectedCrowns || spawns.Length != 1 || spawns[0].NpcType != NPCID.KingSlime)
        { FinishBossReviewNative("failed_setup", "Native gear or crown use did not produce one new King Slime."); return; }

        bossReviewSpawnId = spawns[0].SpawnId;
        NPC? boss = FindBossReviewEncounter();
        float bossX = bossReviewCapturedWait ? CapturedBossFoot.X : bossReviewRunway!.BossX * 16f + 8;
        float bossFeet = bossReviewCapturedWait ? CapturedBossFoot.Y : bossReviewRunway!.FloorY * 16f - 4;
        if (boss == null)
        { FinishBossReviewNative("failed_setup", "The fresh native Boss encounter was unavailable."); return; }
        if (bossPursuitTimeout)
        {
            BeginBossPursuitTimeout(npc, observer, boss);
            return;
        }
        bool bossDry = BossReviewDryBody(bossX, bossFeet, boss.width, boss.height);
        if (bossReviewCapturedWait)
        {
            bool supported = TilePathfinder.IsGroundedAt(bossX, bossFeet, 0, boss.width);
            bossReviewEvents.Add(new { kind = "captured_wait_actual_boss_preflight", x = bossX, feetY = bossFeet,
                boss.width, boss.height, dry = bossDry, supported, spawnId = bossReviewSpawnId });
            if (!bossDry || !supported)
            { FinishBossReviewNative("uncovered_setup", "The actual summoned Boss did not fit its captured dry supported position."); return; }
        }
        else if (!bossDry)
        { FinishBossReviewNative("failed_setup", "Fresh Boss or its measured dry placement was unavailable."); return; }

        var before = new { x = boss.Center.X, y = boss.Center.Y, feetY = boss.Bottom.Y, vx = boss.velocity.X, vy = boss.velocity.Y };
        boss.Bottom = new Vector2(bossX, bossFeet);
        if (!bossReviewCapturedWait) boss.velocity = Vector2.Zero;
        bossReviewEvents.Add(new { kind = "one_boss_position_injection", spawnId = bossReviewSpawnId, before,
            after = CaptureBossReviewFrame(npc, observer, boss), boss.width, boss.height, boss.lifeMax });
        bossReviewAttack = new AIPlan { Action = AIActionType.Attack, Target = "king_slime" };
        bossReviewSequence = agentJournal.LastSequence;
        if (!ApplyPlan(bossReviewAttack, null))
        { FinishBossReviewNative("failed_setup", "Named Attack was not admitted."); return; }
        SetBossReviewPhase(BossReviewPhase.Acquire);
    }

    private void BeginBossPursuitTimeout(NPC actor, Player observer, NPC boss)
    {
        var runway = bossReviewRunway!;
        foreach (int side in new[] { -1, 1 })
        foreach (int distance in Enumerable.Range(205, 24))
        foreach (int floor in Enumerable.Range(runway.FloorY - 20, 46).OrderBy(y => Math.Abs(y - runway.FloorY)))
        {
            int x = runway.ActorX + side * distance;
            if (!WorldGen.InWorld(x, floor, 10) || !BossReviewDryFloor(x, floor, observer.width, observer.height) ||
                !BossReviewDryFloor(x + side * 5, floor, boss.width, boss.height)) continue;
            observer.Bottom = new Vector2(x * 16f + 8, floor * 16f);
            boss.Bottom = new Vector2((x + side * 5) * 16f + 8, floor * 16f);
            observer.velocity = boss.velocity = Vector2.Zero;
            bossPursuitStartedAt = Main.GameUpdateCount;
            bossReviewSequence = agentJournal.LastSequence;
            bossReviewEvents.Add(new { kind = "far_pursuit_setup", spawnId = bossReviewSpawnId, frame = CaptureBossReviewFrame(actor, observer, boss) });
            if (!ApplyPlan(new AIPlan { Action = AIActionType.Attack, Target = "king_slime" }, null))
            { FinishBossReviewNative("failed_setup", "The named Attack was not admitted."); return; }
            SetBossReviewPhase(BossReviewPhase.PursuitTimeout);
            return;
        }
        FinishBossReviewNative("uncovered_setup", "No dry far stance for the observer and actual Boss was found.");
    }

    private void VerifyBossPursuitTimeout(NPC boss, BossReviewFrame sample)
    {
        ulong elapsed = sample.Tick - bossPursuitStartedAt;
        if (sample.Distance is not > 160 * 16f)
        { FinishBossReviewNative("uncovered_setup", "The same Boss entered the pursuit range before timeout."); return; }
        AgentOutcome? outcome = agentJournal.Outcomes.LastOrDefault(o =>
            o.Sequence > bossReviewSequence && o.Task.Action == AIActionType.Attack);
        if (outcome == null && elapsed <= 185) return;
        if (outcome?.Status != "blocked" || elapsed is < 180 or > 185 || !goalReviewPending ||
            sample.Shots != 0 || sample.BossDamage != 0)
        { FinishBossReviewNative("failed", "Bounded wait did not hand off a blocked Attack with no substitute or damage."); return; }
        bossReviewEvents.Add(new { kind = "far_pursuit_handoff", elapsed, outcome, frame = sample });
        if (!outcome.Result.StartsWith("{", StringComparison.Ordinal))
        { FinishBossReviewNative("failed", "Attack review lacks a structured live Boss position and identity."); return; }
        using JsonDocument result = JsonDocument.Parse(outcome.Result);
        JsonElement report = result.RootElement;
        if (report.GetProperty("code").GetString() != "boss_outside_pursuit" ||
            report.GetProperty("spawnId").GetInt64() != bossReviewSpawnId ||
            report.GetProperty("x").GetInt32() != (int)(boss.Center.X / 16f) ||
            report.GetProperty("y").GetInt32() != (int)(boss.Center.Y / 16f))
        { FinishBossReviewNative("failed", "The handoff did not describe the same measured live Boss."); return; }
        FinishBossReviewNative("passed", "");
    }

    private void InjectBossReviewNavigationFailure(NPC npc, NPC boss, BossReviewFrame sample)
    {
        var acquired = typeof(AIExecutor).GetField("attackLastTarget", BindingFlags.Instance | BindingFlags.NonPublic);
        var failure = typeof(AIExecutor).GetMethod("FailNavigation", BindingFlags.Instance | BindingFlags.NonPublic);
        if (acquired == null || failure == null)
        { FinishBossReviewNative("failed_setup", "Existing attack witness or FailNavigation reflection endpoint was unavailable."); return; }
        if (Executor.CurrentAction == AIActionType.Attack && Executor.HasCurrentPlan && ReferenceEquals(acquired.GetValue(Executor), boss))
        {
            bossReviewEvents.Add(new { kind = "named_attack_acquired", spawnId = bossReviewSpawnId, task = bossReviewAttack, sample });
            SetBossReviewPhase(BossReviewPhase.Block);
            failure.Invoke(Executor, new object[] { npc, "acceptance_boss_review_navigation", BossReviewFailure });
            return;
        }
        if (bossReviewFrame - bossReviewPhaseFrame > 120)
            FinishBossReviewNative("failed_setup", "Named Attack did not acquire the exact live encounter.");
    }

    private void VerifyBossReviewBlock(BossReviewFrame sample)
    {
        AgentOutcome[] outcomes = agentJournal.Outcomes.Where(outcome => outcome.Sequence > bossReviewSequence).ToArray();
        if (outcomes.Length == 0 && bossReviewFrame - bossReviewPhaseFrame <= 30) return;
        bool blocked = outcomes.Length == 1 && outcomes[0].Status == "blocked" && outcomes[0].Result == BossReviewFailure &&
            PlanTaskIdentity.AreEquivalent(bossReviewAttack!, outcomes[0].Task);
        bossReviewBlockObserved = blocked && BossReviewWaiting(sample);
        bossReviewEvents.Add(new { kind = "real_blocked_wait", observed = bossReviewBlockObserved, outcomes, sample });
        float bossX = sample.BossX.GetValueOrDefault();
        if (!bossReviewBlockObserved || !sample.BossX.HasValue || (!bossReviewCapturedWait && Math.Abs(sample.X - bossX) < 1f))
        { FinishBossReviewNative("failed_setup", "No actual named Attack block with empty review Wait and same live Boss was observed."); return; }

        bossReviewSequence = outcomes[0].Sequence;
        bossReviewAwaySign = Math.Sign(sample.X - bossX);
        bossReviewBlockAmmo = sample.Ammo;
        bossReviewBlockShots = sample.Shots;
        bossReviewBlockDamage = sample.BossDamage;
        SetBossReviewPhase(BossReviewPhase.Review);
        if (bossReviewCapturedWait) BeginBossReviewCapturedObservation(sample);
    }

    private void WatchBossReviewResponse(Player observer, BossReviewFrame previous, BossReviewFrame sample)
    {
        if (!BossReviewWaiting(sample) || agentJournal.LastSequence != bossReviewSequence)
        { FinishBossReviewNative("failed", "Review changed task/goal state or executed another decision."); return; }
        float delta = sample.X - previous.X;
        bool impact = sample.JustHit || sample.Life < previous.Life;
        bool groundedStep = previous.Grounded && sample.Grounded && !impact && !sample.BossOverlapsActor;
        if (groundedStep && sample.BossX is float bossX && Math.Sign(sample.X - bossX) == bossReviewAwaySign)
            bossReviewGroundedAway = Math.Max(0, bossReviewGroundedAway + delta * bossReviewAwaySign);

        if (bossReviewRetreatTick == 0 && bossReviewGroundedAway >= BossReviewRetreatPixels && sample.Shots == bossReviewBlockShots)
        {
            bossReviewRetreatTick = sample.Tick;
            bossReviewEvents.Add(new { kind = "grounded_retreat_before_shot", sample, awayPixels = bossReviewGroundedAway });
        }
        if (sample.Shots > bossReviewBlockShots && bossReviewRetreatTick == 0)
        { FinishBossReviewNative("failed", "A new shot preceded observed grounded retreat."); return; }

        bool danger = sample.BossChaseable && sample.LineOfSight && sample.Distance is <= 15f * 16f;
        bool stationary = Math.Abs(delta) < 0.15f && Math.Abs(sample.Vx) < 0.15f && groundedStep;
        bossReviewIdleFrames = danger && stationary && bossReviewRetreatTick == 0 && sample.Shots == bossReviewBlockShots && sample.Ammo == bossReviewBlockAmmo
            ? bossReviewIdleFrames + 1 : 0;
        if (bossReviewIdleFrames >= BossReviewIdleTicks)
        {
            bossReviewIdleObserved = true;
            FinishBossReviewNative("failed", "review_guard_idle_under_danger");
            return;
        }

        BossReviewShot? fired = bossReviewShots.Skip(bossReviewBlockShots).FirstOrDefault(shot =>
            shot.Tick > bossReviewRetreatTick && shot.Phase == BossReviewPhase.Review && shot.Action == AIActionType.Wait &&
            !shot.HasPlan && shot.Type == ProjectileID.Bullet && bossReviewHits.Any(hit => hit.Owner == shot.Owner && hit.Identity == shot.Identity &&
                (bossReviewTerminalGate == BossReviewTerminalGate.None && bossReviewIdentityVariant == BossReviewIdentityVariant.None || hit.Tick >= shot.Tick)));
        if (fired != null && sample.Ammo < bossReviewBlockAmmo && sample.BossDamage > bossReviewBlockDamage)
        {
            GoalNativeConditionResult? native = ReadBossReviewPreservation();
            bossReviewProductionBeforeHold = PreservationEvidence.DebugSnapshot();
            bossReviewEvents.Add(new { kind = "new_review_shot_and_same_spawn_damage", shot = fired, sample, native });
            if (native?.Met != true)
            { FinishBossReviewNative("failed", "Original goal protection failed during review."); return; }
            if (bossReviewIdentityVariant != BossReviewIdentityVariant.None)
                BeginBossReviewIdentity(observer, sample);
            else if (bossReviewTerminalGate == BossReviewTerminalGate.None)
                BeginBossReviewHold(observer, sample);
            else
                BeginBossReviewTerminal(sample);
            return;
        }
        if (bossReviewFrame - bossReviewPhaseFrame > 180)
            FinishBossReviewNative("failed", "Review deadline without an ammo-backed new shot and same-encounter hit after retreat.");
    }

    private bool BossReviewWaiting(BossReviewFrame sample) => sample.Action == AIActionType.Wait && !sample.HasPlan &&
        sample.Queue == 0 && sample.ReviewPending && !sample.Hold && ActiveGoal == BossReviewGoal && !goalWaitingForPlayer;

    private void BeginBossReviewHold(Player observer, BossReviewFrame sample)
    {
        bossReviewHoldAmmo = sample.Ammo;
        bossReviewHoldShotCount = sample.Shots;
        bossReviewHoldTick = sample.Tick;
        var inFlight = Main.projectile.Where(p => p.active && p.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion)
            .Select(p => new { p.owner, p.identity, p.type }).ToArray();
        string stopMessage = Environment.GetEnvironmentVariable("AITEAMMATE_BOSS_STOP_PUNCTUATION") == "1" ? "停下！" : "停下";
        bool repeatAfterNewTask = Environment.GetEnvironmentVariable("AITEAMMATE_BOSS_STOP_REPEAT") == "1";
        bool firstStopRouted = true;
        if (stopMessage != "停下" || repeatAfterNewTask) firstStopRouted = HandleChatMessage(stopMessage);
        else HandlePlayerMessage(observer, stopMessage);
        bool immediate = Executor.HoldPosition && Executor.CurrentAction == AIActionType.Wait && !Executor.HasCurrentPlan &&
            Executor.SnapshotTasks().Count == 0 && !Executor.IsAutoDefending && ManualControlActive && ActiveGoal.Length == 0;
        bossReviewEvents.Add(new { kind = "exact_stop_command", command = stopMessage, immediate, inFlight, before = sample });
        if (!immediate || !firstStopRouted)
        { FinishBossReviewNative("failed", "Exact stop did not synchronously clear the review action and hold."); return; }
        if (repeatAfterNewTask)
        {
            // A fresh direct command can arrive between two identical chat stops.
            // Exercise real entry points synchronously, without advancing physics.
            HandlePlayerMessage(observer, "攻击");
            bool armed = !Executor.HoldPosition && Executor.CurrentAction == AIActionType.Attack && Executor.HasCurrentPlan;
            bool sameTick = Main.GameUpdateCount == sample.Tick;
            bool secondStopRouted = HandleChatMessage(stopMessage);
            bool stoppedAgain = Executor.HoldPosition && Executor.CurrentAction == AIActionType.Wait &&
                !Executor.HasCurrentPlan && Executor.SnapshotTasks().Count == 0 && !Executor.IsAutoDefending &&
                ManualControlActive && ActiveGoal.Length == 0;
            bossReviewEvents.Add(new { kind = "repeat_stop_after_new_direct_task", command = stopMessage,
                armed, firstStopRouted, secondStopRouted, stoppedAgain, sameTick });
            if (!armed || !secondStopRouted || !sameTick || !stoppedAgain)
            { FinishBossReviewNative("failed", "A second submission of the same stop did not cancel a fresh direct attack."); return; }
        }
        if (sample.JustHit) bossReviewKnockbackUntil = sample.Tick + 2;
        SetBossReviewPhase(BossReviewPhase.Hold);
    }

    private void WatchBossReviewHold(BossReviewFrame previous, BossReviewFrame sample)
    {
        if (bossReviewCapturedWait && !ReferenceEquals(CompanionNpc?.ModNPC, bossReviewCapturedActor))
        { FinishBossReviewNative("failed", "The original companion instance changed during the captured Stop observation."); return; }
        if (sample.JustHit || sample.Life < previous.Life) bossReviewKnockbackUntil = sample.Tick + 2;
        float delta = Math.Abs(sample.X - previous.X);
        if (delta > 0.15f && sample.Tick <= bossReviewKnockbackUntil) bossReviewHoldKnockback += delta;
        bool voluntaryMovement = delta > 0.15f && sample.Tick > bossReviewKnockbackUntil;
        if (!sample.Hold || sample.Action != AIActionType.Wait || sample.HasPlan || sample.Queue != 0 || sample.AutoDefend ||
            sample.Ammo != bossReviewHoldAmmo || sample.Shots != bossReviewHoldShotCount || voluntaryMovement)
        { FinishBossReviewNative("failed", "Hold lost, new shot/ammo use, or horizontal movement without a native hit."); return; }
        if (bossReviewFrame - bossReviewPhaseFrame >= BossReviewHoldTicks)
        {
            bool passed = !bossReviewCapturedWait || bossReviewCapturedWindowPassed;
            FinishBossReviewNative(passed ? "passed" : "failed", passed ? "" : bossReviewCapturedWindowError);
        }
    }

    private void BeginBossReviewTerminal(BossReviewFrame sample)
    {
        if (CompanionNpc?.ModNPC is not { } companion)
        { FinishBossReviewNative("failed_setup", "The original companion instance was unavailable at terminal injection."); return; }

        // Remove only shots that already exist at the measurement boundary and
        // keep their spawn/hit history in the report. No new attack is injected.
        Projectile[] inFlight = Main.projectile.Where(p => p.active && p.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion).ToArray();
        var removed = inFlight.Select(p => new { p.owner, p.identity, p.type, p.timeLeft }).ToArray();
        foreach (Projectile projectile in inFlight) projectile.active = false;

        const string configuration = "controlled_disabled_llm_boss_review";
        long state = decisionGate.State;
        if (bossReviewTerminalGate == BossReviewTerminalGate.Api)
            plannerApiFailures.RecordIfTerminal(new HttpRequestException("Controlled gate probe", null, HttpStatusCode.PaymentRequired), configuration);
        else
            outputLimitGate.Record(configuration, 8192, 8192, 8192, state);

        bossReviewTerminalBaseline = new(sample.Ammo, sample.Shots, GoalIdentity, GoalStartSequence, agentJournal.LastSequence,
            companion, Math.Max(18, (Loadout.GetBestWeapon()?.useTime ?? 16) + 2));
        bool latched = BossReviewGateLatched();
        bossReviewEvents.Add(new { kind = "terminal_gate_injected", gate = bossReviewTerminalGate, latched, state,
            goalId = GoalIdentity, goalStart = GoalStartSequence, spawnId = bossReviewSpawnId, sample, removedInFlight = removed,
            api = plannerApiFailures.LastFailure, output = outputLimitGate.LastFailure });
        if (!latched)
        { FinishBossReviewNative("failed_setup", "The native terminal gate did not latch."); return; }
        SetBossReviewPhase(BossReviewPhase.Terminal);
    }

    private bool BossReviewGateLatched() => bossReviewTerminalGate == BossReviewTerminalGate.Api
        ? plannerApiFailures.IsBlocked && plannerApiFailures.LastFailure?.httpStatus == 402
        : outputLimitGate.IsBlocked && outputLimitGate.LastFailure?.code == "output_limit";

    private void WatchBossReviewTerminal(NPC npc, BossReviewFrame sample)
    {
        BossReviewTerminalBaseline baseline = bossReviewTerminalBaseline!;
        bool goalRetained = ActiveGoal == BossReviewGoal && GoalIdentity == baseline.GoalId && GoalStartSequence == baseline.GoalStart &&
            agentJournal.LastSequence == baseline.OutcomeSequence && ReferenceEquals(npc.ModNPC, baseline.Companion) &&
            sample.Action == AIActionType.Wait && !sample.HasPlan && sample.Queue == 0 && !sample.Hold;
        bool firingPosition = sample.BossChaseable && sample.BossX is float bossX && sample.BossY is float bossY &&
            CompanionMovement.InAttackPosition(bossX - sample.X, bossY - sample.Y, CompanionWeapons.AttackRange(Loadout), sample.LineOfSight);
        if (firingPosition) bossReviewTerminalFiringFrames++;
        bool newFire = sample.Shots != baseline.Shots || sample.Ammo != baseline.Ammo;
        bool latched = BossReviewGateLatched();
        int ticks = bossReviewFrame - bossReviewPhaseFrame;
        if (goalRetained && latched && !newFire && ticks < BossReviewHoldTicks) return;

        GoalNativeConditionResult? native = ReadBossReviewPreservation();
        bossReviewProductionBeforeHold = PreservationEvidence.DebugSnapshot();
        bool enoughOpportunity = bossReviewTerminalFiringFrames >= baseline.RequiredFiringFrames;
        bool passed = goalRetained && latched && !newFire && enoughOpportunity && native?.Met == true;
        bossReviewEvents.Add(new { kind = "terminal_gate_observation", gate = bossReviewTerminalGate, ticks,
            firingPositionFrames = bossReviewTerminalFiringFrames, baseline.RequiredFiringFrames, goalRetained, latched, newFire,
            ammoBefore = baseline.Ammo, shotsBefore = baseline.Shots, spawnId = bossReviewSpawnId, native, sample });
        string error = newFire ? "terminal_gate_continued_attack" : !goalRetained || !latched ? "Terminal gate or original goal/companion changed." :
            !enoughOpportunity ? "Too few real in-range visible frames after the terminal gate." : "Original goal preservation failed after the terminal gate.";
        FinishBossReviewNative(passed ? "passed" : "failed", passed ? "" : error);
    }

    private void SetBossReviewPhase(BossReviewPhase phase)
    {
        bossReviewPhase = phase;
        bossReviewPhaseFrame = bossReviewFrame;
    }

    private NPC? FindBossReviewEncounter()
    {
        BossEncounter? encounter = BossEvidence.ReadEncounter(bossReviewSpawnId);
        if (encounter == null || encounter.Slot < 0 || encounter.Slot >= Main.maxNPCs) return null;
        NPC boss = Main.npc[encounter.Slot];
        return boss.active && boss.type == NPCID.KingSlime && boss.life > 0 && boss.GetGlobalNPC<BossWitness>().SpawnId == encounter.SpawnId
            ? boss : null;
    }

    private BossReviewFrame CaptureBossReviewFrame(NPC npc, Player observer, NPC? boss) => new(
        Main.GameUpdateCount, bossReviewPhase, npc.Center.X, npc.Center.Y, npc.Bottom.Y, npc.velocity.X, npc.velocity.Y,
        TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width), npc.justHit, npc.life,
        observer.Center.X, observer.Center.Y, observer.statLife, boss?.Center.X, boss?.Center.Y, boss?.life,
        boss?.CanBeChasedBy() == true, boss != null && boss.Hitbox.Intersects(npc.Hitbox),
        boss == null ? null : Vector2.Distance(npc.Center, boss.Center), boss != null && Collision.CanHitLine(npc.Center, 1, 1, boss.Center, 1, 1),
        Loadout.CountItem(ItemID.MusketBall), bossReviewShots.Count, BossEvidence.ReadEncounter(bossReviewSpawnId)?.CompanionDamage ?? 0,
        Executor.CurrentAction, Executor.HasCurrentPlan, Executor.SnapshotTasks().Count, goalReviewPending, Executor.HoldPosition, Executor.IsAutoDefending);

    private void MaintainBossReviewObserver(Player observer)
    {
        observer.controlUseItem = observer.controlUseTile = false;
        observer.itemTime = observer.itemAnimation = 0;
        observer.statLife = observer.statLifeMax2;
        foreach (NPC other in Main.npc)
            if (other.active && other != CompanionNpc && (!other.friendly || other.townNPC) &&
                (bossReviewSpawnId == 0 ? other.type != NPCID.KingSlime : other.GetGlobalNPC<BossWitness>().SpawnId != bossReviewSpawnId))
                other.active = false;
    }

    private static BossReviewRunway? FindBossReviewRunway(NPC npc, Player observer)
    {
        NPC king = ContentSamples.NpcsByNetId[NPCID.KingSlime];
        int[] columns = Enumerable.Range(3102, 96).Concat(Enumerable.Range(3275, 56)).OrderBy(x => Math.Abs(x - 3173)).ToArray();
        int[] floors = Enumerable.Range(388, 22).OrderBy(y => Math.Abs(y - 395)).ToArray();
        foreach (int retreat in new[] { 8, 5, 3 })
            foreach (int x in columns)
                foreach (int floor in floors)
                    foreach (int side in new[] { 1, -1 })
                    {
                        if (!Enumerable.Range(0, retreat + 1).All(i => BossReviewDryFloor(x - i * side, floor, npc.width, npc.height))) continue;
                        int observerX = x - side * 3, bossX = x + side * 6;
                        if (!BossReviewDryFloor(observerX, floor, observer.width, observer.height) ||
                            !BossReviewDryBody(bossX * 16f + 8, floor * 16f - 4, king.width, king.height)) continue;
                        return new(x, observerX, bossX, floor, retreat);
                    }
        return null;
    }

    private static bool BossReviewDryFloor(int x, int y, int width, int height)
    {
        Tile tile = Main.tile[x, y];
        return tile.HasTile && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType] && !tile.IsActuated &&
            !tile.IsHalfBlock && (int)tile.Slope == 0 && tile.LiquidAmount == 0 && BossReviewDryBody(x * 16f + 8, y * 16f, width, height);
    }

    private static bool BossReviewDryBody(float centerX, float bottom, int width, int height)
    {
        Vector2 topLeft = new(centerX - width * 0.5f, bottom - height);
        if (Collision.SolidCollision(topLeft, width, height)) return false;
        for (int x = (int)(topLeft.X / 16f); x <= (int)((centerX + width * 0.5f - 0.1f) / 16f); x++)
            for (int y = (int)(topLeft.Y / 16f); y <= (int)((bottom - 0.1f) / 16f); y++)
                if (Main.tile[x, y].LiquidAmount != 0) return false;
        return true;
    }

    private static Dictionary<string, string> ReadBossReviewHashes()
    {
        var hashes = new Dictionary<string, string>();
        string root = Main.SavePath;
        void Hash(string path)
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            hashes[Path.GetRelativePath(root, path).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(stream));
        }
        string package = Path.Combine(root, "Mods", "TerrariaAITeammate.tmod");
        if (File.Exists(package)) Hash(package);
        foreach (string folder in new[] { "Worlds", "Players" })
        {
            string directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (string path in Directory.EnumerateFiles(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                if (Path.GetExtension(path).ToLowerInvariant() is ".wld" or ".twld" or ".plr" or ".tplr") Hash(path);
        }
        return hashes;
    }

    private GoalNativeConditionResult? ReadBossReviewPreservation() =>
        GoalWorldObserver.Capture(this, BossReviewConditions, goalBossDeathBoundary).NativeConditions.GetValueOrDefault(0);

    private void FinishBossReviewNative(string status, string error)
    {
        bossReviewFinished = true;
        try
        {
            if (bossReviewCapturedWait) AddBossReviewCapturedTrace();
            ShelterPreservationReport? preservation = bossReviewAudit?.CheckPreservation();
            if (((status is "passed" or "passed_lifecycle_only") || bossReviewIdleObserved) && preservation is not { preserved: true, coverageComplete: true, diffCount: 0 })
            { status = "failed"; error = "Original-house protection comparison failed."; bossReviewIdleObserved = false; }
            if (bossReviewIdleObserved) ReadBossReviewPreservation();
            string directory = Path.Combine(Main.SavePath, "AITeammate");
            Directory.CreateDirectory(directory);
            string resultFile = BossReviewIdentityResultFile ?? (bossPursuitTimeout ? "boss-pursuit-timeout-native-result.json" :
                bossReviewCapturedWait ? "boss-review-captured-wait-native-result.json" : bossReviewTerminalGate switch
            {
                BossReviewTerminalGate.Api => "boss-review-terminal-api-result.json",
                BossReviewTerminalGate.Output => "boss-review-terminal-output-result.json",
                _ => "boss-review-native-result.json"
            });
            File.WriteAllText(Path.Combine(directory, resultFile), JsonSerializer.Serialize(new
            {
                status, error, world = Main.worldName, worldId = Main.worldID, epoch = WorldKnowledgeObserver.LoadEpoch,
                phase = bossReviewPhase, frames = bossReviewFrame, blockedWaitObserved = bossReviewBlockObserved,
                stationaryDangerObserved = bossReviewIdleObserved, stationaryDangerTicks = bossReviewIdleFrames,
                groundedAwayPixels = bossReviewGroundedAway, retreatTick = bossReviewRetreatTick, holdTick = bossReviewHoldTick,
                holdFrames = bossReviewPhase == BossReviewPhase.Hold ? bossReviewFrame - bossReviewPhaseFrame : 0,
                holdKnockbackPixels = bossReviewHoldKnockback, hitsByAlreadyFiredProjectileDuringHold = bossReviewHits.Where(hit => hit.Tick > bossReviewHoldTick && bossReviewHoldTick > 0),
                encounter = BossEvidence.ReadEncounter(bossReviewSpawnId), primaryRequests = PrimaryDecisionRequestCount,
                combatRequests = CombatDecisionRequestCount, warps = Executor.NavigationTeleportCount, llmSettingsNull = GetLlmSettings() == null,
                fixtureBaseline = bossReviewAudit?.BaselineReport, preservation,
                production = bossReviewProductionBeforeHold ?? PreservationEvidence.DebugSnapshot(),
                copiedInputHashes = bossReviewHashesBefore, isolatedFileHashesAtFinish = ReadBossReviewHashes(),
                events = bossReviewEvents, trace = bossReviewTrace, shots = bossReviewShots, hits = bossReviewHits,
                scope = BossReviewIdentityScope ?? (bossPursuitTimeout ?
                    "Controlled original-house copy. Native Crown and Attack, one far placement of observer and Boss after summon; real Boss AI thereafter. Zero API." :
                    bossReviewCapturedWait ? BossReviewCapturedWaitScope :
                    "Controlled original-house copy. Native equipment, Crown use and named Attack. One fresh Boss position injection and one FailNavigation call after acquisition. " +
                    "Observer is protected and other enemies excluded. No companion repositioning after setup, direct tile edits, decision/API, or kill claim." +
                    (bossReviewTerminalGate == BossReviewTerminalGate.None ? "" : " After a real review hit, only the chosen terminal gate is recorded and any existing in-flight companion projectiles are removed and listed."))
            }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        }
        finally { Stop(); }
        string caseName = BossReviewIdentityCaseName ?? (bossPursuitTimeout ? BossPursuitTimeoutCase :
            bossReviewCapturedWait ? BossReviewCapturedWaitCase : bossReviewTerminalGate == BossReviewTerminalGate.None ? BossReviewNativeCase :
            bossReviewTerminalGate == BossReviewTerminalGate.Api ? BossReviewTerminalApiCase : BossReviewTerminalOutputCase);
        TestLog($"{caseName} {status}: {error}");
    }

    internal void RecordBossReviewShot(Projectile projectile, NPC owner)
    {
        if (!BossReviewObserving || owner != CompanionNpc) return;
        bossReviewShots.Add(new(projectile.owner, projectile.identity, projectile.type, Main.GameUpdateCount, bossReviewPhase,
            Executor.CurrentAction, Executor.HasCurrentPlan, Loadout.CountItem(ItemID.MusketBall), projectile.Center.X, projectile.Center.Y));
    }

    internal void RecordBossReviewHit(Projectile projectile, NPC target, int damage)
    {
        if (!BossReviewObserving || bossReviewSpawnId == 0 || target.GetGlobalNPC<BossWitness>().SpawnId != bossReviewSpawnId ||
            damage <= 0 || !projectile.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion) return;
        bossReviewHits.Add(new(projectile.owner, projectile.identity, Main.GameUpdateCount, bossReviewSpawnId, damage));
    }
}

public sealed class BossReviewProjectileWitness : GlobalProjectile
{
    public override void OnSpawn(Projectile projectile, IEntitySource source)
    {
        if (source is EntitySource_Parent { Entity: NPC owner })
            AITeammateSystem.Instance?.RecordBossReviewShot(projectile, owner);
    }

    public override void OnHitNPC(Projectile projectile, NPC target, NPC.HitInfo hit, int damageDone) =>
        AITeammateSystem.Instance?.RecordBossReviewHit(projectile, target, damageDone);
}

public sealed class BossReviewObserver : ModPlayer
{
    public override bool ImmuneTo(PlayerDeathReason damageSource, int cooldownCounter, bool dodgeable) =>
        AITeammateSystem.Instance?.BossReviewObserving == true;
}
