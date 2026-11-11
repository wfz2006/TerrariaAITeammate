using Microsoft.Xna.Framework;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

/// <summary>Only the unregistered teammate proxy uses these clothes. Native armor/accessory layers remain enabled.</summary>
public sealed class CompanionVisualPlayer : ModPlayer
{
    public override void TransformDrawData(ref PlayerDrawSet drawInfo)
    {
        if (CompanionPlayerRenderer.TryGet(Player, out CompanionPlayerRenderer renderer)) renderer.CaptureDrawDiagnostics(ref drawInfo);
    }

    public override void ModifyDrawInfo(ref PlayerDrawSet drawInfo)
    {
        if (!CompanionPlayerRenderer.TryGet(Player, out _)) return;
        drawInfo.colorHair = Color.Transparent;
        drawInfo.colorShirt = drawInfo.colorUnderShirt = Color.Transparent;
        drawInfo.colorPants = drawInfo.colorShoes = Color.Transparent;
        if (Player.body < 0) drawInfo.colorBodySkin = Color.Transparent;
        // Skin draws bare legs separately from pants/shoes. The custom Legs
        // sheet already contains them; otherwise native walking legs remain
        // visible beneath it. Keep exposed native skin when leg armor is worn.
        if (Player.legs < 0) drawInfo.colorLegs = Color.Transparent;
    }
}

public abstract class CompanionSkinLayer : PlayerDrawLayer
{
    protected abstract string Part { get; }
    public override bool GetDefaultVisibility(PlayerDrawSet drawInfo) => CompanionPlayerRenderer.TryGet(drawInfo.drawPlayer, out _);
    protected override void Draw(ref PlayerDrawSet drawInfo)
    {
        if (CompanionPlayerRenderer.TryGet(drawInfo.drawPlayer, out CompanionPlayerRenderer renderer)) renderer.AddLayer(ref drawInfo, Part);
    }
}
public sealed class CompanionTailLayer : CompanionSkinLayer
{
    protected override string Part => "Tail";
    public override Position GetDefaultPosition() => new BeforeParent(PlayerDrawLayers.Tails);
}
public sealed class CompanionBackHairLayer : CompanionSkinLayer
{
    protected override string Part => "BackHair";
    public override Position GetDefaultPosition() => new BeforeParent(PlayerDrawLayers.Skin);
}
public sealed class CompanionBackArmLayer : CompanionSkinLayer
{
    protected override string Part => "BackArm";
    public override Position GetDefaultPosition() => new AfterParent(PlayerDrawLayers.Skin);
}
public sealed class CompanionLegsLayer : CompanionSkinLayer
{
    protected override string Part => "Legs";
    public override Position GetDefaultPosition() => new BeforeParent(PlayerDrawLayers.Leggings);
}
public sealed class CompanionTorsoLayer : CompanionSkinLayer
{
    protected override string Part => "Torso";
    public override Position GetDefaultPosition() => new BeforeParent(PlayerDrawLayers.Torso);
}
public sealed class CompanionHeadLayer : CompanionSkinLayer
{
    protected override string Part => "Head";
    public override Position GetDefaultPosition() => new AfterParent(PlayerDrawLayers.Head);
}
public sealed class CompanionFrontArmLayer : CompanionSkinLayer
{
    protected override string Part => "FrontArm";
    public override Position GetDefaultPosition() => new BeforeParent(PlayerDrawLayers.ArmOverItem);
}
