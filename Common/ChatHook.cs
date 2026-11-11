using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.Chat.Commands;
using Terraria.ID;
using Terraria.UI.Chat;

namespace TerrariaAITeammate.Common;

internal static class ChatHook
{
    private static readonly ChatCommandId Say = ChatCommandId.FromType<SayChatCommand>();

    public static void Load() => On_ChatCommandProcessor.ProcessIncomingMessage += OnSubmittedChat;

    public static void Unload() => On_ChatCommandProcessor.ProcessIncomingMessage -= OnSubmittedChat;

    private static void OnSubmittedChat(On_ChatCommandProcessor.orig_ProcessIncomingMessage original,
        ChatCommandProcessor processor, ChatMessage message, int clientId)
    {
        if (Main.netMode == NetmodeID.SinglePlayer && clientId == Main.myPlayer &&
            ReferenceEquals(processor, ChatManager.Commands) && !message.IsConsumed && message.CommandId.Equals(Say))
        {
            var system = AITeammateSystem.Instance;
            try
            {
                if (system.HandleChatMessage(message.Text))
                {
                    message.Consume();
                    return;
                }
            }
            catch (Exception error)
            {
                system.RecordTestChatFailure();
                system.Mod.Logger.Warn($"Companion chat dispatch failed ({error.GetType().Name}).");
                Main.NewText(CompanionControlsUI.Text("ChatDispatchError"), Color.OrangeRed);
            }
        }

        original(processor, message, clientId);
    }
}
