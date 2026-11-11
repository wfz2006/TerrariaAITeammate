using System;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

/// <summary>Bounds only the game's chat rendering; callers retain the original execution payload.</summary>
public static class ActionFeedbackDisplay
{
    public const int MaximumLength = 220;

    public static string Summarize(string text, bool isQuery)
    {
        string trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            // Structure queries can exceed 100 KB. Do not render or even parse them for chat.
            if (isQuery) return "已读取真实查询结果，正在根据观察规划。";
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String)
                {
                    bool blocked = root.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.False ||
                        root.TryGetProperty("kind", out JsonElement kind) && kind.ValueKind == JsonValueKind.String &&
                        (kind.GetString()?.EndsWith("blocked", StringComparison.Ordinal) ?? false);
                    return Limit((blocked ? "执行受阻：" : "执行反馈：") + reason.GetString());
                }
            }
            catch (JsonException)
            {
                // A display failure must not interrupt delivery of the original tool result.
            }
            return "已记录执行结果，正在规划下一步。";
        }
        return Limit(text);
    }

    private static string Limit(string text)
    {
        if (text.Length <= MaximumLength) return text;
        int end = MaximumLength - 1;
        if (char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "…";
    }
}
