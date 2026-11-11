using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public readonly record struct WeaponUseResult(bool Fired, int Cooldown, string Message);

public static class CompanionWeapons
{
    internal const float MeleeRange = 3.4f * 16f;

    public static float AttackRange(CompanionLoadout loadout)
    {
        Item? weapon = loadout.GetBestWeapon();
        if (weapon == null)
            return 25f * 16f;

        if (CompanionBoundYoyo.IsYoyo(weapon))
            return Math.Max(MeleeRange, CompanionBoundYoyo.Range(weapon) - 16f);

        if (weapon.useAmmo > 0 && loadout.FindAmmo(weapon.useAmmo) == null)
        {
            Item? melee = loadout.GetBestMeleeWeapon();
            if (melee != null)
                return MeleeRange;
        }

        if (weapon.useAmmo > 0 || weapon.mana > 0 || weapon.DamageType == DamageClass.Ranged || weapon.DamageType == DamageClass.Magic)
            return 22f * 16f;

        return MeleeRange;
    }

    public static WeaponUseResult Use(NPC npc, NPC target, CompanionLoadout loadout, CompanionUseAnimation animation,
        Vector2? aimPoint = null)
    {
        Item? weapon = loadout.GetBestWeapon();
        Vector2 aim = Direction(npc.Center, aimPoint ?? target.Center);

        if (weapon == null)
            return FallbackBolt(npc, aim, loadout);

        npc.direction = npc.spriteDirection = aim.X >= 0f ? 1 : -1;

        if (CompanionBoundYoyo.IsYoyo(weapon))
        {
            bool fired = CompanionBoundYoyo.TryUse(npc, target, weapon, animation, out bool launched, out string error);
            if (launched && weapon.UseSound.HasValue) SoundEngine.PlaySound(weapon.UseSound, npc.Center);
            return new WeaponUseResult(fired, fired ? Cooldown(weapon) : 6, error);
        }

        if (weapon.useAmmo > 0)
        {
            Item? ammo = loadout.FindAmmo(weapon.useAmmo);
            if (ammo != null && ammo.shoot > ProjectileID.None)
            {
                int projectile = ammo.shoot;
                int damage = weapon.damage + Math.Max(0, ammo.damage);
                // Native PickAmmo adds the ammunition's speed to the weapon's speed.
                float speed = Math.Max(4f, weapon.shootSpeed + ammo.shootSpeed);
                float knockback = Math.Max(weapon.knockBack, ammo.knockBack);

                if (!Fire(npc, aim, projectile, damage, knockback, speed, out string error))
                    return FallbackBolt(npc, aim, loadout, error);

                loadout.RemoveItem(ammo.type, 1);
                StartUse(npc, weapon, CompanionUseKind.Shoot, animation, aim);
                return new WeaponUseResult(true, Cooldown(weapon), "");
            }

            Item? melee = loadout.GetBestMeleeWeapon();
            if (melee == null)
                return FallbackBolt(npc, aim, loadout, "没弹药了，先用魔法弹顶着。");

            weapon = melee;
        }

        if (weapon.mana > 0 && weapon.shoot > ProjectileID.None)
        {
            if (loadout.Mana < weapon.mana)
                return new WeaponUseResult(false, 30, "法力不够。");

            loadout.Mana -= weapon.mana;
            int damage = weapon.damage;
            float speed = weapon.shootSpeed > 0f ? weapon.shootSpeed : 10f;
            if (!Fire(npc, aim, weapon.shoot, damage, weapon.knockBack, speed, out string error))
                return FallbackBolt(npc, aim, loadout, error);

            StartUse(npc, weapon, CompanionUseKind.Shoot, animation, aim);
            return new WeaponUseResult(true, Cooldown(weapon), "");
        }

        if (weapon.shoot > ProjectileID.None && weapon.consumable && !IsMelee(weapon) && weapon.useAmmo == 0)
        {
            int damage = weapon.damage;
            float speed = weapon.shootSpeed > 0f ? weapon.shootSpeed : 9f;
            if (!Fire(npc, aim, weapon.shoot, damage, weapon.knockBack, speed, out string error))
                return FallbackBolt(npc, aim, loadout, error);

            loadout.RemoveItem(weapon.type, 1);
            StartUse(npc, weapon, CompanionUseKind.Shoot, animation, aim);
            return new WeaponUseResult(true, Cooldown(weapon), "");
        }

        if (CompanionBoundShortsword.Supports(weapon.shoot))
        {
            int index = Projectile.NewProjectile(npc.GetSource_FromAI(), npc.Center, aim * weapon.shootSpeed,
                weapon.shoot, weapon.damage, weapon.knockBack, Main.myPlayer);
            if (index < 0 || index >= Main.maxProjectiles)
                return new WeaponUseResult(false, 30, "短剑刺击生成失败。");

            Projectile stab = Main.projectile[index];
            stab.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion = true;
            StartUse(npc, weapon, CompanionUseKind.Thrust, animation, aim);
            if (!CompanionBoundShortsword.Attach(stab, npc, animation))
            {
                stab.Kill();
                return new WeaponUseResult(false, 30, "短剑刺击未能绑定到小星。");
            }
            if (weapon.UseSound.HasValue) SoundEngine.PlaySound(weapon.UseSound, npc.Center);
            // Damage comes from the real stab projectile, never a second generic strike.
            return new WeaponUseResult(true, Cooldown(weapon), "");
        }

        HitMelee(npc, aim, loadout, weapon);
        if (weapon.shoot > ProjectileID.None)
        {
            float speed = weapon.shootSpeed > 0f ? weapon.shootSpeed : 9f;
            Fire(npc, aim, weapon.shoot, weapon.damage, weapon.knockBack, speed, out _);
        }

        StartUse(npc, weapon, CompanionUseKind.Swing, animation, aim);
        if (weapon.UseSound.HasValue)
            SoundEngine.PlaySound(weapon.UseSound, npc.Center);
        else
            SoundEngine.PlaySound(SoundID.Item1, npc.Center);

        return new WeaponUseResult(true, Cooldown(weapon), "");
    }

    private static WeaponUseResult FallbackBolt(NPC npc, Vector2 aim, CompanionLoadout loadout, string message = "")
    {
        int damage = Math.Max(12, loadout.WeaponDamage);
        int index = Projectile.NewProjectile(npc.GetSource_FromAI(), npc.Center + aim * 18f, aim * 9f,
            ModContent.ProjectileType<Content.Projectiles.AIBolt>(), damage, 2f, Main.myPlayer);

        if (index < 0 || index >= Main.maxProjectiles)
            return new WeaponUseResult(false, 30, string.IsNullOrEmpty(message) ? "打不出去。" : message);
        Main.projectile[index].GetGlobalProjectile<CompanionProjectileTag>().FromCompanion = true;

        return new WeaponUseResult(true, 28, message);
    }

    private static bool Fire(NPC npc, Vector2 aim, int projectile, int damage, float knockback, float speed,
        out string error)
    {
        error = "";
        if (projectile <= ProjectileID.None)
        {
            error = "这个武器没有弹道。";
            return false;
        }

        Vector2 position = npc.Center + aim * 18f;
        int index = Projectile.NewProjectile(npc.GetSource_FromAI(), position, aim * speed, projectile,
            damage, knockback, Main.myPlayer);

        if (index < 0 || index >= Main.maxProjectiles)
        {
            error = "弹道生成失败。";
            return false;
        }

        for (int i = 0; i < 3; i++)
        {
            Dust dust = Dust.NewDustDirect(position, 8, 8, DustID.Smoke, aim.X * 0.8f, aim.Y * 0.8f, 100, default, 0.7f);
            dust.noGravity = true;
        }

        Main.projectile[index].GetGlobalProjectile<CompanionProjectileTag>().FromCompanion = true;
        return true;
    }

    private static int HitMelee(NPC npc, Vector2 aim, CompanionLoadout loadout, Item weapon)
    {
        int damage = weapon.damage;
        int direction = aim.X >= 0f ? 1 : -1;
        int hits = 0;

        foreach (NPC other in Main.npc)
        {
            if (!other.active || other.friendly || other.life <= 0 || other.dontTakeDamage ||
                !other.CanBeChasedBy())
                continue;

            Vector2 offset = other.Center - npc.Center;
            float distance = offset.Length();
            if (distance > MeleeRange + other.width * 0.5f)
                continue;

            Vector2 toTarget = distance > 0.01f ? offset / distance : aim;
            if (distance > 20f && Vector2.Dot(aim, toTarget) < 0.1f)
                continue;

            if (!CompanionAim.TryFindVisiblePoint(npc.Center, other.Center, other.width, out _))
                continue;

            int beforeLife = other.life;
            other.SimpleStrikeNPC(damage, direction, false, weapon.knockBack);
            other.GetGlobalNPC<CompanionHitWitness>().RecordDirectMelee(weapon.type,
                Math.Min(beforeLife, Math.Max(0, beforeLife - other.life)));
            AITeammateSystem.Instance.BossEvidence.Hit(other.GetGlobalNPC<BossWitness>().SpawnId,
                Math.Min(beforeLife, Math.Max(0, beforeLife - other.life)));
            hits++;

            for (int i = 0; i < 4; i++)
            {
                Dust dust = Dust.NewDustDirect(other.position, other.width, other.height, DustID.Blood,
                    aim.X * 2f, aim.Y * 2f, 100, default, 0.8f);
                dust.noGravity = true;
            }
        }

        return hits;
    }

    private static void StartUse(NPC npc, Item item, CompanionUseKind kind, CompanionUseAnimation animation, Vector2 aim)
    {
        animation.Start(item, kind, item.useAnimation, aim);

        if (kind == CompanionUseKind.Shoot && item.UseSound.HasValue)
            SoundEngine.PlaySound(item.UseSound, npc.Center);
    }

    private static bool IsMelee(Item item) =>
        item.DamageType == DamageClass.Melee || item.DamageType == DamageClass.MeleeNoSpeed;

    private static int Cooldown(Item item) => Math.Max(6, item.useTime);

    private static Vector2 Direction(Vector2 from, Vector2 to)
    {
        Vector2 offset = to - from;
        return offset.LengthSquared() > 0.01f ? Vector2.Normalize(offset) : Vector2.UnitX;
    }
}
