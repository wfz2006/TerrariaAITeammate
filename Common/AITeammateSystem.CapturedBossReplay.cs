using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    // Test-only timing of actual public decisions. Native execution, including
    // failures, is left intact; no response, navigation point or damage is made up.
    private sealed record CapturedBossReply(int RequestId, int ReplyDelayTicks, string SentAt, string ReceivedAt);
    private sealed record CapturedBossLatency(int Version, CapturedBossReply[] Decisions, int FinalPendingTicks,
        string SourceRawSha, string SourceLogSha);
    private CapturedBossLatency? capturedBossLatency;
    private uint? capturedBossReplyAt;
    private long capturedBossIntent, capturedBossWorld;
    private readonly List<object> capturedBossTrace = new();
    private static readonly FieldInfo? CapturedBossReviewField = typeof(AIExecutor).GetField("bossReview", BindingFlags.Instance | BindingFlags.NonPublic);
    private bool CapturedBossReplayDue => capturedBossReplyAt is uint tick && Main.GameUpdateCount >= tick;

    private bool StartCapturedBossLatency()
    {
        string path = Environment.GetEnvironmentVariable("AITEAMMATE_REPLAY_LATENCY_PATH") ?? "";
        if (path.Length == 0) return false;
        CapturedBossLatency? schedule = JsonSerializer.Deserialize<CapturedBossLatency>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (!NaturalFlashReplay || recordedBossDecisions == null || schedule == null || schedule.Version != 1 ||
            schedule.Decisions == null || schedule.Decisions.Length != recordedBossDecisions.Length ||
            schedule.Decisions.Any(row => row == null || row.ReplyDelayTicks is < 0 or > 3600) ||
            schedule.FinalPendingTicks is < 0 or > 3600)
            throw new JsonException("Captured timing requires a version-1 schedule matching every public reply, with bounded delays.");
        capturedBossLatency = schedule;
        capturedBossIntent = decisionGate.Intent;
        capturedBossWorld = decisionGate.World;
        naturalReplayStage = 0;
        naturalEvents.Add(new { kind = "captured_response_latency", controlled = true, wholeGoalProven = false, schedule,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            note = "60-Hz delays begin at the current native review boundary. No network request is sent. Terrain, RNG and trajectories may diverge from the recorded run." });
        ScheduleCapturedBossReply();
        return true;
    }

    private bool CheckCapturedBossReplayIntent()
    {
        if (capturedBossLatency == null || decisionGate.Intent == capturedBossIntent && decisionGate.World == capturedBossWorld) return true;
        capturedBossReplyAt = null;
        naturalFlashFinished = true;
        SaveNaturalFlash("replay_interrupted", "A new intent or world invalidated the captured decisions.");
        naturalFlashRunning = naturalFlashReady = false;
        // A new player command owns execution now; do not clear it with Stop().
        return false;
    }

    private void ScheduleCapturedBossReply()
    {
        CapturedBossLatency schedule = capturedBossLatency!;
        bool finalWait = naturalReplayStage == schedule.Decisions.Length;
        int delay = finalWait ? schedule.FinalPendingTicks : schedule.Decisions[naturalReplayStage].ReplyDelayTicks;
        capturedBossReplyAt = Main.GameUpdateCount + (uint)delay;
        naturalEvents.Add(new { kind = "captured_wait_started", tick = Main.GameUpdateCount, resumeAt = capturedBossReplyAt,
            decision = naturalReplayStage + 1, finalWait, delay, goalStatus, goalReviewPending, action = Executor.CurrentAction,
            hasPlan = Executor.HasCurrentPlan, queue = Executor.SnapshotTasks() });
    }

    private void AdvanceCapturedBossLatency()
    {
        if (capturedBossReplyAt == null)
        {
            if (goalReviewPending && !Executor.IsBusy && !Executor.HasCurrentPlan) ScheduleCapturedBossReply();
            return;
        }
        if (!CapturedBossReplayDue) return;
        capturedBossReplyAt = null;
        if (naturalReplayStage == recordedBossDecisions!.Length)
        {
            bool preserved = naturalHouseAudit?.CheckPreservation() is { preserved: true, coverageComplete: true, diffCount: 0 };
            naturalEvents.Add(new { kind = "captured_wait_completed", tick = Main.GameUpdateCount,
                wholeGoalProven = false, decisions = naturalReplayStage, preserved, goalStatus });
            FinishNaturalFlash(preserved ? "completed_recorded_wait" : "replay_protection_failed",
                preserved ? "" : "Original-house coverage or preservation failed.");
            return;
        }
        try
        {
            AIPlan plan = AIPlanParser.Parse(recordedBossDecisions[naturalReplayStage]);
            bool first = naturalReplayStage == 0;
            manualControlActive = false;
            if (NaturalBossReturnCase)
            {
                // This cell was cited as damaged in the captured P0 replies. Keep
                // the live read and engine verdict separate from those replies.
                const int x = 3147, y = 399;
                Tile tile = Main.tile[x, y];
                var completion = CurrentGoalCompletion();
                naturalEvents.Add(new { kind = "captured_boss_native_review", decision = naturalReplayStage + 1,
                    tick = Main.GameUpdateCount, completion, preservation = PreservationEvidence.DebugSnapshot(),
                    cell = new { x, y, tile.HasTile, tile.TileType, tile.WallType, tile.TileFrameX, tile.TileFrameY,
                        tileName = tile.HasTile ? TileID.Search.GetName(tile.TileType) : "air" } });
            }
            naturalEvents.Add(new { kind = "recorded_public_decision", decision = naturalReplayStage + 1, tick = Main.GameUpdateCount,
                plan.CompletionConditions, plan.Steps, capturedDelay = true, attempted = true });
            if (ApplyPlan(plan, null, first ? "chat" : "goal", first ? NaturalRequest : null))
                naturalReplayStage++;
            else
                FinishNaturalFlash("replay_dispatch_failed", "Captured public decision was rejected. See lastReceipt.");
        }
        catch (JsonException exception)
        { FinishNaturalFlash("replay_parse_failed", exception.Message); }
    }

    private void TrackCapturedBossFrame(NPC npc, Player player)
    {
        if (capturedBossLatency == null) return;
        BossEncounter? encounter = BossEvidence.ReadEncounter(naturalBossSpawnId);
        NPC? boss = encounter != null && encounter.Slot >= 0 && encounter.Slot < Main.maxNPCs ? Main.npc[encounter.Slot] : null;
        bool sameBoss = boss != null && boss.GetGlobalNPC<BossWitness>().SpawnId == naturalBossSpawnId;
        object? review = CapturedBossReviewField?.GetValue(Executor);
        object? Read(string name) => review?.GetType().GetProperty(name)?.GetValue(review);
        TileStandPose? stand = Read("Stand") is TileStandPose pose ? pose : null;
        capturedBossTrace.Add(new
        {
            tick = Main.GameUpdateCount, decision = naturalReplayStage, replyAt = capturedBossReplyAt,
            x = npc.Center.X, y = npc.Center.Y, feetY = npc.Bottom.Y, vx = npc.velocity.X, vy = npc.velocity.Y,
            hp = npc.life, maxHp = npc.lifeMax, npc.justHit,
            grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width),
            observerX = player.Center.X, observerY = player.Center.Y, observerLife = player.statLife,
            action = Executor.CurrentAction, hasPlan = Executor.HasCurrentPlan, busy = Executor.IsBusy,
            autoDefend = Executor.IsAutoDefending, hold = Executor.HoldPosition, queue = Executor.SnapshotTasks().Count,
            goalStatus, goalReviewPending, bossReviewPending = BossReviewPending,
            review = review == null ? null : new { startTick = Read("StartTick"), lastWalkProbe = Read("LastWalkProbe"),
                spawnId = Read("SpawnId"), walkDirection = Read("WalkDirection"),
                standX = stand?.Node.X, standY = stand?.Node.Y, standCenterX = stand?.Center.X, standFeetY = stand?.Bottom },
            boss = !sameBoss ? null : new { boss!.active, boss.life, boss.dontTakeDamage, x = boss.Center.X, y = boss.Center.Y,
                vx = boss.velocity.X, vy = boss.velocity.Y, chaseable = boss.CanBeChasedBy(),
                distancePixels = Vector2.Distance(npc.Center, boss.Center), overlaps = boss.Hitbox.Intersects(npc.Hitbox),
                lineOfSight = Collision.CanHitLine(npc.Center, 1, 1, boss.Center, 1, 1), encounter!.SpawnId,
                encounter.CompanionDamage, encounter.DeathSequence, encounter.LootPickedUp },
            ammo = Loadout.CountItem(ItemID.MusketBall), healingPotions = Loadout.CountItem(ItemID.HealingPotion)
        });
    }

    private void SaveCapturedBossTrace(string status, string error)
    {
        if (capturedBossLatency == null) return;
        string path = Path.Combine(Main.SavePath, "AITeammate", "captured-boss-trace.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { status, error, controlled = true, wholeGoalProven = false,
            appliedDecisions = naturalReplayStage, schedule = capturedBossLatency,
            frames = capturedBossTrace, primaryRequests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount,
            warps = Executor.NavigationTeleportCount,
            scope = FiringStandDiagnosticEnabled
                ? "Actual captured public decisions and response delays, native original-house-copy execution with an optional single controlled firing-stand rejection handoff or settle-counter acceleration. See firing-stand-native-result.json."
                : "Actual captured public decisions and response delays, native original-house-copy execution. No injected path, Boss placement, failure, HP change or network call. Replayed decisions are not fresh model choices." },
            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }));
    }
}
