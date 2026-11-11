using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Content.Projectiles;

public class AIBolt : ModProjectile
{
    public override void SetDefaults()
    {
        Projectile.width = 12;
        Projectile.height = 12;
        Projectile.friendly = true;
        Projectile.hostile = false;
        Projectile.DamageType = DamageClass.Ranged;
        Projectile.penetrate = 1;
        Projectile.timeLeft = 120;
        Projectile.light = 0.6f;
        Projectile.ignoreWater = true;
        Projectile.tileCollide = true;
        Projectile.aiStyle = -1;
    }

    public override void AI()
    {
        Projectile.rotation = Projectile.velocity.ToRotation() + MathHelper.PiOver2;
        Lighting.AddLight(Projectile.Center, 0.2f, 0.7f, 0.9f);

        if (Main.rand.NextBool(2))
            Dust.NewDust(Projectile.position, Projectile.width, Projectile.height, DustID.Electric, 0f, 0f, 100, default, 0.6f);

        NPC? target = FindTarget();
        if (target != null)
        {
            Vector2 desired = target.Center - Projectile.Center;
            if (desired.LengthSquared() > 0.01f)
            {
                desired.Normalize();
                desired *= Projectile.velocity.Length();
                Projectile.velocity = Vector2.Lerp(Projectile.velocity, desired, 0.08f);
            }
        }
    }

    private NPC? FindTarget()
    {
        NPC? best = null;
        float bestDistance = 20f * 16f;

        foreach (NPC npc in Main.npc)
        {
            if (!npc.active || npc.friendly || npc.life <= 0 || npc.dontTakeDamage)
                continue;

            float distance = Vector2.Distance(npc.Center, Projectile.Center);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = npc;
            }
        }

        return best;
    }

    public override void OnKill(int timeLeft)
    {
        for (int i = 0; i < 8; i++)
            Dust.NewDust(Projectile.position, Projectile.width, Projectile.height, DustID.Electric, 0f, 0f, 100, default, 0.8f);
    }
}
