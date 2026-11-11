using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string NaturalOrderRequest = "请用自己的材料新做4个木平台，再带着至少4个平台、10木材和5石块走到我身边。会合后才新做3个普通火把。最后把至少9木材、5石块、4木平台和3普通火把留在你背包里。原有成品不能抵新做数量。缺料只允许捡附近地上的原料，不碰箱子、不拆建原屋、不传送。";
    private const string NaturalBossRequest = "请根据你的背包和现场，先确认史莱姆王的真实召唤条件，自行准备好装备并合法召唤它。独自打败刚召唤的这一只，捡起它实际掉落的战利品，再告诉我。我留在附近吸引它，不攻击。不要打开箱子、采挖或改动原屋，不能传送。";
    private const string NaturalBossReturnRequest = "请根据你的背包和现场，先确认史莱姆王的真实召唤条件，自行准备装备并合法召唤它。独自打败刚召唤的这一只，捡起它实际掉落的战利品，步行回到我身边后汇报。受阻时根据现场自行调整做法。我留在附近吸引它，不攻击。不要打开箱子、采挖或改动原屋，不能传送。";
    private const string NaturalHouseRequest = "在原屋附近选一块合适的地面空地，先勘查和预检，再用你背包的材料独立新建一间外框10格宽、7格高的普通木屋。屋里要有铺满的普通木背景墙、木门、工作台、木椅和普通火把，游戏要判定为可供NPC入住。不要借用或改动原屋、家具和通路，不开箱、不采挖、不传送。实际建好并核实后再告诉我。";
    private static string NaturalFlashProfile => Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") ?? "";
    internal static bool NaturalFlashSelected => NaturalFlashProfile is "natural_ordered_flash" or "natural_boss_flash" or "natural_house_flash";
    internal static bool NaturalFlashObserverNoPickup => HouseMode && (NaturalFlashSelected || NaturalFlashProfile == BossLootSelectionNativeCase);
    private static bool NaturalFlashReplay => Environment.GetEnvironmentVariable("AITEAMMATE_NATURAL_REPLAY") == "1";
    private static bool NaturalBossReturnCase => NaturalFlashProfile == "natural_boss_flash" &&
        (NaturalBossFromScratch || Environment.GetEnvironmentVariable("AITEAMMATE_NATURAL_BOSS_RETURN") == "1");
    private static string NaturalRequest => NaturalFlashProfile switch
    {
        "natural_boss_flash" => NaturalBossFromScratch ? NaturalBossPreparationRequest : NaturalBossAltarReplay ?
            NaturalBossAltarReplayRequest : NaturalBossReturnCase ? NaturalBossReturnRequest : NaturalBossRequest,
        "natural_house_flash" => NaturalHouseRequest,
        _ => NaturalOrderRequest
    };
    private bool naturalFlashReady, naturalFlashRunning, naturalFlashFinished;
    private int naturalFlashLimit, naturalFlashSeconds, naturalPrimaryStart, naturalCombatStart, naturalFrames;
    private DateTime naturalFlashStarted;
    private long naturalOutcomeSequence;
    private int naturalMadePlatforms, naturalMadeTorches, naturalGelPicked;
    private bool naturalCarryingReturn, naturalPhysicalViolation;
    private ShelterWorldAudit? naturalHouseAudit;
    private (Terraria.ModLoader.ModNPC Actor, bool PreviousDamageFlag)? naturalBossReadyProtection;
    private Dictionary<int, int> naturalStock = new();
    private readonly List<object> naturalEvents = new();
    private readonly List<AgentOutcome> naturalOutcomes = new();
    private string NaturalResultPath => Path.Combine(Main.SavePath, "AITeammate", "natural-flash-result.json");
    private string NaturalStartPath => Path.Combine(Main.SavePath, "AITeammate", "natural-flash-start.txt");
    private int NaturalRequests => PrimaryDecisionRequestCount - naturalPrimaryStart + CombatDecisionRequestCount - naturalCombatStart;
    private bool NaturalFlashWithinTime => DateTime.UtcNow - naturalFlashStarted < TimeSpan.FromSeconds(naturalFlashSeconds);
    private bool NaturalFlashCanPlan => HouseMode && !NaturalFlashReplay && naturalFlashRunning && !naturalFlashFinished && NaturalFlashWithinTime && NaturalRequests < naturalFlashLimit;

    private void BeginNaturalFlashAcceptance()
    {
        ReleaseNaturalBossReadyProtection();
        Stop();
        Memory.Clear();
        LlmConversationStore.ResetAll();
        agentJournal.Clear();
        Executor.ResetNavigationTelemetry();
        naturalPrimaryStart = PrimaryDecisionRequestCount;
        naturalCombatStart = CombatDecisionRequestCount;
        CombatPlannerEnabled = false;
        LlmSettings? settings = GetLlmSettings();
        bool modeValid = NaturalFlashReplay ? settings == null && NaturalFlashProfile != "natural_ordered_flash" : settings?.Model == "deepseek-flash";
        if (!int.TryParse(Environment.GetEnvironmentVariable("AITEAMMATE_NATURAL_REQUEST_LIMIT"), out naturalFlashLimit) ||
            (NaturalFlashReplay ? naturalFlashLimit != 0 : naturalFlashLimit is < 1 or > 20) ||
            !int.TryParse(Environment.GetEnvironmentVariable("AITEAMMATE_NATURAL_SECONDS"), out naturalFlashSeconds) || naturalFlashSeconds is < 1 or > 600 ||
            !modeValid || !NaturalBossPreparationConfig() || File.Exists(NaturalStartPath))
        { FinishNaturalFlash("uncovered_config", "Use a fresh isolated save with explicit bounds, configured deepseek-flash for natural mode or disabled LLM for replay."); return; }

        NPC npc = CompanionNpc!;
        Player player = Main.LocalPlayer;
        object? fixture = NaturalFlashProfile switch
        {
            "natural_boss_flash" => PrepareNaturalBoss(npc, player),
            "natural_house_flash" => PrepareNaturalHouse(npc, player),
            _ => PrepareNaturalOrder(npc, player)
        };
        if (fixture == null)
        { FinishNaturalFlash("uncovered_fixture", "The real-house starting geometry, stock or native evidence baseline was unavailable."); return; }
        naturalStock = CompanionCrafting.InventoryCounts(Loadout);
        naturalHouseAudit = ShelterWorldAudit.Capture(3220, 390);
        naturalOutcomeSequence = agentJournal.LastSequence;
        naturalEvents.Add(new { kind = "ready", model = settings?.Model, maxTokens = settings?.MaxTokens, thinkingMode = settings?.ThinkingMode, reasoningEffort = settings?.ReasoningEffort,
            actorX = npc.Center.X, playerX = player.Center.X, actorFeet = npc.Bottom.Y, playerFeet = player.Bottom.Y, directPassage = OrderedGoalPassage(npc, player),
            stock = naturalStock, fixture,
            protection = naturalHouseAudit.CheckPreservation() });
        if (NaturalFlashProfile == "natural_boss_flash" && npc.ModNPC is { } actor)
        {
            // READY can wait on a human or window activation. Begin the measured
            // run from the disclosed health, then use ordinary combat damage.
            naturalBossReadyProtection = (actor, npc.dontTakeDamage);
            npc.dontTakeDamage = true;
        }
        naturalFlashReady = true;
        SaveNaturalFlash(NaturalBossPreparation ? "awaiting_preparation_survey" : "ready", "");
    }

    private object? PrepareNaturalOrder(NPC npc, Player player)
    {
        npc.Center = Stand(3206, 393) - new Vector2(4, 0);
        player.Center = Stand(3208, 393) + new Vector2(4, 0);
        npc.velocity = player.velocity = Vector2.Zero;
        if (OrderedGoalPassage(npc, player) || !TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, 0, npc.width)) return null;
        Loadout.AddItem(ItemID.Wood, 12);
        Loadout.AddItem(ItemID.StoneBlock, 5);
        Loadout.AddItem(ItemID.WoodPlatform, 2);
        Loadout.AddItem(ItemID.Torch, 3);
        int gel = Item.NewItem(new EntitySource_Misc("NaturalFlashFixture"), Stand(3204, 393), ItemID.Gel, 1);
        if (gel < 0 || gel >= Main.maxItems || !Main.item[gel].active) return null;
        Main.item[gel].velocity = Vector2.Zero;
        Main.item[gel].noGrabDelay = 0;
        return new { groundGel = new { slot = gel, item = Main.item[gel].type, count = Main.item[gel].stack } };
    }

    private void TickNaturalFlashAcceptance()
    {
        if (!HouseMode || naturalFlashFinished || Main.gameMenu || !naturalFlashReady) return;
        Player player = Main.LocalPlayer;
        player.immune = true;
        player.immuneTime = 600;
        if (NaturalFlashProfile == "natural_boss_flash") MaintainNaturalBossObserver(player);
        else foreach (NPC hostile in Main.npc) if (hostile.active && !hostile.friendly) hostile.active = false;
        if (naturalFlashReady && !naturalFlashRunning)
        {
            if (NaturalBossPreparation && !naturalBossPreparationSurveyReady)
            {
                if (!JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Status()).GetProperty("ready").GetBoolean()) return;
                if (CompanionNpc is not { } surveyActor || !SurveyNaturalBossPreparation(surveyActor))
                { FinishNaturalFlash("uncovered_preparation_survey", "Companion, clean stock or required indexed natural samples are unavailable."); return; }
                naturalBossPreparationSurveyReady = true;
                SaveNaturalFlash("ready", "");
            }
            if (!File.Exists(NaturalStartPath)) return;
            if (NaturalBossPreparation && !NaturalBossPreparationStockAtStart())
            { FinishNaturalFlash("uncovered_fixture", "Unexpected Ruby or Crown appeared before START."); return; }
            if (naturalBossReadyProtection is { } warmup)
            {
                if (!ReferenceEquals(CompanionNpc?.ModNPC, warmup.Actor))
                { FinishNaturalFlash("uncovered_fixture", "The prepared companion changed before START."); return; }
                ReleaseNaturalBossReadyProtection();
                NPC prepared = warmup.Actor.NPC;
                naturalEvents.Add(new { kind = "boss_fixture_start", tick = Main.GameUpdateCount,
                    life = prepared.life, maxLife = prepared.lifeMax, invulnerable = prepared.dontTakeDamage });
            }
            naturalFlashStarted = DateTime.UtcNow;
            naturalFlashRunning = true;
            if (NaturalFlashReplay) StartNaturalReplay();
            else
            {
                if (NaturalBossFromScratch) Executor.SetHold(false);
                HandlePlayerMessage(player, NaturalRequest);
            }
            if (naturalFlashFinished) return;
            SaveNaturalFlash("running", "");
            return;
        }
        if (!naturalFlashRunning) return;
        if (!CheckCapturedBossReplayIntent()) return;
        if (CompanionNpc is not { active: true } npc)
        { FinishNaturalFlash("failed_runtime", "Companion is no longer active."); return; }
        if (Executor.NavigationTeleportCount != 0 || CombatDecisionRequestCount != naturalCombatStart || NaturalFlashReplay && NaturalRequests != 0)
        { FinishNaturalFlash("failed_runtime", "Unexpected teleport or model request."); return; }
        if (decisionFailures > 0 || dispatchRepairs.LastCode == "protocol_parse_rejected")
        { FinishNaturalFlash("first_api_error", outputLimitGate.IsBlocked ? "output_limit" :
            plannerApiFailures.LastFailure is { } failure ? $"HTTP {failure.httpStatus}" : dispatchRepairs.LastCode == "protocol_parse_rejected" ? "protocol_parse_rejected" : "api_or_transport_failure"); return; }

        naturalFrames++;
        var fresh = agentJournal.Outcomes.Where(outcome => outcome.Sequence > naturalOutcomeSequence).ToArray();
        var stock = CompanionCrafting.InventoryCounts(Loadout);
        foreach (AgentOutcome outcome in fresh)
        {
            naturalOutcomeSequence = outcome.Sequence;
            naturalOutcomes.Add(outcome);
            if (NaturalFlashProfile != "natural_ordered_flash") continue;
            if (outcome.Status != "succeeded") continue;
            int Delta(int id) => stock.GetValueOrDefault(id) - naturalStock.GetValueOrDefault(id);
            if (outcome.Task.Action == AIActionType.Craft)
            {
                int platforms = Delta(ItemID.WoodPlatform), torches = Delta(ItemID.Torch);
                if (platforms > 0 && !naturalCarryingReturn) naturalMadePlatforms += platforms;
                else if (torches > 0 && naturalCarryingReturn) naturalMadeTorches += torches;
                else naturalPhysicalViolation = true;
                naturalPhysicalViolation |= naturalMadePlatforms > 4 || naturalMadeTorches > 3;
            }
            else if (outcome.Task.Action == AIActionType.Come)
            {
                float horizontal = Math.Abs(npc.Center.X - player.Center.X), vertical = Math.Abs(npc.Bottom.Y - player.Bottom.Y);
                bool grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
                bool passage = OrderedGoalPassage(npc, player);
                if (naturalMadePlatforms == 4 && stock.GetValueOrDefault(ItemID.WoodPlatform) >= 4 &&
                    stock.GetValueOrDefault(ItemID.Wood) >= 10 && stock.GetValueOrDefault(ItemID.StoneBlock) >= 5 &&
                    horizontal <= 48 && vertical <= 24 && grounded && passage)
                    naturalCarryingReturn = true;
                naturalEvents.Add(new { kind = "return_observation", outcome.Sequence, horizontal, vertical, grounded, passage, stock });
            }
            else if (outcome.Task.Action == AIActionType.Pickup)
            {
                naturalGelPicked += Math.Max(0, Delta(ItemID.Gel));
                naturalPhysicalViolation |= naturalGelPicked > 1 || stock.Keys.Any(id => id != ItemID.Gel && Delta(id) > 0);
            }
        }
        if (NaturalFlashProfile == "natural_boss_flash")
        {
            if (NaturalBossPreparation) TrackNaturalBossPreparation(npc, fresh, stock);
            TrackNaturalBoss(npc, player, fresh);
            TrackCapturedBossFrame(npc, player);
            if (NaturalFlashReplay && FiringStandDiagnosticEnabled)
                TrackFiringStandDiagnostic(npc, ContinuousField("attackLastTarget") as NPC);
        }
        if (NaturalFlashProfile == "natural_house_flash") TrackNaturalHouse();
        if (fresh.Any(outcome => outcome.Task.Action is AIActionType.Craft or AIActionType.Pickup))
            naturalEvents.Add(new { kind = "stock_delta", sequence = naturalOutcomeSequence, before = naturalStock, after = stock });
        naturalStock = stock;
        if (NaturalFlashProfile == "natural_boss_flash" && player.dead)
        { FinishNaturalFlash("invalid_fixture_observer_dead", "The non-attacking spectator died; do not classify this as a boss kill or an AI combat loss."); return; }
        if (naturalPhysicalViolation)
        { FinishNaturalFlash("instruction_violation", "Observed an action or result outside the case constraints."); return; }
        if (goalStatus == "completed")
        {
            bool physical = NaturalFlashProfile switch
            {
                "natural_boss_flash" => VerifyNaturalBoss(npc, player),
                "natural_house_flash" => VerifyNaturalHouse(),
                _ => naturalMadePlatforms == 4 && naturalCarryingReturn && naturalMadeTorches == 3 && naturalGelPicked == 1 &&
                    stock.GetValueOrDefault(ItemID.Wood) >= 9 && stock.GetValueOrDefault(ItemID.StoneBlock) >= 5 &&
                    stock.GetValueOrDefault(ItemID.WoodPlatform) >= 4 && stock.GetValueOrDefault(ItemID.Torch) >= 3 &&
                    NaturalCompletionProof(NaturalOrderRequest, "ordered_actions")
            };
            bool verified = physical &&
                naturalHouseAudit?.CheckPreservation() is { preserved: true, coverageComplete: true, diffCount: 0 };
            FinishNaturalFlash(verified ? NaturalFlashReplay ? "passed_replay" : "passed" : "incomplete_proof",
                verified ? "" : "The case-specific native facts, goal contract and original-house preservation are required.");
            return;
        }
        if (NaturalFlashReplay && (fresh.Length > 0 || BossReplayAttackDue || capturedBossLatency != null)) AdvanceNaturalReplay(fresh);
        if (naturalFlashFinished) return;
        if (!NaturalFlashWithinTime || goalWaitingForPlayer || goalStatus == "cancelled" || dispatchRepairs.IsPaused ||
            !NaturalFlashReplay && NaturalRequests >= naturalFlashLimit && primaryCancellation == null && !Executor.HasCurrentPlan && !Executor.IsInCombat)
        { FinishNaturalFlash("incomplete", !NaturalFlashWithinTime ? "wall_deadline" : goalWaitingForPlayer ? "waiting_for_player" :
            goalStatus == "cancelled" ? "cancelled" : dispatchRepairs.IsPaused ? "repair_budget" : "request_budget_exhausted"); return; }
        if (fresh.Length > 0 || naturalFrames % 120 == 0) SaveNaturalFlash("running", "");
    }

    private bool NaturalCompletionProof(string request, string conditionType)
    {
        JsonElement proof = JsonSerializer.SerializeToElement(lastCompletedGoal);
        return proof.ValueKind == JsonValueKind.Object && proof.GetProperty("sourcePlayerRequest").GetString() == request &&
            proof.GetProperty("worldId").GetInt32() == Main.worldID &&
            proof.GetProperty("evidence").GetProperty("Verified").GetBoolean() &&
            proof.GetProperty("conditions").EnumerateArray().Any(condition => condition.GetProperty("type").GetString() == conditionType);
    }

    private bool ValidateNaturalFlashPlan(AIPlan plan)
    {
        if (!naturalFlashRunning || naturalFlashFinished) return true;
        bool Allowed(AIPlan task) => task.Action is AIActionType.None or AIActionType.Inspect or AIActionType.Explore or
            AIActionType.Come or AIActionType.Wait or AIActionType.Follow || (NaturalFlashProfile switch
            {
                "natural_boss_flash" => NaturalBossAltarReplay ? AllowNaturalBossPreparationAction(task) :
                    task.Action is AIActionType.Equip or AIActionType.Use or AIActionType.Attack or AIActionType.Retreat or AIActionType.Pickup ||
                    NaturalBossFromScratch && AllowNaturalBossPreparationAction(task),
                "natural_house_flash" => AllowNaturalHousingAction(plan, task),
                _ => task.Action is AIActionType.Craft or AIActionType.Pickup
            });
        if (Allowed(plan) && plan.Steps.All(Allowed)) return true;
        FinishNaturalFlash("instruction_violation", "A forbidden operation was proposed before execution.");
        return false;
    }

    private void FinishNaturalFlash(string status, string error)
    {
        ReleaseNaturalBossReadyProtection();
        naturalFlashFinished = true;
        SaveNaturalFlash(status, error);
        naturalFlashRunning = naturalFlashReady = false;
        Stop();
    }

    private void ReleaseNaturalBossReadyProtection()
    {
        if (naturalBossReadyProtection is not { } warmup) return;
        NPC actor = warmup.Actor.NPC;
        if (ReferenceEquals(actor.ModNPC, warmup.Actor)) actor.dontTakeDamage = warmup.PreviousDamageFlag;
        naturalBossReadyProtection = null;
    }

    private void SaveNaturalFlash(string status, string error)
    {
        var preservation = naturalFlashFinished ? naturalHouseAudit?.CheckPreservation() : null;
        var terminalGoalCompletion = naturalFlashFinished && goalPlanningState.HasGoal ? CurrentGoalCompletion() : null;
        var terminalGoalPreservation = terminalGoalCompletion != null ? PreservationEvidence.DebugSnapshot() : null;
        if (naturalFlashFinished)
        {
            SaveCapturedBossTrace(status, error);
            if (NaturalFlashReplay && NaturalFlashProfile == "natural_boss_flash" && FiringStandDiagnosticEnabled)
                SaveFiringStandDiagnostic(status, error);
        }
        File.WriteAllText(NaturalResultPath, JsonSerializer.Serialize(new
        {
            status, error, profile = NaturalFlashProfile, replay = NaturalFlashReplay, request = NaturalRequest, frames = naturalFrames,
            startedAt = naturalFlashStarted, elapsedSeconds = naturalFlashRunning ? (DateTime.UtcNow - naturalFlashStarted).TotalSeconds : 0,
            requests = PrimaryDecisionRequestCount - naturalPrimaryStart, combatRequests = CombatDecisionRequestCount - naturalCombatStart,
            requestLimit = naturalFlashLimit, secondsLimit = naturalFlashSeconds, warps = Executor.NavigationTeleportCount,
            naturalMadePlatforms, naturalCarryingReturn, naturalMadeTorches, naturalGelPicked, naturalPhysicalViolation,
            goalStatus, activeGoal, currentQueue = Executor.SnapshotTasks(), lastCompletedGoal, lastReceipt = decisionReceipts.Last,
            contract = goalPlanningState.Completion.Conditions, terminalGoalCompletion, terminalGoalPreservation,
            stock = naturalStock, events = naturalEvents, outcomes = naturalOutcomes,
            preservation = preservation == null ? null : new { preservation.protectedCells, preservation.coverageComplete, preservation.preserved, preservation.diffCount },
            preservationDetails = preservation,
            boss = NaturalFlashProfile == "natural_boss_flash" ? BossEvidence.Snapshot() : null,
            bossPreparation = !NaturalBossPreparation ? null : new { mode = NaturalBossPreparationMode, naturalRubyDrops,
                surveyReady = naturalBossPreparationSurveyReady, naturalRubySequence, naturalMetalCrownSequence, naturalMetalCrownType,
                naturalSlimeCrownSequence, naturalPreparedSummonSequence },
            room = naturalNewRoom,
            scope = NaturalFlashReplay ? "Zero-API original-house runtime replay; controlled continuation, not a fresh model run." :
                "Fresh Flash natural request, isolated original house, disclosed initial setup; all task actions are dispatched by the model."
        }));
        if (naturalFlashFinished) SaveBossLootSelectionBatch(status, error);
    }
}
