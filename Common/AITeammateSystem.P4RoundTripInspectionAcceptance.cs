using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string P4RoundTripCase = "p4_round_trip_inspection_native";
    private static readonly Point P4RoundTripTarget = new(3877, 465);
    private static string P4RoundTripPath => Path.Combine(Main.SavePath, "AITeammate", "p4-round-trip-inspection-native-result.json");
    private bool p4RoundTripRunning;
    private ShelterWorldAudit? p4RoundTripAudit;
    private ModNPC? p4RoundTripActor;
    private Vector2 p4RoundTripStart;
    private Point p4RoundTripOrigin;
    private ulong p4RoundTripTick;
    private long p4RoundTripBoundary;
    private float p4RoundTripMaximumDisplacement;

    private bool P4RoundTripIsolated() => RubyNativeGatesClosed() &&
        Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") == P4RoundTripCase && Executor.NavigationTeleportCount == 0;

    private void BeginP4RoundTripInspectionAcceptance()
    {
        if (!HouseMode || File.Exists(P4RoundTripPath)) return;
        Executor.ResetNavigationTelemetry();
        p4RoundTripAudit = ShelterWorldAudit.Capture(3220, 390);
        if (!P4RoundTripIsolated() || CompanionNpc is not { active: true, ModNPC: { } actor } npc ||
            p4RoundTripAudit.CheckPreservation() is not { protectedCells: 5803, coverageComplete: true, preserved: true, diffCount: 0 } ||
            !TilePathfinder.TryResolveGroundedLaunch(npc.Center, npc.Bottom.Y, npc.velocity.Y, out p4RoundTripOrigin))
        { FinishP4RoundTripInspection("failed_setup", "Expected a grounded ordinary spawn in the untouched original-house copy."); return; }
        p4RoundTripActor = actor;
        p4RoundTripStart = npc.Center;
        p4RoundTripTick = Main.GameUpdateCount;
        p4RoundTripBoundary = agentJournal.LastSequence;
        Executor.Apply(new AIPlan { Action = AIActionType.Inspect, Target = "route", Item = "round_trip",
            X = P4RoundTripTarget.X, Y = P4RoundTripTarget.Y });
        p4RoundTripRunning = true;
        WriteP4RoundTripInspection("running", "");
    }

    private void TickP4RoundTripInspectionAcceptance()
    {
        Player player = Main.LocalPlayer;
        if (!P4RoundTripIsolated() || Main.worldID != houseWorldId || player.dead ||
            p4RoundTripAudit?.BaselineReport.epoch != WorldKnowledgeObserver.LoadEpoch ||
            CompanionNpc is not { active: true } npc || !ReferenceEquals(npc.ModNPC, p4RoundTripActor))
        { FinishP4RoundTripInspection("failed_runtime", "Actor, original world, or zero API/warp isolation changed."); return; }
        player.immune = true;
        player.immuneTime = 600;
        foreach (NPC hostile in Main.npc) if (hostile.active && !hostile.friendly) hostile.active = false;
        p4RoundTripMaximumDisplacement = Math.Max(p4RoundTripMaximumDisplacement, Vector2.Distance(p4RoundTripStart, npc.Center));
        AgentOutcome? outcome = agentJournal.Outcomes.FirstOrDefault(row => row.Sequence > p4RoundTripBoundary &&
            row.Task is { Action: AIActionType.Inspect, Target: "route", Item: "round_trip" });
        if (outcome == null)
        {
            if (Main.GameUpdateCount - p4RoundTripTick > 650)
                FinishP4RoundTripInspection("deadline", "No retained route inspection result arrived.");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(outcome.Result);
            var response = document.RootElement;
            var measured = response.GetProperty("measurement");
            var back = measured.GetProperty("returnCheck");
            var anchor = back.GetProperty("anchor");
            var expanded = measured.GetProperty("expanded");
            var from = measured.GetProperty("resolvedStart");
            var to = measured.GetProperty("resolvedDestination");
            int unique = expanded.GetProperty("unique").GetInt32() + back.GetProperty("uniqueExpanded").GetInt32();
            int discovered = expanded.GetProperty("discovered").GetInt32() + back.GetProperty("discovered").GetInt32();
            bool passed = outcome.Status == "succeeded" && response.GetProperty("available").GetBoolean() &&
                response.GetProperty("requestedInput").GetProperty("checkReturn").GetBoolean() &&
                !response.GetProperty("actorsMoved").GetBoolean() && !measured.GetProperty("travelExecuted").GetBoolean() &&
                measured.GetProperty("pathFound").GetBoolean() && back.GetProperty("status").GetString() == "found" &&
                back.GetProperty("roundTripPathFound").GetBoolean() && back.GetProperty("waypointCount").GetInt32() > 1 &&
                back.GetProperty("originRelation").GetString() == "nearby_walk_connected_origin" &&
                anchor.GetProperty("x").GetInt32() == p4RoundTripOrigin.X + 1 && anchor.GetProperty("standRow").GetInt32() == p4RoundTripOrigin.Y &&
                anchor.GetProperty("reachability").GetString() == "on_current_found_return_graph_path" &&
                from.GetProperty("x").GetInt32() == p4RoundTripOrigin.X && from.GetProperty("standRow").GetInt32() == p4RoundTripOrigin.Y &&
                to.GetProperty("x").GetInt32() == P4RoundTripTarget.X && to.GetProperty("standRow").GetInt32() == P4RoundTripTarget.Y &&
                unique <= NavigationRouteObservation.MaxUniqueExpanded && discovered <= NavigationRouteObservation.MaxDiscovered &&
                p4RoundTripMaximumDisplacement <= 1f;
            FinishP4RoundTripInspection(passed ? "passed" : "route_proof_failed",
                passed ? "" : "The result lacked exact separate graph paths within the shared budget or changed the observer pose.", outcome);
        }
        catch (Exception exception) { FinishP4RoundTripInspection("invalid_outcome", exception.Message, outcome); }
    }

    private void FinishP4RoundTripInspection(string status, string error, AgentOutcome? outcome = null)
    {
        p4RoundTripRunning = false;
        try { WriteP4RoundTripInspection(status, error, outcome); }
        finally { Stop(); }
    }

    private void WriteP4RoundTripInspection(string status, string error, AgentOutcome? outcome = null)
    {
        ShelterPreservationReport? protection = status == "running" ? null : p4RoundTripAudit?.CheckPreservation();
        if (status == "passed" && protection is not { protectedCells: 5803, coverageComplete: true, preserved: true, diffCount: 0 })
        { status = "failed_preservation"; error = "Original house protection did not verify."; }
        Directory.CreateDirectory(Path.GetDirectoryName(P4RoundTripPath)!);
        File.WriteAllText(P4RoundTripPath, JsonSerializer.Serialize(new
        {
            status, error, worldId = Main.worldID, epoch = WorldKnowledgeObserver.LoadEpoch, elapsedTicks = Main.GameUpdateCount - p4RoundTripTick,
            origin = new { x = p4RoundTripOrigin.X, y = p4RoundTripOrigin.Y, actorX = p4RoundTripStart.X, actorY = p4RoundTripStart.Y },
            target = new { x = P4RoundTripTarget.X, y = P4RoundTripTarget.Y }, maximumDisplacementPixels = p4RoundTripMaximumDisplacement,
            requests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount, warps = Executor.NavigationTeleportCount, protection, outcome,
            scope = "Retained read-only production route query from the ordinary original-house spawn to the natively traversed east cave ledge. Graph evidence only. No actor relocation after spawn, terrain edits, or model calls."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
