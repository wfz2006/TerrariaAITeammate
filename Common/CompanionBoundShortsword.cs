using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

/// <summary>NPC attachment for the eight vanilla shortsword stabs; never runs their player-owner AI.</summary>
public sealed class CompanionBoundShortsword : GlobalProjectile
{
    private NPC? companion;
    private ModNPC? companionInstance;
    private CompanionUseAnimation? animation;
    private uint useSequence;
    private int itemType;

    public override bool InstancePerEntity => true;
    public bool Bound { get; private set; }

    public static bool Supports(int projectileType) => projectileType is >= 938 and <= 945;

    public static bool Attach(Projectile projectile, NPC npc, CompanionUseAnimation animation)
    {
        if (!Supports(projectile.type) || projectile.ModProjectile != null ||
            projectile.aiStyle != ProjAIStyleID.ShortSword ||
            !projectile.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion ||
            npc.ModNPC is not Content.NPCs.AICompanion || !npc.active ||
            !animation.Active || animation.Kind != CompanionUseKind.Thrust ||
            animation.Item?.shoot != projectile.type)
            return false;

        var attachment = projectile.GetGlobalProjectile<CompanionBoundShortsword>();
        attachment.Bound = true;
        attachment.companion = npc;
        attachment.companionInstance = npc.ModNPC;
        attachment.animation = animation;
        attachment.useSequence = animation.UseSequence;
        attachment.itemType = animation.Item.type;
        // Native stabs normally test line of sight from their Player owner and are
        // drawn via Player.heldProj. Both references must instead stay with this NPC.
        projectile.ownerHitCheck = false;
        projectile.hide = true;
        projectile.Center = npc.Center;
        return true;
    }

    private bool IsCurrent => companion is { active: true } &&
        ReferenceEquals(companion.ModNPC, companionInstance) &&
        animation is { Active: true, Kind: CompanionUseKind.Thrust } &&
        animation.UseSequence == useSequence && animation.Item?.type == itemType;

    public override bool PreAI(Projectile projectile)
    {
        if (!Bound) return true;
        if (!IsCurrent) { projectile.Kill(); return false; }

        // The installed AI_161_RapierStabs uses these 16 substeps and opacity
        // ramps. Retain its native extraUpdates, texture, hitbox and damage path.
        float age = ++projectile.ai[0];
        projectile.rotation = projectile.velocity.ToRotation() + MathHelper.PiOver2;
        projectile.Opacity = Utils.GetLerpValue(0f, 7f, age, true) * Utils.GetLerpValue(16f, 12f, age, true);
        projectile.Center = companion!.Center + projectile.velocity * (age - 1f);
        projectile.spriteDirection = projectile.velocity.X < 0f ? -1 : 1;
        if (age >= 16f) projectile.Kill();
        return false;
    }

    public override bool ShouldUpdatePosition(Projectile projectile) => !Bound;

    public override bool? CanHitNPC(Projectile projectile, NPC target)
    {
        if (!Bound) return null;
        return IsCurrent && CompanionAim.TryFindVisiblePoint(companion!.Center, target.Center, target.width, out _)
            ? null : false;
    }

    public override void DrawBehind(Projectile projectile, int index, List<int> behindNPCsAndTiles,
        List<int> behindNPCs, List<int> behindProjectiles, List<int> overPlayers, List<int> overWiresUI)
    {
        if (Bound) overPlayers.Add(index);
    }

    public override bool PreDraw(Projectile projectile, ref Color lightColor)
    {
        if (!Bound) return true;
        if (!IsCurrent) return false;
        Main.instance.LoadProjectile(projectile.type);
        Texture2D texture = TextureAssets.Projectile[projectile.type].Value;
        Rectangle frame = texture.Frame(1, Main.projFrames[projectile.type], 0, projectile.frame);
        Main.EntitySpriteDraw(texture, projectile.Center - Main.screenPosition, frame,
            projectile.GetAlpha(lightColor), projectile.rotation, frame.Size() / 2f,
            projectile.scale, projectile.spriteDirection < 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None);
        return false;
    }
}
