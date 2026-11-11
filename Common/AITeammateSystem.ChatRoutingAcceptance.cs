using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI.Chat;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string ChatRoutingNativeCase = "chat_routing_native";
    private const string ChatRoutingNativeGoal = "受控聊天分流与原目标保持";
    private const string ChatRoutingV16Request =
        "请自己筹集材料，使用家里已有的工作台，做1把木椅、20个石墙、6个普通火把和4个木平台，做好后带着成品回到我身边。" +
        "木材和石块要自己采，凝胶可以从家里现有箱子取；不要拿箱里的成品，不要拆现有建筑或另放工作台。" +
        "遇到受阻自己换路线或材料来源，保留已经完成的部分继续做。" +
        "允许在原屋外用自己筹集的材料搭少量临时平台或土阶，用于通行和返程；保留原建筑和物品，不恢复自主建屋。";

    private sealed record ChatRoutingSnapshot(string Goal, string GoalId, string Status, long GoalStartSequence, long OutcomeSequence,
        AIActionType Action, bool HasPlan, bool Hold, bool WaitingForPlayer, bool ReviewPending, bool ReviewBlocked,
        bool ManualControl, bool Auto, int ContractRevision, string Contract, string Queue, string Stock,
        float ActorX, float ActorY);
    private sealed record ChatRoutingCheck(string Name, bool Passed, object Evidence);

    private void RunChatRoutingNative()
    {
        if (!HouseMode || Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") != ChatRoutingNativeCase) return;
        string path = Path.Combine(Main.SavePath, "AITeammate", "chat-routing-native-result.json");
        if (File.Exists(path))
        { TestLog("chat routing requires a fresh isolated copy"); return; }

        var checks = new List<ChatRoutingCheck>();
        Dictionary<string, string> inputHashes = ReadBossReviewHashes();
        var audit = ShelterWorldAudit.Capture(3220, 390);
        ShelterPreservationReport initialProtection = audit.CheckPreservation();
        AIConfig config = ModContent.GetInstance<AIConfig>();
        var original = (config.UseLlm, config.ApiKey, config.BaseUrl, config.Model, config.ChatMode);
        uint startedTick = Main.GameUpdateCount;
        string error = "";
        bool IsClosed() => HouseMode && !HousePlanningAllowed && !CombatPlannerEnabled &&
            PrimaryDecisionRequestCount == 0 && CombatDecisionRequestCount == 0 && primaryCancellation == null && !combatPlanInFlight;
        void RequireClosed()
        {
            if (!IsClosed()) throw new InvalidOperationException("The house planning and combat request gates must be closed.");
        }
        static void Submit(string text)
        {
            var message = ChatManager.Commands.CreateOutgoingMessage(text);
            ChatManager.Commands.ProcessIncomingMessage(message, Main.myPlayer);
            if (!message.IsConsumed) throw new InvalidOperationException("Terraria did not consume the submitted chat line.");
        }

        try
        {
            RequireClosed();
            if (GetLlmSettings() != null || CompanionNpc is not { active: true } || Main.LocalPlayer.dead ||
                initialProtection is not { protectedCells: 5803, preserved: true, coverageComplete: true, diffCount: 0 })
                throw new InvalidOperationException("The case requires disabled LLM, live actors and the original-house baseline.");

            Stop();
            Executor.ResetNavigationTelemetry();
            manualControlActive = false;
            Executor.SetHold(false);
            var seed = new AIPlan
            {
                Goal = ChatRoutingNativeGoal,
                ReplaceTasks = true,
                CompletionConditions = new() { new() { Type = "structure_preserved" } },
                Steps = new()
                {
                    new() { Action = AIActionType.Inspect, Target = "recipe", Item = "94" },
                    new() { Action = AIActionType.Inspect, Target = "item", Item = "560" }
                }
            };
            if (!ApplyPlan(seed, null, "chat", ChatRoutingNativeGoal) ||
                !ApplyPlan(new AIPlan { Hold = true, KeepTasks = true }, null, "goal") ||
                !Executor.HoldPosition || !Executor.HasCurrentPlan || Executor.SnapshotTasks().Count != 2 ||
                ActiveGoal != ChatRoutingNativeGoal || goalStatus != "paused")
                throw new InvalidOperationException("The paused native goal and its two queued tasks could not be seeded.");

            Player player = Main.LocalPlayer;
            config.ChatMode = CompanionChatMode.Addressed;
            var beforeAmbient = CaptureChatRouting();
            string beforeAmbientMemory = Memory.Snapshot();
            long beforeAmbientIntent = decisionGate.Intent;
            Submit("普通聊天验收，不呼叫小星。");
            checks.Add(new("native_unaddressed_chat", beforeAmbient == CaptureChatRouting() && pendingCommand == null &&
                beforeAmbientMemory == Memory.Snapshot() && beforeAmbientIntent == decisionGate.Intent,
                new { requestsClosed = IsClosed(), intent = decisionGate.Intent }));

            foreach (var (name, message) in new[]
            {
                ("offline_v16", ChatRoutingV16Request),
                ("offline_house_no_mining", NaturalHouseRequest),
                ("offline_boss", NaturalBossRequest)
            })
            {
                RequireClosed();
                var before = CaptureChatRouting();
                string? pendingBefore = pendingCommand;
                long intentBefore = decisionGate.Intent;
                HandlePlayerMessage(player, message);
                var after = CaptureChatRouting();
                bool passed = before == after && pendingCommand == pendingBefore && decisionGate.Intent == intentBefore &&
                    LastReply.Contains("LLM", StringComparison.Ordinal);
                checks.Add(new(name, passed, new { message, before, after, pendingBefore, pendingAfter = pendingCommand,
                    intentBefore, intentAfter = decisionGate.Intent, reply = LastReply, requestsClosed = IsClosed() }));
                RequireClosed();
            }

            // These temporary properties live only on this synchronous main-thread call.
            // StartPrimaryDecision returns at the HouseMode gate before creating a request.
            config.UseLlm = true;
            config.ApiKey = "fixture-no-network";
            config.BaseUrl = "http://127.0.0.1:1";
            config.Model = "deepseek-flash";
            if (GetLlmSettings() is not { Model: "deepseek-flash", BaseUrl: "http://127.0.0.1:1" })
                throw new InvalidOperationException("The temporary loopback-only fixture settings were not active.");

            foreach (var (name, message, chatEntry) in new[]
            {
                ("online_compound_follow", "跟着我 穿过那扇门", false),
                ("online_compound_attack", "攻击 史莱姆王 然后捡起战利品", false),
                ("online_equipment_constraint", "装备 Copper Helmet 再来我身边", false),
                ("online_marked_chat", "小星：先检查[蓝图]里的[TEST]范围，别施工。", true)
            })
            {
                RequireClosed();
                var before = CaptureChatRouting();
                if (chatEntry) Submit(message);
                else HandlePlayerMessage(player, message);
                var after = CaptureChatRouting();
                string expected = chatEntry ? "先检查[蓝图]里的[TEST]范围，别施工。" : message;
                bool passed = pendingCommand == expected && before == after;
                checks.Add(new(name, passed, new { message, chatEntry, pending = pendingCommand, before, after, requestsClosed = IsClosed() }));
                RequireClosed();
            }

            config.ChatMode = CompanionChatMode.CommandsOnly;
            var beforeCommandsOnly = CaptureChatRouting();
            string? commandsOnlyPending = pendingCommand;
            long commandsOnlyIntent = decisionGate.Intent;
            Submit("@小星 停下！");
            checks.Add(new("native_commands_only", beforeCommandsOnly == CaptureChatRouting() &&
                commandsOnlyPending == pendingCommand && commandsOnlyIntent == decisionGate.Intent,
                new { requestsClosed = IsClosed(), pendingCommand }));

            var command = ModContent.GetInstance<AICommand>();
            command.Action(new NativeBasicsCommandCaller(player), "/ai SAY  MiXeD  Spacing", new[] { "SAY", "", "MiXeD", "", "Spacing" });
            checks.Add(new("explicit_say_literal_body", pendingCommand == "MiXeD  Spacing" &&
                beforeCommandsOnly == CaptureChatRouting(), new { requestsClosed = IsClosed(), pendingCommand }));

            config.ChatMode = CompanionChatMode.All;
            Submit("请继续检查原屋的材料。");
            checks.Add(new("native_listen_all", pendingCommand == "请继续检查原屋的材料。" &&
                beforeCommandsOnly == CaptureChatRouting(), new { requestsClosed = IsClosed(), pendingCommand }));
            config.ChatMode = CompanionChatMode.Addressed;
            RequireClosed();

            long outcomeSequence = agentJournal.LastSequence;
            var beforeStop = CaptureChatRouting();
            HandlePlayerMessage(player, "停下");
            var afterStop = CaptureChatRouting();
            bool stopped = ActiveGoal.Length == 0 && !goalPlanningState.HasGoal && pendingCommand == null &&
                Executor.HoldPosition && Executor.CurrentAction == AIActionType.Wait && !Executor.HasCurrentPlan &&
                Executor.SnapshotTasks().Count == 0 && agentJournal.LastSequence == outcomeSequence;
            checks.Add(new("online_exact_stop", stopped, new { before = beforeStop, after = afterStop, pending = pendingCommand,
                requestsClosed = IsClosed() }));
            RequireClosed();
        }
        catch (InvalidOperationException exception) { error = exception.Message; }
        finally
        {
            config.UseLlm = original.UseLlm;
            config.ApiKey = original.ApiKey;
            config.BaseUrl = original.BaseUrl;
            config.Model = original.Model;
            config.ChatMode = original.ChatMode;
        }

        bool sameTick = startedTick == Main.GameUpdateCount;
        bool restored = GetLlmSettings() == null && config.UseLlm == original.UseLlm && config.ApiKey == original.ApiKey &&
            config.BaseUrl == original.BaseUrl && config.Model == original.Model && config.ChatMode == original.ChatMode;
        var protection = audit.CheckPreservation();
        Dictionary<string, string> outputHashes = ReadBossReviewHashes();
        bool sameFiles = inputHashes.Count > 0 && inputHashes.Count == outputHashes.Count &&
            inputHashes.All(pair => outputHashes.TryGetValue(pair.Key, out string? hash) && hash == pair.Value);
        bool passedAll = error.Length == 0 && checks.Count == 12 && checks.All(check => check.Passed) && sameTick && restored &&
            IsClosed() && Executor.NavigationTeleportCount == 0 && sameFiles &&
            protection is { protectedCells: 5803, preserved: true, coverageComplete: true, diffCount: 0 };
        if (!passedAll && error.Length == 0) error = "A chat route, paused state, request gate or preservation check failed.";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                status = passedAll ? "passed" : "failed", error, worldId = Main.worldID, epoch = WorldKnowledgeObserver.LoadEpoch,
                checks, sameTick, restored, requestsClosed = IsClosed(), requests = PrimaryDecisionRequestCount,
                combatRequests = CombatDecisionRequestCount, warps = Executor.NavigationTeleportCount,
                initialProtection, protection, sameFiles, copiedInputHashes = inputHashes, isolatedFileHashesAtFinish = outputHashes,
                scope = "Synchronous original-house-copy checks through the production chat and player-message handlers. The house planning gate stays closed while testing the pending command."
            }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        }
        finally { Stop(); }
        TestLog($"{ChatRoutingNativeCase} {(passedAll ? "passed" : "failed")} {checks.Count} checks. {error}");
    }

    private ChatRoutingSnapshot CaptureChatRouting() => new(ActiveGoal, GoalIdentity, goalStatus, goalStartSequence, agentJournal.LastSequence,
        Executor.CurrentAction, Executor.HasCurrentPlan, Executor.HoldPosition, goalWaitingForPlayer, goalReviewPending, goalReviewBlocked,
        manualControlActive, autoMode, goalPlanningState.Completion.Revision, JsonSerializer.Serialize(goalPlanningState.Completion.Conditions),
        JsonSerializer.Serialize(Executor.SnapshotTasks()), JsonSerializer.Serialize(CompanionCrafting.InventoryCounts(Loadout)),
        CompanionNpc?.Center.X ?? 0, CompanionNpc?.Center.Y ?? 0);
}
