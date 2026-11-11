using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Content.NPCs;

public class AICompanion : ModNPC
{
    private Common.CompanionPlayerRenderer? playerRenderer;
    internal Common.CompanionPlayerRenderer? Renderer => playerRenderer;

    public override void SetStaticDefaults()
    {
        Main.npcFrameCount[Type] = 1;
    }

    public override void SetDefaults()
    {
        NPC.width = 20;
        NPC.height = 42;
        NPC.friendly = true;
        NPC.townNPC = false;
        NPC.lifeMax = 200;
        NPC.damage = 20;
        NPC.defense = 10;
        NPC.knockBackResist = 0.2f;
        NPC.aiStyle = -1;
        NPC.noGravity = true;
        NPC.noTileCollide = false;
        NPC.HitSound = SoundID.NPCHit1;
        NPC.DeathSound = SoundID.NPCDeath1;
        NPC.value = 0f;
        NPC.netAlways = true;
        NPC.npcSlots = 0f;
    }

    public override void AI()
    {
        NPC.defense = Common.AITeammateSystem.Instance.Loadout.Defense;
        Common.AITeammateSystem.Instance.Executor.Tick(NPC);
    }

    // Exploration can legitimately take the teammate beyond the player's active NPC range.
    // Preserve that NPC until it is explicitly removed or actually killed.
    public override bool CheckActive() => false;

    public override bool? CanFallThroughPlatforms()
        => Common.AITeammateSystem.Instance.Executor.DropThroughPlatforms;

    public override bool PreDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        playerRenderer ??= new Common.CompanionPlayerRenderer();
        playerRenderer.Draw(NPC, Common.AITeammateSystem.Instance.Loadout, Common.AITeammateSystem.Instance.Executor);
        return false;
    }

    public override void OnKill()
    {
        Common.AITeammateSystem.Instance.RecordNaturalBossActorDeath(NPC);
        Common.AITeammateSystem.Instance.RecordComplexCompanionDeath(NPC);
        Main.NewText(Common.CompanionControlsUI.Text("Fallen", Common.AITeammateSystem.CompanionDisplayName), Color.OrangeRed);
    }
}
