using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;
using TerrariaAITeammate.Common;

namespace TerrariaAITeammate.Content.Items;

public class BlueprintWand : ModItem
{
    public override void SetDefaults()
    {
        Item.width = 32;
        Item.height = 32;
        Item.useStyle = ItemUseStyleID.Shoot;
        Item.useAnimation = 20;
        Item.useTime = 20;
        Item.useTurn = true;
        Item.autoReuse = false;
        Item.noMelee = true;
        Item.maxStack = 1;
        Item.rare = ItemRarityID.Blue;
        Item.value = 0;
        Item.UseSound = SoundID.Item1;
    }

    public override bool AltFunctionUse(Player player) => true;

    public override bool? UseItem(Player player)
    {
        if (player.altFunctionUse == 2)
        {
            BlueprintManager.ClearSelection();
            Main.NewText("[蓝图] 选区已清空。", Color.LightBlue);
            return true;
        }

        int x = (int)(Main.MouseWorld.X / 16f);
        int y = (int)(Main.MouseWorld.Y / 16f);
        string message = BlueprintManager.SetCorner(x, y);
        Main.NewText("[蓝图] " + message, Color.LightBlue);

        for (int i = 0; i < 6; i++)
        {
            Dust dust = Dust.NewDustDirect(new Vector2(x * 16, y * 16), 16, 16, DustID.MagicMirror,
                0f, 0f, 100, Color.Cyan, 0.9f);
            dust.noGravity = true;
        }

        SoundEngine.PlaySound(SoundID.Item1, new Vector2(x * 16, y * 16));
        return true;
    }
}
