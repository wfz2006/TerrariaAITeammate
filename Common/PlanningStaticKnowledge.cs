using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

/// <summary>Stable capability documentation sent once in the shared system prefix.</summary>
public static class PlanningStaticKnowledge
{
    private static readonly Dictionary<string, string> Definitions = new(StringComparer.Ordinal)
    {
        ["sourceInspect"] = "筹料可用inspect target=sources,item=wood/stone/矿类等资源名，支持world相同分页/距离选项及成对x/y距离原点；逐帧对索引样本做现有定点mine预检，一次给简短候选/木种/保护/工具/工位/路径事实，由你选择mine种子和数量。canAttempt不代表已采到或挖后返程安全；原点不移动角色，部分索引/样本/未测仍未知。",
        ["observationRecall"] = "planning.worldObservations为本目标已保存的world/terrain/site/route历史原文目录；inspect target=observation,item=完整id按需取回。摘要省略的网格或站位仍在原文中；历史记录不刷新世界，路线记录不是实际通行证明。新目标/世界或容量淘汰后id会失效。",
        ["worldInspect"] = "inspect target=world，item=wood/stone/矿物分类、item:物品ID、tile:方块ID、chests、stations、items、npc:类型ID或boss:名称；默认4项、最多8，可追加;offset=N;limit=N，位置列表可用;minDistance=0..100000;maxDistance=0..100000整数闭区间先过滤再分页。距离原点用成对x/y或队友本轮取整格坐标，资源过滤按返回sample真实坐标计算，非chunk近边距离；最多3样本/分区类型，没命中仍可能有未采样资源。world+chest仍需实际箱子坐标且不接受距离过滤，可远读锁箱；知道内容不等于能取。瓦片数非产量/矿脉边界，样本非可站工位，路径未知。",
        ["inspectUsage"] = "inspect: target=查询类型, item=名称或数字ID；recipe可用 名称 x数量。structure/house用item=方块x,y或task.x/y选择真实结构；分层视图view=overview只给明确省略的概形/测量，格数统计不是材料账单。按需structure_detail取完整有界grid，无坐标刷新已选结构，显式坐标才重新选择。site用item=x,y,width,height核对拟施工矩形，可追加;tile=类型ID;wall=类型ID。查询不消耗物品。",
        ["craftingInventory"] = "消耗材料统一只使用队友背包；ownedPlayer仅为观察，不抵扣可执行材料缺额，也不会隐式借用玩家材料。需要的物品先实际交给队友。",
        ["craftingSelection"] = "craft可带recipe_id严格使用本轮inspect recipe返回的原生recipeId；仍须满足该配方产物、材料、全部工作站和条件。不存在/禁用/产物不匹配则明确失败，不换配方；不填recipe_id保留自动选择。",
        ["conditionContext"] = "配方环境条件由游戏当前玩家上下文检查；工作站距离以队友为中心。",
        ["navigationInspect"] = "terrain可读世界内指定远端中心或完整矩形≤97x81，默认周围33x33。已知真实目标可直接显式explore(x,y)，route按需而非强制。正常inspect route同会话逐帧64唯一节点/软2ms续算，总限30000唯一/60000发现、600游戏ticks后一次返回，无需LLM轮询；兼容即时查询可能只有searching。计算未完/预算耗尽/失效/超时不是无路。图框是解析后两端包络加横64/纵48格padding。sameProjectedStand不代表实际到达，alreadyAtResolvedStand须真实脚底/接地/竖速/水平位置证据；pathFound不代表已移动成功。",
        ["miningSelection"] = "只凑数量而不指定矿脉时，mine可不填x/y，在既有附近扫描范围中有界检查分散候选的真实交互工位及返回本次Mine起点的路线后选源，包括多棵小树。returnSafety未知不当作安全，失败可由模型换来源或调整独立步骤，不表示整个区域不可达。count是本次希望新增的产物量，结合库存计算缺额。成对x/y严格限定资源起始格及初始同源连通区≤256格，不换远源；显式选择未检查返程会如实标明。dirt/grass必须显式坐标。不会自动挖无关通道；去返图也不保证采挖改变地形后仍然有效。",
        ["miningInspect"] = "inspect target=mine,item=资源分类，不填x/y可只读查询附近自动选源的候选与工位短摘要；带成对x/y则检查严格种子。两者与实际采集共用工位/路线判据；没有范围内工位、图搜索失败和探测预算耗尽是不同结果。资源顶部路线不能替代交互工位检查；预览不移动或挖掘，候选可接近不是已取得产物。",
        ["designInspect"] = "inspect target=design, design={bounds:[x,y,w,h],clearSpaces:[[x,y,w,h]],placements:[[itemId,x,y,w,h,standX,feetY]]}。bounds是全部施工含临时支撑/脚手架的审查包络，不只是成品房间；最多97x81。最多96操作，家具x/y沿用place左上角，feetY允许半格。平台仅single；已映射kind=platform/nativeObjectNoAnchors作条件式预检，未知变体仍unsupported，不能改当批量方块或放宽家具地基。只审查你给出的设计，不自动布局/修改世界。先收取issues/bill再交付；静态通过不是已施工或原生住房认证。",
        ["note"] = "先查询真实配方和前置条件，再由你决定如何拆解目标。没有列出的能力不可凭空执行。",
    };

    public static string Get(string field) => Definitions[field];
    public static string SystemBlock { get; } = "\n固定能力说明（原 state.knowledge 同名字段，整段在所有规划模式保持一致；状态若显式携带不同值，以该轮值为准）：\n" +
        JsonSerializer.Serialize(Definitions, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static void Lift(JsonObject frame, string system)
    {
        if (!system.EndsWith(SystemBlock, StringComparison.Ordinal) || frame["state"]?["knowledge"] is not JsonObject knowledge) return;
        HashSet<string> targets = PlanningHistoryWire.LocalReferenceTargets(frame);
        foreach (var pair in Definitions)
            if (knowledge[pair.Key] is JsonValue value && value.TryGetValue(out string? text) && text == pair.Value &&
                !PlanningHistoryWire.IsReferenceTarget("/state/knowledge/" + pair.Key, targets))
                knowledge.Remove(pair.Key);
    }
}
