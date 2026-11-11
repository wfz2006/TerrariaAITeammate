using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public class AIPlanner
{
    private static readonly DeferredPlanHistory PrimaryHistory = new();

    public static bool ApplyAcceptedPlan(AIPlan plan, DecisionGate gate, DecisionTicket ticket, Func<bool> apply)
        => PrimaryHistory.ApplyAndCommit(plan, gate, ticket, apply);

    public static void DiscardPlanHistory(AIPlan plan) => PrimaryHistory.Discard(plan);

    public static string BuildState(Player player, AIExecutor executor, AIMemory memory)
    {
        AITeammateSystem.Instance.SynchronizeGoalQueue();
        NPC? companion = AITeammateSystem.Instance.CompanionNpc;
        Vector2 focus = companion?.Center ?? player.Center;
        int focusX = (int)(focus.X / 16f);
        int focusY = (int)(focus.Y / 16f);
        string focusLayer = focusY > Main.rockLayer ? "洞穴" : focusY > Main.worldSurface ? "地下" : "地表";

        var enemies = Main.npc
            .Where(n => n.active && !n.friendly && n.life > 0 && n.CanBeChasedBy() &&
                        Vector2.Distance(n.Center, focus) < (n.boss ? 160f * 16f : 60f * 16f))
            .OrderByDescending(n => n.boss)
            .ThenBy(n => Vector2.Distance(n.Center, focus))
            .Take(6)
            .Select(n => new
            {
                name = n.FullName,
                distance = (int)(Vector2.Distance(n.Center, focus) / 16f),
                hp = n.life,
                npcType = n.type, boss = n.boss
            })
            .ToArray();

        var nearbyItems = Main.item
            .Where(item => item.active && !item.IsAir &&
                           Vector2.Distance(item.Center, focus) < 80f * 16f)
            .OrderByDescending(item => item.GetGlobalItem<BossLootTag>().BossSpawnId > 0)
            .ThenBy(item => Vector2.Distance(item.Center, focus))
            .Take(8)
            .Select(item => new
            {
                name = Lang.GetItemNameValue(item.type),
                id = item.type,
                bossLootSpawnId = item.GetGlobalItem<BossLootTag>().BossSpawnId,
                stack = item.stack,
                distance = (int)(Vector2.Distance(item.Center, focus) / 16f)
            })
            .ToArray();

        CompanionLoadout loadout = AITeammateSystem.Instance.Loadout;

        var state = new
        {
            version = 2,
            planning = AITeammateSystem.Instance.PlanningContext,
            executionPolicy = executor.Policy,
            knowledge = AgentKnowledge.Snapshot(player, companion, loadout),
            inventory = loadout.Inventory.Where(item => !item.IsAir).Select(item => new
            {
                id = item.type,
                name = Lang.GetItemNameValue(item.type),
                count = item.stack,
                pick = item.pick,
                axe = item.axe,
                createTile = item.createTile
            }).ToArray(),
            player = new
            {
                name = player.name,
                hp = player.statLife,
                maxHp = player.statLifeMax2,
                x = (int)(player.Center.X / 16f),
                y = (int)(player.Center.Y / 16f),
                biome = BiomeName(player),
                day = Main.dayTime,
                hardmode = Main.hardMode
            },
            companion = new
            {
                summoned = executor.HasCompanion,
                hp = executor.CompanionHp,
                x = focusX,
                y = focusY,
                layer = focusLayer,
                task = executor.StatusText(),
                inventory = loadout.Summary(),
                equipment = loadout.EquipmentSummary(),
                defense = loadout.Defense,
                weaponDamage = loadout.WeaponDamage,
                mana = loadout.Mana,
                manaMax = loadout.ManaMax,
                recovering = executor.RecentlyRetreated,
                hold = executor.HoldPosition,
                autoDefending = executor.IsAutoDefending,
                playerAttackedRecently = executor.PlayerRecentlyAttacked,
                hasPickaxe = loadout.HasPickaxe,
                hasAxe = loadout.HasAxe,
                hasWeapon = loadout.HasWeapon
            },
            nearbyEnemies = enemies,
            nearbyItems,
            perception = AIPerception.Build(player, companion),
            currentTask = executor.StatusText(),
            navigation = executor.NavigationState,
            goal = AITeammateSystem.Instance.ActiveGoal,
            lastStepResult = AITeammateSystem.Instance.LastGoalResult,
            goalBlocked = AITeammateSystem.Instance.GoalBlocked,
            blueprints = BlueprintManager.ListNames(),
            blockedTask = executor.BlockedPlan == null ? null : new
            {
                type = executor.BlockedPlan.Action.ToString(),
                blueprint = executor.BlockedPlan.Blueprint,
                reason = executor.BlockedReason
            },
            tasks = executor.SnapshotTasks().Select(task => new
            {
                type = task.Action.ToString(),
                target = task.Target,
                item = task.Item,
                slot = task.Slot,
                recipe_id = task.RecipeId,
                plan_step = task.PlanStep,
                count = task.Count,
                blueprint = task.Blueprint,
                x = task.X,
                y = task.Y,
                width = task.Width,
                height = task.Height,
                reason = task.Reason,
                success_criteria = task.SuccessCriteria
            }).ToArray(),
            facts = memory.FactsSnapshot(),
            observedFacts = memory.ObservedFactsSnapshot(),
            evidencePriority = "当前live_world结构网格/执行后的世界复核 > observedFacts历史实测 > facts与模型旧推断。模板材料表和come成功都不等于读取原屋。",
            recentEvents = memory.Snapshot()
        };

        return JsonSerializer.Serialize(state);
    }

    public static string BuildCombatState(Player player, NPC companion, AIExecutor executor)
    {
        CompanionLoadout loadout = AITeammateSystem.Instance.Loadout;
        float nearestDistance = 999f;
        NPC? nearest = null;

        var enemies = Main.npc
            .Where(n => n.active && !n.friendly && n.life > 0 && n.CanBeChasedBy() &&
                        Vector2.Distance(n.Center, companion.Center) < (n.boss ? 160f * 16f : 45f * 16f))
            .OrderByDescending(n => n.boss)
            .ThenBy(n => Vector2.Distance(n.Center, companion.Center))
            .Take(4)
            .Select(n =>
            {
                float distance = Vector2.Distance(n.Center, companion.Center);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = n;
                }

                return new
                {
                    index = n.whoAmI,
                    name = n.FullName,
                    hp = n.life,
                    maxHp = n.lifeMax,
                    distance = (int)(distance / 16f),
                    boss = n.boss,
                    width = n.width
                };
            })
            .ToArray();

        Item? current = loadout.GetBestWeapon();
        Item? currentAmmo = current is { useAmmo: > 0 } ? loadout.FindAmmo(current.useAmmo) : null;

        var weapons = loadout.Inventory
            .Where(CompanionLoadout.IsCombatWeapon)
            .OrderByDescending(item => item.damage)
            .Take(6)
            .Select(item => new
            {
                name = Lang.GetItemNameValue(item.type),
                damage = item.damage,
                kind = WeaponKind(item),
                ammo = item.useAmmo > 0 ? (loadout.FindAmmo(item.useAmmo) is { } ammo ? Lang.GetItemNameValue(ammo.type) : "") : "",
                ammoCount = item.useAmmo > 0
                    ? loadout.Inventory.Where(a => !a.IsAir && a.ammo == item.useAmmo).Sum(a => a.stack)
                    : 0,
                mana = item.mana,
                useTime = item.useTime
            })
            .ToArray();

        var potions = loadout.Inventory
            .Where(item => !item.IsAir && (item.healLife > 0 || item.healMana > 0 || item.buffType > 0))
            .Take(6)
            .Select(item => new
            {
                name = Lang.GetItemNameValue(item.type),
                stack = item.stack,
                healLife = item.healLife,
                healMana = item.healMana,
                buff = item.buffType
            })
            .ToArray();

        var state = new
        {
            self = new
            {
                name = companion.GivenName,
                hp = companion.life,
                maxHp = companion.lifeMax,
                hpPercent = (int)(companion.life * 100f / Math.Max(1, companion.lifeMax)),
                mana = loadout.Mana,
                manaMax = loadout.ManaMax,
                x = (int)(companion.Center.X / 16f),
                y = (int)(companion.Center.Y / 16f)
            },
            player = new
            {
                hp = player.statLife,
                maxHp = player.statLifeMax2,
                hpPercent = (int)(player.statLife * 100f / Math.Max(1, player.statLifeMax2)),
                distance = (int)(Vector2.Distance(player.Center, companion.Center) / 16f),
                attackedRecently = executor.PlayerRecentlyAttacked
            },
            currentAction = executor.CurrentAction.ToString(),
            currentWeapon = current == null ? null : new
            {
                name = Lang.GetItemNameValue(current.type),
                damage = current.damage,
                kind = WeaponKind(current),
                ammo = currentAmmo == null ? "" : Lang.GetItemNameValue(currentAmmo.type),
                ammoCount = currentAmmo?.stack ?? 0,
                mana = current.mana
            },
            weapons,
            potions,
            potionCooldownTicks = executor.CombatPotionCooldown,
            weaponSwitchCooldownTicks = executor.WeaponSwitchCooldown,
            enemies,
            enemyCount = enemies.Length,
            nearestDistance = nearest == null ? 999 : (int)(nearestDistance / 16f),
            nearestName = nearest?.FullName ?? ""
        };

        return JsonSerializer.Serialize(state);
    }

    public async Task<CombatDecision> PlanCombatAsync(LlmSettings settings, string stateJson, bool debug, Action<string> log,
        CancellationToken cancellationToken = default, Action<LlmUsage>? onUsage = null)
    {
        const string CombatSystem = """
你是泰拉瑞亚 AI 队友“小星”的战斗指挥。你要根据实时状态做一个即时决定，代码会负责执行。
只输出一个 JSON 对象，不要 markdown，不要代码块，不要聊天，不要任务队列。
格式：
{"action":"fight|switch|use|retreat","weapon":"武器名，可空","item":"药水名，可空","reason":"不超过12字"}
规则：
- 由你结合双方血量、敌人的数量/距离/威胁、武器射程、弹药、法力和撤退后恢复机会选择战术；不要机械使用固定血量阈值。
- fight继续战斗；switch换武器；use使用药物；retreat拉开距离恢复。一次只选一种。
- 优先维持生存和玩家安全，兼顾正在完成的目标。根据实际压力决定何时治疗、换武器或撤退。
- potionCooldownTicks和weaponSwitchCooldownTicks是执行限制，不要反复要求冷却中的操作。
- weapon/item必须从state真实武器/药水中选，不编造；考虑消耗品、弹药和法力是否够用。
""";

        LlmHistorySnapshot history = LlmConversationStore.CaptureForRequest("combat", 6, 18000);
        var messages = PlanningRequest.Build(CombatSystem + "\n只以最后一条user的state作为当前战斗状态，之前是历史。",
            "战斗", "根据最新状态选择即时战术。", stateJson, history.Messages);

        LlmSettings combatSettings = settings;
        if (settings.ThinkingMode == LlmThinkingMode.Enabled &&
            Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out Uri? endpoint) &&
            endpoint.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
            combatSettings = settings with { ThinkingMode = LlmThinkingMode.Disabled };
        var client = new DeepSeekClient();
        LlmResponse response;
        try { response = await client.ChatJsonAsync(combatSettings, messages, cancellationToken, onUsage).ConfigureAwait(false); }
        catch (LlmOutputLimitException exception)
        {
            LogOutputLimit("combat", exception, log);
            throw;
        }
        if (debug)
            log($"combat raw: {response.Content}");
        log("combat usage " + response.Usage.LogSummary);
        log($"combat response actualModel={JsonSerializer.Serialize(response.ActualModel)} reasoningTokens={response.Usage.ReasoningTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not_reported"}");

        cancellationToken.ThrowIfCancellationRequested();
        CombatDecision decision = ParseCombat(response.Content);
        if (!string.IsNullOrEmpty(decision.Action))
            LlmConversationStore.TryAppendPair("combat", history, messages[^1].Content, response.Content, 6);
        return decision;
    }

    public static CombatDecision ParseCombat(string raw)
    {
        string json = ExtractJson(raw);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string action = root.TryGetProperty("action", out JsonElement a)
                ? (a.GetString() ?? "").Trim().ToLowerInvariant()
                : "";

            if (action is not ("fight" or "switch" or "use" or "retreat"))
                action = "";

            return new CombatDecision
            {
                Action = action,
                Weapon = root.TryGetProperty("weapon", out JsonElement w) ? (w.GetString() ?? "").Trim() : "",
                Item = root.TryGetProperty("item", out JsonElement i) ? (i.GetString() ?? "").Trim() : "",
                Reason = root.TryGetProperty("reason", out JsonElement r) ? (r.GetString() ?? "").Trim() : ""
            };
        }
        catch
        {
            return new CombatDecision();
        }
    }

    private static string WeaponKind(Item item)
    {
        if (item.useAmmo > 0)
            return "ranged";
        if (item.mana > 0)
            return "magic";
        return "melee";
    }

    public async Task<AIPlan> PlanAsync(LlmSettings settings, string stateJson, string message,
        bool debug, Action<string> log, bool autonomous = false, bool exploreNudge = false,
        CancellationToken cancellationToken = default, Action<LlmUsage>? onUsage = null)
    {
        string mode = autonomous ? "自主决策" : "玩家消息";
        string input = autonomous ? "请根据最新状态自主决定有价值的下一步。" : $"玩家说：{message}";
        return await RequestPlanAsync(settings, mode, autonomous ? "auto" : "chat", input, stateJson, null,
            debug, log, cancellationToken, onUsage).ConfigureAwait(false);
    }

    public async Task<AIPlan> PlanGoalAsync(LlmSettings settings, string stateJson, string goal,
        string lastResult, bool blocked, bool debug, Action<string> log,
        CancellationToken cancellationToken = default, Action<LlmUsage>? onUsage = null)
    {
        string input = $"复盘当前目标：{goal}。最近调用/执行受阻：{blocked}（此布尔只指调用或执行，不代表方案通过；inspect design看reviewStatus/requiresRevision及review）。请观察目标差距并决定下一步。";
        return await RequestPlanAsync(settings, PlanningRequest.GoalReviewMode, "goal", input, stateJson, lastResult,
            debug, log, cancellationToken, onUsage).ConfigureAwait(false);
    }

    private static async Task<AIPlan> RequestPlanAsync(LlmSettings settings, string mode,
        string historyKey, string input, string stateJson, string? feedback, bool debug, Action<string> log,
        CancellationToken cancellationToken, Action<LlmUsage>? onUsage)
    {
        LlmHistorySnapshot history = LlmConversationStore.CaptureForRequest(historyKey, maxMessages: 12, maxCharacters: 96000);
        var messages = PlanningRequest.Build(AgentPlanningPrompt.For(mode), mode, input, stateJson, history.Messages, feedback,
            usePhysicsStructureView: settings.UsePhysicsStructureView,
            useStructureOverview: settings.UseStructureOverview);
        if (debug)
        {
            log($"{mode} context messageCount={messages.Count} historicalMessageCount={history.Messages.Count} " +
                $"historyCharacters={history.Messages.Sum(message => (long)message.Content.Length)} freshFrameCharacters={messages[^1].Content.Length} " +
                $"totalCharacters={messages.Sum(message => (long)message.Content.Length)}");
            using JsonDocument sizeFrame = JsonDocument.Parse(messages[^1].Content);
            JsonElement sizeState = sizeFrame.RootElement.GetProperty("state");
            if (sizeFrame.RootElement.TryGetProperty("contextReuse", out JsonElement reuse))
                log($"{mode} contextReuse=" + reuse.GetRawText());
            log($"{mode} contextSections=" + JsonSerializer.Serialize(sizeState.EnumerateObject()
                .Select(property => new { field = property.Name, characters = property.Value.GetRawText().Length })
                .OrderByDescending(section => section.characters).Take(6)));
        }

        LlmResponse response;
        try
        {
            response = await new DeepSeekClient()
                .ChatJsonAsync(settings, messages, cancellationToken, onUsage).ConfigureAwait(false);
        }
        catch (LlmOutputLimitException exception)
        {
            LogOutputLimit(mode, exception, log);
            throw;
        }
        if (debug)
            log($"{mode} raw: {response.Content}");
        log($"{mode} usage " + response.Usage.LogSummary);
        log($"{mode} response actualModel={JsonSerializer.Serialize(response.ActualModel)} reasoningTokens={response.Usage.ReasoningTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not_reported"}");

        // Invalid decisions never enter history or become local rule-based actions.
        AIPlan plan = AIPlanParser.Parse(response.Content);
        plan.NativeObservationReceipt = WorldObservationReceipt.FromPlanningFrame(messages[^1].Content);
        cancellationToken.ThrowIfCancellationRequested();
        PrimaryHistory.Stage(plan, historyKey, history, messages[^1].Content, response.Content);
        return plan;
    }

    public static AIPlan Parse(string raw, string originalMessage) => AIPlanParser.Parse(raw);

    private static void LogOutputLimit(string mode, LlmOutputLimitException exception, Action<string> log)
    {
        log($"{mode} output_limit usage " + exception.Usage.LogSummary);
        log($"{mode} output_limit actualModel={JsonSerializer.Serialize(exception.ActualModel)} reasoningTokens={exception.Usage.ReasoningTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not_reported"}");
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "{}";

        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        return start < 0 || end <= start ? "{}" : raw.Substring(start, end - start + 1);
    }

    private static string BiomeName(Player player)
    {
        if (player.ZoneForest) return "森林";
        if (player.ZoneDesert) return "沙漠";
        if (player.ZoneJungle) return "丛林";
        if (player.ZoneSnow) return "雪地";
        if (player.ZoneCorrupt) return "腐化";
        if (player.ZoneCrimson) return "猩红";
        if (player.ZoneHallow) return "神圣";
        if (player.ZoneDungeon) return "地牢";
        if (player.ZoneUnderworldHeight) return "地狱";
        return "未知";
    }
}
