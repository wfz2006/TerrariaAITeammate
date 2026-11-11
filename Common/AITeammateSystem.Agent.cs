using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private readonly DecisionGate decisionGate = new();
    private readonly PlannerOutputLimitGate outputLimitGate = new();
    private readonly AgentJournal agentJournal = new();
    private readonly GoalPlanningState goalPlanningState = new();
    internal readonly GoalVisitEvidence VisitEvidence = new();
    internal readonly GoalStructurePreservation PreservationEvidence = new();
    internal long GoalStartSequence => goalStartSequence;
    internal string GoalIdentity => goalPlanningState.InstanceId;
    public object ReadWorldObservation(string id) => goalPlanningState.WorldObservations.Read(id,
        Main.worldID, WorldKnowledgeObserver.LoadEpoch, goalStartSequence);
    internal bool RequiresStructurePreservation =>
        goalPlanningState.Completion.Conditions.Any(condition => condition.Type == "structure_preserved") ||
        GoalCompletionState.PlayerRequiresPreservation(goalPlanningState.SourcePlayerRequest);
    private static readonly Regex TemporaryPlatformClause = new(
        @"(?:^|[，,。！？!?；;\r\n])\s*(?:请\s*)?(?:允许|可以)[^，,。！？!?；;\r\n]{0,32}?(?:搭建?|放置|架设)\s*(?:少量|几块|几格)?\s*临时(?:木?平台|脚手架)",
        RegexOptions.CultureInvariant);
    internal bool MayPreviewTemporaryPlatforms => TemporaryPlatformClause.IsMatch(goalPlanningState.SourcePlayerRequest ?? "");
    public GoalConstructionProtection InspectConstructionProtection(int x, int y, int width, int height) =>
        PreservationEvidence.ReadConstructionProtection(
            RequiresStructurePreservation,
            x, y, width, height, Main.worldID, WorldKnowledgeObserver.LoadEpoch, goalStartSequence, activeGoal, Main.GameUpdateCount);
    private readonly TaskRetryLedger taskRetries = new();
    private readonly PlanDecisionReceipts decisionReceipts = new();
    private readonly DispatchRepairBudget dispatchRepairs = new();
    private CancellationTokenSource? primaryCancellation;
    private CancellationTokenSource? combatCancellation;
    private DecisionTicket primaryTicket;
    private string? pendingCommand;
    private string? pendingPausedQuestion;
    private DateTime nextDecisionAt = DateTime.MinValue;
    private bool worldReady;
    private bool reviewAfterEachStep = true;
    private string goalCriteria = "";
    private string goalCompletionAction = "";
    private long goalStartSequence;
    private string goalStatus = "idle";
    private string decisionReason = "";
    private int decisionFailures;
    private int emptyDecisions;
    private int repeatedFailurePlans;
    private object? lastCompletedGoal;

    public void SynchronizeGoalQueue()
        => goalPlanningState.SynchronizeQueue(Executor.SnapshotTasks(), Executor.HasCurrentPlan);

    private bool HasActiveModelGoal => worldReady && !manualControlActive && goalPlanningState.HasGoal;

    internal bool BossReviewPending => HasActiveModelGoal && !goalWaitingForPlayer && !dispatchRepairs.IsPaused &&
        !plannerApiFailures.IsBlocked && !outputLimitGate.IsBlocked &&
        (goalReviewPending || primaryCancellation != null && primaryTicket.Kind == "goal");

    internal bool PlanningSafetyPending => BossReviewPending || worldReady && !manualControlActive &&
        primaryCancellation != null && primaryTicket.Kind == "chat";

    internal GoalNativeConditionResult ReadOrderedActions(GoalCondition condition)
        => goalPlanningState.Completion.ReadOrderedActions(condition);

    internal bool CanFinishCome(NPC npc, Player player) =>
        !HasActiveModelGoal || !goalPlanningState.Completion.ExpectsOrderedReturn ||
        player == Main.LocalPlayer && Math.Abs(npc.Bottom.Y - player.Bottom.Y) <= 24f &&
        Collision.CanHitLine(npc.position, npc.width, npc.height, player.position, player.width, player.height);

    internal void RecordOrderedReturn()
    {
        if (HasActiveModelGoal && goalPlanningState.Completion.RecordOrderedReturn(Loadout.CountItem))
            dispatchRepairs.ObserveProgress();
    }

    internal string? OrderedCraftBlockReason(int itemId) => HasActiveModelGoal
        ? goalPlanningState.Completion.OrderedCraftBlockReason(itemId) : null;

    internal void RecordOrderedCraft(int itemId, int producedCount)
    {
        if (HasActiveModelGoal && goalPlanningState.Completion.RecordOrderedCraft(itemId, producedCount))
            dispatchRepairs.ObserveProgress();
    }

    // Called at the actual start of every queued operation, after any prefix has
    // changed the world. Checking a whole future tail against today's state is wrong.
    public TaskRetryDecision CheckTaskStart(AIPlan task)
        => taskRetries.EvaluateStart(HasActiveModelGoal, TaskFingerprint(task), () => Executor.CaptureRetryConditions(task));

    public void NotifyTaskStartRejected(AIPlan task, string code, string reason)
    {
        if (!HasActiveModelGoal) return;
        string detail = $"Task did not start (executed=false): {task.Action}. {reason}";
        decisionReceipts.Record(task, "executor_start", "rejected", code, detail,
            Executor.SnapshotTasks(), Executor.HoldPosition);
        decisionGate.StateChanged();
        // This was admission, not another physical attempt: do not call
        // NotifyTaskResult/RecordFailure or invent an execution outcome.
        ScheduleDecisionRepair(code, detail, "executor_start", null);
        TestLog("agent start rejected executed=false code=" + code + " task=" + TaskFingerprint(task));
    }

    public GoalCompletionEvaluation CurrentGoalCompletion()
    {
        var conditions = goalPlanningState.Completion.Conditions;
        return AgentCompletionEvidence.Evaluate(conditions,
            GoalWorldObserver.Capture(this, conditions, goalBossDeathBoundary));
    }

    private object CompletionObservation()
    {
        GoalCompletionEvaluation result = CurrentGoalCompletion();
        return new { verified = result.Verified, spec_count = result.SpecCount, status = result.Status,
            observedAtTick = Main.GameUpdateCount, source = "live_world",
            conditions = result.Conditions.Select(c => new { index = c.Index, type = c.Type, status = c.Status, detail = c.Detail }).ToArray() };
    }

    public object PlanningContext => new
    {
        version = decisionGate.State,
        world = decisionGate.World,
        intent = decisionGate.Intent,
        objective = activeGoal,
        success_criteria = goalCriteria,
        completion_action = goalCompletionAction,
        completion_target = goalCompletionTarget,
        evidence_start_sequence = goalStartSequence,
        sourcePlayerRequest = goalPlanningState.SourcePlayerRequest,
        latestDesign = goalPlanningState.LatestDesign,
        interruptedPlan = goalPlanningState.InterruptedPlan,
        savedGoalHistory = restoredGoalHistory,
        awaitingExplicitContinue = restoredGoalAwaitingContinue,
        workingPlan = goalPlanningState.WorkingPlan,
        acceptedExecutionPlan = goalPlanningState.AcceptedExecutionPlan,
        executionPlans = goalPlanningState.ExecutionPlans,
        goalEvidence = goalPlanningState.Evidence,
        recovery = goalPlanningState.Recovery,
        completion_contract = new { revision = goalPlanningState.Completion.Revision,
            revision_reason = goalPlanningState.Completion.RevisionReason,
            conditions = goalPlanningState.Completion.Conditions },
        completion_evidence = CompletionObservation(),
        preservation = PreservationEvidence.Snapshot(Main.worldID, WorldKnowledgeObserver.LoadEpoch, goalStartSequence, activeGoal),
        acknowledgedOutcomeSequence = goalPlanningState.AcknowledgedOutcomeSequence,
        worldObservations = goalPlanningState.WorldObservations.Directory(Main.worldID, WorldKnowledgeObserver.LoadEpoch, goalStartSequence),
        lastCompletedGoal,
        lastDecisionReceipt = decisionReceipts.Last,
        dispatchRepair = new { rejections = dispatchRepairs.Rejections, lastCode = dispatchRepairs.LastCode,
            paused = dispatchRepairs.IsPaused, automaticRepairsRemaining = dispatchRepairs.RemainingAutomaticRepairs },
        status = goalStatus,
        last_decision_reason = decisionReason,
        awaiting_review = goalReviewPending,
        api_failures = decisionFailures,
        api_retry_blocked = outputLimitGate.IsBlocked,
        last_api_failure = outputLimitGate.LastFailure,
        apiFailure = plannerApiFailures.LastFailure,
        apiFailureBlocked = plannerApiFailures.IsBlocked,
        completedConstructionSteps = agentJournal.Outcomes.Where(o => o.Sequence > goalStartSequence &&
            o.Status == "succeeded" && o.Task.Action == AIActionType.Build)
            .Select(o => new { task = o.Task, actualResult = o.Result }).ToArray(),
        outcomes = agentJournal.Outcomes.Select(outcome => new
        {
            sequence = outcome.Sequence,
            task = new
            {
                type = outcome.Task.Action.ToString().ToLowerInvariant(),
                target = outcome.Task.Target, item = outcome.Task.Item, recipe_id = outcome.Task.RecipeId,
                count = outcome.Task.Count, blueprint = outcome.Task.Blueprint,
                x = outcome.Task.X, y = outcome.Task.Y, width = outcome.Task.Width, height = outcome.Task.Height
            },
            status = outcome.Status,
            observationId = goalPlanningState.WorldObservations.IdFor(outcome.Sequence, Main.worldID, WorldKnowledgeObserver.LoadEpoch, goalStartSequence),
            result = outcome.Result
        }).ToArray()
    };

    public override void ClearWorld()
    {
        ResetAgentRuntime();
        Loadout.Reset();
    }

    private void CancelPrimaryDecision()
    {
        CancellationTokenSource? cancellation = primaryCancellation;
        primaryCancellation = null;
        if (cancellation == null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void InvalidateAgentIntent()
    {
        Executor.ClearBossReview();
        dispatchRepairs.Reset();
        plannerApiFailures.Clear();
        CancelPrimaryDecision();
        CancelCombatDecision();
        decisionGate.NewIntent();
        combatCallId++;
        pendingCommand = null;
        pendingPausedQuestion = null;
        nextDecisionAt = DateTime.MinValue;
        decisionFailures = 0;
        outputLimitGate.Clear();
        emptyDecisions = 0;
        repeatedFailurePlans = 0;
        LlmConversationStore.InvalidatePending();
    }

    private void ResetAgentRuntime()
    {
        worldReady = false;
        CancelPrimaryDecision();
        CancelCombatDecision();
        decisionGate.ResetWorld();
        agentJournal.Clear();
        goalPlanningState.Clear();
        ClearGoalCheckpoint();
        VisitEvidence.Clear();
        PreservationEvidence.Clear();
        lastCompletedGoal = null;
        BossEvidence.Clear();
        PrimaryDecisionRequestCount = CombatDecisionRequestCount = 0;
        goalCompletionTarget = "";
        goalBossDeathBoundary = 0;
        mainThreadQueue.Clear();
        Executor.Clear();
        Executor.ResetExplorationMemory();
        activeGoal = lastGoalResult = goalStepLog = goalCriteria = goalCompletionAction = decisionReason = "";
        goalStartSequence = 0;
        goalStatus = "idle";
        autoMode = goalReviewPending = goalReviewBlocked = goalWaitingForPlayer = false;
        manualControlActive = true;
        autoPlanInFlight = goalPlanInFlight = combatPlanInFlight = false;
        autoTestStarted = false;
        pendingCommand = null;
        pendingPausedQuestion = null;
        taskRetries.Clear();
        decisionReceipts.Clear();
        dispatchRepairs.Reset();
        repeatedFailurePlans = decisionFailures = emptyDecisions = 0;
        outputLimitGate.Clear();
        nextDecisionAt = lastAutoPlanAt = lastCommandAt = lastGoalPlanAt = DateTime.MinValue;
        plannerApiFailures.Clear();
        lastCombatPlanAt = DateTime.MinValue;
        lastCombatSignature = "";
        combatCallId++;
        goalGeneration++;
        LlmConversationStore.ResetAll();
    }

    public void HandlePlayerMessage(Player player, string message, bool fromChat = false)
    {
        if (!worldReady || Main.netMode != NetmodeID.SinglePlayer || string.IsNullOrWhiteSpace(message))
            return;
        if (TryContinueRestoredGoal(player, message, fromChat)) return;

        // Exact shortcut grammar only. Complex conversation stays on the normal
        // model path; this is never called for autonomous feedback/replan requests.
        if (BasicCompanionCommand.TryParseChat(message, out AIPlan? basicCommand))
        {
            lastCommandAt = DateTime.UtcNow;
            Memory.Add($"玩家说：{message}");
            if (!fromChat) Main.NewText($"你：{message}", Color.White);
            ExecuteBasicCommand(player, basicCommand!);
            return;
        }

        // No model is available to interpret negation, order or constraints.
        // Do not turn a question into a physical task or disturb an existing goal.
        if (GetLlmSettings() == null)
        {
            Memory.Add($"玩家说：{message}");
            if (!fromChat) Main.NewText($"你：{message}", Color.White);
            LastReply = CompanionControlsUI.Text(ModContent.GetInstance<AIConfig>().UseLlm ? "MissingKey" : "ModelDisabled");
            Main.NewText("小星：" + LastReply, Color.LightBlue);
            Memory.Add("小星：" + LastReply);
            return;
        }

        InvalidateAgentIntent();
        manualControlActive = false;
        lastCommandAt = DateTime.UtcNow;
        if (CompanionNpc == null) SpawnCompanion(player);
        Memory.Add($"玩家说：{message}");
        if (!fromChat) Main.NewText($"你：{message}", Color.White);

        pendingCommand = message;
        StartPrimaryDecision("chat", message);
        if (primaryCancellation != null)
            Main.NewText(CompanionControlsUI.Text("Thinking"), Color.LightBlue);
    }

    internal void OnModelConfigChanged(bool connectionChanged, bool useCombatLlm)
    {
        if (!CanUseCompanionControls) return;
        if (connectionChanged)
        {
            bool cancelledChat = pendingCommand != null || pendingPausedQuestion != null ||
                (primaryCancellation != null && primaryTicket.Kind == "paused_answer");
            bool resumeReview = primaryCancellation != null && primaryTicket.Kind == "goal" && HasActiveModelGoal;
            InvalidateAgentIntent();
            if (resumeReview) goalReviewPending = true;
            if (cancelledChat) Main.NewText(CompanionControlsUI.Text("SettingsChanged"), Color.LightBlue);
        }
        else if (!useCombatLlm)
        {
            CancelCombatDecision();
        }
    }

    private void TickAgentPlanning()
    {
        if (primaryCancellation != null || DateTime.UtcNow < nextDecisionAt || dispatchRepairs.IsPaused)
            return;
        Player player = Main.player[Main.myPlayer];
        if (!player.active || player.dead || CompanionNpc == null)
            return;
        if (pendingCommand != null)
        {
            StartPrimaryDecision("chat", pendingCommand);
            return;
        }
        if (pendingPausedQuestion != null)
        {
            if (Executor.HoldPosition) StartPrimaryDecision("paused_answer", pendingPausedQuestion);
            else pendingPausedQuestion = null;
            return;
        }
        if (Executor.HoldPosition || Executor.IsInCombat || Executor.IsAutoDefending)
            return;
        if (!string.IsNullOrWhiteSpace(activeGoal))
        {
            if (goalReviewPending && !goalWaitingForPlayer)
                StartPrimaryDecision("goal");
            return;
        }
        if (!autoMode || Executor.IsBusy || DateTime.UtcNow - lastCommandAt < TimeSpan.FromSeconds(3) ||
            DateTime.UtcNow - lastAutoPlanAt < TimeSpan.FromSeconds(8))
            return;
        lastAutoPlanAt = DateTime.UtcNow;
        StartPrimaryDecision("auto");
    }

    private void StartPrimaryDecision(string kind, string message = "")
    {
        if (HouseMode && !HousePlanningAllowed) return;
        if (System.Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") == "complex" && !ComplexAcceptanceRequestAllowed(false)) return;
        LlmSettings? settings = GetLlmSettings();
        if (settings == null || !worldReady) return;
        if (NaturalFlashSelected && (!NaturalFlashCanPlan || settings.Model != "deepseek-flash")) return;
        string configuration = PrimaryDecisionConfiguration(settings) + "/policy=" + System.Text.Json.JsonSerializer.Serialize(Executor.Policy);
        if (!plannerApiFailures.CanRequest(configuration) || !outputLimitGate.CanRequest(configuration, decisionGate.State))
        {
            nextDecisionAt = DateTime.UtcNow.AddSeconds(2);
            return;
        }
        CancelPrimaryDecision();
        Player player = Main.player[Main.myPlayer];
        Terraria.ModLoader.ModNPC? companionAtRequest = CompanionNpc?.ModNPC;
        string state = AIPlanner.BuildState(player, Executor, Memory);
        string goal = activeGoal, result = lastGoalResult;
        bool blocked = goalReviewBlocked;
        bool debug = Terraria.ModLoader.ModContent.GetInstance<AIConfig>().DebugLog;
        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        primaryCancellation = cancellation;
        DecisionTicket ticket = primaryTicket = decisionGate.Begin(kind);
        PrimaryDecisionRequestCount++;
        TestModelRequest? testRequest = BeginTestModelRequest(kind, ticket.Request);
        Action<LlmUsage>? testUsage = testRequest == null ? null : testRequest.CaptureUsage;
        // One observation-informed answer per paused question. Errors or stale
        // replies cannot turn this into a second autonomous planning loop.
        if (kind == "paused_answer") pendingPausedQuestion = null;
        goalReviewPending = false;
        TestLog($"agent request kind={kind} request={ticket.Request} state={ticket.State} goal={goal}");

        _ = Task.Run(async () =>
        {
            try
            {
                var planner = new AIPlanner();
                AIPlan plan = kind == "goal"
                    ? await planner.PlanGoalAsync(settings, state, goal, result, blocked, debug,
                        raw => Mod.Logger.Info(raw), cancellationToken: token, onUsage: testUsage).ConfigureAwait(false)
                    : await planner.PlanAsync(settings, state, kind == "paused_answer"
                        ? "这是暂停中的只读查询续答，不是新指令。只根据刚记录的观察回答原问题；tasks为空、goal为空，不恢复、取消或更改原任务，不再查询。若现有观察不足请如实说明。原问题：" + message
                        : message, debug, raw => Mod.Logger.Info(raw),
                        autonomous: kind == "auto", cancellationToken: token, onUsage: testUsage).ConfigureAwait(false);
                mainThreadQueue.Enqueue(() =>
                {
                    FinishTestModelRequest(testRequest);
                    ReleasePrimary(cancellation);
                    ApplyPrimaryPlanResponse(plan, kind, message, ticket, companionAtRequest);
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                mainThreadQueue.Enqueue(() =>
                {
                    FinishTestModelRequest(testRequest, cancelled: true);
                    ReleasePrimary(cancellation);
                });
            }
            catch (System.Text.Json.JsonException exception) when (exception is not LlmOutputLimitException)
            {
                mainThreadQueue.Enqueue(() =>
                {
                    FinishTestModelRequest(testRequest, exception);
                    ReleasePrimary(cancellation);
                    if (naturalFlashRunning)
                    { FinishNaturalFlash("first_api_error", "protocol_parse_rejected: " + exception.Message); return; }
                    if (!worldReady || !decisionGate.IsCurrent(ticket)) return;
                    HandleProtocolRejection(kind, message, exception.Message, ticket);
                });
            }
            catch (Exception exception)
            {
                mainThreadQueue.Enqueue(() =>
                {
                    FinishTestModelRequest(testRequest, exception);
                    ReleasePrimary(cancellation);
                    if (naturalFlashRunning)
                    { FinishNaturalFlash("first_api_error", exception is LlmOutputLimitException ? "output_limit" : exception.Message); return; }
                    if (!worldReady || !decisionGate.IsLatestRequest(ticket)) return;
                    bool terminalApiFailure = plannerApiFailures.RecordIfLatestTerminal(exception, configuration, decisionGate, ticket);
                    if (!terminalApiFailure && !decisionGate.IsCurrent(ticket)) return;
                    decisionFailures++;
                    if (kind == "chat") pendingCommand = message;
                    else if (kind != "paused_answer" && !string.IsNullOrWhiteSpace(activeGoal)) goalReviewPending = true;
                    if (exception is LlmOutputLimitException limit)
                    {
                        outputLimitGate.Record(configuration, Math.Clamp(settings.MaxTokens, 1400, 32768),
                            limit.Usage.CompletionTokens, limit.Usage.ReasoningTokens, ticket.State);
                        Executor.ClearBossReview();
                        nextDecisionAt = DateTime.UtcNow.AddSeconds(2);
                        Mod.Logger.Warn($"[AIAGENT] decision output_limit kind={kind} automaticRetry=false; goal retained.");
                        Main.NewText("小星：这次思考超出额度，目标已保留，暂不重复请求。", Color.OrangeRed);
                        return;
                    }
                    if (terminalApiFailure)
                    {
                        Executor.ClearBossReview();
                        goalReviewBlocked = true;
                        nextDecisionAt = DateTime.UtcNow.AddSeconds(30);
                        int status = plannerApiFailures.LastFailure!.httpStatus;
                        Mod.Logger.Warn($"[AIAGENT] decision terminal_api HTTP={status} kind={kind} automaticRetry=false; goal retained.");
                        Main.NewText($"小星：请求被服务端拒绝（HTTP {status}），目标已保留。处理账户或配置后再让我继续。", Color.OrangeRed);
                        return;
                    }
                    int delay = Math.Min(60, 1 << Math.Min(decisionFailures, 6));
                    nextDecisionAt = DateTime.UtcNow.AddSeconds(delay);
                    Mod.Logger.Warn($"[AIAGENT] decision failed kind={kind} " +
                        (kind == "paused_answer" ? "automaticRetry=false" : $"retryIn={delay}s") + $": {exception.Message}");
                    if (decisionFailures == 1)
                        Main.NewText(kind == "paused_answer" ? "小星：这次只读回答没有成功，仍保持暂停，不会重复请求。" :
                            "小星：这次思考没有成功，我稍后重新判断。", Color.OrangeRed);
                });
            }
        });
    }

    private bool ApplyPrimaryPlanResponse(AIPlan plan, string kind, string message, DecisionTicket ticket,
        Terraria.ModLoader.ModNPC? companionAtRequest)
    {
        bool currentState = worldReady && decisionGate.IsCurrent(ticket);
        // Death and SpawnCompanion can reuse an NPC slot without producing a task
        // outcome or new player intent. Such a reply describes a different actor.
        bool sameCompanion = ReferenceEquals(CompanionNpc?.ModNPC, companionAtRequest);
        if (!currentState || !sameCompanion)
        {
            bool changedCompanion = currentState && !sameCompanion;
            string code = changedCompanion ? "stale_companion_instance" : "stale_state_or_intent";
            AIPlanner.DiscardPlanHistory(plan);
            if (worldReady && ticket.World == decisionGate.World)
            {
                RecordTestDecision(kind, "superseded", code, ticket);
                decisionReceipts.Record(plan, kind, "superseded", code, changedCompanion
                    ? "Response was not applied because the companion present at request time is no longer the current instance."
                    : "Response was not applied because its request state or intent is no longer current.", Executor.SnapshotTasks(), Executor.HoldPosition, ticket);
            }
            if (worldReady && decisionGate.SameIntent(ticket))
            {
                if (kind == "chat") pendingCommand = message;
                else if (kind != "paused_answer" && !string.IsNullOrWhiteSpace(activeGoal))
                {
                    goalReviewPending = true;
                    if (changedCompanion) lastGoalResult = "同伴实例发生变化，本次旧回复未执行。请依据当前状态继续原目标。";
                }
            }
            TestLog($"agent stale result discarded request={ticket.Request} reason={code}");
            return false;
        }
        decisionFailures = 0;
        plannerApiFailures.Clear();
        outputLimitGate.Clear();
        nextDecisionAt = DateTime.UtcNow.AddMilliseconds(100);
        if (kind == "chat") pendingCommand = null;
        if (kind == "paused_answer") pendingPausedQuestion = null;
        if (kind == "auto") LastAutoPlan = $"{plan.Goal} | {plan.Reply}";
        return AIPlanner.ApplyAcceptedPlan(plan, decisionGate, ticket, () => ApplyPlan(plan,
            kind == "auto" ? "（自主）" : null, kind,
            sourcePlayerRequest: kind == "chat" ? message : null, ticket: ticket));
    }

    private void ReleasePrimary(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(primaryCancellation, cancellation)) return;
        primaryCancellation = null;
        cancellation.Dispose();
    }

    private static string PrimaryDecisionConfiguration(LlmSettings settings) =>
        string.Join("|", settings.BaseUrl, settings.Model, settings.MaxTokens,
            settings.ThinkingMode, settings.ReasoningEffort, settings.UsePhysicsStructureView,
            settings.UseStructureOverview);

    public bool NotifyTaskResult(AIPlan? task, string result, bool blocked, bool finished)
    {
        // Only a design actually submitted to the synchronous inspect tool becomes current.
        // Merely queued future designs must not replace the proposal whose feedback is being reviewed.
        if (!string.IsNullOrWhiteSpace(activeGoal) && goalPlanningState.HasGoal && GoalPlanningState.IsDesignTask(task))
            goalPlanningState.SubmitProposal(task!.Design!.Value, agentJournal.MarkPlanningProposal());
        AgentOutcome outcome = agentJournal.Record(task, result, blocked);
        if (IsTestRecording)
            DiagnosticsRecorder.Record("task_result", new { action = task?.Action.ToString(), status = outcome.Status, finished });
        goalPlanningState.ArchiveWorldOutcome(outcome, Main.worldID, WorldKnowledgeObserver.LoadEpoch, Main.GameUpdateCount);
        if (!string.IsNullOrWhiteSpace(activeGoal) && !blocked && goalPlanningState.HasGoal)
            GoalWorldObserver.RecordVisit(this, task, outcome.Sequence);
        if (blocked && !string.IsNullOrWhiteSpace(activeGoal))
            goalPlanningState.CaptureInterruptedPlan(outcome, Executor.SnapshotTasks());
        goalPlanningState.RecordReview(outcome);
        AIPlan[] remaining = Executor.SnapshotTasks().Skip(1).ToArray();
        goalPlanningState.RecordOutcome(outcome, remaining);
        decisionGate.StateChanged();
        TestLog($"agent outcome task={task?.Action} target={task?.Target} item={task?.Item} blocked={blocked} result={result}");
        if (blocked)
        {
            if (task != null) taskRetries.RecordFailure(TaskFingerprint(task), Executor.CaptureRetryConditions(task), outcome.Sequence);
        }
        else
        {
            taskRetries.RecordSuccess(TaskFingerprint(task));
            repeatedFailurePlans = 0;
        }
        // Equipment already queued by the model can finish this batch. A later
        // use, attack or world action still needs the inspection review first.
        bool inspectionCheckpoint = task?.Action == AIActionType.Inspect &&
            remaining.FirstOrDefault()?.Action != AIActionType.Inspect &&
            !(HasActiveModelGoal && !Executor.HoldPosition && remaining.Length > 0 &&
                remaining.All(next => next.Action == AIActionType.Equip));
        if (string.IsNullOrWhiteSpace(activeGoal) || Executor.HoldPosition)
        {
            lastGoalResult = result;
            RefreshGoalCheckpoint();
            return inspectionCheckpoint;
        }
        lastGoalResult = result;
        goalReviewBlocked = blocked;
        goalWaitingForPlayer = false;
        goalStatus = "active";
        // Gather consecutive read-only queries as one batch, then let the model interpret their results
        // before executing a dependent physical action. Query execution success is not design approval.
        bool review = blocked || finished || reviewAfterEachStep || inspectionCheckpoint;
        goalReviewPending |= review;
        RefreshGoalCheckpoint();
        return review;
    }

    public void NotifyGoalStep(string result, bool blocked, bool finished)
        => NotifyTaskResult(null, result, blocked, finished);

    private static string TaskFingerprint(AIPlan? task)
        => PlanTaskIdentity.Fingerprint(task);

    // Parse failures are paid planner responses too. They share the bounded repair
    // budget with rejected dispatches instead of entering unbounded network backoff.
    private void HandleProtocolRejection(string source, string sourcePlayerRequest, string detail, DecisionTicket? ticket = null)
    {
        detail = "Planner response could not be parsed: " + detail;
        if (detail.Length > 1600) detail = detail[..1600] + " [truncated]";
        decisionReceipts.Record(new AIPlan(), source, "rejected", "protocol_parse_rejected", detail,
            Executor.SnapshotTasks(), Executor.HoldPosition, ticket);
        ScheduleDecisionRepair("protocol_parse_rejected", detail, source, sourcePlayerRequest);
        TestLog($"agent dispatch status=rejected code=protocol_parse_rejected request={ticket?.Request.ToString() ?? "direct"} detail=" +
            detail.Replace('\r', ' ').Replace('\n', ' '));
    }

    private void ScheduleDecisionRepair(string code, string detail, string source, string? sourcePlayerRequest)
    {
        bool repair = dispatchRepairs.Reject(code, untilProgress: code == "ordered_action_pending");
        lastGoalResult = "本地未采用本次计划：" + detail;
        goalReviewBlocked = true;
        if (!Executor.HoldPosition && !goalWaitingForPlayer)
        {
            goalReviewPending = repair && !string.IsNullOrWhiteSpace(activeGoal);
            if (repair && source == "chat" && !string.IsNullOrWhiteSpace(sourcePlayerRequest))
                pendingCommand = sourcePlayerRequest;
            nextDecisionAt = DateTime.UtcNow.AddSeconds(2);
        }
        if (!repair)
        {
            goalReviewPending = false;
            pendingCommand = null;
            lastGoalResult += " 自动纠正预算已用完，目标和进度保留；等待新的玩家指令后再尝试。";
            LastReply = "规划校验连续未通过，目标与进度已保留，暂缓自动请求；可以用新指令继续。";
            Main.NewText("小星：" + LastReply, Color.LightBlue);
            Memory.Add("小星：" + LastReply);
        }
    }

    private bool ApplyPlan(AIPlan plan, string? suffix, string source = "direct", string? sourcePlayerRequest = null, DecisionTicket? ticket = null)
    {
        bool Receipt(string status, string code, string detail)
        {
            RecordTestDecision(source, status, code, ticket);
            decisionReceipts.Record(plan, source, status, code, detail, Executor.SnapshotTasks(), Executor.HoldPosition, ticket);
            if (status == "accepted") dispatchRepairs.AcceptDecision();
            else if (status == "rejected")
                ScheduleDecisionRepair(code, detail, source, sourcePlayerRequest);
            RefreshGoalCheckpoint();
            TestLog($"agent dispatch status={status} code={code} request={ticket?.Request.ToString() ?? "direct"} detail=" +
                detail.Replace('\r', ' ').Replace('\n', ' '));
            return status == "accepted";
        }
        if (source == "paused_answer")
        {
            pendingPausedQuestion = null;
            if (!Executor.HoldPosition || !IsAnswerWithoutPlanControl(plan))
                return Receipt("rejected", "paused_answer_must_be_read_only",
                    "This is a single automatic answer to a paused question, not a new player command. No task, resume, cancellation or goal/working-plan revision was applied; no further answer request is scheduled.");
            PublishReadOnlyAnswer(plan);
            goalPlanningState.AcknowledgeObservationReceipt(plan);
            return Receipt("accepted", "paused_answered", "Answered from observations while retaining pause, goal and the entire suspended queue.");
        }
        if (plan.ClearTasks && source != "chat")
            return Receipt("rejected", "clear_tasks_requires_player_chat", "An explicit queue clear belongs to a player chat decision.");

        bool readOnlyChat = source == "chat" && IsAnswerWithoutPlanControl(plan);
        if (readOnlyChat && (Executor.HoldPosition || Executor.HasCurrentPlan || Executor.SnapshotTasks().Count > 0))
        {
            // Flash may repeat keep_tasks=false with an empty task list even for plain conversation.
            PublishReadOnlyAnswer(plan, suffix);
            goalPlanningState.AcknowledgeObservationReceipt(plan);
            return Receipt("accepted", Executor.HoldPosition ? "kept_paused_answer" : "kept_chat_answer",
                "The answer retains the current goal, queue and pause state.");
        }
        if (source == "goal" && Executor.HoldPosition && plan.Resume)
            return Receipt("rejected", "paused_goal_requires_resume", "An autonomous review cannot authorize its own resume. A new player instruction is required.");
        // Explicit cancellation ends the contract; its empty replacement is not a weakening attempt.
        if (plan.GoalStatus == "cancelled")
        {
            InvalidateAgentIntent();
            autoMode = false;
            ClearActiveGoal();
            Executor.Clear(plan.Hold);
            goalStatus = "cancelled";
            if (!string.IsNullOrWhiteSpace(plan.Reply))
            {
                LastReply = plan.Reply;
                Main.NewText($"小星：{plan.Reply}{(suffix == null ? "" : " " + suffix)}", Color.LightGreen);
                Memory.Add($"小星：{plan.Reply}");
            }
            return Receipt("accepted", "cancelled", "Goal and queue were cancelled explicitly.");
        }
        if (!ValidateHousePlanningPlan(plan)) return Receipt("rejected", "acceptance_scope_denied", "The active acceptance case prohibits this operation.");

        // Asking a question is not permission to resume a paused physical task.
        // Keep the suspended executor and working plan intact while recording
        // these extra observations separately from its unfinished action steps.
        AIPlan[] requested = plan.Steps.Count > 0 ? plan.Steps.ToArray() :
            plan.Action == AIActionType.None ? Array.Empty<AIPlan>() : new[] { plan };
        bool newPlayerGoal = source == "chat" && !plan.KeepTasks && !string.IsNullOrWhiteSpace(plan.Goal) &&
            plan.Goal.Trim() != activeGoal;
        if (restoredGoalAwaitingContinue && !newPlayerGoal &&
            (plan.Resume || plan.GoalDone || plan.GoalStatus == "completed" ||
                requested.Any(task => task.Action != AIActionType.Inspect)))
            return Receipt("rejected", "restored_goal_requires_continue",
                "Say 继续 to start a fresh review of the restored goal. Previous actions cannot be resumed or used as current evidence.");
        if (Executor.HoldPosition && !newPlayerGoal && !plan.Resume && !plan.Hold && !plan.WaitForPlayer &&
            !plan.KeepTasks && !plan.GoalDone && plan.GoalStatus != "completed" && requested.Length > 0)
        {
            if (requested.All(task => task.Action == AIActionType.Inspect))
            {
                if (requested.Any(task => task.Target.Equals("chest", StringComparison.OrdinalIgnoreCase)))
                    return Receipt("rejected", "paused_inspection_requires_movement",
                        "inspect chest includes physical approach and cannot run while paused. Use read-only inspect world with a chest query, or explicitly resume movement.");
                if (CompanionNpc is not { active: true } observer)
                    return Receipt("rejected", "paused_inspection_missing_actor", "No current companion is available for a stationary observation.");
                foreach (AIPlan task in requested) RecordPausedInspection(task, observer);
                goalPlanningState.AcknowledgeObservationReceipt(plan);
                if (!string.IsNullOrWhiteSpace(plan.Remember)) Memory.Remember(plan.Remember);
                if (!string.IsNullOrWhiteSpace(plan.Reply))
                {
                    LastReply = plan.Reply;
                    Main.NewText($"小星：{plan.Reply}{(suffix == null ? "" : " " + suffix)}", Color.LightGreen);
                    Memory.Add($"小星：{plan.Reply}");
                }
                // A chat may need one more observation-informed answer. This is
                // still the player's question, never a resumption of the goal.
                if (source == "chat" && !string.IsNullOrWhiteSpace(sourcePlayerRequest))
                    pendingPausedQuestion = sourcePlayerRequest;
                return Receipt("accepted", "paused_inspections_completed",
                    "Stationary observations were recorded. Pause, external wait, goal contract and the original physical queue/working plan were retained; proposed replacement goal metadata was not adopted.");
            }
            if (source == "goal" && plan.Action != AIActionType.Stop)
                return Receipt("rejected", "paused_goal_requires_resume",
                    "An autonomous review cannot resume a paused physical goal. Keep it paused or wait for an explicit player resume/new task.");
        }

        // Validate revisions without altering the old goal or executing any proposed actions.
        bool canSetGoal = !string.IsNullOrWhiteSpace(plan.Goal) && (source != "goal" || activeGoal.Length == 0);
        bool createsGoal = !plan.KeepTasks && canSetGoal && activeGoal != plan.Goal.Trim();
        GoalPlanningState proposedGoal = createsGoal ? new GoalPlanningState() : goalPlanningState;
        if (!proposedGoal.ValidateDecision(plan, out string planError))
            return Receipt("rejected", "working_plan_rejected", planError);
        var proposedContract = new GoalCompletionState();
        if (!createsGoal && goalPlanningState.Completion.Conditions.Count > 0)
            proposedContract.TryRevise(goalPlanningState.Completion.Conditions, "", out _);
        var proposedConditions = GoalCompletionState.IncludePlayerPreservation(plan.CompletionConditions,
            createsGoal ? sourcePlayerRequest : goalPlanningState.SourcePlayerRequest);
        if (!proposedContract.TryRevise(proposedConditions, plan.ContractRevisionReason, out string contractError))
            return Receipt("rejected", "completion_contract_rejected", contractError);

        decisionReason = plan.Reason;
        if (!plan.KeepTasks) reviewAfterEachStep = plan.ReplanAfterStep;
        bool preserveGoal = plan.KeepTasks && activeGoal.Length > 0;
        // A fresh goal cannot credit an in-flight action from the previous goal, including a taskless first plan.
        if (createsGoal)
        {
            if (newPlayerGoal) Executor.SetHold(false);
            Executor.ClearForGoalReview();
        }
        if (canSetGoal)
            SetActiveGoal(plan.Goal, sourcePlayerRequest, plan.KeepTasks);
        if (!preserveGoal && !string.IsNullOrWhiteSpace(plan.SuccessCriteria) && (source != "goal" || goalCriteria.Length == 0))
            goalCriteria = plan.SuccessCriteria;
        if (!preserveGoal && !string.IsNullOrWhiteSpace(plan.CompletionAction) && (source != "goal" || goalCompletionAction.Length == 0))
            goalCompletionAction = plan.CompletionAction;
        if (!preserveGoal && !string.IsNullOrWhiteSpace(plan.CompletionTarget) && (source != "goal" || goalCompletionTarget.Length == 0))
            goalCompletionTarget = plan.CompletionTarget;
        if (!string.IsNullOrWhiteSpace(plan.Remember)) Memory.Remember(plan.Remember);
        if (goalPlanningState.HasGoal)
        {
            if (!goalPlanningState.Completion.TryRevise(proposedConditions, plan.ContractRevisionReason, out string liveContractError))
                return Receipt("rejected", "completion_contract_rejected", liveContractError);
            if (!string.IsNullOrWhiteSpace(plan.SuccessCriteria) && !string.IsNullOrWhiteSpace(plan.ContractRevisionReason))
                goalCriteria = plan.SuccessCriteria;
        }

        TestLog($"agent decision goal={activeGoal} criteria={goalCriteria} reason={plan.Reason} done={plan.GoalDone} " +
                $"wait={plan.WaitForPlayer} keep={plan.KeepTasks} tasks={plan.Steps.Count} action={plan.Action}");
        bool claimsCompletion = plan.GoalDone || plan.GoalStatus == "completed";
        GoalCompletionEvaluation completionEvidence = CurrentGoalCompletion();
        bool completionVerified = goalPlanningState.HasGoal && completionEvidence.Verified;
        int bossType = BossRules.ResolveBoss(goalCompletionTarget);
        if (bossType != 0) completionVerified &= BossEvidence.Defeats(bossType, goalBossDeathBoundary) > 0 &&
            (goalCompletionAction != "pickup" || BossLootPickups(bossType, goalBossDeathBoundary) > 0);
        if (!string.IsNullOrWhiteSpace(plan.Reply) && (!claimsCompletion || completionVerified))
        {
            LastReply = plan.Reply;
            Main.NewText($"小星：{plan.Reply}{(suffix == null ? "" : " " + suffix)}", Color.LightGreen);
            Memory.Add($"小星：{plan.Reply}");
        }

        if (claimsCompletion)
        {
            if (completionVerified)
            {
                var proof = new { objective = activeGoal, sourcePlayerRequest = goalPlanningState.SourcePlayerRequest,
                    world = decisionGate.World, worldId = Main.worldID, observedAtTick = Main.GameUpdateCount,
                    contractRevision = goalPlanningState.Completion.Revision, conditions = goalPlanningState.Completion.Conditions,
                    evidence = completionEvidence,
                    preservation = PreservationEvidence.Snapshot(Main.worldID, WorldKnowledgeObserver.LoadEpoch, goalStartSequence, activeGoal),
                    historical = true, reusableForAnotherGoal = false };
                Executor.Clear();
                ClearActiveGoal();
                lastCompletedGoal = proof;
                goalStatus = "completed";
                TestLog("goal completion facts " + System.Text.Json.JsonSerializer.Serialize(proof));
                return Receipt("accepted", "completion_evidence_verified", "All bound goal conditions were verified from current native facts, independently of the last action label.");
            }
            else if (!string.IsNullOrWhiteSpace(activeGoal))
            {
                lastGoalResult = "目标完成声明未通过实际状态核对。请查看planning.completion_contract和completion_evidence，补齐数量、位置或尚未验证的条件；不能删减要求或把动作成功当成整个目标完成。";
                goalReviewPending = true;
                goalReviewBlocked = true;
            }
            return Receipt("rejected", "completion_evidence_missing", "Completion claim was not accepted: " + completionEvidence.Status + "; bound current-world goal conditions must all be met.");
        }
        if (plan.Resume)
        {
            Executor.SetHold(false);
            goalPlanningState.MarkPaused(false);
            goalWaitingForPlayer = false;
            if (!string.IsNullOrWhiteSpace(activeGoal)) goalStatus = "active";
        }
        if (plan.Hold || plan.Action == AIActionType.Stop)
        {
            Executor.SetHold(true);
            goalPlanningState.AdoptDecision(plan, agentJournal.LastSequence, Executor.SnapshotTasks());
            goalPlanningState.MarkPaused(true);
            goalStatus = "paused";
            return Receipt("accepted", "paused", "Explicit pause preserves the current queue and task progress.");
        }
        if (plan.WaitForPlayer)
        {
            // A blocked goal waiting for the player must not start a fresh idle-follow route.
            Executor.SetHold(true);
            goalPlanningState.AdoptDecision(plan, agentJournal.LastSequence, Executor.SnapshotTasks());
            goalPlanningState.MarkPaused(true);
            goalWaitingForPlayer = true;
            goalStatus = "waiting_for_player";
            return Receipt("accepted", "waiting_for_player", "Explicit external wait pauses the queue; resume must be explicit.");
        }

        AIPlan? first = plan.Steps.FirstOrDefault() ?? (plan.Action == AIActionType.None ? null : plan);
        if (first?.Action == AIActionType.Build && agentJournal.Outcomes.Any(o =>
            o.Sequence > goalStartSequence && o.Status == "succeeded" && o.Task.Action == AIActionType.Build &&
            TaskFingerprint(o.Task) == TaskFingerprint(first)))
        {
            lastGoalResult = "本目标中相同参数的build已经实际成功，不能再消耗材料另建一间。请inspect structure核对刚建好的真实位置、尺寸和原目标差距；不符合时明确规划不同的修正步骤，不能把模板当原屋。";
            goalReviewPending = goalReviewBlocked = true;
            nextDecisionAt = DateTime.UtcNow.AddSeconds(2);
            TestLog("agent rejected duplicate completed construction: " + TaskFingerprint(first));
            return Receipt("rejected", "duplicate_completed_build", lastGoalResult);
        }
        TaskRetryDecision? retry = first == null ? null : taskRetries.Evaluate(TaskFingerprint(first), Executor.CaptureRetryConditions(first));
        if (retry is { allowed: false })
        {
            repeatedFailurePlans++;
            lastGoalResult = $"当前操作在相同有界前置条件下已有{retry.actualFailures}次真实失败，暂不重复执行。请根据失败原因核对实际位置、门/路径、工具、工作站等相关条件，或规划其他步骤；这不代表整个目标不可能完成。";
            goalReviewPending = true;
            goalReviewBlocked = true;
            nextDecisionAt = DateTime.UtcNow.AddSeconds(Math.Min(30, repeatedFailurePlans * 2));
            TestLog("agent rejected exhausted retry conditions: " + TaskFingerprint(first));
            return Receipt("rejected", retry.code, lastGoalResult + " " + retry.note);
        }

        bool clearedEmptyQueue = !readOnlyChat && !plan.KeepTasks && plan.ReplaceTasks && first == null;
        if (clearedEmptyQueue)
        {
            if (!string.IsNullOrWhiteSpace(activeGoal)) Executor.ClearForGoalReview();
            else Executor.Clear(Executor.HoldPosition);
        }

        if (!plan.KeepTasks && first != null)
        {
            emptyDecisions = 0;
            Executor.SetHold(false);
            if (plan.ReplaceTasks) Executor.ApplyTasks(plan.Steps);
            else Executor.Apply(plan);
            goalPlanningState.AdoptDecision(plan, agentJournal.LastSequence, Executor.SnapshotTasks());
            goalPlanningState.MarkPaused(false);
            goalReviewBlocked = false;
            goalReviewPending = false;
            goalWaitingForPlayer = false;
            if (!string.IsNullOrWhiteSpace(activeGoal)) goalStatus = "active";
            return Receipt("accepted", "tasks_applied", "Requested short task queue was adopted; this is not proof of physical completion.");
        }
        else if (Executor.HoldPosition)
        {
            goalPlanningState.AdoptDecision(plan, agentJournal.LastSequence, Executor.SnapshotTasks());
            goalPlanningState.MarkPaused(true);
            goalReviewPending = false;
            if (!string.IsNullOrWhiteSpace(activeGoal)) goalStatus = goalWaitingForPlayer ? "waiting_for_player" : "paused";
            return Receipt("accepted", clearedEmptyQueue ? "tasks_cleared_while_paused" : "kept_paused",
                "Pause is preserved. Clearing or keeping tasks does not resume execution without an explicit resume.");
        }
        else if (plan.KeepTasks && Executor.SnapshotTasks().Count > 0)
        {
            // A checkpoint keeps the unexecuted tail; "keep going" must actually restart that tail.
            if (!Executor.HasCurrentPlan && Executor.CurrentAction == AIActionType.Wait)
                Executor.ApplyTasks(Executor.SnapshotTasks());
            goalPlanningState.AdoptDecision(plan, agentJournal.LastSequence, Executor.SnapshotTasks());
            goalReviewPending = goalReviewBlocked = false;
            return Receipt("accepted", plan.Resume ? "resumed" : "kept_tasks", "Existing queue and active-step progress were preserved.");
        }
        else if (!string.IsNullOrWhiteSpace(activeGoal) && !goalWaitingForPlayer && !Executor.IsBusy)
        {
            // Empty plans are a new thinking turn, not an implicit request for the player to intervene.
            emptyDecisions++;
            lastGoalResult = "当前没有执行中的步骤。目标未完成且没有明确外部依赖，请查询所需事实并安排可执行的下一步。";
            goalReviewPending = true;
            nextDecisionAt = DateTime.UtcNow.AddSeconds(Math.Min(30, emptyDecisions * 2));
        }
        goalPlanningState.AdoptDecision(plan, agentJournal.LastSequence, Executor.SnapshotTasks());
        return Receipt("accepted", plan.Resume ? "resumed_requires_replan" : "no_task_change", "No task was dispatched. An unfinished idle goal remains scheduled for model review.");
    }

    private void SetActiveGoal(string goal, string? sourcePlayerRequest = null, bool keepTasks = false, bool capturePreservation = true)
    {
        goal = goal.Trim();
        if (goal.Length == 0) return;
        bool began = goalPlanningState.BeginGoal(goal, agentJournal.LastSequence, sourcePlayerRequest, keepTasks);
        if (!began && (activeGoal != goal || keepTasks)) return;
        if (activeGoal != goal)
        {
            dispatchRepairs.Reset();
            lastCompletedGoal = null;
            activeGoal = goal;
            goalCompletionTarget = "";
            goalBossDeathBoundary = BossEvidence.DeathSequence;
            goalCompletionAction = "";
            goalCriteria = lastGoalResult = goalStepLog = "";
            goalReviewBlocked = false;
            taskRetries.Clear();
            repeatedFailurePlans = 0;
            Executor.ForgetBlockedGoal();
            Executor.ResetExplorationMemory();
            LlmConversationStore.Reset("goal");
        }
        if (began)
        {
            ClearGoalCheckpoint();
            emptyDecisions = 0;
            goalStartSequence = agentJournal.LastSequence;
            VisitEvidence.Begin(WorldKnowledgeObserver.LoadEpoch, goalStartSequence);
            goalPlanningState.BeginWorldObservations(Main.worldID, WorldKnowledgeObserver.LoadEpoch);
            // Capture on the main thread before ApplyPlan can dispatch any task of
            // this new goal. Pauses, review turns and resumes cannot overwrite it.
            if (capturePreservation)
                GoalWorldObserver.BeginPreservation(this, goal, goalPlanningState.SourcePlayerRequest);
        }
        goalStatus = "active";
        goalWaitingForPlayer = false;
        TestLog("goal set: " + goal);
    }

    private void ClearActiveGoal()
    {
        goalPlanningState.Clear();
        ClearGoalCheckpoint();
        VisitEvidence.Clear();
        PreservationEvidence.Clear();
        lastCompletedGoal = null;
        dispatchRepairs.Reset();
        taskRetries.Clear();
        activeGoal = goalCriteria = goalCompletionAction = lastGoalResult = goalStepLog = "";
        goalCompletionTarget = "";
        goalReviewPending = goalReviewBlocked = goalWaitingForPlayer = false;
        goalStatus = "idle";
        goalGeneration++;
        Executor.ForgetBlockedGoal();
        Executor.ResetExplorationMemory();
        LlmConversationStore.Reset("goal");
    }

    private void CancelCombatDecision()
    {
        CancellationTokenSource? cancellation = combatCancellation;
        combatCancellation = null;
        combatPlanInFlight = false;
        if (cancellation == null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private bool ReleaseCombatDecision(CancellationTokenSource cancellation, int callId)
    {
        if (!ReferenceEquals(combatCancellation, cancellation) || callId != combatCallId) return false;
        combatCancellation = null;
        combatPlanInFlight = false;
        cancellation.Dispose();
        return worldReady;
    }
}
