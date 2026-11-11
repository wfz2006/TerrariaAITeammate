using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

/// <summary>An unregistered, inactive visual Player. Never runs Player.Update, ItemCheck or equipment effects.</summary>
public sealed class CompanionPlayerRenderer
{
    private static ConditionalWeakTable<Player, CompanionPlayerRenderer> proxies = new();
    private static readonly Dictionary<string, Asset<Texture2D>> textures = new();
    private readonly Player visual = new() { active = false, whoAmI = 255 };
    private readonly Item?[] equipmentSources = new Item?[6];
    private readonly int[] equipmentTypes = new int[6], equipmentPrefixes = new int[6];
    private Item? heldSource;
    private int heldType, heldPrefix;
    private ulong? preparedTick;
    private float walkPhase;
    private readonly CompanionVisualPose poseState = new();
    private bool usingItem;
    private float armRotation;
    private CompanionUseAnimation? preparedUseAnimation;
    public int PoseIndex { get; private set; }
    public Player VisualPlayer => visual;
    public Vector2 PreparedFrontHandPosition => usingItem
        ? visual.GetFrontHandPosition(Player.CompositeArmStretchAmount.Full, armRotation)
        : visual.Center + new Vector2(visual.direction * 3f, 7f);
    public static bool DiagnosticsEnabled { get; set; } = Environment.GetEnvironmentVariable("AITEAMMATE_RENDER_DIAGNOSTICS") == "1";
    public object? LastDrawDiagnostics { get; private set; }

    public CompanionPlayerRenderer()
    {
        visual.Male = false;
        visual.name = "AI 队友（仅渲染）";
        visual.hair = 0;
        visual.hairColor = new Color(91, 160, 218);
        visual.eyeColor = new Color(49, 112, 185);
        visual.skinColor = new Color(255, 218, 202);
        proxies.Add(visual, this);
    }

    public static bool TryGet(Player player, out CompanionPlayerRenderer renderer) => proxies.TryGetValue(player, out renderer!);
    internal static void Unload() { proxies = new(); textures.Clear(); }

    public void Draw(NPC npc, CompanionLoadout loadout, AIExecutor executor)
    {
        if (Main.dedServ) return;
        Prepare(npc, loadout, executor);
        Main.PlayerRenderer.DrawPlayer(Main.Camera, visual, visual.position, npc.rotation, Vector2.Zero, 0f, npc.scale);
    }

    private void Prepare(NPC npc, CompanionLoadout loadout, AIExecutor executor)
    {
        // Draw can be invoked repeatedly by native passes; animation advances only once per game tick.
        if (preparedTick != Main.GameUpdateCount)
        {
            preparedTick = Main.GameUpdateCount;
            if (Math.Abs(npc.velocity.X) > 0.2f) walkPhase += Math.Abs(npc.velocity.X) * 0.12f;
            else walkPhase = 0f;
        }
        int direction = npc.direction < 0 ? -1 : 1;
        visual.width = npc.width; visual.height = npc.height;
        visual.position = npc.position; visual.velocity = npc.velocity;
        visual.direction = direction; visual.gravDir = 1f;
        visual.active = false; visual.dead = false; visual.ghost = false;
        visual.heldProj = -1;
        visual.headRotation = visual.bodyRotation = visual.legRotation = 0f;
        visual.ResetVisibleAccessories();
        visual.wings = visual.wingsLogic = 0;
        for (int index = 0; index < 6; index++)
        {
            Item source = index < 3 ? loadout.Armor[index] : loadout.Accessories[index - 3];
            if (!ReferenceEquals(source, equipmentSources[index]) || source.type != equipmentTypes[index] || source.prefix != equipmentPrefixes[index])
            {
                equipmentSources[index] = source; equipmentTypes[index] = source.type; equipmentPrefixes[index] = source.prefix;
                // A draw snapshot is never put in Main.item or a live player's inventory.
                visual.armor[index] = source.Clone();
            }
        }
        visual.head = visual.armor[0].headSlot;
        visual.body = visual.armor[1].bodySlot;
        visual.legs = visual.armor[2].legSlot;
        for (int index = 3; index < 6; index++)
            if (!visual.armor[index].IsAir) visual.UpdateVisibleAccessory(index, visual.armor[index], false);

        Item? held = executor.VisualItem;
        if (held == null || held.IsAir)
        {
            visual.inventory[0].TurnToAir(); heldSource = null; heldType = heldPrefix = 0;
        }
        else if (!ReferenceEquals(held, heldSource) || held.type != heldType || held.prefix != heldPrefix)
        {
            heldSource = held; heldType = held.type; heldPrefix = held.prefix;
            visual.inventory[0] = held.Clone();
        }
        visual.selectedItem = 0;
        // CopyBasicPlayerFields reads this draw snapshot, not inventory[selectedItem].
        // Our inactive proxy never runs the Player update which normally sets it.
        visual.lastVisualizedSelectedItem = visual.inventory[0];
        CompanionUseAnimation animation = executor.Animation;
        preparedUseAnimation = animation;
        usingItem = animation.Active && held is { IsAir: false };
        visual.itemAnimationMax = Math.Max(4, animation.Duration);
        visual.itemAnimation = usingItem ? animation.Ticks : 0;
        visual.itemTimeMax = Math.Max(1, held?.useTime ?? 1);
        visual.itemTime = usingItem ? Math.Min(animation.Ticks, visual.itemTimeMax) : 0;
        visual.itemWidth = held?.width ?? 0; visual.itemHeight = held?.height ?? 0;
        float rotation = animation.Rotation(direction);
        visual.itemRotation = rotation - (direction < 0 ? MathHelper.Pi : 0f);
        armRotation = rotation - MathHelper.PiOver2;
        visual.SetCompositeArmFront(usingItem, Player.CompositeArmStretchAmount.Full, armRotation);
        visual.SetCompositeArmBack(usingItem && animation.Kind == CompanionUseKind.Shoot, Player.CompositeArmStretchAmount.Full, armRotation);
        visual.itemLocation = usingItem ? visual.GetFrontHandPosition(Player.CompositeArmStretchAmount.Full, armRotation)
            : visual.Center + new Vector2(direction * 7f, -7f);

        bool grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
        bool airborne = !grounded;
        // Native WingFrame's ordinary, non-flapping branch uses resting0/gliding1.
        // Do not call that whole method: special wings also emit dust, sound and RNG.
        // This is a pose only; no wingTime, wingTimeMax or flight capability is granted.
        visual.wingFrame = visual.wings > 0 && airborne ? 1 : 0;
        int groundPose = Math.Abs(npc.velocity.X) > 0.2f ? 1 + (int)walkPhase % 4 : 0;
        PoseIndex = poseState.Update(Main.GameUpdateCount, grounded, npc.velocity.Y, groundPose,
            usingItem && animation.Kind is CompanionUseKind.Swing or CompanionUseKind.Mine,
            usingItem && animation.Kind is CompanionUseKind.Shoot or CompanionUseKind.Thrust or CompanionUseKind.Yoyo);
        int legFrame = airborne ? 5 : groundPose == 0 ? 0 : 7 + (int)(walkPhase * 3f) % 13;
        int bodyFrame = usingItem ? animation.Kind is CompanionUseKind.Swing or CompanionUseKind.Mine
            ? 1 + Math.Min(2, (int)(animation.Progress * 3f)) : 3 : legFrame;
        visual.legFrame = new Rectangle(0, legFrame * 56, 40, 56);
        visual.bodyFrame = new Rectangle(0, bodyFrame * 56, 40, 56);
    }

    /// <summary>Optional pre-render cache inspection, not pixel visibility or gameplay evidence.</summary>
    internal void CaptureDrawDiagnostics(ref PlayerDrawSet drawInfo)
    {
        if (!DiagnosticsEnabled) return;
        var cache = drawInfo.DrawDataCache;
        Item held = visual.HeldItem;
        Vector2 frontHand = visual.GetFrontHandPosition(Player.CompositeArmStretchAmount.Full, armRotation);
        object Frame(Rectangle rect) => new { rect.X, rect.Y, rect.Width, rect.Height };
        object ColorFacts(Color color) => new { red = color.R, green = color.G, blue = color.B, alpha = color.A };
        object TextureFacts(int slot, Asset<Texture2D>[]? assets, Asset<Texture2D>? selectedAsset = null)
        {
            Asset<Texture2D>? asset = selectedAsset ?? (assets != null && slot > 0 && slot < assets.Length ? assets[slot] : null);
            Texture2D? texture = asset is { IsLoaded: true } ? asset.Value : null;
            var matches = cache.Select((data, index) => (data, index))
                .Where(entry => texture != null && ReferenceEquals(entry.data.texture, texture)).ToArray();
            return new
            {
                slot, assetName = asset?.Name, assetLoaded = texture != null,
                textureWidth = texture?.Width, textureHeight = texture?.Height,
                matchingDrawDataCount = matches.Length,
                visibleDrawDataCount = matches.Count(entry => entry.data.color.A != 0 && entry.data.scale.X != 0f && entry.data.scale.Y != 0f),
                matches = matches.Take(8).Select(entry => new
                {
                    index = entry.index, x = entry.data.position.X, y = entry.data.position.Y,
                    source = entry.data.sourceRect is Rectangle rect ? (object)new { rect.X, rect.Y, rect.Width, rect.Height } : null,
                    red = entry.data.color.R, green = entry.data.color.G, blue = entry.data.color.B, alpha = entry.data.color.A,
                    rotation = entry.data.rotation, originX = entry.data.origin.X, originY = entry.data.origin.Y,
                    scaleX = entry.data.scale.X, scaleY = entry.data.scale.Y,
                    effects = entry.data.effect.ToString(), shader = entry.data.shader
                }).ToArray(),
                matchesOmitted = Math.Max(0, matches.Length - 8)
            };
        }
        Asset<Texture2D>? nativeLegAsset = drawInfo.skinVar >= 0 && drawInfo.skinVar < TextureAssets.Players.GetLength(0)
            ? TextureAssets.Players[drawInfo.skinVar, 10] : null;
        textures.TryGetValue("Legs", out Asset<Texture2D>? customLegAsset);
        LastDrawDiagnostics = new
        {
            observedAtTick = Main.GameUpdateCount, preparedAtTick = preparedTick,
            visualActive = visual.active, visualPlayerIndex = visual.whoAmI,
            registeredInMainPlayers = Main.player.Any(player => ReferenceEquals(player, visual)),
            position = new { x = visual.position.X, y = visual.position.Y, feet = visual.Bottom.Y },
            velocity = new { x = visual.velocity.X, y = visual.velocity.Y },
            armorItems = visual.armor.Take(6).Select(item => new { item.type, item.prefix, item.stack }).ToArray(),
            visual.head, visual.body, visual.legs, visual.wings, visual.wingsLogic, visual.wingFrame,
            visual.wingTime, visual.wingTimeMax, visual.shield, visual.shieldRaised,
            visual.itemAnimation, visual.itemRotation,
            heldItem = visual.HeldItem.type, poseIndex = PoseIndex, grounded = poseState.Grounded,
            landingUntilTick = poseState.LandingUntilTick, drawDataCount = cache.Count,
            wing = TextureFacts(visual.wings, TextureAssets.Wings),
            shieldTexture = TextureFacts(visual.shield, TextureAssets.AccShield),
            heldTexture = TextureFacts(held.type, TextureAssets.Item),
            legLayers = new
            {
                nativeSkinVariant = drawInfo.skinVar, nativePlayerTextureIndex = 10,
                nativeLegSkinTint = ColorFacts(drawInfo.colorLegs), bodySkinTint = ColorFacts(drawInfo.colorBodySkin),
                customLegsActive = visual.legs < 0,
                nativeLegSkinTexture = TextureFacts(10, null, nativeLegAsset),
                customLegTexture = TextureFacts(0, null, customLegAsset),
                scope = "Native Skin uses Players[skinVar,10] and colorLegs; with the custom bare-leg sheet its native draws must have zero alpha. Leg armor retains native exposed skin. Cache visibility counts are nonzero tint/scale only, not proof of visible pixels or final occlusion."
            },
            heldState = new
            {
                itemType = held.type, held.prefix, held.stack, held.useStyle, held.holdStyle,
                drawSetHeldItemType = drawInfo.heldItem.type,
                drawSetHeldItemMatches = drawInfo.heldItem.type == held.type,
                held.noUseGraphic, held.noMelee, held.channel, held.shoot, held.shootSpeed,
                held.useAnimation, held.useTime, held.useAmmo, held.mana,
                damageClassType = held.DamageType.Type, useSoundConfigured = held.UseSound.HasValue,
                visual.direction, visual.gravDir, visual.heldProj, usingItem,
                visual.itemAnimation, visual.itemAnimationMax, visual.itemTime, visual.itemTimeMax,
                visual.itemRotation, visual.itemWidth, visual.itemHeight,
                itemLocation = new { x = visual.itemLocation.X, y = visual.itemLocation.Y },
                frontHandPosition = new { x = frontHand.X, y = frontHand.Y },
                frontArmRotation = armRotation, frontArmStretch = "Full",
                itemLocationToFrontHandPixels = Vector2.Distance(visual.itemLocation, frontHand),
                drawSetItemLocation = new { x = drawInfo.ItemLocation.X, y = drawInfo.ItemLocation.Y },
                screenPosition = new { x = Main.screenPosition.X, y = Main.screenPosition.Y },
                bodyFrame = Frame(visual.bodyFrame), legFrame = Frame(visual.legFrame),
                animation = preparedUseAnimation == null ? null : new
                {
                    active = preparedUseAnimation.Active, kind = preparedUseAnimation.Kind.ToString(),
                    ticks = preparedUseAnimation.Ticks, duration = preparedUseAnimation.Duration,
                    progress = preparedUseAnimation.Progress, itemType = preparedUseAnimation.Item?.type,
                    aim = new { x = preparedUseAnimation.Aim.X, y = preparedUseAnimation.Aim.Y }
                },
                scope = "Read-only pose/item metadata and actual native draw-cache item texture matches. noUseGraphic weapons can legitimately have no held-item texture and draw through a projectile instead; these data do not prove projectile attachment, visible pixels, sound, a hit or normal weapon-specific effects. Hand/item fields alone do not measure the grip pixel inside a texture."
            },
            scope = "Actual native pre-render DrawDataCache for the inactive, unregistered visual proxy. Texture membership, original source rectangles and tint only; not proof of nontransparent pixels, final occlusion, flight ability or equipment acquisition. No extra texture readback, lighting adjustment or Player update is performed."
        };
    }

    internal void AddLayer(ref PlayerDrawSet drawInfo, string name)
    {
        Player player = drawInfo.drawPlayer;
        bool armorBody = player.body >= 0, armorLegs = player.legs >= 0;
        if ((name is "Torso" or "FrontArm" or "BackArm") && armorBody || name == "Legs" && armorLegs || name == "Head" && player.head >= 0) return;
        if (name == "BackHair" && (drawInfo.hideHair || player.head >= 0 && !drawInfo.fullHair && !drawInfo.hatHair)) return;
        // The real accessory visuals take precedence over the character's decorative tail.
        if (name == "Tail" && (player.tail >= 0 || player.back >= 0 || player.wings > 0)) return;
        if (!textures.TryGetValue(name, out Asset<Texture2D>? asset))
            textures[name] = asset = ModContent.Request<Texture2D>("TerrariaAITeammate/Content/Skins/SharkMaid/" + name, AssetRequestMode.ImmediateLoad);
        Texture2D texture = asset.Value;
        int frameCount = texture.Width / CompanionVisualPose.FrameWidth;
        if (texture.Height != CompanionVisualPose.FrameHeight || texture.Width % CompanionVisualPose.FrameWidth != 0 ||
            frameCount is not (6 or CompanionVisualPose.FrameCount)) return;
        bool arm = name is "FrontArm" or "BackArm";
        // Active arms keep the neutral, aligned grip and rotate with the real use
        // animation; the torso/head/legs still use the melee/shoot body pose.
        int frame = arm && usingItem ? 0 : CompanionVisualPose.ResolveAtlasFrame(PoseIndex, frameCount);
        Vector2 topLeft = drawInfo.Position + new Vector2(player.width / 2f - 20f, player.height - 54f);
        Vector2 origin = Vector2.Zero;
        float rotation = 0f;
        if (arm && usingItem && !(name == "BackArm" && preparedUseAnimation?.Kind is CompanionUseKind.Thrust or CompanionUseKind.Yoyo))
        {
            int pivotX = name == "FrontArm" ? 23 : 17;
            origin = new Vector2(player.direction < 0 ? 40 - pivotX : pivotX, 26);
            rotation = armRotation;
            Vector2 hand = name == "FrontArm" ? player.GetFrontHandPosition(Player.CompositeArmStretchAmount.Full, armRotation)
                : player.GetBackHandPosition(Player.CompositeArmStretchAmount.Full, armRotation);
            topLeft = hand - Vector2.UnitY.RotatedBy(rotation) * 14f;
        }
        Color tint = drawInfo.colorArmorBody * (1f - drawInfo.shadow);
        drawInfo.DrawDataCache.Add(new DrawData(texture, topLeft - Main.screenPosition,
            new Rectangle(frame * 40, 0, 40, 56), tint, rotation, origin, 1f,
            player.direction < 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0));
    }
}

public sealed class CompanionRenderingSystem : ModSystem
{
    public override void Unload() => CompanionPlayerRenderer.Unload();
}
