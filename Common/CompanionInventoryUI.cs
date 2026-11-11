using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public class CompanionInventoryUI
{
    private const int SlotSize = 38;
    private const int Columns = 10;
    private const int Rows = 5;
    private const int Padding = 12;
    private const int TitleHeight = 30;
    private const int EquipHeight = 40;
    private const int StatsHeight = 30;
    private const int DepositHeight = 28;

    public bool Visible { get; private set; }

    private readonly Rectangle[] slotRects = new Rectangle[CompanionLoadout.InventorySize + 6];
    private Rectangle panelRect;
    private Rectangle closeRect;
    private Rectangle depositRect;
    private int hoveredSlot = -1;
    private bool lastMouseLeft;
    private bool openedVanillaInventory;

    private static CompanionLoadout Loadout => AITeammateSystem.Instance.Loadout;
    private static int PanelWidth => Columns * SlotSize + Padding * 2;
    private static int PanelHeight => TitleHeight + EquipHeight + Rows * SlotSize + StatsHeight + DepositHeight + Padding * 2;

    public void Toggle()
    {
        Visible = !Visible;

        if (Visible)
        {
            openedVanillaInventory = !Main.playerInventory;
            Main.playerInventory = true;
            UpdateLayout();
            AITeammateSystem.Instance.TestLog("inventory ui opened");
        }
        else
        {
            if (openedVanillaInventory)
                Main.playerInventory = false;
            openedVanillaInventory = false;
            hoveredSlot = -1;
            AITeammateSystem.Instance.TestLog("inventory ui closed");
        }
    }

    public void Close()
    {
        if (Visible)
            Toggle();
    }

    public void Update()
    {
        if (!Visible)
            return;

        UpdateLayout();

        Point mouse = new Point(Main.mouseX, Main.mouseY);
        hoveredSlot = -1;

        for (int i = 0; i < slotRects.Length; i++)
        {
            if (!slotRects[i].Contains(mouse))
                continue;

            hoveredSlot = i;
            break;
        }

        if (panelRect.Contains(mouse))
            Main.LocalPlayer.mouseInterface = true;

        bool clicked = Main.mouseLeft && !lastMouseLeft;
        lastMouseLeft = Main.mouseLeft;

        if (!clicked)
            return;

        if (closeRect.Contains(mouse))
        {
            Toggle();
            return;
        }

        if (depositRect.Contains(mouse))
        {
            DepositCursorItem();
            return;
        }

        if (hoveredSlot >= 0)
            ClickSlot(hoveredSlot);
    }

    public void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible)
            return;

        UpdateLayout();

        Texture2D pixel = TextureAssets.MagicPixel.Value;
        spriteBatch.Draw(pixel, panelRect, new Color(18, 24, 40) * 0.96f);
        DrawBorder(spriteBatch, panelRect, new Color(90, 140, 210) * 0.9f, 2);

        Utils.DrawBorderString(spriteBatch, CompanionControlsUI.Text("InventoryTitle", AITeammateSystem.CompanionDisplayName),
            new Vector2(panelRect.X + Padding, panelRect.Y + 5), Color.White, 0.95f);
        Utils.DrawBorderString(spriteBatch, "×", new Vector2(closeRect.X + 5, closeRect.Y - 2), Color.IndianRed, 1f);

        string[] equipmentLabels = { "头", "身", "腿", "饰", "饰", "饰" };
        for (int i = 0; i < 6; i++)
        {
            Rectangle rect = slotRects[CompanionLoadout.InventorySize + i];
            Item item = GetSlot(CompanionLoadout.InventorySize + i);
            DrawSlot(spriteBatch, rect, item, i == hoveredSlot);
            Utils.DrawBorderString(spriteBatch, equipmentLabels[i], new Vector2(rect.X + 2, rect.Y + 1), Color.LightGray, 0.6f);
        }

        for (int i = 0; i < CompanionLoadout.InventorySize; i++)
            DrawSlot(spriteBatch, slotRects[i], GetSlot(i), i == hoveredSlot);

        int statsY = panelRect.Bottom - Padding - DepositHeight - StatsHeight + 8;
        Utils.DrawBorderString(spriteBatch, $"防御 {Loadout.Defense}  ·  武器 {Loadout.WeaponDamage}  ·  法力 {Loadout.Mana}/{Loadout.ManaMax}",
            new Vector2(panelRect.X + Padding, statsY), new Color(180, 230, 255), 0.72f);

        Color depositColor = Main.mouseItem.IsAir ? Color.Gray : Color.LightGreen;
        Utils.DrawBorderString(spriteBatch, "存入手持物品", new Vector2(depositRect.X + 4, depositRect.Y + 2), depositColor, 0.75f);
        DrawBorder(spriteBatch, depositRect, depositColor * 0.7f, 1);
    }

    private void UpdateLayout()
    {
        int width = PanelWidth;
        int height = PanelHeight;
        int x = Math.Max(4, Main.screenWidth - width - 12);
        int y = Math.Max(4, Math.Min(80, Main.screenHeight - height - 4));

        panelRect = new Rectangle(x, y, width, height);
        closeRect = new Rectangle(x + width - 28, y + 4, 22, 22);

        int equipY = y + TitleHeight;
        for (int i = 0; i < 6; i++)
        {
            slotRects[CompanionLoadout.InventorySize + i] = new Rectangle(
                x + Padding + i * SlotSize, equipY, SlotSize - 2, SlotSize - 2);
        }

        int inventoryY = equipY + EquipHeight;
        for (int i = 0; i < CompanionLoadout.InventorySize; i++)
        {
            int column = i % Columns;
            int row = i / Columns;
            slotRects[i] = new Rectangle(
                x + Padding + column * SlotSize,
                inventoryY + row * SlotSize,
                SlotSize - 2,
                SlotSize - 2);
        }

        depositRect = new Rectangle(x + Padding, y + height - Padding - DepositHeight + 2, 142, 24);
    }

    private void DrawSlot(SpriteBatch spriteBatch, Rectangle rect, Item item, bool hovered)
    {
        spriteBatch.Draw(TextureAssets.InventoryBack9.Value, rect, Color.White);
        DrawBorder(spriteBatch, rect, hovered ? Color.Gold : new Color(90, 110, 150) * 0.6f, hovered ? 2 : 1);

        if (item.IsAir)
            return;

        Texture2D texture = TextureAssets.Item[item.type].Value;
        float scale = Math.Min(1f, (SlotSize - 10f) / Math.Max(texture.Width, texture.Height));
        Vector2 center = new Vector2(rect.Center.X, rect.Center.Y);
        spriteBatch.Draw(texture, center, null, Color.White, 0f,
            new Vector2(texture.Width / 2f, texture.Height / 2f), scale, SpriteEffects.None, 0f);

        if (item.stack > 1)
        {
            Utils.DrawBorderString(spriteBatch, item.stack.ToString(),
                new Vector2(rect.Right - 13, rect.Bottom - 16), Color.White, 0.7f);
        }
    }

    private static void DrawBorder(SpriteBatch spriteBatch, Rectangle rect, Color color, int thickness)
    {
        Texture2D pixel = TextureAssets.MagicPixel.Value;
        spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
        spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
        spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
        spriteBatch.Draw(pixel, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
    }

    private void ClickSlot(int index)
    {
        Item slot = GetSlot(index);

        if (Main.mouseItem.IsAir)
        {
            if (slot.IsAir)
                return;

            Main.mouseItem = slot;
            SetSlot(index, new Item());
            SoundEngine.PlaySound(SoundID.Grab);
            return;
        }

        if (!CanPlace(index, Main.mouseItem))
            return;

        Item previous = slot;
        SetSlot(index, Main.mouseItem);
        Main.mouseItem = previous;
        SoundEngine.PlaySound(SoundID.Grab);
        AITeammateSystem.Instance.TestLog($"equipment changed: {Loadout.Summary()} | defense={Loadout.Defense}");
    }

    private void DepositCursorItem()
    {
        if (Main.mouseItem.IsAir)
            return;

        if (!Loadout.AddItem(Main.mouseItem.type, Main.mouseItem.stack))
        {
            Main.NewText(AITeammateSystem.CompanionSpeech("背包满了。"), Color.OrangeRed);
            return;
        }

        Main.mouseItem.SetDefaults(ItemID.None);
        SoundEngine.PlaySound(SoundID.Grab);
    }

    private static bool CanPlace(int index, Item item)
    {
        if (index < CompanionLoadout.InventorySize)
            return true;

        int equipmentIndex = index - CompanionLoadout.InventorySize;
        if (equipmentIndex < 3)
            return CompanionLoadout.CanEquipArmor(item, equipmentIndex);

        return CompanionLoadout.CanEquipAccessory(item);
    }

    private static Item GetSlot(int index)
    {
        if (index < CompanionLoadout.InventorySize)
            return Loadout.Inventory[index];

        int equipmentIndex = index - CompanionLoadout.InventorySize;
        return equipmentIndex < 3 ? Loadout.Armor[equipmentIndex] : Loadout.Accessories[equipmentIndex - 3];
    }

    private static void SetSlot(int index, Item item)
    {
        if (index < CompanionLoadout.InventorySize)
        {
            Loadout.Inventory[index] = item;
            return;
        }

        int equipmentIndex = index - CompanionLoadout.InventorySize;
        if (equipmentIndex < 3)
            Loadout.Armor[equipmentIndex] = item;
        else
            Loadout.Accessories[equipmentIndex - 3] = item;
    }
}
