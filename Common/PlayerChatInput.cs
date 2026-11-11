using System;

namespace TerrariaAITeammate.Common;

public enum CompanionChatMode
{
    Addressed,
    CommandsOnly,
    All
}

internal static class PlayerChatInput
{
    private static readonly string[] Addresses = { "@小星", "＠小星", "@Xiaoxing", "＠Xiaoxing", "小星" };
    private const int MaxMessageLength = 300;

    public static bool IsPlayerMessage(string? message)
    {
        string text = message?.Trim() ?? "";
        return text.Length is > 0 and <= MaxMessageLength && text[0] != '/';
    }

    // Null leaves the line in Terraria chat. An empty addressed line opens the help hint.
    public static string? Route(string? message, CompanionChatMode mode, bool explicitResume = false)
    {
        string text = message?.Trim() ?? "";
        if (text.Length == 0 || text[0] == '/' || mode == CompanionChatMode.CommandsOnly)
            return null;

        if (text == "小星来") return "过来";

        foreach (string address in Addresses)
        {
            if (!text.StartsWith(address, StringComparison.OrdinalIgnoreCase))
                continue;
            if (text.Length == address.Length) return "";
            if (!IsAddressSeparator(text[address.Length])) continue;

            int start = address.Length;
            while (start < text.Length && IsAddressSeparator(text[start])) start++;
            return text[start..];
        }

        return IsPlayerMessage(text) && (mode == CompanionChatMode.All ||
            explicitResume || BasicCompanionCommand.TryParseChat(text, out _)) ? text : null;
    }

    private static bool IsAddressSeparator(char value)
        => char.IsWhiteSpace(value) || value is ':' or '：' or ',' or '，' or '、';

    public static string CommandBody(string input, int prefixWords = 2)
    {
        string[] parts = input.Split((char[]?)null, prefixWords + 1, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > prefixWords ? parts[prefixWords].Trim() : "";
    }
}
