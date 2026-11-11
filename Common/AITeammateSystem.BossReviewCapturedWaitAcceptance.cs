using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string BossReviewCapturedWaitCase = "boss_review_captured_wait_native";
    private const int BossReviewCapturedWaitTicks = 1989, BossReviewCapturedLateTicks = 300;
    private const string BossReviewCapturedWaitScope = "Controlled zero-API encounter in the original-house copy. Initial positions approximate outward-rounded tick 4777 envelopes; the native Boss starts at full life with its original velocity. " +
        "Real gear, Crown, named Attack, one initial Boss position and one production FailNavigation call. The immune observer does not attack and other enemies are excluded. " +
        "No tile edits, life changes to either combatant, companion resets after setup, or extended review timer. This is a historical-position approximation, not a replay of the original fight or a natural model test.";

    private sealed record CapturedBossEnvelope(int x, int y, int width, int height);
    private sealed record CapturedBossFootCheck(float x, float feetY, int width, int height, bool dryBody, bool supported);
    private sealed record CapturedBossStand(int x, int standRow, float centerX, float centerY, float feetY);
    private sealed record CapturedBossReviewState(long? SpawnId, int? BossSlot, string? GoalId, bool SameCompanion,
        uint? StartTick, uint? LastWalkProbe, int? WalkDirection, CapturedBossStand? Stand);
    private sealed record CapturedBossControlFrame(ulong Tick, int Frame, BossReviewPhase Phase, bool SameCompanion,
        string GoalId, int ActorSlot, long EncounterSpawnId, int? BossSlot, float? BossFeetY, float? BossVx, float? BossVy,
        int? BossWidth, int? BossHeight, bool? BossInvulnerable, long DeathSequence, CapturedBossReviewState? Review);

    private static readonly CapturedBossEnvelope CapturedCompanionEnvelope = new(3166, 397, 2, 3);
    private static readonly CapturedBossEnvelope CapturedKingSlimeEnvelope = new(3162, 393, 8, 7);
    private static readonly CapturedBossEnvelope CapturedObserverEnvelope = new(3176, 392, 3, 3);
    private static readonly FieldInfo? CapturedWaitReviewField = typeof(AIExecutor).GetField("bossReview", BindingFlags.Instance | BindingFlags.NonPublic);
    private static Vector2 CapturedBossFoot => CapturedEnvelopeFeet(CapturedKingSlimeEnvelope);

    private bool bossReviewCapturedWait, bossReviewCapturedWindowPassed;
    private string bossReviewCapturedGoalId = "", bossReviewCapturedWindowError = "The captured Wait window did not complete.";
    private ModNPC? bossReviewCapturedActor;
    private int bossReviewCapturedActorSlot = -1;
    private ulong bossReviewCapturedBlockTick;
    private long bossReviewCapturedDeathSequence;
    private ulong? bossReviewCapturedDeathObservedTick;
    private readonly List<CapturedBossControlFrame> bossReviewCapturedTrace = new();

    private static Vector2 CapturedEnvelopeFeet(CapturedBossEnvelope envelope) =>
        new((envelope.x + envelope.width / 2f) * 16f, (envelope.y + envelope.height) * 16f);

    private static CapturedBossFootCheck CheckCapturedBossFoot(Vector2 feet, int width, int height) =>
        new(feet.X, feet.Y, width, height, BossReviewDryBody(feet.X, feet.Y, width, height),
            TilePathfinder.IsGroundedAt(feet.X, feet.Y, 0, width));

    private bool PrepareBossReviewCapturedWait(NPC npc, Player observer)
    {
        Vector2 companionFeet = CapturedEnvelopeFeet(CapturedCompanionEnvelope);
        Vector2 observerFeet = CapturedEnvelopeFeet(CapturedObserverEnvelope);
        NPC template = ContentSamples.NpcsByNetId[NPCID.KingSlime];
        CapturedBossFootCheck companion = CheckCapturedBossFoot(companionFeet, npc.width, npc.height);
        CapturedBossFootCheck owner = CheckCapturedBossFoot(observerFeet, observer.width, observer.height);
        CapturedBossFootCheck boss = CheckCapturedBossFoot(CapturedBossFoot, template.width, template.height);
        bool legal = companion.dryBody && companion.supported && owner.dryBody && owner.supported && boss.dryBody && boss.supported;
        bossReviewEvents.Add(new
        {
            kind = "captured_wait_envelope_preflight", legal,
            historicalSource = "TerrariaAITeammate-boss-phases-natural-20261109-agent-acceptance/AITeammate/natural-flash-result.json",
            historicalSecondBlockTick = 4104, historicalEnvelopeTick = 4777, historicalLastHealthSampleTick = 6093,
            actorEnvelope = CapturedCompanionEnvelope, bossEnvelope = CapturedKingSlimeEnvelope, observerEnvelope = CapturedObserverEnvelope,
            companion, bossTemplate = boss, observer = owner,
            placementRule = "Envelope horizontal midpoint and bottom edge, in pixels. Native dimensions and current dry collision/support checks determine legality."
        });
        if (!legal) return false;
        npc.Bottom = companionFeet;
        observer.Bottom = observerFeet;
        bossReviewCapturedActor = npc.ModNPC;
        bossReviewCapturedActorSlot = npc.whoAmI;
        return true;
    }

    private void BeginBossReviewCapturedObservation(BossReviewFrame sample)
    {
        if (bossReviewCapturedActor == null || !ReferenceEquals(CompanionNpc?.ModNPC, bossReviewCapturedActor) || GoalIdentity.Length == 0)
        { FinishBossReviewNative("failed_setup", "The original companion or goal identity was unavailable at the captured Wait boundary."); return; }
        bossReviewCapturedBlockTick = sample.Tick;
        bossReviewCapturedGoalId = GoalIdentity;
        bossReviewEvents.Add(new { kind = "captured_wait_started", sample, spawnId = bossReviewSpawnId,
            goalId = bossReviewCapturedGoalId, requiredTicks = BossReviewCapturedWaitTicks, lateTicks = BossReviewCapturedLateTicks,
            review = ReadCapturedBossReviewState() });
    }

    private void WatchBossReviewCapturedWait(NPC npc, Player observer, BossReviewFrame sample)
    {
        if (!ReferenceEquals(npc.ModNPC, bossReviewCapturedActor) || GoalIdentity != bossReviewCapturedGoalId ||
            !BossReviewWaiting(sample) || agentJournal.LastSequence != bossReviewSequence)
        { FinishBossReviewNative("failed", "The captured Wait lost its original companion or goal, or another task executed."); return; }

        ulong elapsed = sample.Tick - bossReviewCapturedBlockTick;
        if (elapsed < BossReviewCapturedWaitTicks) return;

        bool NewReviewShot(BossReviewShot shot) => shot.Tick > bossReviewCapturedBlockTick && shot.Phase == BossReviewPhase.Review &&
            shot.Action == AIActionType.Wait && !shot.HasPlan && shot.Type == ProjectileID.Bullet;
        BossReviewShot[] shots = bossReviewShots.Where(NewReviewShot).ToArray();
        BossReviewHit[] hits = bossReviewHits.Where(hit => hit.SpawnId == bossReviewSpawnId && hit.Tick > bossReviewCapturedBlockTick &&
            bossReviewShots.LastOrDefault(shot => shot.Owner == hit.Owner && shot.Identity == hit.Identity && shot.Tick <= hit.Tick) is { } source &&
            NewReviewShot(source)).ToArray();
        int ammoConsumed = bossReviewBlockAmmo - sample.Ammo;
        int lateDamage = hits.Where(hit => hit.Tick > sample.Tick - BossReviewCapturedLateTicks).Sum(hit => hit.Damage);
        bool continued = shots.Length >= 2 && ammoConsumed >= 2 && hits.Length > 0;
        bool killed = bossReviewCapturedDeathSequence > 0;
        GoalNativeConditionResult? native = ReadBossReviewPreservation();
        bossReviewProductionBeforeHold = PreservationEvidence.DebugSnapshot();
        bossReviewCapturedWindowPassed = continued && (lateDamage > 0 || killed) && native?.Met == true;
        bossReviewCapturedWindowError = !continued ? "The captured Wait lacked repeated actual shots, ammo use, and a new hit on the same Boss." :
            lateDamage == 0 && !killed ? "The captured Wait had no new same-Boss damage in its final 300 ticks and no native OnKill." :
            "Original goal preservation failed during the captured Wait.";
        bossReviewEvents.Add(new
        {
            kind = "captured_wait_completed", passed = bossReviewCapturedWindowPassed, ticks = elapsed,
            ammoConsumed, newShotCount = shots.Length, newHitCount = hits.Length, newDamage = hits.Sum(hit => hit.Damage), lateDamage,
            spawnId = bossReviewSpawnId, deathSequence = bossReviewCapturedDeathSequence, deathObservedTick = bossReviewCapturedDeathObservedTick,
            goalId = bossReviewCapturedGoalId, bossAliveAtStop = sample.BossLife.HasValue, sample, native
        });
        // Even a completed window with insufficient combat evidence exercises the separate Stop check.
        BeginBossReviewHold(observer, sample);
    }

    private CapturedBossReviewState? ReadCapturedBossReviewState()
    {
        if (CapturedWaitReviewField?.GetValue(Executor) is not { } window) return null;
        Type type = window.GetType();
        object? Read(string property) => type.GetProperty(property)?.GetValue(window);
        CapturedBossStand? stand = Read("Stand") is TileStandPose pose
            ? new(pose.Node.X, pose.Node.Y, pose.Center.X, pose.Center.Y, pose.Bottom) : null;
        return new(Read("SpawnId") as long?, Read("Slot") as int?, Read("GoalId") as string,
            ReferenceEquals(Read("Companion"), bossReviewCapturedActor), Read("StartTick") as uint?, Read("LastWalkProbe") as uint?,
            Read("WalkDirection") as int?, stand);
    }

    private void RecordBossReviewCapturedFrame(NPC npc, NPC? boss, BossReviewFrame sample)
    {
        if (boss == null && bossReviewCapturedDeathSequence == 0 &&
            BossEvidence.ReadEncounter(bossReviewSpawnId) is { DeathSequence: > 0 } encounter)
        {
            bossReviewCapturedDeathSequence = encounter.DeathSequence;
            bossReviewCapturedDeathObservedTick = sample.Tick;
            bossReviewEvents.Add(new { kind = "captured_same_boss_native_death", encounter, observedAtTick = sample.Tick });
        }
        bossReviewCapturedTrace.Add(new(sample.Tick, bossReviewFrame, sample.Phase, ReferenceEquals(npc.ModNPC, bossReviewCapturedActor),
            GoalIdentity, npc.whoAmI, bossReviewSpawnId, boss?.whoAmI, boss?.Bottom.Y, boss?.velocity.X, boss?.velocity.Y,
            boss?.width, boss?.height, boss?.dontTakeDamage, bossReviewCapturedDeathSequence, ReadCapturedBossReviewState()));
    }

    private void RecordBossReviewCapturedInterruption()
    {
        NPC? slot = bossReviewCapturedActorSlot >= 0 && bossReviewCapturedActorSlot < Main.maxNPCs ? Main.npc[bossReviewCapturedActorSlot] : null;
        bool sameInstance = slot != null && ReferenceEquals(slot.ModNPC, bossReviewCapturedActor);
        bossReviewEvents.Add(new { kind = "captured_wait_interrupted", tick = Main.GameUpdateCount, originalSlot = bossReviewCapturedActorSlot,
            sameInstance, actor = sameInstance && slot is { } current ? new { current.active, current.life, x = current.Center.X, y = current.Center.Y, feetY = current.Bottom.Y } : null,
            observerDead = Main.LocalPlayer.dead, goalId = GoalIdentity, review = ReadCapturedBossReviewState() });
    }

    private void AddBossReviewCapturedTrace() => bossReviewEvents.Add(new
    {
        kind = "captured_wait_control_trace", requiredWaitTicks = BossReviewCapturedWaitTicks, blockTick = bossReviewCapturedBlockTick,
        internalReviewFieldAvailable = CapturedWaitReviewField != null, frames = bossReviewCapturedTrace,
        note = "Join to the shared numeric HP, position, LOS, ammo and shot trace by Tick. Review stance and identity are read-only reflections of the executor's current window."
    });
}
