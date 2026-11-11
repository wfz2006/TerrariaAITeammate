using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AIExecutor
{
    private const uint BossReviewTickLimit = 40 * 60;
    private const uint BossReviewWalkTicks = 45;
    private const float BossReviewClearance = 32f;
    private static readonly int[] BossReviewWalkTiles = { 5, 3, 2 };
    private static readonly int[] BossReviewWalkRows = { 0, -1, 1 };
    private BossReviewWindow? bossReview;

    private sealed class BossReviewWindow(ModNPC companion, int slot, int type, long spawnId, string goalId, uint startTick)
    {
        public ModNPC Companion { get; } = companion;
        public int Slot { get; } = slot;
        public int Type { get; } = type;
        public long SpawnId { get; } = spawnId;
        public string GoalId { get; } = goalId;
        public uint StartTick { get; } = startTick;
        public uint LastWalkProbe { get; set; }
        public TileStandPose? Stand { get; set; }
        public int WalkDirection { get; set; }
        public bool ReportedShot { get; set; }
    }

    internal void ClearBossReview()
    {
        if (bossReview == null) return;
        bossReview = null;
        Animation.Release();
    }

    // Only a failed approach to the specific boss selected by the current attack may
    // leave a continuation. Holding position or a new command revokes it.
    private void BeginBossReview(NPC npc)
    {
        var system = AITeammateSystem.Instance;
        if (npc.ModNPC is not { } companion || system.ManualControlActive || string.IsNullOrWhiteSpace(system.ActiveGoal) ||
            attackLastTarget is not { active: true, boss: true, friendly: false, life: > 0 } boss ||
            attackCommandTarget?.NpcType != boss.type) return;

        long spawnId = boss.GetGlobalNPC<BossWitness>().SpawnId;
        if (spawnId <= 0) return;
        uint now = Main.GameUpdateCount;
        bossReview = new BossReviewWindow(companion, boss.whoAmI, boss.type, spawnId, system.GoalIdentity, now)
        {
            LastWalkProbe = unchecked(now - BossReviewWalkTicks)
        };
        system.TestLog($"boss review armed spawn={spawnId} tick={now}");
    }

    private bool TickBossReview(NPC npc)
    {
        if (bossReview is not { } window) return false;
        var system = AITeammateSystem.Instance;
        uint now = Main.GameUpdateCount;
        NPC boss = Main.npc[window.Slot];
        if (!ReferenceEquals(npc.ModNPC, window.Companion) || !system.BossReviewPending ||
            system.GoalIdentity != window.GoalId || now - window.StartTick >= BossReviewTickLimit ||
            !boss.active || boss.type != window.Type || boss.friendly || boss.life <= 0 ||
            boss.GetGlobalNPC<BossWitness>().SpawnId != window.SpawnId)
        {
            ClearBossReview();
            return false;
        }

        if (attackCooldown > 0) attackCooldown--;
        float deltaX = boss.Center.X - npc.Center.X;
        bool threatened = Math.Abs(deltaX) <= (boss.width + npc.width) * 0.5f + BossReviewClearance &&
            Math.Abs(boss.Center.Y - npc.Center.Y) <= (boss.height + npc.height) * 0.5f + BossReviewClearance;
        bool grounded = IsGrounded(npc);
        if (window.Stand == null && threatened && grounded && now - window.LastWalkProbe >= BossReviewWalkTicks)
        {
            window.LastWalkProbe = now;
            window.Stand = FindBossReviewStand(npc, boss);
            if (window.Stand is { } selected)
            {
                window.WalkDirection = Math.Sign(selected.Center.X - npc.Center.X);
                Animation.Release();
                system.TestLog($"boss review evade spawn={window.SpawnId} stand={selected.Node.X},{selected.Node.Y}");
            }
        }

        if (window.Stand is { } stand)
        {
            float dx = stand.Center.X - npc.Center.X;
            if (now - window.LastWalkProbe < BossReviewWalkTicks && window.WalkDirection * dx > 4f &&
                (!threatened || window.WalkDirection * deltaX <= 4f) &&
                TilePathfinder.MatchesStandPose(stand))
            {
                // Recheck the short walk from the actor's actual contact pose.
                // Landing and knockback are handled by the normal NPC physics.
                if (grounded && BossReviewCanWalk(npc, stand))
                {
                    TryStepUpOneTile(npc, window.WalkDirection, true);
                    FaceNavigation(npc, window.WalkDirection);
                    npc.velocity.X = Math.Clamp(dx, -4.2f, 4.2f);
                    return true;
                }
                if (!grounded)
                {
                    npc.velocity.X = 0f;
                    return true;
                }
            }
            window.Stand = null;
        }

        npc.velocity.X = 0f;
        if (attackCooldown > 0 || boss.dontTakeDamage || !boss.CanBeChasedBy() ||
            !CompanionMovement.InAttackPosition(deltaX, boss.Center.Y - npc.Center.Y, CompanionWeapons.AttackRange(Loadout),
                Collision.CanHitLine(npc.Center, 1, 1, boss.Center, 1, 1))) return true;

        WeaponUseResult shot = CompanionWeapons.Use(npc, boss, Loadout, Animation);
        attackCooldown = shot.Cooldown;
        if (shot.Fired && !window.ReportedShot)
        {
            window.ReportedShot = true;
            system.TestLog($"boss review continued attack spawn={window.SpawnId} tick={now}");
        }
        return true;
    }

    private bool TickPlanningDefense(NPC npc)
    {
        var system = AITeammateSystem.Instance;
        if (!system.PlanningSafetyPending || Loadout.GetBestWeapon() == null) return false;
        if (attackCooldown > 0) attackCooldown--;
        NPC? threat = FindNearestEnemyNear(npc.Center, 6f * 16f);
        if (threat == null) return false;
        npc.velocity.X = 0f;
        if (attackCooldown == 0 && threat.CanBeChasedBy() && CompanionMovement.InAttackPosition(threat.Center.X - npc.Center.X,
            threat.Center.Y - npc.Center.Y, CompanionWeapons.AttackRange(Loadout),
            Collision.CanHitLine(npc.Center, 1, 1, threat.Center, 1, 1)))
        {
            WeaponUseResult shot = CompanionWeapons.Use(npc, threat, Loadout, Animation);
            attackCooldown = shot.Cooldown;
            if (shot.Fired) system.TestLog($"planning self-defense shot targetType={threat.type} slot={threat.whoAmI}");
        }
        return true;
    }

    private static TileStandPose? FindBossReviewStand(NPC npc, NPC boss)
    {
        int startX = (int)(npc.Center.X / 16f);
        int row = CompanionMovement.StandRow(npc.Bottom.Y);
        int away = npc.Center.X < boss.Center.X ? -1 : 1;
        float distance = Math.Abs(npc.Center.X - boss.Center.X);
        // If the boss is to one side, the walk must never cross it. At almost
        // coincident centers either side can increase separation.
        foreach (int direction in distance <= 2f ? new[] { away, -away } : new[] { away })
        {
            foreach (int tiles in BossReviewWalkTiles)
            foreach (int vertical in BossReviewWalkRows)
            {
                var node = new Point(startX + direction * tiles, row + vertical);
                if (!TilePathfinder.TryGetStandPose(node, out TileStandPose pose) ||
                    Math.Abs(pose.Center.X - boss.Center.X) < distance + 24f ||
                    !TilePathfinder.IsStableStand(node.X, node.Y) || !BossReviewCanWalk(npc, pose)) continue;
                return pose;
            }
        }
        return null;
    }

    private static bool BossReviewCanWalk(NPC npc, TileStandPose stand)
        => Collision.CanHitLine(npc.position, npc.width, npc.height, stand.Center - npc.Size * 0.5f, npc.width, npc.height) &&
            TilePathfinder.HasWalkingConnection(npc.Center.X, npc.Bottom.Y, stand.Node);
}
