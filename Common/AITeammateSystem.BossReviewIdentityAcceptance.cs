using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string BossReviewCompanionRespawnCase = "boss_review_companion_respawn_native";
    private const string BossReviewReplacedBossCase = "boss_review_replaced_boss_native";
    private const int BossReviewIdentityObservationTicks = 72;
    private static readonly FieldInfo? BossReviewIdentityWindowField = typeof(AIExecutor).GetField("bossReview", BindingFlags.Instance | BindingFlags.NonPublic);

    private enum BossReviewIdentityVariant { None, CompanionRespawn, ReplacedBoss }
    private sealed record BossReviewIdentityState(string Goal, string GoalId, long Intent, long OutcomeSequence,
        bool ReviewEligible, bool Hold, AIActionType Action, bool HasPlan, int Queue, bool WindowPresent, bool? ExplicitWait, bool AutoDefend);
    private sealed record BossReviewIdentityBaseline(object OldWindow, ModNPC Companion, long TargetSpawnId,
        BossReviewIdentityState OriginalState, bool DirectGuardChallenge, int Ammo, int Shots, int RequiredFiringFrames);

    private BossReviewIdentityVariant bossReviewIdentityVariant;
    private ModNPC? bossReviewIdentityKilledCompanion;
    private bool bossReviewIdentityCompanionOnKill;
    private BossReviewIdentityBaseline? bossReviewIdentityBaseline;
    private int bossReviewIdentityFiringFrames;
    private ulong? bossReviewIdentityWindowGoneTick;
    private ulong bossReviewIdentityKnockbackUntil;
    private float bossReviewIdentityKnockbackPixels;

    private object? BossReviewIdentityWindow => BossReviewIdentityWindowField?.GetValue(Executor);
    private string? BossReviewIdentityCaseName => bossReviewIdentityVariant switch
    {
        BossReviewIdentityVariant.CompanionRespawn => BossReviewCompanionRespawnCase,
        BossReviewIdentityVariant.ReplacedBoss => BossReviewReplacedBossCase,
        _ => null
    };
    private string? BossReviewIdentityResultFile => bossReviewIdentityVariant switch
    {
        BossReviewIdentityVariant.CompanionRespawn => "boss-review-companion-respawn-native-result.json",
        BossReviewIdentityVariant.ReplacedBoss => "boss-review-replaced-boss-native-result.json",
        _ => null
    };
    private string? BossReviewIdentityScope => bossReviewIdentityVariant switch
    {
        BossReviewIdentityVariant.CompanionRespawn => "Controlled original-house copy, zero API. Real first Crown, named Attack, one injected navigation failure and real review hit. " +
            "Native StrikeInstantKill and OnKill on the old companion, then production SpawnCompanion in the same game tick. " +
            "The observer faces the measured dry runway at setup; the new companion uses the unmodified spawn position. No Stop before observation completes, no review/goal/gate restoration, no tile edits. Observer immune and other enemies excluded.",
        BossReviewIdentityVariant.ReplacedBoss => "Controlled original-house copy, zero API. Real first Crown, named Attack, one injected navigation failure and real review hit. " +
            "Old Boss is killed by native StrikeInstantKill and native OnKill, followed in the same game tick by production summoner use with a second Crown. " +
            "Replacement must have a different SpawnId in the same native slot. One replacement Boss position at the old dry feet; no companion movement injection, Stop before observation completes, review/goal/gate restoration, or tile edits. Observer immune and other enemies excluded.",
        _ => null
    };

    private BossReviewIdentityState ReadBossReviewIdentityState() => new(ActiveGoal, GoalIdentity, decisionGate.Intent,
        agentJournal.LastSequence, BossReviewPending, Executor.HoldPosition, Executor.CurrentAction,
        Executor.HasCurrentPlan, Executor.SnapshotTasks().Count, BossReviewIdentityWindow != null,
        ContinuousField("waitRequested") as bool?, Executor.IsAutoDefending);

    private void BeginBossReviewIdentity(Player observer, BossReviewFrame sample)
    {
        if (BossReviewIdentityWindow is not { } oldWindow || CompanionNpc is not { active: true } originalActor ||
            originalActor.ModNPC is not { } originalCompanion || FindBossReviewEncounter() is not { } originalBoss)
        { FinishBossReviewNative("uncovered_setup", "A live review window, original companion and original Boss are required at identity handoff."); return; }

        BossReviewIdentityState before = ReadBossReviewIdentityState();
        bossReviewEvents.Add(new { kind = "identity_before_handoff", variant = bossReviewIdentityVariant, state = before, sample,
            companionSlot = originalActor.whoAmI, bossSlot = originalBoss.whoAmI, spawnId = bossReviewSpawnId });
        bool replaced = bossReviewIdentityVariant == BossReviewIdentityVariant.CompanionRespawn
            ? RespawnBossReviewIdentityCompanion(observer, originalActor, originalCompanion)
            : ReplaceBossReviewIdentityBoss(observer, originalActor, originalBoss);
        if (!replaced) return;
        if (CompanionNpc is not { active: true } actor || actor.ModNPC is not { } companion || FindBossReviewEncounter() is not { } boss)
        { FinishBossReviewNative("failed", "The handoff did not leave a live companion and a tracked Boss."); return; }

        BossReviewIdentityState after = ReadBossReviewIdentityState();
        bool sameWindow = ReferenceEquals(BossReviewIdentityWindow, oldWindow);
        bool sameIntent = before.Intent == after.Intent && before.GoalId == after.GoalId && before.Goal == after.Goal;
        bool direct = sameWindow && sameIntent && before.ReviewEligible && after.ReviewEligible && !after.Hold &&
            after.Action == AIActionType.Wait && !after.HasPlan && after.Queue == 0 && after.ExplicitWait == false &&
            !after.AutoDefend && before.OutcomeSequence == after.OutcomeSequence;
        SetBossReviewPhase(BossReviewPhase.Identity);
        BossReviewFrame boundary = CaptureBossReviewFrame(actor, observer, boss);
        bossReviewPrevious = boundary;
        if (boundary.JustHit) bossReviewIdentityKnockbackUntil = boundary.Tick + 2;
        bossReviewIdentityBaseline = new(oldWindow, companion, bossReviewSpawnId, before, direct,
            boundary.Ammo, boundary.Shots, Math.Max(18, (Loadout.GetBestWeapon()?.useTime ?? 16) + 2));
        var inFlight = Main.projectile.Where(projectile => projectile.active && projectile.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion)
            .Select(projectile => new { projectile.owner, projectile.identity, projectile.type }).ToArray();
        bossReviewEvents.Add(new { kind = "identity_observation_started", variant = bossReviewIdentityVariant, before, after,
            sameWindow, sameIntent, directGuardChallenge = direct, spawnId = bossReviewSpawnId, newCompanionSlot = actor.whoAmI,
            newBossSlot = boss.whoAmI, boundary, inFlight, ticks = BossReviewIdentityObservationTicks });
    }

    private bool RespawnBossReviewIdentityCompanion(Player observer, NPC originalActor, ModNPC originalCompanion)
    {
        int oldSlot = originalActor.whoAmI;
        float spawnX = observer.Center.X - 80f * observer.direction, spawnFeet = observer.Center.Y + originalActor.height * 0.5f;
        bool dry = BossReviewDryBody(spawnX, spawnFeet, originalActor.width, originalActor.height);
        bool supported = TilePathfinder.IsGroundedAt(spawnX, spawnFeet, 0, originalActor.width);
        bossReviewEvents.Add(new { kind = "identity_native_companion_spawn_preflight", x = spawnX, feetY = spawnFeet,
            observer.direction, dry, supported });
        if (!dry || !supported)
        { FinishBossReviewNative("uncovered_setup", "Production companion spawn is outside the measured dry supported runway."); return false; }

        bossReviewIdentityKilledCompanion = originalCompanion;
        originalActor.StrikeInstantKill();
        bool absentBeforeRespawn = !originalActor.active && CompanionNpc == null;
        bossReviewEvents.Add(new { kind = "identity_companion_end", oldSlot, nativeOnKill = bossReviewIdentityCompanionOnKill,
            absentBeforeRespawn, life = originalActor.life, state = ReadBossReviewIdentityState() });
        if (!absentBeforeRespawn || !bossReviewIdentityCompanionOnKill)
        { FinishBossReviewNative("failed_setup", "The controlled lethal strike did not end the original companion through native OnKill."); return false; }

        SpawnCompanion(observer);
        NPC? replacement = CompanionNpc;
        bool newInstance = replacement?.ModNPC is { } newCompanion && !ReferenceEquals(newCompanion, originalCompanion);
        bossReviewEvents.Add(new { kind = "identity_native_companion_respawn", oldSlot, newSlot = replacement?.whoAmI,
            sameSlot = replacement?.whoAmI == oldSlot, newInstance, life = replacement?.life,
            x = replacement?.Center.X, feetY = replacement?.Bottom.Y, state = ReadBossReviewIdentityState() });
        if (!newInstance || replacement is not { active: true, life: > 0 })
        { FinishBossReviewNative("failed_setup", "Production SpawnCompanion did not create a live new ModNPC instance."); return false; }
        return true;
    }

    private bool ReplaceBossReviewIdentityBoss(Player observer, NPC actor, NPC originalBoss)
    {
        int oldSlot = originalBoss.whoAmI;
        long oldSpawnId = bossReviewSpawnId;
        Vector2 oldFeet = originalBoss.Bottom;
        int crownIndex = Loadout.FindInventoryIndex(ItemID.SlimeCrown.ToString());
        if (crownIndex < 0 || Loadout.CountItem(ItemID.SlimeCrown) != 1)
        { FinishBossReviewNative("failed_setup", "The second Crown was unavailable before Boss replacement."); return false; }

        originalBoss.StrikeInstantKill();
        BossEncounter? death = BossEvidence.ReadEncounter(oldSpawnId);
        bool absentBeforeSummon = !originalBoss.active && !NPC.AnyNPCs(NPCID.KingSlime);
        bossReviewEvents.Add(new { kind = "identity_old_boss_end", oldSlot, oldSpawnId, death, absentBeforeSummon,
            state = ReadBossReviewIdentityState() });
        if (!absentBeforeSummon || death is not { DeathSequence: > 0 })
        { FinishBossReviewNative("failed_setup", "The controlled lethal strike did not end the first Boss through its native OnKill."); return false; }

        bool summoned = BossKnowledge.TryUseSummoner(Loadout.Inventory[crownIndex], actor, observer, out bool handled, out string detail);
        BossEncounter[] spawns = BossEvidence.Snapshot().Where(encounter => encounter.SpawnId > oldSpawnId).ToArray();
        int remainingCrown = Loadout.CountItem(ItemID.SlimeCrown);
        bool sameSlot = spawns.Length == 1 && spawns[0].Slot == oldSlot;
        bossReviewEvents.Add(new { kind = "identity_native_boss_resummon", oldSlot, oldSpawnId, handled, summoned, detail,
            spawns, sameSlot, remainingCrown, state = ReadBossReviewIdentityState() });
        if (!handled || !summoned || remainingCrown != 0 || spawns.Length != 1 || spawns[0].NpcType != NPCID.KingSlime)
        { FinishBossReviewNative("failed_setup", "Production summoner use did not create one fresh King Slime."); return false; }

        bossReviewSpawnId = spawns[0].SpawnId;
        if (!sameSlot)
        { FinishBossReviewNative("uncovered_slot", "The native Boss allocator did not reuse the original slot."); return false; }
        NPC? replacement = FindBossReviewEncounter();
        bool dry = replacement != null && BossReviewDryBody(oldFeet.X, oldFeet.Y, replacement.width, replacement.height);
        if (replacement == null || !dry)
        {
            bossReviewEvents.Add(new { kind = "identity_replacement_boss_preflight", x = oldFeet.X, feetY = oldFeet.Y, dry,
                width = replacement?.width, height = replacement?.height });
            FinishBossReviewNative("uncovered_setup", "The fresh Boss did not fit at the original Boss's dry feet.");
            return false;
        }
        var before = new { x = replacement.Center.X, feetY = replacement.Bottom.Y, vx = replacement.velocity.X, vy = replacement.velocity.Y };
        replacement.Bottom = oldFeet;
        bossReviewEvents.Add(new { kind = "identity_one_replacement_boss_position", before,
            after = new { x = replacement.Center.X, feetY = replacement.Bottom.Y, vx = replacement.velocity.X, vy = replacement.velocity.Y },
            replacement.whoAmI, spawnId = bossReviewSpawnId });
        return true;
    }

    private void WatchBossReviewIdentity(NPC npc, NPC boss, BossReviewFrame previous, BossReviewFrame sample)
    {
        BossReviewIdentityBaseline baseline = bossReviewIdentityBaseline!;
        BossReviewIdentityState state = ReadBossReviewIdentityState();
        bool oldWindowPresent = ReferenceEquals(BossReviewIdentityWindow, baseline.OldWindow);
        if (!oldWindowPresent) bossReviewIdentityWindowGoneTick ??= sample.Tick;
        bool sameActors = ReferenceEquals(npc.ModNPC, baseline.Companion) && npc.life > 0 &&
            boss.GetGlobalNPC<BossWitness>().SpawnId == baseline.TargetSpawnId;
        bool sameWait = !baseline.DirectGuardChallenge || BossReviewWaiting(sample) && state.GoalId == baseline.OriginalState.GoalId &&
            state.Intent == baseline.OriginalState.Intent && state.OutcomeSequence == baseline.OriginalState.OutcomeSequence &&
            state.ExplicitWait == false && !state.AutoDefend;
        if (sample.JustHit || sample.Life < previous.Life) bossReviewIdentityKnockbackUntil = sample.Tick + 2;
        float delta = Math.Abs(sample.X - previous.X);
        if (delta > 0.15f && sample.Tick <= bossReviewIdentityKnockbackUntil) bossReviewIdentityKnockbackPixels += delta;
        bool voluntaryWalk = baseline.DirectGuardChallenge && delta > 0.15f && sample.Tick > bossReviewIdentityKnockbackUntil;
        bool newFire = sample.Ammo != baseline.Ammo || sample.Shots != baseline.Shots;
        bool firingPosition = sample.BossChaseable && sample.Ammo > 0 && Loadout.GetBestWeapon()?.type == ItemID.PhoenixBlaster &&
            CompanionMovement.InAttackPosition(boss.Center.X - npc.Center.X, boss.Center.Y - npc.Center.Y,
                CompanionWeapons.AttackRange(Loadout), sample.LineOfSight);
        if (firingPosition) bossReviewIdentityFiringFrames++;

        int ticks = bossReviewFrame - bossReviewPhaseFrame;
        if (sameActors && sameWait && !newFire && !voluntaryWalk && ticks < BossReviewIdentityObservationTicks) return;
        bool enoughOpportunity = bossReviewIdentityFiringFrames >= baseline.RequiredFiringFrames;
        GoalNativeConditionResult? native = state.GoalId == baseline.OriginalState.GoalId ? ReadBossReviewPreservation() : null;
        if (native != null) bossReviewProductionBeforeHold = PreservationEvidence.DebugSnapshot();
        bossReviewEvents.Add(new { kind = "identity_observation_completed", ticks, baseline.DirectGuardChallenge,
            oldWindowPresent, firstWindowGoneTick = bossReviewIdentityWindowGoneTick, sameActors, sameWait, newFire, voluntaryWalk,
            knockbackPixels = bossReviewIdentityKnockbackPixels, firingPositionFrames = bossReviewIdentityFiringFrames,
            baseline.RequiredFiringFrames, ammoBefore = baseline.Ammo, shotsBefore = baseline.Shots, state, sample, native });
        if (!sameActors || !sameWait || newFire || voluntaryWalk)
            FinishBossReviewNative("failed", "Replacement identity or review state changed, or an old-window shot, ammo change or voluntary walk was observed.");
        else if (!enoughOpportunity)
            FinishBossReviewNative("uncovered_firing_opportunity", "Too few live, visible, in-range frames to judge review revocation.");
        else if (oldWindowPresent || baseline.DirectGuardChallenge && native?.Met != true)
            FinishBossReviewNative("failed", "The old review window was retained or original goal preservation failed.");
        else if (baseline.DirectGuardChallenge)
            FinishBossReviewNative("passed", "");
        else
            FinishBossReviewNative("passed_lifecycle_only", "The native handoff changed or cleared review eligibility before the first replacement tick. Isolating the identity guard requires a native handoff that preserves the old window, goal and intent through that boundary.");
    }

    internal void RecordBossReviewIdentityDeath(NPC npc)
    {
        if (!BossReviewObserving || bossReviewIdentityKilledCompanion == null || !ReferenceEquals(npc.ModNPC, bossReviewIdentityKilledCompanion)) return;
        bossReviewIdentityCompanionOnKill = true;
        bossReviewEvents.Add(new { kind = "identity_original_companion_native_onkill", slot = npc.whoAmI,
            life = npc.life, x = npc.Center.X, feetY = npc.Bottom.Y, tick = Main.GameUpdateCount });
    }
}

public sealed class BossReviewIdentityDeathWitness : GlobalNPC
{
    public override void OnKill(NPC npc) => AITeammateSystem.Instance?.RecordBossReviewIdentityDeath(npc);
}
