using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

/// <summary>Basic vanilla yoyo flight and tether owned by an NPC, without Player/channel or draw-side input writes.</summary>
public sealed class CompanionBoundYoyo : GlobalProjectile
{
    private NPC? companion, target;
    private ModNPC? companionInstance;
    private CompanionUseAnimation? animation;
    private int weaponType, ageTicks;
    private ulong? sampledTick;
    private float maxRange, topSpeed;
    private int lifetimeTicks;
    public bool Bound { get; private set; }
    public bool Returning { get; private set; }
    public Vector2? LastStringAnchor { get; private set; }
    public ulong? LastDrawTick { get; private set; }
    public override bool InstancePerEntity => true;

    public static bool IsYoyo(Item weapon) => weapon.type > ItemID.None && weapon.type < ItemID.Sets.Yoyo.Length && ItemID.Sets.Yoyo[weapon.type];
    public static float Range(Item weapon) => weapon.shoot > ProjectileID.None && weapon.shoot < ProjectileID.Sets.YoyosMaximumRange.Length
        ? Math.Max(32f, ProjectileID.Sets.YoyosMaximumRange[weapon.shoot]) : 32f;

    public static bool TryUse(NPC owner, NPC enemy, Item weapon, CompanionUseAnimation use, out bool launched, out string error)
    {
        launched = false; error = "";
        if (Main.netMode != NetmodeID.SinglePlayer)
        { error = "悠悠球的同伴适配目前只支持单人游戏。"; return false; }
        if (!IsYoyo(weapon) || weapon.ModItem != null || weapon.type >= ItemID.Count ||
            weapon.shoot <= ProjectileID.None || weapon.shoot >= ProjectileID.Count)
        { error = "这种悠悠球还没有同伴适配，先换其他武器。"; return false; }

        foreach (Projectile existing in Main.projectile)
        {
            if (!existing.active) continue;
            var binding = existing.GetGlobalProjectile<CompanionBoundYoyo>();
            if (!binding.Bound || !ReferenceEquals(binding.companion, owner)) continue;
            if (binding.Returning || binding.weaponType != weapon.type)
            { binding.RequestReturn(existing); return false; }
            use.Start(weapon, CompanionUseKind.Yoyo, weapon.useAnimation, enemy.Center - owner.Center);
            binding.target = enemy; binding.animation = use;
            return true;
        }

        Vector2 aim = (enemy.Center - owner.Center).SafeNormalize(Vector2.UnitX);
        int index = Projectile.NewProjectile(owner.GetSource_FromAI(), owner.Center, aim * Math.Max(1f, weapon.shootSpeed),
            weapon.shoot, weapon.damage, weapon.knockBack, Main.myPlayer);
        if (index < 0 || index >= Main.maxProjectiles) { error = "悠悠球生成失败。"; return false; }
        Projectile projectile = Main.projectile[index];
        projectile.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion = true;
        use.Start(weapon, CompanionUseKind.Yoyo, weapon.useAnimation, aim);
        if (!Attach(projectile, owner, enemy, weapon, use))
        { projectile.Kill(); error = "悠悠球未能绑定到小星。"; return false; }
        launched = true;
        return true;
    }

    public static bool Attach(Projectile projectile, NPC owner, NPC enemy, Item weapon, CompanionUseAnimation use)
    {
        if (Main.netMode != NetmodeID.SinglePlayer || !IsYoyo(weapon) || weapon.ModItem != null || projectile.ModProjectile != null ||
            projectile.aiStyle != ProjAIStyleID.Yoyo || projectile.type != weapon.shoot ||
            !projectile.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion ||
            !owner.active || owner.ModNPC is not Content.NPCs.AICompanion ||
            !use.Active || use.Kind != CompanionUseKind.Yoyo || use.Item?.type != weapon.type)
            return false;

        var binding = projectile.GetGlobalProjectile<CompanionBoundYoyo>();
        binding.Bound = true; binding.companion = owner; binding.companionInstance = owner.ModNPC;
        binding.target = enemy; binding.animation = use; binding.weaponType = weapon.type;
        binding.maxRange = Range(weapon);
        binding.topSpeed = Math.Max(1f, ProjectileID.Sets.YoyosTopSpeed[projectile.type]);
        float seconds = ProjectileID.Sets.YoyosLifeTimeMultiplier[projectile.type];
        binding.lifetimeTicks = seconds < 0f ? int.MaxValue : Math.Max(1, (int)(seconds * 60f));
        projectile.ownerHitCheck = false; projectile.hide = true;
        projectile.Center = owner.Center; projectile.netUpdate = true;
        return true;
    }

    private bool OwnerPresent => companion is { active: true } && ReferenceEquals(companion.ModNPC, companionInstance);
    private bool Held => animation is { Active: true, Kind: CompanionUseKind.Yoyo } && animation.Item?.type == weaponType;

    private void RequestReturn(Projectile projectile)
    {
        Returning = true; projectile.ai[0] = -1f;
        projectile.friendly = false; projectile.tileCollide = false;
    }

    public override bool PreAI(Projectile projectile)
    {
        if (!Bound) return true;
        if (!OwnerPresent) { projectile.Kill(); return false; }
        if (sampledTick != Main.GameUpdateCount) { sampledTick = Main.GameUpdateCount; ageTicks++; }
        projectile.timeLeft = 2;
        float speed = topSpeed / Math.Max(1, projectile.extraUpdates + 1);
        Vector2 anchor = companion!.Center;
        if (!Held || target is not { active: true, life: > 0 } || ageTicks >= lifetimeTicks ||
            Vector2.Distance(projectile.Center, anchor) > maxRange + 80f)
            RequestReturn(projectile);

        if (Returning)
        {
            // Finish retracting in the same hand pose after release; a newer
            // weapon animation is never replaced by the returning yoyo.
            if (animation is { Active: false, Kind: CompanionUseKind.Yoyo, Item: { } held } && held.type == weaponType)
                animation.Start(held, CompanionUseKind.Yoyo, 4, projectile.Center - anchor);
            Vector2 delta = anchor - projectile.Center;
            float returnSpeed = Math.Max(8f, speed * 1.5f);
            if (delta.LengthSquared() <= returnSpeed * returnSpeed) { projectile.Kill(); return false; }
            projectile.velocity = delta.SafeNormalize(Vector2.Zero) * returnSpeed;
        }
        else
        {
            Vector2 desired = target!.Center - anchor;
            float limit = Math.Max(16f, maxRange - 8f);
            if (desired.LengthSquared() > limit * limit) desired = desired.SafeNormalize(Vector2.Zero) * limit;
            Vector2 delta = anchor + desired - projectile.Center;
            Vector2 wanted = delta.LengthSquared() <= speed * speed ? delta : delta.SafeNormalize(Vector2.Zero) * speed;
            projectile.velocity = Vector2.Lerp(projectile.velocity, wanted, 0.35f);
            Vector2 next = projectile.Center + projectile.velocity - anchor;
            if (next.LengthSquared() > maxRange * maxRange)
                projectile.velocity = anchor + next.SafeNormalize(Vector2.Zero) * maxRange - projectile.Center;
        }
        animation?.TrackYoyo(projectile.Center - anchor);
        projectile.rotation += 0.35f;
        projectile.spriteDirection = projectile.velocity.X < 0f ? -1 : 1;
        return false;
    }

    public override bool OnTileCollide(Projectile projectile, Vector2 oldVelocity)
    {
        if (!Bound) return true;
        RequestReturn(projectile);
        return false;
    }

    public override bool? CanHitNPC(Projectile projectile, NPC enemy) => !Bound ? null :
        OwnerPresent && Held && !Returning && Collision.CanHitLine(companion!.Center, 1, 1, enemy.Center, 1, 1) ? null : false;

    public override bool PreDrawExtras(Projectile projectile) => !Bound;

    public override void DrawBehind(Projectile projectile, int index, List<int> behindNPCsAndTiles,
        List<int> behindNPCs, List<int> behindProjectiles, List<int> overPlayers, List<int> overWiresUI)
    { if (Bound) overPlayers.Add(index); }

    public override bool PreDraw(Projectile projectile, ref Color lightColor)
    {
        if (!Bound) return true;
        if (!OwnerPresent) return false;
        Vector2 hand = companion!.Center;
        if (companion.ModNPC is Content.NPCs.AICompanion { Renderer: { } renderer })
        {
            // Use the pose that actually drew the arm, not a second angle
            // computed from the ball after the engine moved it this frame.
            hand += renderer.PreparedFrontHandPosition - renderer.VisualPlayer.Center;
        }
        LastStringAnchor = hand; LastDrawTick = Main.GameUpdateCount;
        Texture2D line = TextureAssets.FishingLine.Value;
        Vector2 span = projectile.Center - hand;
        float distance = span.Length();
        if (distance > .1f && distance < 2000f)
        {
            Vector2 step = span / distance;
            for (float offset = 0; offset < distance; offset += 8f)
            {
                Vector2 point = hand + step * offset;
                Color tint = Lighting.GetColor((int)point.X / 16, (int)point.Y / 16, new Color(180, 180, 180));
                Main.EntitySpriteDraw(line, point - Main.screenPosition,
                    new Rectangle(0, 0, line.Width, Math.Min(line.Height, (int)Math.Ceiling(Math.Min(8f, distance - offset)))),
                    tint, span.ToRotation() - MathHelper.PiOver2, new Vector2(line.Width / 2f, 0f), 1f, SpriteEffects.None);
            }
        }
        Main.instance.LoadProjectile(projectile.type);
        Texture2D texture = TextureAssets.Projectile[projectile.type].Value;
        Rectangle frame = texture.Frame(1, Main.projFrames[projectile.type], 0, projectile.frame);
        Main.EntitySpriteDraw(texture, projectile.Center - Main.screenPosition, frame, projectile.GetAlpha(lightColor),
            projectile.rotation, frame.Size() / 2f, projectile.scale, SpriteEffects.None);
        return false;
    }
}
