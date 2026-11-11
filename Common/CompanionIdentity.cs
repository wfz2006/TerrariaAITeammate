using System.Globalization;

namespace TerrariaAITeammate.Common;

internal static class CompanionIdentity
{
    public const string DefaultName = "小鲸鱼";

    public static string NormalizeName(string? value)
    {
        string name = value?.Trim() ?? "";
        if (name.Length is 0 or > 16 || name[0] is '/' or '@' or '＠')
            return DefaultName;

        foreach (char character in name)
        {
            if (char.IsControl(character) || character is '[' or ']' ||
                char.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return DefaultName;
        }

        return name;
    }
}
