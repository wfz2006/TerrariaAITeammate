using System;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public class CompanionProjectileTag : GlobalProjectile
{
    public override bool InstancePerEntity => true;
    public bool FromCompanion;
    public override void OnSpawn(Projectile projectile, IEntitySource source)
    {
        if (source is EntitySource_Parent parent && parent.Entity is Projectile origin)
            FromCompanion = origin.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion;
    }
}

public class BossWitness : GlobalNPC
{
    public override bool InstancePerEntity => true;
    public long SpawnId;
    public override void OnSpawn(NPC npc, IEntitySource source)
    {
        if (npc.boss && Main.netMode == NetmodeID.SinglePlayer)
        {
            SpawnId = AITeammateSystem.Instance.BossEvidence.Spawn(npc.type, npc.whoAmI);
            AITeammateSystem.Instance.TestLog($"boss spawned id={SpawnId} type={npc.type} lifeMax={npc.lifeMax} damage={npc.damage}");
        }
    }
    public override void OnHitByProjectile(NPC npc, Projectile projectile, NPC.HitInfo hit, int damageDone)
    {
        if (SpawnId != 0 && projectile.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion)
            AITeammateSystem.Instance.BossEvidence.Hit(SpawnId, damageDone);
    }
    public override void OnKill(NPC npc)
    {
        if (SpawnId == 0) return;
        AITeammateSystem.Instance.BossEvidence.Kill(SpawnId);
        AITeammateSystem.Instance.TestLog($"boss real death id={SpawnId} type={npc.type}; disappearance is not a death event");
    }
}

public class ComplexObserverNoPickup : GlobalItem
{
    public override bool CanPickup(Item item, Player player)
        => !AITeammateSystem.NaturalFlashObserverNoPickup && !(Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") == "complex" &&
             Main.netMode == NetmodeID.SinglePlayer && Main.SavePath.Contains("agent-acceptance", StringComparison.OrdinalIgnoreCase));
}

public class NaturalFlashSpectator : ModPlayer
{
    public override bool ImmuneTo(PlayerDeathReason damageSource, int cooldownCounter, bool dodgeable)
        => AITeammateSystem.NaturalFlashObserverNoPickup;
}

public class BossLootTag : GlobalItem
{
    public override bool InstancePerEntity => true;
    public long BossSpawnId;
    public override void OnSpawn(Item item, IEntitySource source)
    {
        if (source is EntitySource_Parent parent && parent.Entity is NPC npc && npc.boss)
            BossSpawnId = npc.GetGlobalNPC<BossWitness>().SpawnId;
    }
}
