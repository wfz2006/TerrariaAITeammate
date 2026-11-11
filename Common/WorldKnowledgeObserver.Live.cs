using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;

namespace TerrariaAITeammate.Common;

public static partial class WorldKnowledgeObserver
{
    private sealed record LiveEntry(double Distance, string Identity, Func<object> Data);
    private static double Distance(int x, int y, int ox, int oy)
        => Math.Sqrt((x - ox) * (double)(x - ox) + (y - oy) * (double)(y - oy));

    private static object LivePage(IEnumerable<LiveEntry> source, WorldKnowledgeRequest request, string coverage)
    {
        LiveEntry[] entries = source.Where(e => request.MatchesDistance(e.Distance))
            .OrderBy(e => e.Distance).ThenBy(e => e.Identity, StringComparer.Ordinal).ToArray();
        LiveEntry[] page = entries.Skip(request.Offset).Take(request.Limit).ToArray();
        int next = request.Offset + page.Length;
        return new { entries = page.Select(e => e.Data()).ToArray(), totalMatches = entries.Length,
            offset = request.Offset, limit = request.Limit, truncated = next < entries.Length,
            nextOffset = next < entries.Length && next <= 50000 ? next : -1,
            distanceFilter = new { minDistance = request.MinDistance, maxDistance = request.MaxDistance,
                appliedBeforePagination = true, inclusive = true, units = "tiles",
                meaning = "straight_line_between_reported_tile_coordinates_and_query_origin; chest_anchor_or_item_npc_center_floored_to_tile; not_route_length" },
            coverage, observedAtTick = Main.GameUpdateCount, freshness = "live_query_snapshot" };
    }

    private static object ChestDescription(Chest chest, int index, int ox, int oy, bool detailed, int? matchingItem = null)
    {
        Item[] items = chest.item.Where(i => !i.IsAir && (!matchingItem.HasValue || i.type == matchingItem)).ToArray();
        int limit = detailed ? 40 : 6;
        return new
        {
            kind = "chest", id = $"chest:{chest.x}:{chest.y}", index, x = chest.x, y = chest.y,
            name = chest.name, locked = Chest.IsLocked(chest.x, chest.y),
            estimatedDistanceTiles = Math.Round(Distance(chest.x, chest.y, ox, oy), 2),
            matchingItemId = matchingItem, matchingItemCount = matchingItem.HasValue ? items.Sum(i => i.stack) : (int?)null,
            occupiedSlots = items.Length,
            items = items.Take(limit).Select(i => new { id = i.type, name = Lang.GetItemNameValue(i.type), count = i.stack }).ToArray(),
            contentsTruncated = items.Length > limit,
            transfer = "observation_only; use_real_withdraw_near_target; locked_chests_not_unlocked; revalidate_on_execution"
        };
    }

    private static IEnumerable<LiveEntry> ChestEntries(int ox, int oy, int? item = null)
    {
        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < Main.chest.Length; i++)
        {
            Chest? chest = Main.chest[i];
            if (chest == null || !CompanionChests.IsChestBuilt(chest) || !seen.Add((chest.x, chest.y)) ||
                item.HasValue && !chest.item.Any(v => !v.IsAir && v.type == item)) continue;
            int index = i;
            yield return new LiveEntry(Distance(chest.x, chest.y, ox, oy), $"chest:{chest.x}:{chest.y}",
                () => ChestDescription(chest, index, ox, oy, false, item));
        }
    }

    private static IEnumerable<LiveEntry> GroundEntries(int ox, int oy, int? type = null)
    {
        for (int i = 0; i < Main.item.Length; i++)
        {
            Item item = Main.item[i];
            if (!item.active || item.IsAir || type.HasValue && item.type != type) continue;
            int x = (int)(item.Center.X / 16), y = (int)(item.Center.Y / 16);
            int index = i;
            yield return new LiveEntry(Distance(x, y, ox, oy), $"ground:{index}", () => new
            {
                kind = "ground_item", index, itemId = item.type, name = Lang.GetItemNameValue(item.type), count = item.stack,
                x, y, estimatedDistanceTiles = Math.Round(Distance(x, y, ox, oy), 2),
                pickup = "not_tested; item_may_move_or_be_collected_before_arrival"
            });
        }
    }

    private static IEnumerable<LiveEntry> NpcEntries(int type, int ox, int oy)
    {
        for (int i = 0; i < Main.npc.Length; i++)
        {
            NPC npc = Main.npc[i];
            if (!npc.active || npc.type != type) continue;
            int x = (int)(npc.Center.X / 16), y = (int)(npc.Center.Y / 16);
            int index = i;
            yield return new LiveEntry(Distance(x, y, ox, oy), $"npc:{index}", () => new
            {
                kind = "active_npc", index, npcType = type, name = npc.FullName, x, y,
                npc.boss, npc.friendly, npc.life, npc.lifeMax,
                estimatedDistanceTiles = Math.Round(Distance(x, y, ox, oy), 2),
                combatOrInteraction = "not_tested; position_can_change; not_spawn_or_kill_evidence"
            });
        }
    }
}
