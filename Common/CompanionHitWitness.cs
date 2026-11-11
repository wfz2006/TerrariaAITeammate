using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public sealed record CompanionHitSnapshot(ulong lastTick, string lastType, int? lastItemType,
    int? lastProjectileType, int lastDamage, long directMeleeHits, long directMeleeDamage,
    long taggedProjectileHits, long taggedProjectileDamage, long count, long total);

/// <summary>Passive actual-hit attribution. Never attacks, changes life, chooses a target or grants a kill claim.</summary>
public sealed class CompanionHitWitness : GlobalNPC
{
    public override bool InstancePerEntity => true;
    private ulong lastTick;
    private string lastType = "";
    private int? lastItemType, lastProjectileType;
    private int lastDamage;
    private long directMeleeHits, directMeleeDamage, taggedProjectileHits, taggedProjectileDamage;

    public override void OnSpawn(NPC npc, IEntitySource source)
    {
        lastTick = 0; lastType = ""; lastItemType = lastProjectileType = null; lastDamage = 0;
        directMeleeHits = directMeleeDamage = taggedProjectileHits = taggedProjectileDamage = 0;
    }

    internal void RecordDirectMelee(int itemType, int actualHealthLoss)
    {
        if (actualHealthLoss <= 0) return;
        directMeleeHits++; directMeleeDamage += actualHealthLoss;
        lastTick = Main.GameUpdateCount; lastType = "direct_melee";
        lastItemType = itemType; lastProjectileType = null; lastDamage = actualHealthLoss;
    }

    public override void OnHitByProjectile(NPC npc, Projectile projectile, NPC.HitInfo hit, int damageDone)
    {
        if (damageDone <= 0 || !projectile.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion) return;
        taggedProjectileHits++; taggedProjectileDamage += damageDone;
        lastTick = Main.GameUpdateCount; lastType = "tagged_projectile";
        lastItemType = null; lastProjectileType = projectile.type; lastDamage = damageDone;
    }

    public CompanionHitSnapshot Snapshot() => new(lastTick, lastType, lastItemType, lastProjectileType,
        lastDamage, directMeleeHits, directMeleeDamage, taggedProjectileHits, taggedProjectileDamage,
        directMeleeHits + taggedProjectileHits, directMeleeDamage + taggedProjectileDamage);
}
