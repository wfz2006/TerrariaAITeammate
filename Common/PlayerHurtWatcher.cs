using Terraria;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public class PlayerHurtWatcher : ModPlayer
{
    public override void PostHurt(Player.HurtInfo info)
    {
        if (Player.whoAmI != Main.myPlayer || !Player.active || Player.dead)
            return;

        AITeammateSystem.Instance.OnPlayerHurt(info.DamageSource);
    }
}
