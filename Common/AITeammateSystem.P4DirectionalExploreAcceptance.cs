using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string P4DirectionalExploreCase = "p4_directional_explore_native";
    private const string P4DirectionalExploreReply = """{"goal":"向左探索一段","tasks":[{"type":"explore","target":"left"}]}""";
    private static string P4DirectionalExplorePath => Path.Combine(Main.SavePath, "AITeammate", "p4-directional-explore-native-result.json");
    private readonly List<object> p4DirectionalExploreTrace = new();
    private bool p4DirectionalExploreRunning;
    private ShelterWorldAudit? p4DirectionalExploreAudit;
    private ModNPC? p4DirectionalExploreActor;
    private Vector2 p4DirectionalExploreStart;
    private ulong p4DirectionalExploreTick;
    private long p4DirectionalExploreSequence;
    private object? p4DirectionalExploreSetup;

    private bool P4DirectionalExploreIsolated()
    {
        AIConfig config = ModContent.GetInstance<AIConfig>();
        return HouseMode && Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") == P4DirectionalExploreCase &&
            !config.UseLlm && !config.AllowTeleportRecovery && !HousePlanningAllowed && !CombatPlannerEnabled &&
            PrimaryDecisionRequestCount == 0 && CombatDecisionRequestCount == 0 && Executor.NavigationTeleportCount == 0;
    }

    private void BeginP4DirectionalExplore()
    {
        if (!HouseMode || File.Exists(P4DirectionalExplorePath)) return;
        Executor.ResetNavigationTelemetry();
        p4DirectionalExploreAudit = ShelterWorldAudit.Capture(3220, 390);
        if (!P4DirectionalExploreIsolated() || CompanionNpc is not { active: true } npc || npc.ModNPC is not { } actor ||
            p4DirectionalExploreAudit.CheckPreservation() is not { protectedCells: 5803, coverageComplete: true, preserved: true, diffCount: 0 })
        { FinishP4DirectionalExplore("failed_setup", "Expected a fresh isolated original-house copy and closed model/teleport gates."); return; }

        Player player = Main.LocalPlayer;
        npc.Center = player.Center - new Vector2(64f, 0f); // Same one-time initial pose as PrepareNaturalBoss.
        npc.velocity = player.velocity = Vector2.Zero;
        bool grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
        p4DirectionalExploreSetup = new { actorX = npc.Center.X, actorY = npc.Center.Y, actorFeet = npc.Bottom.Y,
            playerX = player.Center.X, playerFeet = player.Bottom.Y, grounded, initialOffsetPixels = 64 };
        if (!grounded)
        { FinishP4DirectionalExplore("failed_setup", "The original natural starting pose is not grounded."); return; }

        p4DirectionalExploreActor = actor;
        p4DirectionalExploreStart = npc.Center;
        p4DirectionalExploreTick = Main.GameUpdateCount;
        p4DirectionalExploreSequence = agentJournal.LastSequence;
        manualControlActive = false;
        if (!ApplyPlan(AIPlanParser.Parse(P4DirectionalExploreReply), null, "chat", "向左探索一段，保留原屋。") ||
            Executor.CurrentAction != AIActionType.Explore || !Executor.HasCurrentPlan)
        { FinishP4DirectionalExplore("dispatch_rejected", "The ordinary parser/plan receiver did not start the direction-only Explore."); return; }
        p4DirectionalExploreRunning = true;
        WriteP4DirectionalExplore("running", "");
    }

    private void TickP4DirectionalExplore()
    {
        Player player = Main.LocalPlayer;
        if (!P4DirectionalExploreIsolated() || Main.worldID != houseWorldId || player.dead ||
            p4DirectionalExploreAudit?.BaselineReport.epoch != WorldKnowledgeObserver.LoadEpoch ||
            CompanionNpc is not { active: true } npc || !ReferenceEquals(npc.ModNPC, p4DirectionalExploreActor))
        { FinishP4DirectionalExplore("failed_runtime", "The original actor, world or zero API/warp gate changed."); return; }
        player.immune = true;
        player.immuneTime = 600;
        foreach (NPC hostile in Main.npc) if (hostile.active && !hostile.friendly) hostile.active = false;

        ulong ticks = Main.GameUpdateCount - p4DirectionalExploreTick;
        bool grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
        if (ticks % 30 == 0)
            p4DirectionalExploreTrace.Add(new { ticks, actorX = npc.Center.X, actorY = npc.Center.Y, feet = npc.Bottom.Y, npc.velocity.X,
                vy = npc.velocity.Y, grounded, action = Executor.CurrentAction.ToString(), navigation = Executor.NavigationState });
        AgentOutcome? outcome = agentJournal.Outcomes.FirstOrDefault(entry => entry.Sequence > p4DirectionalExploreSequence &&
            entry.Task is { Action: AIActionType.Explore, Target: "left", X: null, Y: null });
        if (outcome == null)
        {
            if (ticks >= 1500) FinishP4DirectionalExplore("deadline", "No native direction Explore outcome arrived in 1500 ticks.");
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(outcome.Result);
            JsonElement result = document.RootElement;
            Point origin = P4DirectionalCell(p4DirectionalExploreStart), end = P4DirectionalCell(npc.Center);
            Point requested = new(origin.X - 40, origin.Y);
            JsonElement destination = result.GetProperty("resolvedDestination");
            bool reportedDestination = destination.ValueKind == JsonValueKind.Object &&
                destination.GetProperty("poseMatchesCurrentWorld").GetBoolean();
            JsonElement observedStand = reportedDestination ? destination : result.GetProperty("navigation").GetProperty("goalStandPose");
            Vector2 requestedCenter = new(requested.X * 16f + 8, requested.Y * 16f + 8);
            bool projectionValid = TilePathfinder.TryResolveStableStand(requestedCenter, out Point projected) &&
                TilePathfinder.TryGetStandPose(projected, out TileStandPose pose) && observedStand.ValueKind == JsonValueKind.Object &&
                observedStand.GetProperty("x").GetInt32() == projected.X && observedStand.GetProperty("standRow").GetInt32() == projected.Y &&
                Math.Abs(observedStand.GetProperty("centerTileX").GetSingle() * 16f - pose.Center.X) <= 0.1f &&
                Math.Abs(observedStand.GetProperty(reportedDestination ? "feet" : "feetTileY").GetSingle() * 16f - pose.Bottom) <= 0.1f &&
                Math.Abs(npc.Center.X - pose.Center.X) <= 8f && Math.Abs(npc.Bottom.Y - pose.Bottom) <= 5f;
            float leftwardTiles = (p4DirectionalExploreStart.X - npc.Center.X) / 16f;
            bool physical = grounded && Math.Abs(npc.velocity.Y) <= 0.6f && leftwardTiles >= 30 && projectionValid;
            bool reportMatches = result.GetProperty("kind").GetString() == "exploration_result" &&
                ReadP4DirectionalCell(result, "origin") == origin && ReadP4DirectionalCell(result, "requestedDestination") == requested &&
                ReadP4DirectionalCell(result, "actualEnd") == end && result.GetProperty("horizontalTravelTiles").GetInt32() >= 30;
            bool passed = outcome.Status == "succeeded" && result.GetProperty("success").GetBoolean() && reportedDestination && physical && reportMatches;
            var arrival = new { ticks, actorX = npc.Center.X, actorY = npc.Center.Y, feet = npc.Bottom.Y, grounded, vy = npc.velocity.Y,
                leftwardTiles, reportedDestination, projectionValid, physical, reportMatches, projected = new { projected.X, projected.Y } };
            FinishP4DirectionalExplore(passed ? "passed" : physical && reportMatches && outcome.Status == "blocked" ? "arrival_not_credited" : "arrival_unverified",
                passed ? "" : "Required successful direction Explore and current grounded arrival at its same frozen projection.", outcome, arrival);
        }
        catch (Exception exception) { FinishP4DirectionalExplore("invalid_outcome", exception.Message, outcome); }
    }

    private static Point P4DirectionalCell(Vector2 center) => new((int)(center.X / 16f), (int)(center.Y / 16f));
    private static Point ReadP4DirectionalCell(JsonElement result, string name)
    {
        JsonElement cell = result.GetProperty(name);
        return new(cell.GetProperty("x").GetInt32(), cell.GetProperty("y").GetInt32());
    }

    private void FinishP4DirectionalExplore(string status, string error, AgentOutcome? outcome = null, object? arrival = null)
    {
        p4DirectionalExploreRunning = false;
        try { WriteP4DirectionalExplore(status, error, outcome, arrival); }
        finally { Stop(); }
    }

    private void WriteP4DirectionalExplore(string status, string error, AgentOutcome? outcome = null, object? arrival = null)
    {
        ShelterPreservationReport? protection = status == "running" ? null : p4DirectionalExploreAudit?.CheckPreservation();
        if (status == "passed" && protection is not { protectedCells: 5803, coverageComplete: true, preserved: true, diffCount: 0 })
        { status = "failed_preservation"; error = "The original 5803 protected house cells did not verify."; }
        Directory.CreateDirectory(Path.GetDirectoryName(P4DirectionalExplorePath)!);
        File.WriteAllText(P4DirectionalExplorePath, JsonSerializer.Serialize(new
        {
            status, error, worldId = Main.worldID, epoch = WorldKnowledgeObserver.LoadEpoch, setup = p4DirectionalExploreSetup,
            requests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount, warps = Executor.NavigationTeleportCount,
            input = P4DirectionalExploreReply, outcome, arrival, protection, trace = p4DirectionalExploreTrace,
            scope = "Original house copy, one initial matching natural pose, one production direction-only Explore left. No actor reposition after dispatch, waypoint input, model, or direct tile edits. Other hostile NPCs are suppressed."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
