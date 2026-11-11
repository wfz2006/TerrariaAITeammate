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
    private const string P4LowerGapCase = "p4_lower_gap_native";
    private const string P4CaveLedgeCase = "p4_cave_ledge_native", P4CaveLedgeHouseCase = "p4_cave_ledge_house_native";
    private const string P4WestGapCase = "p4_west_gap_native";
    private string p4LowerGapCase = P4LowerGapCase;
    private bool P4FromHouse => p4LowerGapCase == P4CaveLedgeHouseCase;
    private bool P4WestGap => p4LowerGapCase == P4WestGapCase;
    private string P4LowerGapResultPath => Path.Combine(Main.SavePath, "AITeammate", p4LowerGapCase.Replace('_', '-') + "-result.json");
    private readonly List<object> p4LowerGapTrace = new();
    private bool p4LowerGapRunning, p4LowerGapReturning;
    private ShelterWorldAudit? p4LowerGapAudit;
    private ModNPC? p4LowerGapActor;
    private TileStandPose p4LowerGapLaunchPose, p4LowerGapLandingPose;
    private TileStandPose? p4LowerGapReturnPose;
    private ulong p4LowerGapStarted, p4LowerGapRiseTick, p4LowerGapCrossTick;
    private ulong p4LowerGapReturnCrossTick;
    private long p4LowerGapSequence;
    private int p4LowerGapRiseJump, p4LowerGapLastJump;
    private object? p4LowerGapSetup, p4LowerGapArrival;
    private AgentOutcome? p4LowerGapOutbound;

    private bool P4LowerGapIsolated() => RubyNativeGatesClosed() &&
        Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") == p4LowerGapCase && Executor.NavigationTeleportCount == 0;

    private void BeginP4LowerGapAcceptance()
    {
        p4LowerGapCase = Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") ?? P4LowerGapCase;
        if (!HouseMode || File.Exists(P4LowerGapResultPath)) return;
        Executor.ResetNavigationTelemetry();
        p4LowerGapAudit = ShelterWorldAudit.Capture(3220, 390);
        if (!P4LowerGapIsolated() || CompanionNpc is not { active: true } npc || npc.ModNPC is not { } actor ||
            p4LowerGapAudit.CheckPreservation() is not { protectedCells: 5803, coverageComplete: true, preserved: true, diffCount: 0 })
        { FinishP4LowerGap("failed_setup", "Expected an isolated untouched original-house copy with closed API and teleport gates."); return; }

        (Point lip, Point ledge, int dropPixels) = p4LowerGapCase switch
        {
            P4LowerGapCase => (new Point(3789, 412), new Point(3795, 414), 32),
            P4WestGapCase => (new Point(2591, 430), new Point(2582, 435), 80),
            _ => (new Point(3871, 455), new Point(3877, 465), 160)
        };
        Vector2 requested = new(ledge.X * 16f + 8f, ledge.Y * 16f + 8f);
        if (!TilePathfinder.TryGetStandPose(lip, out p4LowerGapLaunchPose) ||
            !TilePathfinder.TryGetStandPose(ledge, out p4LowerGapLandingPose) ||
            !TilePathfinder.TryResolveStableStand(requested, out Point projected) || projected != ledge ||
            p4LowerGapLandingPose.Bottom - p4LowerGapLaunchPose.Bottom != dropPixels ||
            TilePathfinder.HasWalkingConnection(lip, ledge))
        { FinishP4LowerGap("gap_geometry_uncovered", "Live tiles did not reproduce the measured lower landing and unsupported crossing."); return; }
        if (P4WestGap)
        {
            Point returnShelf = new(2592, 430);
            if (!TilePathfinder.IsStableStand(returnShelf.X, returnShelf.Y) ||
                !TilePathfinder.TryGetStandPose(returnShelf, out TileStandPose returnPose) ||
                !TilePathfinder.HasWalkingConnection(returnShelf, lip))
            { FinishP4LowerGap("return_geometry_uncovered", "The real west ledge does not have the recorded stable return pose."); return; }
            p4LowerGapReturnPose = returnPose;
        }

        if (!P4FromHouse)
        {
            npc.Center = p4LowerGapLaunchPose.Center; // The sole test placement for the isolated edge case.
            npc.velocity = Vector2.Zero;
        }
        bool grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
        p4LowerGapSetup = new { launch = P4LowerGapPose(p4LowerGapLaunchPose), landing = P4LowerGapPose(p4LowerGapLandingPose),
            actorX = npc.Center.X, actorFeet = npc.Bottom.Y, playerX = Main.LocalPlayer.Center.X,
            playerFeet = Main.LocalPlayer.Bottom.Y, grounded, lowerPixels = dropPixels, continuousWalk = false, fromHouse = P4FromHouse,
            returnStand = p4LowerGapReturnPose is { } returnStand ? P4LowerGapPose(returnStand) : null };
        if (!P4FromHouse && (!grounded || !TilePathfinder.TryResolveGroundedLaunch(npc.Center, npc.Bottom.Y, npc.velocity.Y, out Point launch) || launch != lip))
        { FinishP4LowerGap("launch_unverified", "The live actor did not start grounded on the exact recorded launch lip."); return; }

        p4LowerGapActor = actor;
        p4LowerGapSequence = agentJournal.LastSequence;
        p4LowerGapStarted = Main.GameUpdateCount;
        manualControlActive = false;
        string goal = P4WestGap ? "跨过西侧天然沟" : "跨过东侧天然沟";
        AIPlan plan = AIPlanParser.Parse($$"""{"goal":"{{goal}}","tasks":[{"type":"explore","x":{{ledge.X}},"y":{{ledge.Y}}}]}""");
        if (!ApplyPlan(plan, null, "chat", goal + "并保留原屋。") || Executor.CurrentAction != AIActionType.Explore || !Executor.HasCurrentPlan)
        { FinishP4LowerGap("dispatch_rejected", "The normal parser and plan receiver did not start Explore to the recorded landing."); return; }
        p4LowerGapRunning = true;
        WriteP4LowerGap("running", "");
    }

    private void TickP4LowerGapAcceptance()
    {
        Player observer = Main.LocalPlayer;
        if (!P4LowerGapIsolated() || Main.worldID != houseWorldId || observer.dead ||
            p4LowerGapAudit?.BaselineReport.epoch != WorldKnowledgeObserver.LoadEpoch ||
            CompanionNpc is not { active: true } npc || !ReferenceEquals(npc.ModNPC, p4LowerGapActor))
        { FinishP4LowerGap("failed_runtime", "World, original actor or zero-API/warp isolation changed."); return; }
        observer.immune = true;
        observer.immuneTime = 600;
        foreach (NPC hostile in Main.npc) if (hostile.active && !hostile.friendly) hostile.active = false;

        ulong now = Main.GameUpdateCount, frames = now - p4LowerGapStarted;
        int jumps = Executor.NavigationJumpCount;
        float x = npc.Center.X, feet = npc.Bottom.Y, vy = npc.velocity.Y;
        bool grounded = TilePathfinder.IsGroundedAt(x, feet, vy, npc.width);
        if (p4LowerGapReturning)
        {
            TickP4LowerGapReturn(observer, npc, frames, grounded);
            return;
        }
        bool rising = jumps > p4LowerGapLastJump && !grounded && vy < -0.6f &&
            Math.Abs(feet - p4LowerGapLaunchPose.Bottom) <= 15f && feet < p4LowerGapLaunchPose.Bottom - 4f &&
            Math.Abs(x - p4LowerGapLaunchPose.Center.X) <= 20f;
        p4LowerGapLastJump = jumps;
        if (rising && p4LowerGapRiseTick == 0) { p4LowerGapRiseTick = now; p4LowerGapRiseJump = jumps; }
        float midpoint = (p4LowerGapLaunchPose.Center.X + p4LowerGapLandingPose.Center.X) / 2f;
        int direction = Math.Sign(p4LowerGapLandingPose.Center.X - p4LowerGapLaunchPose.Center.X);
        if (p4LowerGapRiseTick > 0 && p4LowerGapCrossTick == 0 && jumps == p4LowerGapRiseJump && !grounded &&
            direction * (x - midpoint) >= 0 && direction * (p4LowerGapLandingPose.Center.X - x) > 8f)
            p4LowerGapCrossTick = now;
        int traceInterval = P4FromHouse && Math.Abs(x - p4LowerGapLaunchPose.Center.X) > 160f ? 60 : 4;
        if (frames % (ulong)traceInterval == 0 || now == p4LowerGapRiseTick || now == p4LowerGapCrossTick)
            p4LowerGapTrace.Add(new { tick = now, frames, x, feet, vy, vx = npc.velocity.X, grounded, jumps, navigation = Executor.NavigationState });

        Point ledge = p4LowerGapLandingPose.Node;
        AgentOutcome? outcome = agentJournal.Outcomes.FirstOrDefault(row => row.Sequence > p4LowerGapSequence &&
            row.Task is { Action: AIActionType.Explore } && row.Task.X == ledge.X && row.Task.Y == ledge.Y);
        if (outcome == null)
        {
            if (frames >= (P4FromHouse ? 9_600UL : 900UL)) FinishP4LowerGap("deadline", "Native Explore did not finish within the fixture tick budget.");
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(outcome.Result);
            JsonElement report = document.RootElement, resolved = report.GetProperty("resolvedDestination");
            bool exactReport = report.GetProperty("success").GetBoolean() && resolved.ValueKind == JsonValueKind.Object &&
                resolved.GetProperty("x").GetInt32() == ledge.X && resolved.GetProperty("standRow").GetInt32() == ledge.Y &&
                Math.Abs(resolved.GetProperty("centerTileX").GetSingle() * 16f - p4LowerGapLandingPose.Center.X) <= 0.1f &&
                Math.Abs(resolved.GetProperty("feet").GetSingle() * 16f - p4LowerGapLandingPose.Bottom) <= 0.1f &&
                resolved.GetProperty("poseMatchesCurrentWorld").GetBoolean();
            float dx = p4LowerGapLandingPose.Center.X - x, footError = p4LowerGapLandingPose.Bottom - feet;
            float actualTravel = direction * (x - p4LowerGapLaunchPose.Center.X);
            bool landed = grounded && Math.Abs(vy) <= 0.6f && Math.Abs(dx) <= 8f && Math.Abs(footError) <= 5f &&
                TilePathfinder.MatchesStandPose(p4LowerGapLandingPose) &&
                actualTravel >= Math.Abs(p4LowerGapLandingPose.Center.X - p4LowerGapLaunchPose.Center.X) - 8f;
            bool crossed = p4LowerGapRiseTick > 0 && p4LowerGapCrossTick > p4LowerGapRiseTick;
            bool passed = outcome.Status == "succeeded" && exactReport && crossed && landed;
            var arrival = new { tick = now, frames, x, feet, vy, grounded, dx, footError, actualTravel, exactReport, landed, crossed,
                launchJumpTick = p4LowerGapRiseTick, airborneMidGapTick = p4LowerGapCrossTick };
            if (passed && (P4FromHouse || p4LowerGapReturnPose.HasValue))
            {
                p4LowerGapOutbound = outcome;
                p4LowerGapArrival = arrival;
                p4LowerGapReturning = true;
                p4LowerGapSequence = outcome.Sequence;
                p4LowerGapStarted = now;
                Executor.Apply(p4LowerGapReturnPose is { } back
                    ? new AIPlan { Action = AIActionType.Explore, X = back.Node.X, Y = back.Node.Y }
                    : new AIPlan { Action = AIActionType.Come });
                WriteP4LowerGap("running", "");
                return;
            }
            FinishP4LowerGap(passed ? "passed" : outcome.Status == "blocked" ? "native_explore_blocked" : "physical_crossing_unverified",
                passed ? "" : "Required native Explore success, a rising jump at the launch lip, an airborne mid-gap sample and exact grounded arrival.", outcome, arrival);
        }
        catch (Exception exception) { FinishP4LowerGap("invalid_outcome", exception.Message, outcome); }
    }

    private void TickP4LowerGapReturn(Player player, NPC npc, ulong frames, bool grounded)
    {
        TileStandPose? returnPose = p4LowerGapReturnPose;
        AIActionType action = returnPose.HasValue ? AIActionType.Explore : AIActionType.Come;
        if (returnPose is { } shelf)
        {
            float midpoint = (shelf.Center.X + p4LowerGapLandingPose.Center.X) / 2f;
            bool crossedNow = p4LowerGapReturnCrossTick == 0 && !grounded && npc.Center.X >= midpoint && npc.Center.X < shelf.Center.X - 8f;
            if (crossedNow) p4LowerGapReturnCrossTick = Main.GameUpdateCount;
            if (crossedNow || frames % 4 == 0)
                p4LowerGapTrace.Add(new { phase = "return", tick = Main.GameUpdateCount, frames, x = npc.Center.X,
                    feet = npc.Bottom.Y, vy = npc.velocity.Y, grounded, jumps = Executor.NavigationJumpCount, navigation = Executor.NavigationState });
        }
        AgentOutcome? outcome = agentJournal.Outcomes.FirstOrDefault(row => row.Sequence > p4LowerGapSequence && row.Task.Action == action &&
            (!returnPose.HasValue || row.Task.X == returnPose.Value.Node.X && row.Task.Y == returnPose.Value.Node.Y));
        if (outcome == null && frames < (returnPose.HasValue ? 900UL : 9_600UL)) return;
        float horizontal = Math.Abs(npc.Center.X - (returnPose?.Center.X ?? player.Center.X));
        float vertical = Math.Abs(npc.Bottom.Y - (returnPose?.Bottom ?? player.Bottom.Y));
        bool verified = returnPose is { } exact
            ? horizontal <= 8f && vertical <= 5f && p4LowerGapReturnCrossTick > 0 && TilePathfinder.MatchesStandPose(exact)
            : horizontal <= 48f && vertical <= 24f;
        bool passed = outcome?.Status == "succeeded" && grounded && Math.Abs(npc.velocity.Y) <= 0.6f && verified;
        FinishP4LowerGap(passed ? "passed" : "crossing_passed_return_blocked", passed ? "" : "The outbound arrival passed. The ordinary executor did not verify the requested return.",
            outcome, new { frames, horizontal, vertical, grounded, actorX = npc.Center.X, actorFeet = npc.Bottom.Y,
                airborneMidGapTick = p4LowerGapReturnCrossTick, navigation = Executor.NavigationState });
    }

    private static object P4LowerGapPose(TileStandPose pose) => new { x = pose.Node.X, row = pose.Node.Y, centerX = pose.Center.X, centerY = pose.Center.Y, feet = pose.Bottom };

    private void FinishP4LowerGap(string status, string error, AgentOutcome? outcome = null, object? arrival = null)
    {
        p4LowerGapRunning = false;
        try { WriteP4LowerGap(status, error, outcome, arrival); }
        finally { Stop(); }
    }

    private void WriteP4LowerGap(string status, string error, AgentOutcome? outcome = null, object? arrival = null)
    {
        ShelterPreservationReport? protection = status == "running" ? null : p4LowerGapAudit?.CheckPreservation();
        if (status == "passed" && protection is not { protectedCells: 5803, coverageComplete: true, preserved: true, diffCount: 0 })
        { status = "failed_preservation"; error = "The original 5803 protected house cells did not verify."; }
        Directory.CreateDirectory(Path.GetDirectoryName(P4LowerGapResultPath)!);
        File.WriteAllText(P4LowerGapResultPath, JsonSerializer.Serialize(new
        {
            status, error, worldId = Main.worldID, epoch = WorldKnowledgeObserver.LoadEpoch, setup = p4LowerGapSetup,
            requests = PrimaryDecisionRequestCount, combatRequests = CombatDecisionRequestCount,
            warps = Executor.NavigationTeleportCount, jumps = Executor.NavigationJumpCount, protection, outcome, arrival,
            outbound = p4LowerGapOutbound, outboundArrival = p4LowerGapArrival, trace = p4LowerGapTrace,
            scope = p4LowerGapCase switch
            {
                P4CaveLedgeHouseCase => "Original world copy, ordinary house spawn, production Explore to the cave ledge followed by Come. No prescribed route, relocation after spawn, terrain edits, or model calls. Hostile NPCs suppressed.",
                P4WestGapCase => "Original world copy, one controlled placement on the west lip, production Explore to the lower landing and back to the upper shelf. No prescribed route, movement injection after dispatch, terrain edits, or model calls. Hostile NPCs suppressed.",
                _ => "Original world copy, one controlled placement on the lip, production Explore to the measured landing. No prescribed route, movement injection after dispatch, terrain edits, or model calls. Hostile NPCs suppressed."
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
