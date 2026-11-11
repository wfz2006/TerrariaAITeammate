using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    internal static ModKeybind? PanelKeybind { get; private set; }
    private UserInterface? companionLauncher;
    private GameTime? launcherTime;

    internal bool CanUseCompanionControls => worldReady && !Main.gameMenu && Main.netMode == NetmodeID.SinglePlayer;
    internal static string CompanionDisplayName => CompanionIdentity.NormalizeName(ModContent.GetInstance<AIConfig>().CompanionName);
    internal static string CompanionSpeech(string message) => $"{CompanionDisplayName}：{message}";

    internal void RefreshCompanionName(string name)
    {
        if (worldReady && CompanionNpc is { } companion)
            companion.GivenName = name;
        companionLauncher = null;
    }

    internal string ModelChatStatus => CompanionControlsUI.Text(!ModContent.GetInstance<AIConfig>().UseLlm
        ? "ModelOff" : GetLlmSettings() == null ? "ModelUnconfigured" : "ModelConfigured");

    private bool ShowCompanionLauncher => CanUseCompanionControls && !Main.hideUI && !Main.mapFullscreen &&
        !Main.playerInventory && !Main.ingameOptionsWindow && !Main.inFancyUI && !Main.drawingPlayerChat;

    private void UpdateCompanionLauncher(GameTime gameTime)
    {
        if (!ShowCompanionLauncher) return;
        if (companionLauncher == null)
        {
            companionLauncher = new UserInterface();
            companionLauncher.SetState(new CompanionLauncherUI(this));
        }
        launcherTime = gameTime;
        companionLauncher.Update(gameTime);
        if (companionLauncher.IsElementUnderMouse()) Main.LocalPlayer.mouseInterface = true;
    }

    private void AddCompanionLauncherLayer(List<GameInterfaceLayer> layers)
    {
        int mouseTextLayer = layers.FindIndex(layer => layer.Name == "Vanilla: Mouse Text");
        if (mouseTextLayer < 0) return;
        layers.Insert(mouseTextLayer, new LegacyGameInterfaceLayer("TerrariaAITeammate: Companion Launcher",
            () =>
            {
                if (ShowCompanionLauncher && launcherTime != null)
                    companionLauncher?.Draw(Main.spriteBatch, launcherTime);
                return true;
            },
            InterfaceScaleType.UI));
    }

    internal void OpenCompanionPanel() => OpenCompanionPanel(null);

    internal void OpenCompanionPanel(bool? inventoryOnClose)
    {
        if (!CanUseCompanionControls) return;
        InventoryUI.Close();
        IngameFancyUI.OpenUIState(new CompanionControlsUI(this, inventoryOnClose ?? Main.playerInventory));
    }

    internal void OpenTestDiagnostics(bool? inventoryOnClose = null)
    {
        if (!CanUseCompanionControls) return;
        InventoryUI.Close();
        IngameFancyUI.OpenUIState(new CompanionTestDiagnosticsUI(this, inventoryOnClose ?? Main.playerInventory));
    }

    private void CloseCompanionPanel()
    {
        if (Main.InGameUI.CurrentState is CompanionControlsUI or CompanionTestDiagnosticsUI)
            IngameFancyUI.Close();
        companionLauncher = null;
        launcherTime = null;
    }

    internal void OpenCompanionSettings(Action? onClose = null)
    {
        if (!CanUseCompanionControls) return;
        InventoryUI.Close();
        bool inventoryWasOpen = Main.playerInventory;
        ModContent.GetInstance<AIConfig>().Open(onClose: onClose ?? (() => Main.playerInventory = inventoryWasOpen));
    }
}
