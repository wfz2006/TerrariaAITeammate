using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public class AICommand : ModCommand
{
    public override CommandType Type => CommandType.Chat;
    public override string Command => "ai";
    public override bool IsCaseSensitive => true;
    public override string Usage => CompanionControlsUI.Text("CommandUsage");
    public override string Description => "召唤/指挥 AI 队友";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            caller.Reply("第一版只支持单机。", Color.OrangeRed);
            return;
        }

        args = args.Where(arg => !string.IsNullOrWhiteSpace(arg)).ToArray();
        if (args.Length == 0)
        {
            caller.Reply(Usage, Color.Yellow);
            return;
        }

        AITeammateSystem system = AITeammateSystem.Instance;

        if (BasicCompanionCommand.TryParse(args, out AIPlan? basic, out string commandError))
        {
            if (commandError.Length > 0) caller.Reply(commandError, Color.Yellow);
            else system.ExecuteBasicCommand(caller.Player, basic!);
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "help":
                caller.Reply(Usage, Color.LightBlue);
                break;
            case "panel":
                system.OpenCompanionPanel();
                break;
            case "settings":
                system.OpenCompanionSettings();
                break;
            case "diagnostics":
                system.OpenTestDiagnostics();
                break;
            case "bug":
                system.AddTestBug(PlayerChatInput.CommandBody(input));
                break;
            case "say":
                system.SendCompanionChat(caller.Player, PlayerChatInput.CommandBody(input));
                break;
            case "spawn":
                system.SpawnCompanion(caller.Player);
                break;
            case "auto":
                if (args.Length > 1 && args[1].Equals("on", StringComparison.OrdinalIgnoreCase))
                    system.SetAutoMode(true);
                else if (args.Length > 1 && args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                    system.SetAutoMode(false);
                else
                    caller.Reply($"自主模式：{(system.AutoMode ? "开" : "关")}", Color.LightBlue);
                break;
            case "memory":
                caller.Reply(system.MemoryText(), Color.LightBlue);
                break;
            case "inventory":
                system.ToggleInventory();
                caller.Reply(CompanionControlsUI.Text("InventorySwitched", AITeammateSystem.CompanionDisplayName), Color.LightBlue);
                break;
            case "tasks":
                caller.Reply(system.TaskText(), Color.LightBlue);
                break;
            case "scan":
                caller.Reply(AIPerception.Summary(caller.Player, system.CompanionNpc), Color.LightBlue);
                break;
            case "status":
                caller.Reply(system.StatusText(), Color.LightBlue);
                break;
            case "give":
                system.GiveInventory(caller.Player);
                break;
            case "kit":
                system.DebugKit(caller.Player);
                break;
            case "wand":
                system.GiveBlueprintWand(caller.Player);
                break;
            case "blueprint":
                HandleBlueprintCommand(caller, system, args);
                break;
            case "build":
                system.ApplyLocalTask(new AIPlan
                {
                    Action = AIActionType.Build,
                    Blueprint = args.Length > 1 ? args[1] : "shelter"
                });
                break;
            case "craft":
                if (args.Length < 2)
                {
                    caller.Reply("用法：/ai craft <物品> [数量]", Color.Yellow);
                    break;
                }
                system.ApplyLocalTask(new AIPlan
                {
                    Action = AIActionType.Craft,
                    Item = args[1],
                    Count = args.Length > 2 && int.TryParse(args[2], out int craftCount) ? craftCount : 1
                });
                break;
            case "deposit":
                system.ApplyLocalTask(new AIPlan
                {
                    Action = AIActionType.Deposit,
                    Item = args.Length > 1 ? args[1] : "",
                    Count = args.Length > 2 && int.TryParse(args[2], out int depositCount) ? depositCount : 0
                });
                break;
            case "withdraw":
                if (args.Length < 2)
                {
                    caller.Reply("用法：/ai withdraw <物品> [数量]", Color.Yellow);
                    break;
                }
                system.ApplyLocalTask(new AIPlan
                {
                    Action = AIActionType.Withdraw,
                    Item = args[1],
                    Count = args.Length > 2 && int.TryParse(args[2], out int withdrawCount) ? withdrawCount : 0
                });
                break;
            case "slime":
                system.DebugSpawnEnemy(caller.Player);
                break;
            case "ore":
                system.DebugOre(caller.Player);
                break;
            default:
                system.SendCompanionChat(caller.Player, PlayerChatInput.CommandBody(input, prefixWords: 1));
                break;
        }
    }

    private static void HandleBlueprintCommand(CommandCaller caller, AITeammateSystem system, string[] args)
    {
        if (args.Length < 2)
        {
            caller.Reply("/ai blueprint save <名字> | list | delete <名字> | clear", Color.Yellow);
            return;
        }

        switch (args[1].ToLowerInvariant())
        {
            case "save":
                if (args.Length < 3)
                {
                    caller.Reply("用法：/ai blueprint save <名字>", Color.Yellow);
                    break;
                }

                if (BlueprintManager.SaveSelection(args[2], out string saved))
                    caller.Reply(saved, Color.LightGreen);
                else
                    caller.Reply(saved, Color.OrangeRed);
                break;

            case "list":
                var names = BlueprintManager.ListNames();
                caller.Reply(names.Count == 0 ? "还没有保存的蓝图。" : "蓝图：" + string.Join("、", names), Color.LightBlue);
                break;

            case "delete":
                if (args.Length < 3)
                {
                    caller.Reply("用法：/ai blueprint delete <名字>", Color.Yellow);
                    break;
                }

                if (BlueprintManager.Delete(args[2], out string deleted))
                    caller.Reply(deleted, Color.LightGreen);
                else
                    caller.Reply(deleted, Color.OrangeRed);
                break;

            case "clear":
                BlueprintManager.ClearSelection();
                caller.Reply("蓝图选区已清空。", Color.LightBlue);
                break;

            default:
                caller.Reply("/ai blueprint save <名字> | list | delete <名字> | clear", Color.Yellow);
                break;
        }
    }
}
