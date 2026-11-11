using System;
using System.Linq;

namespace TerrariaAITeammate.Common;

/// <summary>Explicit chat commands only; ordinary conversation remains the LLM's responsibility.</summary>
public static class BasicCompanionCommand
{
    // Only a complete standalone shortcut is unambiguous in ordinary chat.
    // Arguments belong to the explicit /ai command grammar; natural sentences,
    // including conditions and subsequent actions, belong to the planner.
    public static bool TryParseChat(string message, out AIPlan? plan)
    {
        plan = null;
        // A standalone stop must not wait for the planner just because the
        // player ended it with a full stop or exclamation. Questions, quotes,
        // negations and compound instructions still use the ordinary path.
        string text = message.Trim();
        string stopWord = text.TrimEnd('.', '!', '。', '！');
        if (stopWord.Length < text.Length &&
            TryParse(new[] { stopWord }, out AIPlan? stop, out string stopError) &&
            stopError.Length == 0 && stop?.Action == AIActionType.Wait)
        {
            plan = stop;
            return true;
        }
        string[] words = message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 1 || !TryParse(words, out AIPlan? parsed, out string error) || error.Length != 0)
            return false;
        plan = parsed;
        return plan != null;
    }

    public static bool TryParse(string[] args, out AIPlan? plan, out string error)
    {
        plan = null; error = "";
        if (args.Length == 0) return false;
        string command = args[0].ToLowerInvariant();
        AIActionType action = command switch {
            "follow" or "跟随" or "跟着我" => AIActionType.Follow,
            "come" or "过来" => AIActionType.Come,
            "wait" or "stop" or "等待" or "别动" or "停" or "停止" or "停下" => AIActionType.Wait,
            "attack" or "攻击" => AIActionType.Attack,
            "pickup" or "拾取" or "捡东西" => AIActionType.Pickup,
            "equip" or "装备" => AIActionType.Equip,
            _ => AIActionType.None
        };
        if (action == AIActionType.None) return false;
        if (action is AIActionType.Follow or AIActionType.Come or AIActionType.Wait)
        {
            if (args.Length != 1) { error = $"用法：/ai {command}"; return true; }
            plan = new() { Action = action }; return true;
        }
        if (action == AIActionType.Attack)
        {
            plan = new() { Action = action, Target = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "nearest" }; return true;
        }
        string item = string.Join(" ", args.Skip(1));
        int count = 0;
        if (action == AIActionType.Pickup && args.Length > 2 && int.TryParse(args[^1], out int parsed))
        {
            if (parsed < 1 || parsed > 999) { error = "拾取数量须为1到999；不填数量则尽量收集匹配物品。"; return true; }
            count = parsed; item = string.Join(" ", args.Skip(1).Take(args.Length - 2));
        }
        if (action == AIActionType.Equip && string.IsNullOrWhiteSpace(item))
        { error = "用法：/ai equip <背包中的物品名称或ID>"; return true; }
        plan = new() { Action = action, Item = item, Count = count }; return true;
    }
}
