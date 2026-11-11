using System;

namespace TerrariaAITeammate.Common;

public static class BossRules
{
    // IDs and restrictions verified against the installed vanilla item-use implementation.
    public static int BossTypeForItem(int item) => item switch { 560 => 50, 43 => 4, _ => 0 };
    public static bool CanSummon(int item, bool isNight, bool sameBossActive)
        => BossTypeForItem(item) != 0 && !sameBossActive && (item != 43 || isNight);

    public static int ResolveBoss(string query)
    {
        string value = Normalize(query);
        return value switch
        {
            "50" or "560" or "slimecrown" or "史莱姆王冠" => 50,
            "4" or "43" or "suspiciouslookingeye" or "可疑眼球" => 4,
            "262" => 262,
            _ => ResolveName(value)
        };
    }

    internal static int ResolveNpcName(string query) => ResolveName(Normalize(query));

    private static string Normalize(string query) => (query ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");

    private static int ResolveName(string value) => value switch
    {
        "kingslime" or "slimeking" or "史莱姆王" => 50,
        "eyeofcthulhu" or "eye" or "克苏鲁之眼" or "克眼" => 4,
        "plantera" or "世纪之花" => 262,
        _ => 0
    };
}
