using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public static class BossKnowledge
{
    public static object Progress() => new
    {
        hardmode = Main.hardMode, kingSlimeDefeated = NPC.downedSlimeKing, eyeDefeated = NPC.downedBoss1,
        destroyerDefeated = NPC.downedMechBoss1, twinsDefeated = NPC.downedMechBoss2,
        skeletronPrimeDefeated = NPC.downedMechBoss3, planteraDefeated = NPC.downedPlantBoss,
        gameNight = !Main.IsItDay()
    };

    public static object Query(string query, NPC npc, Player player)
    {
        int type = BossRules.ResolveBoss(query);
        int summon = type == NPCID.KingSlime ? ItemID.SlimeCrown : type == NPCID.EyeofCthulhu ? ItemID.SuspiciousLookingEye : 0;
        bool matchingBossActive = type != 0 && NPC.AnyNPCs(type);
        int ownedSummoner = summon == 0 ? 0 : AITeammateSystem.Instance.Loadout.CountItem(summon);
        float ownerDistance = Vector2.Distance(npc.Center, player.Center);
        bool canSummonNow = ownedSummoner > 0 && ownerDistance <= 30 * 16f &&
            BossRules.CanSummon(summon, !Main.IsItDay(), matchingBossActive);
        return new
        {
            topic = "boss", query, found = type != 0, npcType = type,
            name = type == 0 ? "未知；可查king_slime/eye_of_cthulhu/plantera" : Lang.GetNPCNameValue(type),
            progress = Progress(), supportedSummoningItem = summon,
            summoningItemName = summon == 0 ? "无已支持的直接召唤物" : Lang.GetItemNameValue(summon),
            ownedSummoner, matchingBossActive, ownerDistanceTiles = (int)(ownerDistance / 16f), canSummonNow,
            summonTask = summon == 0 ? null : new { type = "use", item = summon },
            requirements = type switch
            {
                NPCID.KingSlime => "使用真实史莱姆王冠；没有活动的史莱姆王；原生以所属玩家为中心召唤，先回到玩家30格内。无时段/群系/进度限制。",
                NPCID.EyeofCthulhu => "游戏判定为夜晚且无活动眼球；使用真实可疑眼球，在所属玩家30格内。白天不能使用。",
                NPCID.Plantera => "需要困难模式、击败三种机械Boss后生成世纪之花球茎，找到并破坏球茎；当前技能不支持寻找并破坏该特殊球茎，没有可凭空使用的世纪之花召唤物。",
                _ => "先确认支持的Boss和前置条件，不得编造召唤物。"
            },
            defeatsThisSession = AITeammateSystem.Instance.BossEvidence.Snapshot()
                .Where(e => e.DeathSequence > 0).Select(e => new { e.SpawnId, e.NpcType, e.DeathSequence, e.CompanionDamage }).ToArray(),
            rule = "历史击败旗标、召唤成功或Boss消失都不能证明本次击杀；必须有本次真实OnKill及同伴伤害记录。"
        };
    }

    public static bool TryUseSummoner(Item item, NPC npc, Player owner, out bool handled, out string result)
    {
        int type = BossRules.BossTypeForItem(item.type);
        handled = type != 0;
        result = "";
        if (!handled) return false;
        int itemType = item.type;
        string itemName = Lang.GetItemNameValue(itemType);
        if (Main.netMode != NetmodeID.SinglePlayer || !owner.active || owner.dead)
        { result = "当前没有可用的单人所属玩家，不能召唤。"; return false; }
        if (Vector2.Distance(npc.Center, owner.Center) > 30 * 16f)
        { result = "原生召唤以所属玩家为中心；当前离玩家超过30格，先回来再使用召唤物。"; return false; }
        if (!BossRules.CanSummon(item.type, !Main.IsItDay(), NPC.AnyNPCs(type)) || !owner.SummonItemCheck(item))
        { result = "召唤条件不满足：同类Boss已存在，或眼球当前不是游戏判定的夜晚。召唤物未消耗。"; return false; }
        if (!CombinedHooks.CanUseItem(owner, item))
        { result = "游戏/模组CanUseItem拒绝使用该召唤物；未消耗。"; return false; }
        NPC.SpawnOnPlayer(owner.whoAmI, type);
        if (!NPC.AnyNPCs(type))
        { result = "原生召唤位置搜索未能生成Boss；没有消耗召唤物，不能当召唤或击杀成功。"; return false; }
        SoundEngine.PlaySound(SoundID.Roar, npc.Center);
        if (ItemLoader.ConsumeItem(item, owner)) AITeammateSystem.Instance.Loadout.RemoveItem(itemType, 1);
        result = $"已合法使用{itemName}，实际生成{Lang.GetNPCNameValue(type)}（NPC类型{type}）。尚未击杀，需要战斗并取得真实死亡证据。";
        return true;
    }
}
