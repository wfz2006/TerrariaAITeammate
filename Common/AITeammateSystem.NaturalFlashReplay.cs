using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private int naturalReplayStage;
    private int bossReplayReviewTicks;
    private uint? bossReplayAttackAt;
    private string[]? recordedBossDecisions;
    private bool BossReplayAttackDue => bossReplayAttackAt is uint tick && Main.GameUpdateCount >= tick;

    private void StartNaturalReplay()
    {
        if (NaturalBossAltarReplay) { StartNaturalAltarReplay(); return; }
        try
        {
            AIPlan plan;
            if (NaturalFlashProfile == "natural_boss_flash")
            {
                if (int.TryParse(Environment.GetEnvironmentVariable("AITEAMMATE_BOSS_REPLAY_REVIEW_TICKS"), out int delay))
                    bossReplayReviewTicks = Math.Clamp(delay, 0, 600);
                string file = Environment.GetEnvironmentVariable("AITEAMMATE_REPLAY_PUBLIC_DECISION_PATH") ?? "";
                string publicDecision = File.ReadAllText(file);
                using JsonDocument captured = JsonDocument.Parse(publicDecision);
                if (captured.RootElement.ValueKind == JsonValueKind.Array)
                {
                    if (captured.RootElement.GetArrayLength() is < 1 or > 20 ||
                        captured.RootElement.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.Object))
                        throw new JsonException("Recorded decisions must contain 1-20 unmodified public decision objects.");
                    recordedBossDecisions = captured.RootElement.EnumerateArray().Select(row => row.GetRawText()).ToArray();
                    publicDecision = recordedBossDecisions[0];
                    naturalEvents.Add(new { kind = "recorded_public_sequence", decisions = recordedBossDecisions.Length,
                        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))),
                        controlled = true, wholeGoalProven = false });
                    if (StartCapturedBossLatency()) return;
                }
                plan = AIPlanParser.Parse(publicDecision);
                naturalEvents.Add(new { kind = "recorded_public_boss_decision", parsed = true, plan.CompletionConditions, plan.Steps });
            }
            else
            {
                plan = new AIPlan { Goal = "预检并在空地独立新建10×7普通木屋", Action = AIActionType.Inspect, Target = "build", Item = "wood",
                    CompletionConditions = new() { new() { Type = "structure_preserved" }, new() { Type = "new_housing_valid" } } };
            }
            manualControlActive = false;
            if (!ApplyPlan(plan, null, "chat", NaturalRequest))
            { FinishNaturalFlash("replay_dispatch_failed", "Opening replay decision was rejected."); return; }
            naturalReplayStage = 1;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or JsonException)
        { FinishNaturalFlash("replay_setup_failed", exception.Message); }
    }

    private void AdvanceNaturalReplay(IReadOnlyList<AgentOutcome> fresh)
    {
        if (NaturalBossAltarReplay) { AdvanceNaturalAltarReplay(fresh); return; }
        if (capturedBossLatency != null)
        {
            // A recorded failed action is evidence, not a reason to discard the
            // following real public reply. This diagnostic never generates one.
            AdvanceCapturedBossLatency();
            return;
        }
        if (fresh.Any(outcome => outcome.Status == "blocked"))
        { FinishNaturalFlash("replay_native_blocked", "Production executor reported a physical action blocked. See outcomes."); return; }

        if (recordedBossDecisions != null)
        {
            // Reapply only captured public decisions at the actual review boundary.
            // Exhausting a recording proves its prefix, never an autonomous Boss kill.
            if (!goalReviewPending || Executor.IsBusy || Executor.HasCurrentPlan) return;
            if (naturalReplayStage == recordedBossDecisions.Length)
            {
                if (naturalOutcomes.Count == 0 || naturalHouseAudit?.CheckPreservation() is not
                    { preserved: true, coverageComplete: true, diffCount: 0 })
                { FinishNaturalFlash("replay_prefix_unverified", "Recorded prefix lacks outcomes or preserved original-house coverage."); return; }
                naturalEvents.Add(new { kind = "recorded_prefix_completed", decisions = naturalReplayStage,
                    wholeGoalProven = false, controlled = true });
                FinishNaturalFlash("passed_recorded_prefix", "");
                return;
            }
            try
            {
                AIPlan recorded = AIPlanParser.Parse(recordedBossDecisions[naturalReplayStage]);
                naturalReplayStage++;
                naturalEvents.Add(new { kind = "recorded_public_decision", decision = naturalReplayStage,
                    recorded.CompletionConditions, recorded.Steps });
                if (!ApplyPlan(recorded, null, "goal"))
                    FinishNaturalFlash("replay_dispatch_failed", "Recorded decision was rejected. See lastReceipt.");
            }
            catch (JsonException exception)
            { FinishNaturalFlash("replay_parse_failed", exception.Message); }
            return;
        }

        AIPlan? next = NaturalFlashProfile == "natural_boss_flash" ? NextBossReplay(fresh) : NextHousingReplay(fresh);
        if (next == null || naturalFlashFinished) return;
        naturalEvents.Add(new { kind = "controlled_continuation", stage = naturalReplayStage, next });
        if (!ApplyPlan(next, null, "goal")) FinishNaturalFlash("replay_dispatch_failed", "Controlled continuation was rejected. See lastReceipt.");
    }

    private AIPlan? NextBossReplay(IReadOnlyList<AgentOutcome> fresh)
    {
        if (naturalReplayStage == 1 && fresh.Any(outcome => outcome.Task.Action == AIActionType.Inspect && outcome.Task.Target == "boss"))
        {
            naturalReplayStage = 2;
            var tasks = new[] { ItemID.MoltenHelmet, ItemID.MoltenBreastplate, ItemID.MoltenGreaves, ItemID.PhoenixBlaster }
                .Select(item => new AIPlan { Action = AIActionType.Equip, Item = item.ToString(), PlanStep = "s2" }).ToList();
            tasks.Add(new AIPlan { Action = AIActionType.Use, Item = ItemID.SlimeCrown.ToString(), PlanStep = "s3" });
            if (bossReplayReviewTicks == 0)
                tasks.Add(new AIPlan { Action = AIActionType.Attack, Target = "king_slime", PlanStep = "s3" });
            return new AIPlan { ReplaceTasks = true, Steps = tasks };
        }
        if (naturalReplayStage == 2 && bossReplayReviewTicks > 0 && fresh.Any(outcome => outcome.Task.Action == AIActionType.Use))
        {
            bossReplayAttackAt = Main.GameUpdateCount + (uint)bossReplayReviewTicks;
            naturalEvents.Add(new { kind = "simulated_review_delay", ticks = bossReplayReviewTicks, resumeAt = bossReplayAttackAt });
            return null;
        }
        if (BossReplayAttackDue)
        {
            bossReplayAttackAt = null;
            return new AIPlan { Action = AIActionType.Attack, Target = "king_slime", PlanStep = "s3" };
        }
        if (naturalReplayStage == 2 && fresh.Any(outcome => outcome.Task.Action == AIActionType.Attack))
        {
            naturalReplayStage = 3;
            if (BossLootSelectionBatchSelected) return BeginBossLootSelectionBatch();
            return new AIPlan { ReplaceTasks = true, Steps = new() { new() { Action = AIActionType.Pickup, Count = 1, PlanStep = "s4" },
                new() { Action = AIActionType.Come, PlanStep = "s5" } } };
        }
        if (naturalReplayStage == 3 && fresh.Any(outcome => outcome.Task.Action == AIActionType.Come))
        { naturalReplayStage = 4; return new AIPlan { GoalDone = true }; }
        return null;
    }

    private AIPlan? NextHousingReplay(IReadOnlyList<AgentOutcome> fresh)
    {
        if (naturalReplayStage == 1 && fresh.FirstOrDefault(outcome => outcome.Task is { Action: AIActionType.Inspect, Target: "build" }) is { } preflight)
        {
            var report = JsonNode.Parse(preflight.Result)!.AsObject();
            var permitted = report["siteSearch"]!["candidates"]!.AsArray()
                .Where(row => row?["allowedByCurrentChecks"]?.GetValue<bool>() == true).ToArray();
            if (permitted.Length == 0)
            { FinishNaturalFlash("replay_no_site", "The current original-house bounds did not cover an allowed candidate."); return null; }

            // Exercise the production model-facing serializer using real native evidence;
            // a controlled decision then builds using the coordinate visible in that frame.
            const string mode = "执行后复盘";
            AIConfig config = ModContent.GetInstance<AIConfig>();
            string frame = PlanningRequest.Build(AgentPlanningPrompt.For(mode), mode, "复盘当前目标，按原指令新建独立木屋。",
                AIPlanner.BuildState(Main.LocalPlayer, Executor, Memory), Array.Empty<LlmMessage>(), preflight.Result,
                config.UsePhysicsStructureView, config.UseStructureOverview)[^1].Content;
            var wire = JsonNode.Parse(frame)!.AsObject();
            var result = wire["state"]?["planning"]?["outcomes"]?.AsArray()
                .FirstOrDefault(row => row?["sequence"]?.GetValue<long>() == preflight.Sequence)?["result"];
            var search = result?["siteSearch"];
            var visible = search?["candidates"] as JsonArray;
            bool sitesMatch = visible != null && visible.Count == permitted.Length &&
                visible.Zip(permitted, JsonNode.DeepEquals).All(equal => equal);
            bool billMatches = JsonNode.DeepEquals(result?["options"], report["options"]);
            bool sentOnce = wire["feedback"] == null && wire["state"]?["lastStepResult"] == null;
            var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            int nativeCharacters = report.ToJsonString(json).Length, wireCharacters = result?.ToJsonString(json).Length ?? 0;
            string? candidateView = search?["candidateView"]?.GetValue<string>();
            naturalEvents.Add(new { kind = "shelter_preflight_wire", sequence = preflight.Sequence, candidateView,
                nativeCharacters, wireCharacters, freshFrameCharacters = frame.Length, permitted = permitted.Length,
                rejected = search?["rejectedCandidates"]?["rows"]?.AsArray().Count, sitesMatch, billMatches, sentOnce,
                chosen = visible?.FirstOrDefault()?["resolvedTopLeft"] });
            if (candidateView != "usable_and_rejected_v1" || !sitesMatch || !billMatches || !sentOnce || wireCharacters >= nativeCharacters)
            { FinishNaturalFlash("replay_wire_mismatch", "The model-facing preflight did not preserve the original permitted sites and material bill in one smaller result."); return null; }
            var site = visible![0]!["resolvedTopLeft"]!;
            naturalReplayStage = 2;
            return new AIPlan { Action = AIActionType.Build, Target = "wood", X = site["x"]!.GetValue<int>(), Y = site["y"]!.GetValue<int>() };
        }
        if (naturalReplayStage == 2 && fresh.Any(outcome => outcome.Task.Action == AIActionType.Build) && naturalHouseSites.Count == 1)
        {
            var site = naturalHouseSites.Single();
            naturalReplayStage = 3;
            return new AIPlan { GoalDone = true, ContractRevisionReason = "绑定本次原生建造完成的外框", CompletionConditions = new()
                { new() { Type = "structure_preserved" }, new() { Type = "new_housing_valid", X = site.x, Y = site.y, Width = 10, Height = 7 } } };
        }
        return null;
    }
}
