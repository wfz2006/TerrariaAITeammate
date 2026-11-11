using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;

namespace TerrariaAITeammate.Common;

internal sealed class CompanionControlsUI(AITeammateSystem system, bool inventoryWasOpen) : UIState
{
    internal static readonly Color PanelColor = new(21, 28, 52);
    internal static readonly Color BorderColor = new(79, 112, 175);
    internal static readonly Color MutedText = new(170, 196, 228);
    internal static readonly Color ButtonColor = new(45, 62, 102);
    private static readonly Color StopColor = new(112, 53, 67);
    private UIText status = null!;

    internal static string Text(string key, params object[] args)
        => Language.GetTextValue("Mods.TerrariaAITeammate.Controls." + key, args);

    public override void OnInitialize()
    {
        var panel = new UIPanel { BackgroundColor = PanelColor, BorderColor = BorderColor, HAlign = 0.5f, VAlign = 0.46f };
        panel.Width.Set(-32f, 1f);
        panel.MaxWidth.Set(470f, 0f);
        panel.Height.Set(285f, 0f);
        panel.SetPadding(14f);
        Append(panel);

        AddLabel(panel, Text("Title"), 1.2f, 0f, Color.White);
        var close = CreateButton("Close", ButtonColor, IngameFancyUI.Close);
        close.HAlign = 1f;
        close.Width.Set(32f, 0f);
        close.Height.Set(30f, 0f);
        panel.Append(close);

        status = AddLabel(panel, "", 0.85f, 36f, MutedText);
        AddButton(panel, "Inventory", 0, 65f, ButtonColor, OpenInventory, columns: 2);
        AddButton(panel, "Settings", 1, 65f, ButtonColor,
            () => system.OpenCompanionSettings(() => system.OpenCompanionPanel(inventoryWasOpen)), columns: 2);
        AddLabel(panel, Text("BasicCommands"), 0.85f, 110f, MutedText);
        AddButton(panel, "Summon", 0, 134f, ButtonColor, Summon);
        AddButton(panel, "Follow", 1, 134f, ButtonColor, () => Run(AIActionType.Follow));
        AddButton(panel, "Come", 2, 134f, ButtonColor, () => Run(AIActionType.Come));
        AddButton(panel, "Attack", 0, 177f, ButtonColor, () => Run(AIActionType.Attack, "nearest"));
        AddButton(panel, "Pickup", 1, 177f, ButtonColor, () => Run(AIActionType.Pickup));
        AddButton(panel, "Stop", 2, 177f, StopColor, () => Run(AIActionType.Wait));
        AddButton(panel, "Diagnostics", 0, 220f, ButtonColor,
            () => system.OpenTestDiagnostics(inventoryWasOpen), columns: 1);
    }

    public override void OnActivate()
    {
        string companion = Text(system.CompanionNpc == null ? "NotSummoned" : "Present");
        status.SetText(Text("PanelStatus", companion, system.ModelChatStatus));
    }

    public override void OnDeactivate()
    {
        if (!Main.inFancyUI) Main.playerInventory = inventoryWasOpen;
    }

    internal static UIText AddLabel(UIPanel panel, string text, float scale, float top, Color color)
    {
        var label = new UIText(text, scale) { TextColor = color };
        label.Top.Set(top, 0f);
        panel.Append(label);
        return label;
    }

    internal static UITextPanel<string> AddButton(UIPanel panel, string key, int column, float top, Color color,
        Action action, int columns = 3)
    {
        const float gap = 9f;
        var button = CreateButton(key, color, action);
        button.Width.Set(-gap * (columns - 1) / columns, 1f / columns);
        button.Height.Set(36f, 0f);
        button.Left.Set(column * gap / columns, (float)column / columns);
        button.Top.Set(top, 0f);
        panel.Append(button);
        return button;
    }

    internal static UITextPanel<string> CreateButton(string key, Color color, Action action)
    {
        var button = new UITextPanel<string>(Text(key), 0.9f) { BackgroundColor = color, BorderColor = BorderColor };
        button.SetPadding(0f);
        button.OnMouseOver += (_, _) => button.BackgroundColor = Color.Lerp(color, Color.White, 0.16f);
        button.OnMouseOut += (_, _) => button.BackgroundColor = color;
        button.OnLeftClick += (_, _) =>
        {
            SoundEngine.PlaySound(SoundID.MenuTick);
            action();
        };
        return button;
    }

    private void OpenInventory()
    {
        IngameFancyUI.Close();
        system.ToggleInventory();
    }

    private void Summon()
    {
        IngameFancyUI.Close();
        if (system.CompanionNpc == null) system.SpawnCompanion(Main.LocalPlayer);
        else Main.NewText(Text("AlreadyPresent"), Color.LightBlue);
    }

    private void Run(AIActionType action, string target = "")
    {
        IngameFancyUI.Close();
        system.ExecuteBasicCommand(Main.LocalPlayer, new AIPlan { Action = action, Target = target });
    }
}

internal sealed class CompanionLauncherUI(AITeammateSystem system) : UIState
{
    public override void OnInitialize()
    {
        var launcher = CompanionControlsUI.CreateButton("Title", new Color(39, 57, 96), system.OpenCompanionPanel);
        launcher.HAlign = launcher.VAlign = 1f;
        launcher.Left.Set(-16f, 0f);
        launcher.Top.Set(-24f, 0f);
        launcher.Width.Set(98f, 0f);
        launcher.Height.Set(32f, 0f);
        Append(launcher);
    }
}

public sealed class CompanionControlsPlayer : ModPlayer
{
    public override void OnEnterWorld()
    {
        if (Main.netMode != NetmodeID.SinglePlayer || Player.whoAmI != Main.myPlayer ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST")))
            return;

        var keys = AITeammateSystem.PanelKeybind?.GetAssignedKeys(InputMode.Keyboard);
        string tip = keys is { Count: > 0 }
            ? CompanionControlsUI.Text("JoinTip", string.Join(" / ", keys))
            : CompanionControlsUI.Text("JoinTipUnbound");
        Main.NewText(tip, new Color(174, 218, 252));
    }

    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        if (Player.whoAmI == Main.myPlayer && AITeammateSystem.PanelKeybind?.JustPressed == true &&
            !Main.drawingPlayerChat && !Main.editSign && !Main.editChest && !Main.ingameOptionsWindow && !Main.inFancyUI)
            AITeammateSystem.Instance.OpenCompanionPanel();
    }
}
