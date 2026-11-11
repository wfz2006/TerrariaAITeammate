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
    private const int MaxMessageLength = 300;

    public static bool IsPlayerMessage(string? message)
    {
        string text = message?.Trim() ?? "";
        return text.Length is > 0 and <= MaxMessageLength && text[0] != '/';
    }

    // Null leaves the line in Terraria chat. An empty addressed line opens the help hint.
    public static string? Route(string? message, CompanionChatMode mode, bool explicitResume = false,
        string? companionName = null)
    {
        string text = message?.Trim() ?? "";
        if (text.Length == 0 || text[0] == '/' || mode == CompanionChatMode.CommandsOnly)
            return null;

        int offset = text[0] is '@' or '＠' ? 1 : 0;
        string name = CompanionIdentity.NormalizeName(companionName);
        string? body = ReadAddress(text, offset, name) ?? ReadAddress(text, offset, "小星");
        if (body == null && offset != 0)
            body = ReadAddress(text, offset, "Xiaoxing");
        if (body == "" && IsPlayerMessage(text) && BasicCompanionCommand.TryParseChat(text, out _))
            return text;
        if (body != null)
            return body;

        return IsPlayerMessage(text) && (mode == CompanionChatMode.All ||
            explicitResume || BasicCompanionCommand.TryParseChat(text, out _)) ? text : null;
    }

    private static string? ReadAddress(string text, int offset, string name)
    {
        if (!text.AsSpan(offset).StartsWith(name, StringComparison.OrdinalIgnoreCase))
            return null;

        int start = offset + name.Length;
        if (start == text.Length) return "";
        if (offset == 0 && text.AsSpan(start).SequenceEqual("来")) return "过来";
        if (!IsAddressSeparator(text[start])) return null;
        while (start < text.Length && IsAddressSeparator(text[start])) start++;
        return text[start..];
    }

    private static bool IsAddressSeparator(char value)
        => char.IsWhiteSpace(value) || value is ':' or '：' or ',' or '，' or '、';

    public static string CommandBody(string input, int prefixWords = 2)
    {
        string[] parts = input.Split((char[]?)null, prefixWords + 1, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > prefixWords ? parts[prefixWords].Trim() : "";
    }
}
