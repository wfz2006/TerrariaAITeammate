using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string GoalResumeNativeRequest = "先装备熔岩头盔，再步行回到我身边。别打开箱子，不要改动原屋。";
    private const string GoalResumeNativeGoal = "装备后步行会合";
    private static bool GoalResumeNativeSelected => HouseMode &&
        Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") == "goal_resume_native";
    private static string GoalResumeNativePath(string name) => Path.Combine(Main.SavePath, "AITeammate", $"goal-resume-{name}.json");
    private sealed record GoalResumeNativeSeed(string Epoch, string GoalId, Guid WorldFileId, int ProcessId,
        string OriginalBaselineId, DecisionTicket OldTicket);

    private string goalResumeNativeEpoch = "";
    private int goalResumeNativeStage;
    private uint goalResumeNativeAt;
    private ShelterWorldAudit? goalResumeNativeAudit;
    private object? goalResumeNativeRestored, goalResumeNativeReview;

    private void TickGoalResumeNative()
    {
        Player player = Main.LocalPlayer;
        if (Main.gameMenu || !player.active || player.dead || File.Exists(GoalResumeNativePath("result"))) return;
        string epoch = WorldKnowledgeObserver.LoadEpoch;
        if (goalResumeNativeEpoch != epoch)
        {
            goalResumeNativeEpoch = epoch;
            goalResumeNativeStage = 0;
            goalResumeNativeAt = Main.GameUpdateCount;
            goalResumeNativeAudit = null;
            goalResumeNativeRestored = goalResumeNativeReview = null;
        }
        try
        {
            if (goalResumeNativeStage == -1)
            {
                TryGoalResumeNativeSaveAndExit();
                return;
            }
            if (goalResumeNativeStage < 0) return;
            if (HousePlanningAllowed || GetLlmSettings() != null || PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0)
                throw new InvalidOperationException("This isolated save/reload fixture requires all network gates closed.");
            if (Main.GameUpdateCount - goalResumeNativeAt > 600)
                throw new InvalidOperationException("The controlled native action did not finish in 600 ticks.");

            if (goalResumeNativeStage == 0 && Main.GameUpdateCount - goalResumeNativeAt >= 45)
            {
                goalResumeNativeAudit = ShelterWorldAudit.Capture(3220, 390);
                if (goalResumeNativeAudit.CheckPreservation() is not { protectedCells: 5803, coverageComplete: true, diffCount: 0 } ||
                    !ClearBody(3177, 395) || !ClearBody(3172, 395) || ActiveWorldFileId == Guid.Empty)
                    throw new InvalidOperationException("The real original-house fixture or world-file identity is unavailable.");
                player.Center = Stand(3177, 395);
                player.velocity = Vector2.Zero;
                player.direction = 1;
                Executor.ResetNavigationTelemetry();
                if (File.Exists(GoalResumeNativePath("ready"))) ObserveRestoredGoal();
                else SeedGoalForReload(player);
                return;
            }
            if (goalResumeNativeStage == 1 &&
                agentJournal.Outcomes.LastOrDefault(outcome => outcome.Task.Action == AIActionType.Equip) is { Status: "succeeded" } equip)
            {
                if (!ApplyPlan(AIPlanParser.Parse("""{"tasks":[{"type":"come","plan_step":"return"}]}"""), null, "goal") ||
                    !ApplyPlan(new AIPlan { Hold = true, KeepTasks = true }, null, "goal") ||
                    !Executor.HoldPosition || Executor.SnapshotTasks().SingleOrDefault()?.Action != AIActionType.Come ||
                    Loadout.Armor[0].type != ItemID.MoltenHelmet)
                    throw new InvalidOperationException("The fixture did not produce a real Equip and paused Come tail.");
                var original = PreservationEvidence.SavedOriginal(Main.worldID, epoch, goalStartSequence, ActiveGoal);
                if (original == null) throw new InvalidOperationException("No original protection baseline belongs to this goal.");
                var seed = new GoalResumeNativeSeed(epoch, GoalIdentity, ActiveWorldFileId, Environment.ProcessId,
                    original.Identity.baseline_id, decisionGate.Begin("goal"));
                WriteGoalResumeNative("ready", seed);
                WriteGoalResumeNative("saved-preflight", new { equip, goalStatus, goal = ActiveGoal, queue = Executor.SnapshotTasks(),
                    requests = PrimaryDecisionRequestCount, protection = goalResumeNativeAudit!.CheckPreservation() });
                goalResumeNativeStage = -1;
                TestLog("goal_resume_native READY for normal world save and exit");
                return;
            }
            if (goalResumeNativeStage == 2 && Main.GameUpdateCount - goalResumeNativeAt >= 30)
            {
                if (!restoredGoalAwaitingContinue || !Executor.HoldPosition || Executor.SnapshotTasks().Count != 0 ||
                    agentJournal.LastSequence != 0)
                    throw new InvalidOperationException("The saved goal did not remain idle before player continue.");
                ContinueRestoredGoalForFixture(player);
                return;
            }
            if (goalResumeNativeStage == 3 &&
                agentJournal.Outcomes.LastOrDefault(outcome => outcome.Task.Action == AIActionType.Come) is { Status: "succeeded" } come)
            {
                var proof = CurrentGoalCompletion();
                if (!proof.Verified || !ApplyPlan(new AIPlan { GoalDone = true }, null, "goal") || goalStatus != "completed" ||
                    goalCheckpoint != null || Executor.NavigationTeleportCount != 0 ||
                    goalResumeNativeAudit!.CheckPreservation() is not { protectedCells: 5803, coverageComplete: true, diffCount: 0 })
                    throw new InvalidOperationException("The restored goal did not complete through fresh native evidence and walking.");
                WriteGoalResumeNative("result", new
                {
                    status = "passed", restored = goalResumeNativeRestored, freshReview = goalResumeNativeReview, come, proof,
                    requests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount, warps = Executor.NavigationTeleportCount,
                    protection = goalResumeNativeAudit.CheckPreservation()
                });
                Stop();
            }
        }
        catch (Exception exception)
        {
            WriteGoalResumeNative("result", new { status = "failed", error = exception.Message, goalResumeNativeStage,
                restored = goalResumeNativeRestored, freshReview = goalResumeNativeReview, goalStatus, goal = ActiveGoal,
                queue = Executor.SnapshotTasks(), outcomes = agentJournal.Outcomes, requests = PrimaryDecisionRequestCount });
            Stop();
            TestLog("goal_resume_native failed: " + exception.Message);
        }
    }

    private void SeedGoalForReload(Player player)
    {
        Stop();
        SpawnCompanion(player);
        if (CompanionNpc == null || !Loadout.AddItem(ItemID.MoltenHelmet, 1))
            throw new InvalidOperationException("Could not prepare the companion and one fixture helmet.");
        manualControlActive = false;
        AIPlan first = AIPlanParser.Parse($$"""
            {"goal":"{{GoalResumeNativeGoal}}","completion_conditions":[{"type":"equipped","item_id":{{ItemID.MoltenHelmet}}},{"type":"near_player"},{"type":"structure_preserved"}],
             "working_plan":{"summary":"装备后会合","steps":[{"id":"equip","objective":"装备熔岩头盔","success_criteria":"头盔已穿戴","depends_on":[]},
             {"id":"return","objective":"步行会合","success_criteria":"真实距离满足","depends_on":["equip"]}]},
             "tasks":[{"type":"equip","item":{{ItemID.MoltenHelmet}},"plan_step":"equip"}]}
            """);
        if (!ApplyPlan(first, null, "chat", GoalResumeNativeRequest))
            throw new InvalidOperationException("Could not bind the test objective and equip order.");
        goalResumeNativeStage = 1;
        goalResumeNativeAt = Main.GameUpdateCount;
    }

    private void ObserveRestoredGoal()
    {
        var seed = JsonSerializer.Deserialize<GoalResumeNativeSeed>(File.ReadAllText(GoalResumeNativePath("ready")));
        var original = PreservationEvidence.SavedOriginal(Main.worldID, goalResumeNativeEpoch, goalStartSequence, ActiveGoal);
        if (seed == null || seed.WorldFileId != ActiveWorldFileId || seed.Epoch == goalResumeNativeEpoch ||
            seed.GoalId == GoalIdentity || ActiveGoal != GoalResumeNativeGoal || !restoredGoalAwaitingContinue ||
            goalStatus != "paused" || !Executor.HoldPosition || Executor.HasCurrentPlan || Executor.SnapshotTasks().Count != 0 ||
            goalPlanningState.SourcePlayerRequest != GoalResumeNativeRequest || Loadout.Armor[0].type != ItemID.MoltenHelmet ||
            goalPlanningState.Completion.Conditions.Count != 3 || goalPlanningState.AcceptedExecutionPlan != null ||
            original == null || original.Identity.baseline_id != seed.OriginalBaselineId || original.Identity.epoch != seed.Epoch ||
            restoredGoalHistory?.RecentActions.Any(action => action.Type == "equip" && action.Status == "succeeded") != true)
            throw new InvalidOperationException("Actual world reload did not recover only the objective, graph, conditions and historical work.");
        bool sameProcess = seed.ProcessId == Environment.ProcessId;
        if (sameProcess && ApplyPrimaryPlanResponse(new AIPlan { Resume = true }, "goal", "", seed.OldTicket, CompanionNpc?.ModNPC))
            throw new InvalidOperationException("A stale old-world response was accepted.");
        if (ApplyPlan(new AIPlan { Resume = true, Action = AIActionType.Come }, null, "chat", "之前的目标是什么？"))
            throw new InvalidOperationException("A planner reply resumed the paused saved goal without player authorization.");
        goalResumeNativeRestored = new
        {
            previousEpoch = seed.Epoch, currentEpoch = goalResumeNativeEpoch, previousGoalId = seed.GoalId, currentGoalId = GoalIdentity,
            sameProcess, worldFileId = ActiveWorldFileId, hold = Executor.HoldPosition, queueCount = Executor.SnapshotTasks().Count,
            savedHistory = restoredGoalHistory, intent = goalPlanningState.SourcePlayerRequest, workingPlan = goalPlanningState.WorkingPlan,
            conditionCount = goalPlanningState.Completion.Conditions.Count, persistedArmor = Loadout.Armor[0].type,
            originalBaselineId = original.Identity.baseline_id
        };
        goalResumeNativeStage = 2;
        goalResumeNativeAt = Main.GameUpdateCount;
    }

    private void ContinueRestoredGoalForFixture(Player player)
    {
        NPC? existing = CompanionNpc;
        Vector2? existingCenter = existing?.Center;
        AIConfig config = ModContent.GetInstance<AIConfig>();
        var original = (config.UseLlm, config.ApiKey, config.BaseUrl, config.Model);
        try
        {
            config.UseLlm = true;
            config.ApiKey = "fixture-no-network";
            config.BaseUrl = "http://127.0.0.1:1";
            config.Model = "deepseek-flash";
            HandlePlayerMessage(player, "继续");
        }
        finally
        {
            (config.UseLlm, config.ApiKey, config.BaseUrl, config.Model) = original;
        }
        NPC? actor = CompanionNpc;
        if (actor == null || existing != null && (!ReferenceEquals(actor, existing) || actor.Center != existingCenter) ||
            existing == null && (!TilePathfinder.TryResolveStableStand(actor.Center, out Point stand) ||
                TilePathfinder.StandCenter(stand) != actor.Center ||
                !TilePathfinder.IsGroundedAt(actor.Center.X, actor.Bottom.Y, 0) ||
                actor.Hitbox.Intersects(player.Hitbox)))
            throw new InvalidOperationException("Continue moved an existing companion or spawned outside a stable free stand.");
        var completion = CurrentGoalCompletion();
        if (!goalReviewPending || restoredGoalAwaitingContinue || Executor.HoldPosition || Executor.HasCurrentPlan ||
            !completion.Conditions.Any(condition => condition.Type == "equipped" && condition.Status == "met") ||
            !completion.Conditions.Any(condition => condition.Type == "structure_preserved" && condition.Status == "met") ||
            !completion.Conditions.Any(condition => condition.Type == "near_player" && condition.Status == "unmet"))
            throw new InvalidOperationException("Continue did not request a fresh review from live conditions.");
        goalResumeNativeReview = new { completion, pending = goalReviewPending, historicalWork = restoredGoalHistory,
            spawn = new { reusedExisting = existing != null, actor.Center.X, actor.Center.Y, feet = actor.Bottom.Y },
            goals = goalPlanningState.Completion.Conditions, oldQueueCount = Executor.SnapshotTasks().Count };
        if (!ApplyPlan(AIPlanParser.Parse("""{"tasks":[{"type":"come","plan_step":"return"}]}"""), null, "goal"))
            throw new InvalidOperationException("Fresh replayed review failed to issue the remaining walk.");
        goalResumeNativeStage = 3;
        goalResumeNativeAt = Main.GameUpdateCount;
    }

    private static void WriteGoalResumeNative(string name, object value)
    {
        string path = GoalResumeNativePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void TryGoalResumeNativeSaveAndExit()
    {
        string command = Path.Combine(Main.SavePath, "AITeammate", "goal-resume-save-exit.txt");
        if (!File.Exists(command) || File.ReadAllText(command) != "SAVE_EXIT_NATIVE") return;
        if (!GoalResumeNativeSelected || ModContent.GetInstance<AIConfig>().UseLlm || HousePlanningAllowed ||
            PrimaryDecisionRequestCount != 0 || CombatDecisionRequestCount != 0 || primaryCancellation != null ||
            combatCancellation != null || !Executor.HoldPosition || goalStatus != "paused" || ActiveGoal != GoalResumeNativeGoal ||
            Main.ActiveWorldFileData is not { IsCloudSave: false } world ||
            Main.ActivePlayerFileData is not { IsCloudSave: false } player ||
            !IsGoalResumeCopiedFile(world.Path, "Worlds", ".wld") || !IsGoalResumeCopiedFile(player.Path, "Players", ".plr"))
            throw new InvalidOperationException("Native Save & Exit requires the paused no-API goal on local isolated world and player files.");
        string sidecar = Path.ChangeExtension(world.Path, ".twld");
        if (File.Exists(sidecar) && !IsGoalResumeCopiedFile(sidecar, "Worlds", ".twld"))
            throw new InvalidOperationException("The copied world sidecar is not a regular isolated file.");

        goalResumeNativeStage = -2;
        string? before = GoalResumeFileHash(sidecar);
        var begin = new { world = world.Path, sidecar, player = player.Path, worldFileId = ActiveWorldFileId,
            epoch = goalResumeNativeEpoch, requests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount,
            sidecarBefore = before, startedAt = DateTime.UtcNow };
        WriteGoalResumeNative("save-exit-begin", begin);
        SystemLoader.PreSaveAndQuit();
        IngameOptions.Close();
        Main.menuMode = 10;
        Main.gameMenu = true;
        WorldGen.SaveAndQuit(() =>
        {
            string? after = GoalResumeFileHash(sidecar);
            bool saved = Main.gameMenu && !worldReady && after != null && after != before;
            WriteGoalResumeNative("save-exit-end", new { status = saved ? "saved_and_unloaded" : "save_unverified",
                begin, sidecarAfter = after, menu = Main.gameMenu, unloaded = !worldReady, endedAt = DateTime.UtcNow });
        });
    }

    private static bool IsGoalResumeCopiedFile(string path, string folder, string extension)
    {
        string root = Path.GetFullPath(Main.SavePath);
        string expectedDirectory = Path.Combine(root, folder);
        string absolute = Path.GetFullPath(path);
        return string.Equals(Path.GetDirectoryName(absolute), expectedDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(absolute), extension, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(absolute) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0 &&
            (File.GetAttributes(expectedDirectory) & FileAttributes.ReparsePoint) == 0 &&
            (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) == 0;
    }

    private static string? GoalResumeFileHash(string path)
    {
        if (!File.Exists(path)) return null;
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
