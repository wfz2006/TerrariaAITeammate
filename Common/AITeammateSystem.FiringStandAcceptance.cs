using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const int FiringStandObserveAttackFrames = 60;
    private const uint FiringStandExclusionTicks = 1800;
    private static bool FiringStandDiagnosticEnabled => Environment.GetEnvironmentVariable("AITEAMMATE_FIRING_STAND_DIAGNOSTIC") == "1";
    private const BindingFlags FiringStandPrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo? FiringStandRejectMethod = typeof(AIExecutor).GetMethod("RejectUnsettledFiringStand", FiringStandPrivateInstance);
    private static readonly FieldInfo? FiringStandSettleField = typeof(AIExecutor).GetField("endpointSettleTicks", FiringStandPrivateInstance);
    private static readonly MethodInfo? FiringStandSlopeMethod = typeof(AIExecutor).GetMethod("HasSlopedSupport", BindingFlags.Static | BindingFlags.NonPublic);

    private sealed record FiringStandSample(uint Tick, int Decision, AIPlan Plan, ModNPC? Companion, string GoalId, int Step,
        long OutcomeSequence, long SpawnId, int TargetSlot, Vector2 Actor, float FeetY, Vector2 Velocity, int ActorLife,
        bool Grounded, bool JustHit, Vector2 Target, int TargetLife, int Ammo, int Damage, bool Approach, float Range,
        bool Sight, bool InPosition, TileStandPose? Stand, bool MatchesTerrain, bool StandSight, bool StandInPosition,
        bool RouteAligned, int PathCount, int PathIndex, int SettleTicks, string? SearchStatus, int SearchCount,
        int Replans, uint SearchAt, uint BuiltAt, Vector2? SearchTarget)
    {
        public bool UsableRoute => Approach && RouteAligned && MatchesTerrain && StandInPosition;
        public bool SettlePredicate => UsableRoute && PathIndex >= PathCount && !InPosition && Stand is { } stand &&
            Vector2.DistanceSquared(Actor, stand.Center) <= 24f * 24f;
        public object Evidence => new
        {
            Tick, Decision, GoalId, Step, OutcomeSequence, taskTarget = Plan.Target, taskCount = Plan.Count,
            actor = new { x = Actor.X, y = Actor.Y, FeetY, vx = Velocity.X, vy = Velocity.Y, ActorLife, Grounded, JustHit },
            target = new { SpawnId, TargetSlot, x = Target.X, y = Target.Y, TargetLife, Damage },
            Ammo, Approach, Range, Sight, InPosition, distance = Vector2.Distance(Actor, Target),
            stand = FiringStandPoseEvidence(Stand), MatchesTerrain, StandSight, StandInPosition, RouteAligned,
            PathCount, PathIndex, SettleTicks, SettlePredicate, SearchStatus, SearchCount, Replans, SearchAt, BuiltAt,
            searchTargetX = SearchTarget?.X, searchTargetY = SearchTarget?.Y
        };
    }

    private sealed record FiringStandMarks(bool Rejected, uint? BlockedAt, bool FailedPoseMatches);
    private sealed record FiringStandShot(int Owner, int Identity, int Slot, uint Tick, int AmmoBefore,
        bool AtReplacementStand, bool ReacquiredFiringPosition, object Position);
    private readonly List<object> firingStandFrames = new();
    private readonly List<FiringStandShot> firingStandShots = new();
    private readonly Dictionary<int, FiringStandShot> firingStandLiveShots = new();
    private FiringStandSample? firingStandPrevious, firingStandControl, firingStandBefore, firingStandReplacement, firingStandArrival;
    private FiringStandMarks? firingStandRejectedMarks, firingStandReplacementMarks;
    private object? firingStandAttackBoundary, firingStandHit, firingStandReengagementHit;
    private string? firingStandInjection, firingStandTrigger, firingStandError;
    private uint firingStandRejectedAt;
    private int firingStandConsecutiveAttackFrames, firingStandAttackFrames, firingStandSettleFrames;
    private bool firingStandRouteRetired, firingStandRecoveryRequested, firingStandOldValidForNewSearch;
    private uint firingStandRetryAt;

    private static object? FiringStandPoseEvidence(TileStandPose? value) => value is { } pose
        ? new { x = pose.Node.X, y = pose.Node.Y, centerX = pose.Center.X, centerY = pose.Center.Y, feetY = pose.Bottom }
        : null;

    private FiringStandSample? ReadFiringStandSample(NPC npc, NPC? boss)
    {
        if (Executor.CurrentAction != AIActionType.Attack || Executor.IsAutoDefending || Executor.HoldPosition ||
            ContinuousField("currentPlan") is not AIPlan plan ||
            boss is not { active: true, life: > 0 } || !boss.CanBeChasedBy() ||
            !ReferenceEquals(ContinuousField("attackLastTarget"), boss)) return null;
        long spawnId = boss.GetGlobalNPC<BossWitness>().SpawnId;
        if (spawnId == 0 || spawnId != naturalBossSpawnId) return null;

        TileStandPose? stand = ContinuousField("navGoalPose") is TileStandPose pose ? pose : null;
        var path = ContinuousField("navPath") as IReadOnlyList<Point>;
        var poses = ContinuousField("navPathPoses") as IReadOnlyList<TileStandPose>;
        var search = ContinuousField("routeSnapshot") as TilePathSearchSnapshot;
        bool aligned = stand is { } frozen && search is { Status: "found" } && search.Goal == frozen.Node && search.GoalPose == frozen &&
            path != null && poses != null && poses.Count == path.Count && (path.Count == 0 || path[^1] == frozen.Node && poses[^1] == frozen);
        bool matchesTerrain = stand is { } selected && TilePathfinder.MatchesStandPose(selected);
        Vector2 actor = npc.Center, target = boss.Center;
        float range = CompanionWeapons.AttackRange(Loadout);
        bool approach = ContinuousField("routeAttackApproach") is true && ContinuousField("routeAttackRange") is float routeRange &&
            Math.Abs(routeRange - range) < 0.5f;
        bool sight = Collision.CanHitLine(actor, 1, 1, target, 1, 1);
        bool standSight = stand is { } goal && Collision.CanHitLine(goal.Center, 1, 1, target, 1, 1);
        return new(Main.GameUpdateCount, naturalReplayStage, plan, npc.ModNPC, GoalIdentity,
            (int)(ContinuousField("sequenceStep") ?? -1), agentJournal.LastSequence, spawnId, boss.whoAmI,
            actor, npc.Bottom.Y, npc.velocity, npc.life, TilePathfinder.IsGroundedAt(actor.X, npc.Bottom.Y, npc.velocity.Y, npc.width),
            npc.justHit, target, boss.life, Loadout.CountItem(ItemID.MusketBall), BossEvidence.ReadEncounter(spawnId)?.CompanionDamage ?? 0,
            approach, range, sight, CompanionMovement.InAttackPosition(actor.X - target.X, actor.Y - target.Y, range, sight),
            stand, matchesTerrain, standSight, stand is { } frozenStand && CompanionMovement.InAttackPosition(
                frozenStand.Center.X - target.X, frozenStand.Center.Y - target.Y, range, standSight),
            aligned, path?.Count ?? -1, (int)(ContinuousField("navIndex") ?? -1), (int)(ContinuousField("endpointSettleTicks") ?? 0),
            search?.Status, (int)(ContinuousField("routeSearchCount") ?? 0), (int)(ContinuousField("routeReplans") ?? 0),
            (uint)(ContinuousField("searchStartedAt") ?? 0u), (uint)(ContinuousField("navBuiltAt") ?? 0u), search?.RequestedTarget);
    }

    private static bool SameFiringStandAttack(FiringStandSample first, FiringStandSample later) =>
        ReferenceEquals(first.Plan, later.Plan) && ReferenceEquals(first.Companion, later.Companion) &&
        first.GoalId == later.GoalId && first.Step == later.Step && first.SpawnId == later.SpawnId && first.TargetSlot == later.TargetSlot;

    private AgentOutcome? FiringStandAttackOutcome(FiringStandSample start) => agentJournal.LastSequence == start.OutcomeSequence
        ? null : agentJournal.Outcomes.FirstOrDefault(outcome => outcome.Sequence > start.OutcomeSequence && outcome.Task.Action == AIActionType.Attack);

    private FiringStandMarks ReadFiringStandMarks(TileStandPose pose)
    {
        var rejected = ContinuousField("unsettledFiringStands") as IReadOnlySet<Point>;
        var blocked = ContinuousField("blockedWaypoints") as IReadOnlyDictionary<Point, uint>;
        var failed = ContinuousField("failedStandPoses") as IReadOnlyDictionary<Point, TileStandPose>;
        return new(rejected?.Contains(pose.Node) == true,
            blocked != null && blocked.TryGetValue(pose.Node, out uint tick) ? tick : null,
            failed != null && failed.TryGetValue(pose.Node, out TileStandPose stored) && stored == pose);
    }

    private void RecordFiringStandRejection(string mode, FiringStandSample before, FiringStandMarks marks, uint tick)
    {
        firingStandBefore = before;
        firingStandTrigger = mode;
        firingStandRejectedAt = tick;
        firingStandRejectedMarks = marks;
        firingStandRouteRetired = ContinuousField("navPath") == null && ContinuousField("navPathPoses") == null && ContinuousField("navGoalPose") == null;
        firingStandRecoveryRequested = ContinuousField("routeRecoveryRequested") is true;
        firingStandRetryAt = (uint)(ContinuousField("navigationRetryAt") ?? 0u);
    }

    private void TrackFiringStandDiagnostic(NPC npc, NPC? boss)
    {
        FiringStandSample? sample = ReadFiringStandSample(npc, boss);
        FiringStandSample? previous = firingStandPrevious;
        firingStandPrevious = sample;
        if (firingStandBefore is { } start)
        {
            AgentOutcome? outcome = FiringStandAttackOutcome(start);
            if (sample == null || !SameFiringStandAttack(start, sample) || outcome != null)
            {
                firingStandAttackBoundary ??= new { tick = Main.GameUpdateCount, action = Executor.CurrentAction.ToString(), outcome,
                    selectedSpawnId = (ContinuousField("attackLastTarget") as NPC)?.GetGlobalNPC<BossWitness>().SpawnId,
                    afterWitnessedHit = firingStandHit != null, afterReengagementHit = firingStandReengagementHit != null };
                return;
            }
            if (firingStandAttackBoundary != null || sample.Tick - firingStandRejectedAt > FiringStandExclusionTicks) return;
            firingStandFrames.Add(sample.Evidence);
            ObserveFiringStandReplacement(npc, start, sample);
            return;
        }
        if (sample == null) { firingStandConsecutiveAttackFrames = 0; return; }

        bool sameAttack = previous != null && SameFiringStandAttack(previous, sample);
        firingStandConsecutiveAttackFrames = sameAttack ? firingStandConsecutiveAttackFrames + 1 : 1;
        firingStandAttackFrames++;
        if (sample.SettlePredicate) firingStandSettleFrames++;
        firingStandFrames.Add(sample.Evidence);

        if (sameAttack && previous is { UsableRoute: true, Stand: { } old } && sample.Stand == null && sample.PathCount < 0)
        {
            FiringStandMarks marks = ReadFiringStandMarks(old);
            if (marks.Rejected && marks.BlockedAt is uint blockedAt && blockedAt > previous.Tick && blockedAt <= sample.Tick)
            {
                bool accelerated = firingStandInjection == "accelerated_threshold" && firingStandControl is { } control &&
                    SameFiringStandAttack(control, previous) && control.Stand == old && blockedAt - control.Tick == 1;
                RecordFiringStandRejection(accelerated ? "accelerated_threshold" : "natural", previous, marks, blockedAt);
                return;
            }
        }
        bool capturedTiming = capturedBossLatency != null;
        if (firingStandInjection != null || capturedTiming && firingStandConsecutiveAttackFrames < FiringStandObserveAttackFrames ||
            !sample.UsableRoute || sample.InPosition || sample.Stand is not { } candidate || ReadFiringStandMarks(candidate).Rejected) return;

        if (sample.SettlePredicate && sample.SettleTicks is > 0 and < 25)
        {
            firingStandControl = sample;
            firingStandInjection = "accelerated_threshold";
            if (FiringStandSettleField == null) { firingStandError = "Production settle counter is missing."; return; }
            FiringStandSettleField.SetValue(Executor, 30);
            return;
        }
        bool approachHandoff = capturedTiming && firingStandSettleFrames == 0 && sample.PathIndex >= 0 && sample.PathIndex < sample.PathCount;
        // In the short control, only hand off a real body/stand mismatch on a consumed route.
        bool mismatchHandoff = !capturedTiming && sample.SettlePredicate && sample.Grounded && !sample.JustHit;
        if (sample.PathCount <= 0 || !approachHandoff && !mismatchHandoff) return;

        firingStandControl = sample;
        firingStandInjection = mismatchHandoff ? "controlled_handoff_observed_mismatch" : "controlled_handoff_bypasses_settle_predicate";
        if (FiringStandRejectMethod == null) { firingStandError = "Production firing-stand rejection handler is missing."; return; }
        try
        {
            FiringStandRejectMethod.Invoke(Executor, new object[] { npc, candidate, sample.Target, sample.Sight });
            RecordFiringStandRejection(firingStandInjection, sample, ReadFiringStandMarks(candidate), sample.Tick);
        }
        catch (TargetInvocationException exception)
        { firingStandError = exception.InnerException?.Message ?? exception.Message; }
    }

    private void ObserveFiringStandReplacement(NPC npc, FiringStandSample start, FiringStandSample sample)
    {
        if (firingStandReplacement == null && start.Stand is { } old && sample.Stand is { } alternate && alternate.Node != old.Node &&
            sample.Approach && sample.RouteAligned && sample.MatchesTerrain && sample.PathCount > 0 &&
            sample.SearchCount > start.SearchCount && sample.Replans > start.Replans &&
            sample.SearchAt > firingStandRejectedAt && sample.BuiltAt >= sample.SearchAt && sample.SearchTarget is { } frozenTarget)
        {
            FiringStandMarks marks = ReadFiringStandMarks(old);
            if (!marks.Rejected || marks.BlockedAt != firingStandRejectedAt) return;
            firingStandReplacement = sample;
            firingStandReplacementMarks = marks;
            firingStandOldValidForNewSearch = TilePathfinder.MatchesStandPose(old) && CompanionMovement.InAttackPosition(
                old.Center.X - frozenTarget.X, old.Center.Y - frozenTarget.Y, sample.Range,
                Collision.CanHitLine(old.Center, 1, 1, frozenTarget, 1, 1));
        }
        if (AtFiringStandReplacement(npc, sample)) firingStandArrival ??= sample;
    }

    private static bool InFiringPositionOnReplacement(FiringStandSample sample, FiringStandSample alternate) =>
        sample.Stand == alternate.Stand && sample.RouteAligned && sample.MatchesTerrain &&
        sample.BuiltAt == alternate.BuiltAt && sample.SearchCount == alternate.SearchCount &&
        sample.InPosition && sample.Grounded && !sample.JustHit;

    private bool AtFiringStandReplacement(NPC npc, FiringStandSample sample) =>
        firingStandReplacement is { } alternate && sample.Stand is { } stand && InFiringPositionOnReplacement(sample, alternate) &&
        sample.PathIndex > alternate.PathIndex &&
        Vector2.DistanceSquared(sample.Actor, alternate.Actor) >= 8f * 8f &&
        CompanionMovement.ReachedTerminalWaypoint(stand.Center.X - sample.Actor.X, stand.Bottom - sample.FeetY, sample.Velocity.Y,
            sample.Grounded, FiringStandSlopeMethod?.Invoke(null, new object[] { npc }) is true);

    private static bool ReacquiredFiringPosition(FiringStandSample start, FiringStandSample alternate, FiringStandSample sample)
    {
        if (sample.Tick <= alternate.Tick || sample.Stand is not { } stand || !InFiringPositionOnReplacement(sample, alternate) ||
            Vector2.DistanceSquared(sample.Actor, start.Actor) < 4f * 4f ||
            Vector2.DistanceSquared(sample.Actor, stand.Center) >= Vector2.DistanceSquared(alternate.Actor, stand.Center)) return false;

        // Check the old body position against the current target so a moving boss alone cannot satisfy the witness.
        return !CompanionMovement.InAttackPosition(start.Actor.X - sample.Target.X, start.Actor.Y - sample.Target.Y, sample.Range,
            Collision.CanHitLine(start.Actor, 1, 1, sample.Target, 1, 1));
    }

    internal void RecordFiringStandProjectileSpawn(Projectile projectile, IEntitySource source)
    {
        if (firingStandReplacement is not { } alternate || naturalFlashFinished) return;
        firingStandLiveShots.Remove(projectile.whoAmI);
        if (source is not EntitySource_Parent { Entity: NPC owner } || firingStandBefore is not { } start || firingStandAttackBoundary != null ||
            Main.GameUpdateCount - firingStandRejectedAt > FiringStandExclusionTicks ||
            projectile.type != ProjectileID.Bullet || !ReferenceEquals(owner, CompanionNpc) ||
            ReadFiringStandSample(owner, ContinuousField("attackLastTarget") as NPC) is not { } sample ||
            !SameFiringStandAttack(start, sample) || FiringStandAttackOutcome(start) != null) return;
        bool arrived = AtFiringStandReplacement(owner, sample);
        bool reacquired = ReacquiredFiringPosition(start, alternate, sample);
        if (!arrived && !reacquired) return;
        if (arrived) firingStandArrival ??= sample;
        var shot = new FiringStandShot(projectile.owner, projectile.identity, projectile.whoAmI, sample.Tick, sample.Ammo,
            arrived, reacquired, sample.Evidence);
        firingStandLiveShots[projectile.whoAmI] = shot;
        firingStandShots.Add(shot);
    }

    internal void RecordFiringStandHit(Projectile projectile, NPC target, int damage)
    {
        if (naturalFlashFinished || !firingStandLiveShots.TryGetValue(projectile.whoAmI, out FiringStandShot? shot) ||
            shot.Owner != projectile.owner || shot.Identity != projectile.identity || firingStandBefore is not { } start || firingStandAttackBoundary != null ||
            Main.GameUpdateCount - firingStandRejectedAt > FiringStandExclusionTicks || damage <= 0 ||
            !projectile.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion ||
            target.GetGlobalNPC<BossWitness>().SpawnId != start.SpawnId || target.whoAmI != start.TargetSlot ||
            !ReferenceEquals(ContinuousField("attackLastTarget"), target) ||
            Executor.CurrentAction != AIActionType.Attack || !ReferenceEquals(ContinuousField("currentPlan"), start.Plan) ||
            !ReferenceEquals(CompanionNpc?.ModNPC, start.Companion) || GoalIdentity != start.GoalId ||
            (int)(ContinuousField("sequenceStep") ?? -1) != start.Step || FiringStandAttackOutcome(start) != null) return;
        int ammo = Loadout.CountItem(ItemID.MusketBall);
        if (ammo >= shot.AmmoBefore) return;
        var hit = new { tick = Main.GameUpdateCount, shot, damage, ammoAfter = ammo, spawnId = start.SpawnId };
        if (shot.AtReplacementStand) firingStandHit ??= hit;
        if (shot.ReacquiredFiringPosition) firingStandReengagementHit ??= hit;
    }

    private void SaveFiringStandDiagnostic(string replayStatus, string replayError)
    {
        var protection = naturalHouseAudit?.CheckPreservation();
        int primary = PrimaryDecisionRequestCount - naturalPrimaryStart, combat = CombatDecisionRequestCount - naturalCombatStart;
        int warps = Executor.NavigationTeleportCount;
        bool isolated = primary == 0 && combat == 0 && warps == 0 && protection is { preserved: true, coverageComplete: true, diffCount: 0 };
        bool exclusion = firingStandRejectedMarks is { Rejected: true, FailedPoseMatches: true } marks &&
            marks.BlockedAt == firingStandRejectedAt && firingStandRouteRetired && firingStandRecoveryRequested && firingStandRetryAt == firingStandRejectedAt + 1;
        bool replacement = firingStandReplacementMarks is { Rejected: true, FailedPoseMatches: true };
        bool failed = !isolated || firingStandError != null;
        string status = failed ? "failed" : exclusion && replacement && firingStandHit != null ? "passed" : "inconclusive";
        string reengagementStatus = failed ? "failed" : exclusion && replacement && firingStandReengagementHit != null ? "passed" : "inconclusive";
        string detail = !isolated ? "Request, warp or original-house preservation boundary failed." : firingStandError ??
            (firingStandBefore == null ? "No production rejection observed for the sampled native Attack route." :
             !exclusion ? "Rejection observed with incomplete route retirement or old stand exclusion evidence." :
             !replacement ? "No live replacement route with retained old stand exclusion was observed." :
             firingStandArrival == null ? "Alternate route selected; physical arrival with real range and line of sight was not observed." :
             firingStandHit == null ? "Physical arrival observed; no new same-SpawnId hit was attributed to a shot from the replacement stand." : "");
        File.WriteAllText(Path.Combine(Main.SavePath, "AITeammate", "firing-stand-native-result.json"), JsonSerializer.Serialize(new
        {
            status, detail, replayStatus, replayError, controlled = true, replayStage = naturalReplayStage,
            appliedDecisions = capturedBossLatency == null && recordedBossDecisions == null ? (int?)null : naturalReplayStage,
            replayMode = capturedBossLatency != null ? "captured_latency" : recordedBossDecisions != null ? "recorded_decisions" : "scripted_continuation",
            scope = "Original-house-copy Boss replay. At most one settle-counter acceleration or production rejection handoff. Attack, route search, movement, projectiles and damage are native.",
            primaryRequests = primary, combatRequests = combat, warps, protection, attackFrames = firingStandAttackFrames,
            sampledSettlePredicateFrames = firingStandSettleFrames, injection = firingStandInjection, control = firingStandControl?.Evidence,
            trigger = firingStandTrigger, rejectedAt = firingStandRejectedAt, before = firingStandBefore?.Evidence,
            rejection = firingStandRejectedMarks, firingStandRouteRetired, firingStandRecoveryRequested, firingStandRetryAt,
            replacement = firingStandReplacement?.Evidence, replacementExclusion = firingStandReplacementMarks,
            sameAttackReferenceAtReplacement = firingStandReplacement != null, noAttackOutcomeBeforeReplacement = firingStandReplacement != null,
            oldStandValidForReplacementFrozenTarget = firingStandReplacement == null ? (bool?)null : firingStandOldValidForNewSearch,
            arrival = firingStandArrival?.Evidence, sameSpawnNewShotHit = firingStandHit, shots = firingStandShots,
            reengagementStatus, sameSpawnReengagementHit = firingStandReengagementHit,
            firstAttackBoundary = firingStandAttackBoundary, frames = firingStandFrames
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class FiringStandProjectileWitness : GlobalProjectile
{
    public override void OnSpawn(Projectile projectile, IEntitySource source) =>
        AITeammateSystem.Instance?.RecordFiringStandProjectileSpawn(projectile, source);

    public override void OnHitNPC(Projectile projectile, NPC target, NPC.HitInfo hit, int damageDone) =>
        AITeammateSystem.Instance?.RecordFiringStandHit(projectile, target, damageDone);
}
