using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private bool ClosureReplay => HouseMode && ClosureNatural &&
        Environment.GetEnvironmentVariable("AITEAMMATE_CLOSURE_REPLAY") == "1";
    private AIPlan[] longGoalReplay = Array.Empty<AIPlan>();
    private string longGoalReplayHash = "";
    private int longGoalReplayIndex, longGoalReplayLimit;
    private bool longGoalReplayReady, longGoalReplayLive;

    private bool LoadLongGoalReplay(LlmSettings? settings)
    {
        string file = LongGoalFile("replay.json");
        string? expectedHash = Environment.GetEnvironmentVariable("AITEAMMATE_CLOSURE_REPLAY_SHA");
        if (Main.netMode != Terraria.ID.NetmodeID.SinglePlayer ||
            !Main.SavePath.Contains("-agent-acceptance", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(Environment.GetEnvironmentVariable("AITEAMMATE_CLOSURE_REPLAY_LIMIT"), out longGoalReplayLimit) ||
            longGoalReplayLimit is < 0 or > 20 || (longGoalReplayLimit == 0) != (settings == null) ||
            string.IsNullOrWhiteSpace(expectedHash) || File.Exists(LongGoalFile("continue.txt")))
        {
            FinishLongGoalAcceptance("uncovered_replay_config", "Replay needs a fresh isolated save, exact transcript hash, explicit 0–20 suffix limit and no existing continue signal.");
            return false;
        }
        try
        {
            if (new FileInfo(file).Length is <= 0 or > 262144) throw new JsonException("Replay transcript size is outside the bound.");
            byte[] payload = File.ReadAllBytes(file);
            longGoalReplayHash = Convert.ToHexString(SHA256.HashData(payload));
            if (!longGoalReplayHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new JsonException("Replay transcript hash differs.");
            string[]? rows = JsonSerializer.Deserialize<string[]>(payload);
            if (rows is not { Length: >= 1 and <= 20 }) throw new JsonException("Expected one to twenty recorded public responses.");
            longGoalReplay = rows.Select(AIPlanParser.Parse).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            FinishLongGoalAcceptance("uncovered_replay_input", error.Message);
            return false;
        }
        longGoalEvents.Add(new { kind = "recorded_replay_setup", sha256 = longGoalReplayHash,
            decisions = longGoalReplay.Length, suffixLimit = longGoalReplayLimit });
        return true;
    }

    private void AdvanceLongGoalReplay()
    {
        if (PrimaryDecisionRequestCount != longGoalRequestStart || !LongGoalWithinTime || goalWaitingForPlayer)
        { FinishLongGoalAcceptance("replay_stopped", "Recorded prefix exceeded its bounded time, requested player action or crossed the zero-API gate."); return; }
        if (Executor.IsBusy || Executor.IsInCombat || Executor.IsAutoDefending ||
            !goalReviewPending && Executor.SnapshotTasks().Count > 0) return;
        if (longGoalReplayIndex < longGoalReplay.Length)
        {
            int index = longGoalReplayIndex++;
            bool adopted = ApplyPlan(longGoalReplay[index], null, source: index == 0 ? "chat" : "goal",
                sourcePlayerRequest: index == 0 ? longGoalRequest : null);
            longGoalEvents.Add(new { kind = "recorded_replay_dispatch", decision = index + 1, adopted });
            if (!adopted && !longGoalFinished) FinishLongGoalAcceptance("replay_rejected", $"Recorded decision {index + 1} was rejected.");
            else if (adopted) SaveLongGoalAcceptance("running", "");
            return;
        }

        if (!longGoalReplayReady)
        {
            longGoalPreservation = longGoalAudit?.CheckPreservation();
            // Native tree yield can vary while replaying the same decision. The
            // suffix will gather any shortfall; it needs actual wood for the climb.
            bool nativePrefix = longGoalMineWood > 0 && Loadout.CountItem(ItemID.Wood) >= 1 &&
                longGoalMineStone >= 5 && longGoalGelWithdrawn >= 2 &&
                longGoalTorchProduced >= 6 && LongGoalSourcesProven();
            bool recoveredFacts = !longGoalConstraintViolation && !longGoalEvidenceAmbiguous &&
                longGoalPreservation is { preserved: true, coverageComplete: true, diffCount: 0 } &&
                goalPlanningState.SourcePlayerRequest == longGoalRequest;
            longGoalEvents.Add(new { kind = "recorded_replay_finished", requests = 0, nativePrefix, recoveredFacts,
                exactSource = longGoalReplayHash, tick = Main.GameUpdateCount, stoppedUtc = DateTime.UtcNow });
            if (!recoveredFacts || longGoalReplayLimit > 0 && !nativePrefix)
            { FinishLongGoalAcceptance("replay_incomplete", "Current replay did not re-establish the required native prefix; paid continuation remains closed."); return; }
            if (longGoalReplayLimit == 0)
            { FinishLongGoalAcceptance("passed_replay_prefix", "Recorded decisions were accepted and observed without paid planning."); return; }
            longGoalReplayReady = true;
            Executor.SetHold(true);
            SaveLongGoalAcceptance("awaiting_flash_suffix", "Zero-API prefix re-established; waiting for an explicit isolated continuation signal.");
            return;
        }

        string signal = LongGoalFile("continue.txt");
        if (!File.Exists(signal) || File.ReadAllText(signal).Trim() != "CONTINUE_FLASH") return;
        File.Delete(signal);
        if (GetLlmSettings() is not { Model: "deepseek-flash", ThinkingMode: LlmThinkingMode.Enabled, ReasoningEffort: LlmReasoningEffort.Low } ||
            longGoalAudit?.CheckPreservation() is not { preserved: true, coverageComplete: true, diffCount: 0 })
        { FinishLongGoalAcceptance("uncovered_replay_continue", "Flash or the current original-house audit no longer matches the measured prefix."); return; }
        longGoalAt = DateTime.UtcNow;
        longGoalReplayLive = true;
        Executor.SetHold(false);
        goalReviewPending = true;
        longGoalEvents.Add(new { kind = "live_suffix_started", requestLimit = LongGoalLimit, seconds = 600, atUtc = longGoalAt });
        SaveLongGoalAcceptance("running", "");
    }
}
