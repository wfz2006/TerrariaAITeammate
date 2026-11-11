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

public partial class AITeammateSystem
{
    private const string P4PlanningDefenseCase = "p4_planning_defense_native";
    private const string P4PlanningDefenseGoal = "收集一颗红玉";
    private const string P4PlanningDefenseRequest = "请找到一颗天然红玉并带着它，保留原屋。";
    // Exact vanilla eye variants from the failed 30CF planning wait.
    private const int P4ReviewEye = 190, P4StopEye = 192, P4DefenseAmmo = 24, P4DefenseStopTicks = 72;
    private enum P4DefensePhase { GroundedSetup, PendingReview, Stopped }
    private sealed record P4DefenseShot(ulong Tick, int Owner, int Identity, int Type, string Phase, int AmmoAtSpawn);
    private readonly List<P4DefenseShot> p4DefenseShots = new();
    private readonly List<object> p4DefenseEvents = new();
    private bool p4DefenseRunning;
    private P4DefensePhase p4DefensePhase;
    private ShelterWorldAudit? p4DefenseAudit;
    private ModNPC? p4DefenseActor;
    private NPC? p4DefenseThreat;
    private string p4DefenseGoalId = "";
    private long p4DefenseGoalStart, p4DefenseOutcomeBoundary;
    private ulong p4DefensePhaseAt, p4DefenseKnockbackUntil;
    private float p4DefensePreviousX;
    private int p4DefensePreviousLife, p4DefenseStopAmmo, p4DefenseStopShots, p4DefenseStopJumps;
    private int p4DefenseReviewNearFrames, p4DefenseStopNearFrames, p4DefenseStopFrames;
    private static string P4DefenseResultPath => Path.Combine(Main.SavePath, "AITeammate", "p4-planning-defense-native-result.json");
    internal bool P4DefenseObserving => p4DefenseRunning && HouseMode;

    private bool P4DefenseGatesClosed()
    {
        AIConfig config = ModContent.GetInstance<AIConfig>();
        return HouseMode && Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") == P4PlanningDefenseCase &&
            !config.UseLlm && !config.AllowTeleportRecovery && !HousePlanningAllowed && !CombatPlannerEnabled &&
            PrimaryDecisionRequestCount == 0 && CombatDecisionRequestCount == 0 && Executor.NavigationTeleportCount == 0;
    }

    private void BeginP4PlanningDefense()
    {
        if (!HouseMode || File.Exists(P4DefenseResultPath)) return;
        Executor.ResetNavigationTelemetry();
        p4DefenseAudit = ShelterWorldAudit.Capture(3220, 390);
        if (!P4DefenseGatesClosed() || CompanionNpc?.ModNPC is not { } actor ||
            p4DefenseAudit.CheckPreservation() is not { protectedCells: 5803, coverageComplete: true, preserved: true, diffCount: 0 } ||
            !Loadout.AddItem(ItemID.PhoenixBlaster, 1) || !Loadout.AddItem(ItemID.MusketBall, P4DefenseAmmo))
        { FinishP4PlanningDefense("failed_setup", "Fresh original-house actor, ammunition and closed model and teleport gates are required."); return; }
        Loadout.PreferredWeaponType = ItemID.PhoenixBlaster;
        p4DefenseActor = actor;
        p4DefensePhaseAt = Main.GameUpdateCount;
        p4DefenseRunning = true;
        WriteP4DefenseResult("running", "");
    }

    private void TickP4PlanningDefense()
    {
        Player observer = Main.LocalPlayer;
        if (!P4DefenseGatesClosed() || observer.dead || Main.worldID != houseWorldId ||
            p4DefenseAudit?.BaselineReport.epoch != WorldKnowledgeObserver.LoadEpoch ||
            CompanionNpc is not { active: true } npc || !ReferenceEquals(npc.ModNPC, p4DefenseActor))
        { FinishP4PlanningDefense("failed_runtime", "Live original actor or zero API/warp isolation was lost."); return; }
        observer.controlUseItem = observer.controlUseTile = false;
        observer.immune = true;
        observer.immuneTime = 600;
        foreach (NPC other in Main.npc)
            if (other.active && other != npc && other != p4DefenseThreat) other.active = false;
        foreach (Projectile projectile in Main.projectile)
            if (projectile.active && projectile.friendly && projectile.damage > 0 && !projectile.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion)
                projectile.active = false;

        ulong now = Main.GameUpdateCount;
        if (p4DefensePhase == P4DefensePhase.GroundedSetup)
        {
            if (TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width)) StartP4DefenseReview(npc, observer);
            else if (now - p4DefensePhaseAt > 120) FinishP4PlanningDefense("failed_setup", "Original-house actor did not settle at the actual spawn.");
            return;
        }
        if (npc.justHit || npc.life < p4DefensePreviousLife) p4DefenseKnockbackUntil = now + 2;
        bool voluntaryMove = Math.Abs(npc.Center.X - p4DefensePreviousX) > 0.15f && now > p4DefenseKnockbackUntil;
        p4DefensePreviousX = npc.Center.X;
        p4DefensePreviousLife = npc.life;
        if (voluntaryMove || Executor.IsAutoDefending || Executor.HasCurrentPlan ||
            Executor.CurrentAction != AIActionType.Wait || Executor.SnapshotTasks().Count != 0)
        { FinishP4PlanningDefense("failed_state", "Planning defense or Stop produced a chase, autonomous action, or task replacement."); return; }

        int ammo = Loadout.CountItem(ItemID.MusketBall);
        bool near = p4DefenseThreat is { active: true } threat && Vector2.DistanceSquared(npc.Center, threat.Center) <= 96f * 96f &&
            Collision.CanHitLine(npc.Center, 1, 1, threat.Center, 1, 1);
        if (p4DefensePhase == P4DefensePhase.Stopped)
        {
            if (near) p4DefenseStopNearFrames++;
            p4DefenseStopFrames++;
            if (!P4DefenseStopIsActive() || ammo != p4DefenseStopAmmo || p4DefenseShots.Count != p4DefenseStopShots ||
                Executor.NavigationJumpCount != p4DefenseStopJumps)
            { FinishP4PlanningDefense("failed_stop", "After the exact stop command a new shot, movement action, or goal appeared."); return; }
            if (p4DefenseStopFrames >= P4DefenseStopTicks)
                FinishP4PlanningDefense(p4DefenseStopNearFrames > 0 ? "passed" : "stop_threat_uncovered",
                    p4DefenseStopNearFrames > 0 ? "" : "A live close eye was not observed after Stop.");
            return;
        }

        if (near) p4DefenseReviewNearFrames++;
        if (!P4DefenseReviewIsActive())
        { FinishP4PlanningDefense("failed_state", "The accepted unfinished goal or its pending review changed."); return; }
        CompanionHitSnapshot? hit = p4DefenseThreat?.GetGlobalNPC<CompanionHitWitness>().Snapshot();
        if (ammo < P4DefenseAmmo && hit is { taggedProjectileHits: > 0, taggedProjectileDamage: > 0, lastProjectileType: ProjectileID.Bullet } &&
            p4DefenseShots.Any(shot => shot.Type == ProjectileID.Bullet && shot.Tick <= hit.lastTick && shot.Phase == nameof(P4DefensePhase.PendingReview)))
        {
            GoalNativeConditionResult? preservation = GoalWorldObserver.Capture(this, goalPlanningState.Completion.Conditions, goalBossDeathBoundary)
                .NativeConditions.GetValueOrDefault(1);
            p4DefenseEvents.Add(new { kind = "native_review_bullet_hit", tick = now, hit, ammoBefore = P4DefenseAmmo, ammoAfter = ammo,
                targetType = p4DefenseThreat!.type, targetSlot = p4DefenseThreat.whoAmI, targetLife = p4DefenseThreat.life,
                goalId = GoalIdentity, goalStart = GoalStartSequence, pending = goalReviewPending, nativePreservation = preservation });
            if (preservation?.Met != true)
            { FinishP4PlanningDefense("failed_preservation", "The exact goal's original-house preservation could not be verified."); return; }
            StartP4DefenseStop(npc, observer, ammo);
        }
        else if (now - p4DefensePhaseAt > 180)
            FinishP4PlanningDefense(p4DefenseReviewNearFrames > 0 ? "planning_defense_missing" : "review_threat_uncovered",
                p4DefenseReviewNearFrames > 0 ? "A visible close eye was present without an ammo-backed companion bullet hit during the same pending review."
                    : "No live visible close eye was measured in the review window.");
    }

    private void StartP4DefenseReview(NPC npc, Player observer)
    {
        AIPlan plan = new() { Goal = P4PlanningDefenseGoal, ReplaceTasks = true,
            CompletionConditions = new() { new() { Type = "inventory_at_least", ItemId = ItemID.Ruby, Count = 1 },
                new() { Type = "structure_preserved" } } };
        manualControlActive = false;
        if (!ApplyPlan(plan, null, "chat", P4PlanningDefenseRequest))
        { FinishP4PlanningDefense("failed_setup", "The production plan receiver did not adopt the unfinished test goal."); return; }
        p4DefenseGoalId = GoalIdentity;
        p4DefenseGoalStart = GoalStartSequence;
        p4DefenseOutcomeBoundary = agentJournal.LastSequence;
        if (!P4DefenseReviewIsActive() || Executor.HasCurrentPlan || Executor.CurrentAction != AIActionType.Wait ||
            Loadout.CountItem(ItemID.Ruby) != 0 || Loadout.CountItem(ItemID.MusketBall) != P4DefenseAmmo)
        { FinishP4PlanningDefense("failed_setup", "The accepted goal did not enter actual pending review at idle Wait without Hold."); return; }
        int away = observer.Center.X >= npc.Center.X ? -1 : 1;
        p4DefenseThreat = SpawnP4DefenseEye(npc, P4ReviewEye, away, 44f);
        if (p4DefenseThreat == null)
        { FinishP4PlanningDefense("failed_setup", "A visible vanilla review eye could not be spawned at the original house."); return; }
        p4DefensePhase = P4DefensePhase.PendingReview;
        p4DefensePhaseAt = Main.GameUpdateCount;
        p4DefensePreviousX = npc.Center.X;
        p4DefensePreviousLife = npc.life;
        p4DefenseEvents.Add(new { kind = "review_pending_at_original_house", tick = p4DefensePhaseAt, actorX = npc.Center.X,
            actorFeet = npc.Bottom.Y, npc.life, observerX = observer.Center.X, goalId = p4DefenseGoalId,
            goalStart = p4DefenseGoalStart, reviewPending = goalReviewPending, action = Executor.CurrentAction.ToString(),
            weapon = ItemID.PhoenixBlaster, ammo = P4DefenseAmmo });
        WriteP4DefenseResult("running", "");
    }

    private void StartP4DefenseStop(NPC npc, Player observer, int ammo)
    {
        p4DefenseStopAmmo = ammo;
        p4DefenseStopShots = p4DefenseShots.Count;
        p4DefenseStopJumps = Executor.NavigationJumpCount;
        p4DefensePhase = P4DefensePhase.Stopped;
        p4DefensePhaseAt = Main.GameUpdateCount;
        HandlePlayerMessage(observer, "停下");
        bool immediate = P4DefenseStopIsActive();
        p4DefenseEvents.Add(new { kind = "exact_stop_command", command = "停下", tick = p4DefensePhaseAt, immediate, ammo,
            shots = p4DefenseStopShots, priorGoalId = p4DefenseGoalId });
        if (!immediate)
        { FinishP4PlanningDefense("failed_stop", "The exact local Stop did not synchronously hold and cancel the pending goal."); return; }
        if (p4DefenseThreat is { active: true }) p4DefenseThreat.active = false;
        int toward = observer.Center.X >= npc.Center.X ? 1 : -1;
        p4DefenseThreat = SpawnP4DefenseEye(npc, P4StopEye, toward, 24f);
        if (p4DefenseThreat == null)
        { FinishP4PlanningDefense("failed_setup", "A new close vanilla eye could not be spawned after Stop."); return; }
        WriteP4DefenseResult("running", "");
    }

    private NPC? SpawnP4DefenseEye(NPC npc, int type, int direction, float separation)
    {
        foreach (int side in new[] { direction, -direction })
        {
            Vector2 center = npc.Center + new Vector2(side * separation, 0);
            if (!Collision.CanHitLine(npc.Center, 1, 1, center, 1, 1) ||
                Collision.SolidCollision(center - new Vector2(16, 16), 32, 32)) continue;
            int slot = NPC.NewNPC(new EntitySource_Misc("P4PlanningDefenseNative"), (int)center.X, (int)center.Y, type);
            if (slot < 0 || slot >= Main.maxNPCs) return null;
            NPC eye = Main.npc[slot];
            eye.Center = center; // Single controlled enemy placement. Subsequent AI and damage are native.
            if (!eye.active || eye.friendly || eye.type != type || !eye.CanBeChasedBy() ||
                Collision.SolidCollision(eye.position, eye.width, eye.height)) { eye.active = false; return null; }
            p4DefenseEvents.Add(new { kind = "controlled_vanilla_eye_spawn", tick = Main.GameUpdateCount, type, slot,
                x = eye.Center.X, y = eye.Center.Y, eye.life, separation, visible = true });
            return eye;
        }
        return null;
    }

    private bool P4DefenseReviewIsActive() => goalReviewPending && goalPlanningState.HasGoal && !goalWaitingForPlayer &&
        !manualControlActive && !Executor.HoldPosition && ActiveGoal == P4PlanningDefenseGoal && GoalIdentity == p4DefenseGoalId &&
        GoalStartSequence == p4DefenseGoalStart && agentJournal.LastSequence == p4DefenseOutcomeBoundary;

    private bool P4DefenseStopIsActive() => manualControlActive && Executor.HoldPosition && !goalReviewPending &&
        !goalPlanningState.HasGoal && ActiveGoal.Length == 0 && Executor.CurrentAction == AIActionType.Wait &&
        !Executor.HasCurrentPlan && Executor.SnapshotTasks().Count == 0 && !Executor.IsAutoDefending;

    internal void RecordP4PlanningDefenseShot(Projectile projectile, NPC source)
    {
        if (!P4DefenseObserving || !ReferenceEquals(source.ModNPC, p4DefenseActor)) return;
        p4DefenseShots.Add(new(Main.GameUpdateCount, projectile.owner, projectile.identity, projectile.type,
            p4DefensePhase.ToString(), Loadout.CountItem(ItemID.MusketBall)));
    }

    internal void RecordP4PlanningDefenseContact(NPC source, NPC target, int damage)
    {
        if (!P4DefenseObserving || source != p4DefenseThreat || !ReferenceEquals(target.ModNPC, p4DefenseActor) || damage <= 0) return;
        p4DefenseKnockbackUntil = Main.GameUpdateCount + 2;
        p4DefenseEvents.Add(new { kind = "native_eye_contact", tick = Main.GameUpdateCount, phase = p4DefensePhase.ToString(),
            sourceType = source.type, sourceSlot = source.whoAmI, damage, lifeAfter = target.life });
    }

    private void FinishP4PlanningDefense(string status, string error)
    {
        p4DefenseRunning = false;
        try { WriteP4DefenseResult(status, error); }
        finally
        {
            if (p4DefenseThreat is { active: true }) p4DefenseThreat.active = false;
            Stop();
        }
    }

    private void WriteP4DefenseResult(string status, string error)
    {
        ShelterPreservationReport? protection = status == "running" ? null : p4DefenseAudit?.CheckPreservation();
        if (status == "passed" && protection is not { protectedCells: 5803, coverageComplete: true, preserved: true, diffCount: 0 })
        { status = "failed_preservation"; error = "The original 5803 protected house cells did not verify."; }
        Directory.CreateDirectory(Path.GetDirectoryName(P4DefenseResultPath)!);
        File.WriteAllText(P4DefenseResultPath, JsonSerializer.Serialize(new
        {
            status, error, worldId = Main.worldID, epoch = WorldKnowledgeObserver.LoadEpoch, phase = p4DefensePhase.ToString(),
            requests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount, warps = Executor.NavigationTeleportCount,
            reviewNearFrames = p4DefenseReviewNearFrames, stopFrames = p4DefenseStopFrames, stopNearFrames = p4DefenseStopNearFrames,
            stopStillActive = P4DefenseStopIsActive(), ammo = Loadout.CountItem(ItemID.MusketBall),
            protection, events = p4DefenseEvents, shots = p4DefenseShots,
            scope = "Isolated original-house native control. The real goal receiver adopts one test plan without Flash. Two controlled vanilla eye spawns, real NPC physics and damage, no observer attacks, companion relocation or terrain edits. A tagged ammo-backed shot must hit before Stop; a new eye remains a real threat during the quiet observation."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class P4PlanningDefenseProjectileWitness : GlobalProjectile
{
    public override void OnSpawn(Projectile projectile, IEntitySource source)
    {
        if (source is EntitySource_Parent { Entity: NPC npc })
            AITeammateSystem.Instance?.RecordP4PlanningDefenseShot(projectile, npc);
    }
}

public sealed class P4PlanningDefenseContactWitness : GlobalNPC
{
    public override void OnHitNPC(NPC npc, NPC target, NPC.HitInfo hit) =>
        AITeammateSystem.Instance?.RecordP4PlanningDefenseContact(npc, target, hit.Damage);
}

public sealed class P4PlanningDefenseObserver : ModPlayer
{
    public override bool ImmuneTo(PlayerDeathReason damageSource, int cooldownCounter, bool dodgeable) =>
        AITeammateSystem.Instance?.P4DefenseObserving == true;
}
