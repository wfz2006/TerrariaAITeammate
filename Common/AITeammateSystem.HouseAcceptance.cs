using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private sealed class HouseCase
    {
        public string Id = "", Status = "running", Error = "";
        public Vector2 Start, Target;
        public DateTime Started;
        public int Frames, FacingChanges, Jumps, Warps, StableArrivalFrames;
        public Vector2 LastPosition, Min, Max;
        public int LastDirection;
        public float LastVelocityY;
        public readonly List<object> Trace = new();
    }
    private readonly List<HouseCase> houseCases = new();
    private readonly List<(string Id, Vector2 From, Vector2 To)> houseRoutes = new();
    private readonly List<object> houseGeometry = new();
    private HouseCase? houseCase;
    private bool houseRunning, houseFinished;
    private int houseRouteIndex;
    private int houseWorldId;
    private Vector2 housePlayer, houseDefaultPlayer;
    private static bool HouseMode => Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") == "house" &&
        Main.netMode == NetmodeID.SinglePlayer && Main.SavePath.Contains("agent-acceptance", StringComparison.OrdinalIgnoreCase);

    private async Task RunHouseAcceptanceAsync()
    {
        if (!HouseMode) return;
        await Task.Delay(5000).ConfigureAwait(false);
        Enqueue(() =>
        {
            if (!HouseMode || Main.gameMenu) return;
            try
            {
                Stop(); LlmConversationStore.ResetAll(); Memory.Clear(); agentJournal.Clear();
                autoMode = CombatPlannerEnabled = false;
                houseWorldId = Main.worldID;
                houseCases.Clear(); houseRoutes.Clear(); houseGeometry.Clear();
                if (Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") == BossLootSelectionNativeCase)
                { BeginBossLootSelectionNative(); return; }
                // Read the user's existing house. No terrain, platform, door or furniture is generated.
                for (int x = 3160; x <= 3270; x++)
                    for (int y = 350; y <= 420; y++)
                    {
                        Tile t = Main.tile[x,y];
                        if (t.HasTile || t.WallType > WallID.None) houseGeometry.Add(new { x,y,tile=t.HasTile?(int)t.TileType:-1,wall=(int)t.WallType,slope=(int)t.Slope,
                            solid=t.HasTile&&Main.tileSolid[t.TileType],solidTop=t.HasTile&&Main.tileSolidTop[t.TileType],
                            platform=t.HasTile&&TileID.Sets.Platforms[t.TileType],lava=t.LiquidAmount>0&&t.LiquidType==LiquidID.Lava,actuated=t.IsActuated });
                    }
                int column = Enumerable.Range(3165, 36).OrderByDescending(x => Enumerable.Range(355,65)
                    .Count(y => Main.tile[x,y].HasTile && TileID.Sets.Platforms[Main.tile[x,y].TileType])).First();
                int[] levels = Enumerable.Range(355,65).Where(y => Main.tile[column,y].HasTile &&
                    TileID.Sets.Platforms[Main.tile[column,y].TileType] && ClearBody(column,y)).ToArray();
                if (levels.Length < 3) throw new InvalidOperationException("The copied real house does not contain the expected three existing platform levels.");
                Vector2 bottom = Stand(column,levels[^1]), top = Stand(column,levels[0]);
                housePlayer = Stand(column+4,levels[^1]);
                houseDefaultPlayer=housePlayer;
                if(!ClearBody(column+4,levels[^1])) throw new InvalidOperationException("Observer body is not clear.");
                Main.LocalPlayer.Center = housePlayer;
                SpawnCompanion(Main.LocalPlayer);
                if (CompanionNpc == null) throw new InvalidOperationException("No companion.");
                Loadout.Reset(); Loadout.EnsureStarterTools();
                if (Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") == RubyNativeCase)
                { BeginRubyNativeAcceptance(); return; }
                if (Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") == RubyTravelCase)
                { BeginRubyTravelAcceptance(); return; }
                if (Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") == P4PlanningDefenseCase)
                { BeginP4PlanningDefense(); return; }
                if (Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") == P4DirectionalExploreCase)
                { BeginP4DirectionalExplore(); return; }
                if (Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") == CrownAnvilCase)
                { BeginCrownAnvilAcceptance(); return; }
                if (Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") is P4LowerGapCase or P4CaveLedgeCase or P4CaveLedgeHouseCase or P4WestGapCase)
                { BeginP4LowerGapAcceptance(); return; }
                if (Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") == P4RoundTripCase)
                { BeginP4RoundTripInspectionAcceptance(); return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="physics_view_native")
                { RunPhysicsViewNativeAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="overview_native")
                { RunOverviewNativeAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="stage1_native")
                { BeginStage1NativeAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="stage2_native")
                { BeginStage2NativeAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="stage3_mine_baseline")
                { BeginStage3MineAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="stage3_plan_replay")
                { RunStage3PlanReplayAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="stage3_materials_parse")
                { RunStage3MaterialsParseAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="stage3_revision_native")
                { BeginStage3RevisionNativeAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="world_knowledge_native")
                { BeginWorldKnowledgeNativeAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="long_travel_native")
                { BeginLongTravelNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="long_travel_model_target")
                { BeginLongTravelModelTarget();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") is "basic_native" or "basic_natural")
                { BeginBasicAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="basic_replay")
                { BeginBasicReplay();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="basic_skills")
                { BeginBasicSkills();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="autonomous_resource")
                { BeginAutonomousResourceAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") is "ordered_goal_native" or "ordered_goal_door_native")
                { BeginOrderedGoalAcceptance(Stand(column,levels[^2]),Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="ordered_goal_door_native");return; }
                if(NaturalFlashSelected)
                { BeginNaturalFlashAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") is "advanced_workstation" or "advanced_supplies")
                { BeginAdvancedAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") is "long_goal_supplies" or "long_goal_supplies_native" or "autonomous_closure_natural")
                { BeginLongGoalAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="autonomous_closure_sources_moving")
                { BeginMovingSourceAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="autonomous_closure_sources_native")
                { BeginAutonomousClosureSources();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="long_goal_stone_readonly")
                { RunLongGoalStoneReadonly();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="long_goal_gel_native")
                { BeginLongGoalGelNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="task_admission_runtime")
                { BeginTaskAdmissionAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")==AgentRecoveryNativeCase)
                { BeginAgentRecoveryNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")==PausedQueryNativeCase)
                { BeginPausedQueryNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")==ChatRoutingNativeCase)
                { RunChatRoutingNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")==PausedAnswerNativeCase)
                { BeginPausedAnswerNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")==PausedStructureNativeCase)
                { BeginPausedStructureNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")==PausedInitialAnswerNativeCase)
                { BeginPausedInitialAnswerNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") is PrimaryAbsentResponseCase or PrimaryRespawnResponseCase)
                { BeginPrimaryIncarnationNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="autonomous_shelter")
                { BeginShelterAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="shelter_audit_native")
                { RunShelterAuditNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="shelter_transit_native")
                { BeginShelterTransit();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="shelter_ebonwood_native")
                { BeginShelterTransit();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="shelter_mining_native")
                { RunShelterMiningNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="observation_archive_native")
                { RunObservationArchiveNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="spatial_observation_archive_native")
                { RunSpatialObservationArchiveNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="shelter_build_preview_native")
                { RunShelterBuildPreviewNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="shelter_protection_origin_native")
                { RunShelterProtectionOriginNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="shelter_herb_native")
                { RunShelterHerbNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="sources_native")
                { BeginSourcesNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="sources_execute_native")
                { BeginSourcesExecuteNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="weapon_visual_native")
                { BeginWeaponVisual();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") is "complex_combat_staff" or "complex_combat_yoyo")
                { BeginComplexCombat();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="combat_floor_chase_native")
                { BeginCombatFloorChase();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="near_door_follow_native")
                { BeginNearDoorFollow();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="continuous_basics_native")
                { BeginContinuousBasics();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="local_boss_attack_native")
                { BeginLocalBossAttack();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") is BossReviewNativeCase or BossReviewTerminalApiCase or BossReviewTerminalOutputCase or BossReviewCapturedWaitCase or
                    BossReviewCompanionRespawnCase or BossReviewReplacedBossCase or BossPursuitTimeoutCase)
                { BeginBossReviewNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="visual_snapshot_native")
                { BeginVisualSnapshot();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="basic_combat_visual_native")
                { BeginBasicCombatVisual();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="sources_landing_replay_native")
                { BeginSourcesLandingReplay();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="shelter_guard_native")
                { BeginShelterGuardNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")==PlaceProtectionNativeCase)
                { BeginPlaceProtectionNative();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="shelter_mining_replay")
                { BeginShelterMiningReplay();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="shelter_ebonwood_mine_native")
                { BeginShelterMiningReplay();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_SURVEY_AUDIT")=="1")
                { RunHouseNativeSurveyAudit();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_DESIGN_NATIVE")=="1")
                { RunHouseDesignNativeAcceptance();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_SURVEY_ONLY")=="1")
                { BeginHouseSurveyOnly();return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_OBSERVE")=="1")
                { RunHouseObservationAcceptance(); return; }
                if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_DIRECTED")=="1")
                { BeginDirectedMineAcceptance(); return; }
                if (Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CONTINUOUS")=="1")
                { BeginContinuousHouse(); return; }
                houseRoutes.Add(("idle",bottom,bottom));
                houseRoutes.Add(("follow_player_turns",bottom,housePlayer));
                if(ClearBody(column+14,levels[^1]))
                {
                    houseRoutes.Add(("follow_catch_up",Stand(column+14,levels[^1]),housePlayer));
                    houseRoutes.Add(("follow_restart",bottom,Stand(column+14,levels[^1])));
                }
                houseRoutes.Add(("platform_ascent",bottom,top));
                houseRoutes.Add(("platform_descent",top,bottom));
                // Room endpoints come from the actual nearest walkable floor on each storey.
                foreach (int y in Enumerable.Range(levels[0]+1,25).Where(y=>Enumerable.Range(3195,49).Count(x=>
                    Main.tile[x,y].HasTile && Main.tileSolid[Main.tile[x,y].TileType] &&
                    !Main.tileSolidTop[Main.tile[x,y].TileType] && !Main.tile[x,y].IsActuated)>=18))
                {
                    Vector2? room = Enumerable.Range(3210,33).OrderBy(x=>Math.Abs(x-3220)).Where(x => ClearBody(x,y) &&
                        Main.tile[x,y].HasTile && Main.tileSolid[Main.tile[x,y].TileType] &&
                        !Main.tileSolidTop[Main.tile[x,y].TileType] && Main.tile[x,y-1].WallType>WallID.None)
                        .Select(x => (Vector2?)Stand(x,y)).FirstOrDefault();
                    if (room.HasValue) houseRoutes.Add(($"room_{y}",bottom,room.Value));
                    if(houseRoutes.Count(route=>route.Id.StartsWith("room_"))>=3)break;
                }
                houseRouteIndex = 0; houseRunning = true; houseFinished = false;
                BeginHouseCase();
            }
            catch (Exception e) { houseFinished=true; houseRunning=false; TestLog("house setup failed: "+e); SaveHouseReport("failed_setup", e.Message); }
        });
    }
    private static Vector2 Stand(int x,int floorY) => new(x*16f+8,floorY*16f-21);
    private static bool ClearBody(int x,int floorY)
    {
        Vector2 center=Stand(x,floorY);
        return !Collision.SolidCollision(center-new Vector2(10,21),20,42);
    }
    private void BeginHouseCase()
    {
        Stop(); Executor.Clear(); Executor.ForgetBlockedGoal();
        var r=houseRoutes[houseRouteIndex]; NPC npc=CompanionNpc!;
        housePlayer=r.Id.StartsWith("follow_")?r.To:houseDefaultPlayer;
        npc.Center=r.From; npc.velocity=Vector2.Zero;
        houseCase=new HouseCase { Id=r.Id,Start=r.From,Target=r.To,Started=DateTime.UtcNow,
            LastPosition=r.From,Min=r.From,Max=r.From,LastDirection=npc.direction };
        houseCases.Add(houseCase);
        Executor.ResetNavigationTelemetry();
        if(r.Id=="idle") Executor.Apply(new AIPlan { Action=AIActionType.Wait,Hold=true });
        else if(r.Id.StartsWith("follow_")) Executor.Apply(new AIPlan { Action=AIActionType.Follow });
        else Executor.Apply(new AIPlan { Action=AIActionType.Explore,X=(int)(r.To.X/16),Y=(int)(r.To.Y/16) });
        TestLog($"house case {r.Id} start={r.From/16} target={r.To/16}");
        SaveHouseReport("running","");
    }
    private void TickHouseAcceptance()
    {
        if (rubyNativeRunning) { TickRubyNativeAcceptance(); return; }
        if (rubyTravelRunning) { TickRubyTravelAcceptance(); return; }
        if (p4DefenseRunning) { TickP4PlanningDefense(); return; }
        if (p4DirectionalExploreRunning) { TickP4DirectionalExplore(); return; }
        if (crownAnvilRunning) { TickCrownAnvilAcceptance(); return; }
        if (p4LowerGapRunning) { TickP4LowerGapAcceptance(); return; }
        if (p4RoundTripRunning) { TickP4RoundTripInspectionAcceptance(); return; }
        if(stage1NativeStarted && !stage1NativeFinished){TickStage1NativeAcceptance();return;}
        if(stage2NativeStarted && !stage2NativeFinished){TickStage2NativeAcceptance();return;}
        if(stage3MineStarted && !stage3MineFinished){TickStage3MineAcceptance();return;}
        if(stage3RevisionStarted && !stage3RevisionFinished){TickStage3RevisionNativeAcceptance();return;}
        if(worldKnowledgeNativeStarted && !worldKnowledgeNativeFinished){TickWorldKnowledgeNativeAcceptance();return;}
        if(longTravelStarted && !longTravelFinished){TickLongTravelNative();return;}
        if(basicStarted && !basicFinished){TickBasicAcceptance();return;}
        if(basicReplayStarted && !basicReplayFinished){TickBasicReplay();return;}
        if(autonomousResourceStarted && !autonomousResourceFinished){TickAutonomousResourceAcceptance();return;}
        if(orderedGoalRunning){TickOrderedGoalAcceptance();return;}
        if(NaturalFlashSelected){TickNaturalFlashAcceptance();return;}
        if(bossLootSelectionRunning){TickBossLootSelectionNative();return;}
        if(advancedStarted && !advancedFinished){TickAdvancedAcceptance();return;}
        if(longGoalStarted && !longGoalFinished){TickLongGoalAcceptance();return;}
        if(movingSourceStarted && !movingSourceFinished){TickMovingSourceAcceptance();return;}
        if(closureSourcesStarted && !closureSourcesFinished){TickAutonomousClosureSources();return;}
        if(gelProbeStarted && !gelProbeFinished){TickLongGoalGelNative();return;}
        if(admissionTestStarted && !admissionTestFinished){TickTaskAdmissionAcceptance();return;}
        if(agentRecoveryRunning){TickAgentRecoveryNative();return;}
        if(pausedQueryRunning){TickPausedQueryNative();return;}
        if(pausedAnswerRunning){TickPausedAnswerNative();return;}
        if(pausedStructureRunning){TickPausedStructureNative();return;}
        if(pausedInitialAnswerRunning){TickPausedInitialAnswerNative();return;}
        if(primaryIncarnationRunning){TickPrimaryIncarnationNative();return;}
        if(shelterAcceptanceStarted && !shelterAcceptanceFinished){TickShelterAcceptance();return;}
        if(shelterTransitStarted && !shelterTransitFinished){TickShelterTransit();return;}
        if(shelterGuardStarted && !shelterGuardFinished){TickShelterGuardNative();return;}
        if(placeProtectionStarted && !placeProtectionFinished){TickPlaceProtectionNative();return;}
        if(sourcesNativeStarted && !sourcesNativeFinished){TickSourcesNative();return;}
        if(sourcesExecuteStarted && !sourcesExecuteFinished){TickSourcesExecuteNative();return;}
        if(weaponVisualStarted && !weaponVisualFinished){TickWeaponVisual();return;}
        if(complexCombatStarted && !complexCombatFinished){TickComplexCombat();return;}
        if(combatFloorStarted && !combatFloorFinished){TickCombatFloorChase();return;}
        if(nearDoorStarted && !nearDoorFinished){TickNearDoorFollow();return;}
        if(continuousBasicsStarted && !continuousBasicsFinished){TickContinuousBasics();return;}
        if(localBossStarted && !localBossFinished){TickLocalBossAttack();return;}
        if(BossReviewObserving){TickBossReviewNative();return;}
        if(visualSnapshotStarted && !visualSnapshotFinished){TickVisualSnapshot();return;}
        if(basicCombatVisualStarted && !basicCombatVisualFinished){TickBasicCombatVisual();return;}
        if(sourcesLandingStarted && !sourcesLandingFinished){TickSourcesLandingReplay();return;}
        if(shelterMiningReplayStarted && !shelterMiningReplayFinished){TickShelterMiningReplay();return;}
        if(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_DIRECTED")=="1")
        { TickDirectedMineAcceptance(); return; }
        if (Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CONTINUOUS")=="1")
        { TickContinuousHouse(); return; }
        if(!HouseMode || !houseRunning || houseFinished || Main.gameMenu || Main.worldID!=houseWorldId || houseCase==null) return;
        HouseCase c=houseCase; Player p=Main.LocalPlayer;
        p.Center=housePlayer; p.velocity=Vector2.Zero; p.immune=true; p.immuneTime=600; p.statLife=p.statLifeMax2;
        p.controlUseItem=p.controlUseTile=false; p.itemTime=p.itemAnimation=0;
        p.direction=c.Id=="follow_player_turns" && c.Frames%120<60 ? -1 : 1;
        foreach(NPC hostile in Main.npc) if(hostile.active && !hostile.friendly) hostile.active=false;
        NPC? npc=CompanionNpc;
        if(npc==null) { EndHouseCase(false,"companion missing"); return; }
        c.Frames++;
        c.Warps=Executor.NavigationTeleportCount;
        c.FacingChanges=Executor.NavigationFacingChanges;
        c.Jumps=Executor.NavigationJumpCount;
        c.LastDirection=npc.direction; c.LastVelocityY=npc.velocity.Y; c.LastPosition=npc.Center;
        c.Min=Vector2.Min(c.Min,npc.Center);c.Max=Vector2.Max(c.Max,npc.Center);
        if(c.Frames%15==0) c.Trace.Add(new { frame=c.Frames,x=npc.Center.X/16,y=npc.Center.Y/16,bottom=npc.Bottom.Y/16,
            vx=npc.velocity.X,vy=npc.velocity.Y,direction=npc.direction,task=Executor.StatusText() });
        bool stability=c.Id is "idle" or "follow_player_turns";
        bool followTravel=c.Id is "follow_catch_up" or "follow_restart";
        bool arrived = Math.Abs(npc.Center.X-c.Target.X)<24 && Math.Abs(npc.Bottom.Y-(c.Target.Y+21))<5 &&
            Math.Abs(npc.velocity.Y)<0.8f && ClearBody((int)(npc.Center.X/16),(int)Math.Round(npc.Bottom.Y/16)) &&
            Main.tile[(int)(npc.Center.X/16),(int)Math.Round(npc.Bottom.Y/16)].HasTile;
        if(followTravel) arrived=Vector2.Distance(npc.Center,c.Target)<=72f && Math.Abs(npc.velocity.X)<.5f && Math.Abs(npc.velocity.Y)<.8f;
        // Stop walking when the commanded endpoint is reached. Otherwise the ordinary
        // post-task follow action immediately leaves it before stability can be measured.
        if(!stability && arrived && c.StableArrivalFrames==0)
            Executor.Apply(new AIPlan { Action=AIActionType.Wait });
        c.StableArrivalFrames=arrived?c.StableArrivalFrames+1:0;
        if(stability && c.Frames>=600)
            EndHouseCase(c.Jumps==0 && c.Warps==0 && c.FacingChanges<=2 && c.Max.X-c.Min.X<6*16f && c.Max.Y-c.Min.Y<8f,"unstable arrival/jumping/facing");
        else if(!stability && c.Frames>5 && c.StableArrivalFrames>=6)
            EndHouseCase(c.Warps==0,"route used teleport");
        else if(!stability && Executor.BlockedPlan?.Action==AIActionType.Explore)
            EndHouseCase(false,"executor reported unreachable: "+Executor.LastStepResult);
        else if(!stability && (DateTime.UtcNow-c.Started).TotalSeconds>65)
            EndHouseCase(false,"route deadline exceeded: "+Executor.LastStepResult);
        else if(c.Frames%60==0) SaveHouseReport("running","");
    }

    private void RunHouseObservationAcceptance()
    {
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        npc.Center=new Vector2(3270.6123f*16,400.5f*16-21);npc.velocity=Vector2.Zero;
        p.Center=Stand(3173,395);p.velocity=Vector2.Zero;
        var initial=npc.Center;
        var before=new List<(bool has,int type,int wall,int slope,bool half,bool act)>();
        for(int y=398;y<=404;y++)for(int x=3267;x<=3273;x++)
        {Tile t=Main.tile[x,y];before.Add((t.HasTile,t.HasTile?t.TileType:-1,t.WallType,(int)t.Slope,t.IsHalfBlock,t.IsActuated));}
        var protectedBefore=new List<(bool has,int type,int wall,int slope,bool half,bool act)>();
        for(int y=399;y<=405;y++)for(int x=3185;x<=3191;x++)
        {Tile t=Main.tile[x,y];protectedBefore.Add((t.HasTile,t.HasTile?t.TileType:-1,t.WallType,(int)t.Slope,t.IsHalfBlock,t.IsActuated));}
        string terrain=AgentKnowledge.Query("3267,398,7,7","terrain",npc,p,Executor);
        string route=AgentKnowledge.Query("player","route",npc,p,Executor);
        bool firstPhaseMoved=npc.Center!=initial;
        // A second documented setup puts the observer within the tool's native range of the house.
        npc.Center=Stand(3173,395);npc.velocity=Vector2.Zero;Vector2 protectedStart=npc.Center;
        string protectedTerrain=AgentKnowledge.Query("3185,399,7,7","terrain",npc,p,Executor);
        bool nativeMatch=true,foundHalf=false,protectionMatch=true,resourceMatch=true;
        using var terrainDoc=JsonDocument.Parse(terrain);
        var root=terrainDoc.RootElement;
        var measurement=root.GetProperty("measurement");
        foreach(var row in measurement.GetProperty("tileRuns").EnumerateArray())
        {
            int y=row.GetProperty("y").GetInt32();
            foreach(var run in row.GetProperty("runs").EnumerateArray())
            {
                int x0=run[0].GetInt32(),count=run[1].GetInt32(),type=run[2].GetInt32();
                int slope=run[6].GetInt32(),half=run[7].GetInt32();
                for(int x=x0;x<x0+count;x++)
                {
                    Tile t=Main.tile[x,y];
                    nativeMatch &= type==(t.HasTile?(int)t.TileType:-1) && slope==(int)t.Slope && (half!=0)==t.IsHalfBlock;
                    if(x==3269 && y==400 && t.HasTile && t.IsHalfBlock && half!=0)foundHalf=true;
                }
            }
        }
        var miningFacts=root.GetProperty("miningFacts");int protectionCells=0,resourceCells=0;
        foreach(var row in miningFacts.GetProperty("miningProtectionRuns").EnumerateArray())
        {
            int y=row.GetProperty("y").GetInt32();
            foreach(var run in row.GetProperty("runs").EnumerateArray())
            {
                int x0=run[0].GetInt32(),count=run[1].GetInt32();
                for(int x=x0;x<x0+count;x++)
                {
                    Tile t=Main.tile[x,y];protectionCells++;
                    protectionMatch &= t.HasTile && (run[2].GetInt32()!=0)==AIExecutor.IsPartOfHouseOrStructure(x,y) &&
                        (run[3].GetInt32()!=0)==WorldGen.CanKillTile(x,y) && (run[4].GetInt32()!=0)==t.IsActuated;
                }
            }
        }
        var classes=miningFacts.GetProperty("resourceClasses").EnumerateArray().Select(v=>v.GetString()??"").ToArray();
        foreach(var row in miningFacts.GetProperty("resourceTargetRuns").EnumerateArray())
        {
            int y=row.GetProperty("y").GetInt32();
            foreach(var run in row.GetProperty("runs").EnumerateArray())
            {
                int x0=run[0].GetInt32(),count=run[1].GetInt32(),classIndex=run[2].GetInt32();
                for(int x=x0;x<x0+count;x++)
                {
                    resourceCells++;
                    var cell=NavigationWorldObserver.ReadMiningCell(x,y);
                    resourceMatch &= cell.HasTile && classIndex>=0 && classIndex<classes.Length &&
                        classes[classIndex]==(string.IsNullOrEmpty(cell.ResourceTarget)?"none":cell.ResourceTarget);
                }
            }
        }
        int occupied=before.Count(t=>t.has);
        protectionMatch &= protectionCells==occupied;resourceMatch &= resourceCells==occupied;
        using var protectedDoc=JsonDocument.Parse(protectedTerrain);
        var protectedFacts=protectedDoc.RootElement.GetProperty("miningFacts");
        bool protected3187Seen=false,protected3187Match=false;
        foreach(var row in protectedFacts.GetProperty("miningProtectionRuns").EnumerateArray())
        {
            int y=row.GetProperty("y").GetInt32();
            foreach(var run in row.GetProperty("runs").EnumerateArray())
            {
                int x0=run[0].GetInt32(),count=run[1].GetInt32();
                for(int x=x0;x<x0+count;x++)
                {
                    Tile t=Main.tile[x,y];
                    protectionMatch &= t.HasTile && (run[2].GetInt32()!=0)==AIExecutor.IsPartOfHouseOrStructure(x,y) &&
                        (run[3].GetInt32()!=0)==WorldGen.CanKillTile(x,y);
                    if(x==3187 && y==401)
                    {protected3187Seen=true;protected3187Match=run[2].GetInt32()!=0 && AIExecutor.IsPartOfHouseOrStructure(x,y);}
                }
            }
        }
        int changed=0,index=0;
        for(int y=398;y<=404;y++)for(int x=3267;x<=3273;x++)
        {
            Tile t=Main.tile[x,y];var prior=before[index++];
            if(t.HasTile!=prior.has || (t.HasTile && t.TileType!=prior.type) || t.WallType!=prior.wall ||
                (int)t.Slope!=prior.slope || t.IsHalfBlock!=prior.half || t.IsActuated!=prior.act)changed++;
        }
        index=0;
        for(int y=399;y<=405;y++)for(int x=3185;x<=3191;x++)
        {
            Tile t=Main.tile[x,y];var prior=protectedBefore[index++];
            if(t.HasTile!=prior.has || (t.HasTile && t.TileType!=prior.type) || t.WallType!=prior.wall ||
                (int)t.Slope!=prior.slope || t.IsHalfBlock!=prior.half || t.IsActuated!=prior.act)changed++;
        }
        using var routeDoc=JsonDocument.Parse(route);
        var routeRoot=routeDoc.RootElement;var r=routeRoot.GetProperty("measurement");
        bool routeReadOnly=!r.GetProperty("travelExecuted").GetBoolean() && !r.GetProperty("worldModified").GetBoolean();
        bool pass=root.GetProperty("available").GetBoolean() && routeRoot.GetProperty("available").GetBoolean() &&
            nativeMatch && foundHalf && protectionMatch && resourceMatch && protected3187Seen && protected3187Match &&
            changed==0 && !firstPhaseMoved && npc.Center==protectedStart && routeReadOnly;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-observation-result.json"),JsonSerializer.Serialize(new{
            status=pass?"passed":"failed",world=Main.worldName,worldId=Main.worldID,nativeMatch,foundHalf,protectionMatch,resourceMatch,
            terrainJsonChars=terrain.Length,miningFactsChars=miningFacts.GetRawText().Length,protected3187Seen,protected3187Match,
            protectedTerrainJsonChars=protectedTerrain.Length,protectedMiningFactsChars=protectedFacts.GetRawText().Length,changed,
            npcMovedDuringQuery=firstPhaseMoved || npc.Center!=protectedStart,secondSetup=new{x=protectedStart.X/16,y=protectedStart.Y/16},
            routeReadOnly,terrain=terrainDoc.RootElement,protectedTerrain=protectedDoc.RootElement,route=routeRoot,
            scope="Native read-only query comparison, not proof that a returned path was physically walked."
        },new JsonSerializerOptions{WriteIndented=true}));
        TestLog($"house observation {(pass?"passed":"failed")} nativeMatch={nativeMatch} half={foundHalf} changed={changed}");
        houseFinished=true;Stop();
    }
    private bool directedMineStarted,directedMineFinished;
    private bool directedSeedGrassStripped;
    private int directedX,directedY,directedFrames,directedFirstRemovedFrame=-1,directedInitialStone;
    private int directedTileType,directedItemType,directedRequested;
    private string directedTarget="";
    private Vector2 directedStart;
    private DateTime directedAt;
    private readonly HashSet<(int x,int y)> directedSource=new();
    private readonly Dictionary<(int x,int y),(bool has,int type,int wall)> directedBefore=new();
    private readonly List<object> directedRemoved=new(),directedTrace=new();
    private readonly HashSet<(int x,int y)> directedReported=new();
    private readonly List<object> directedOutsideChanges=new();
    private readonly HashSet<(int x,int y)> directedOutsideReported=new();
    private void BeginDirectedMineAcceptance()
    {
        directedTarget=Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_DIRECTED_TARGET")=="grass"?"grass":"stone";
        directedTileType=directedTarget=="grass"?TileID.Grass:TileID.Stone;
        directedItemType=directedTarget=="grass"?ItemID.DirtBlock:ItemID.StoneBlock;
        directedRequested=directedTarget=="grass"?1:2;
        // Search only natural, already existing tiles close to a real, collision-clear standing place.
        var candidates=new List<(int x,int y,int standX,int floor,double distance)>();
        for(int x=3000;x<=3400;x++)for(int y=380;y<=600;y++)
        {
            Tile t=Main.tile[x,y];
            if(!t.HasTile || t.TileType!=directedTileType || t.IsActuated ||
                t.WallType!=WallID.None && t.WallType<Main.wallHouse.Length && Main.wallHouse[t.WallType] ||
                AIExecutor.IsPartOfHouseOrStructure(x,y) || !WorldGen.CanKillTile(x,y))continue;
            bool exposed=false;
            foreach(var d in new[]{(-1,0),(1,0),(0,-1),(0,1)})
                if(!Main.tile[x+d.Item1,y+d.Item2].HasTile){exposed=true;break;}
            if(!exposed)continue;
            for(int sx=x-5;sx<=x+5;sx++)for(int floor=y-4;floor<=y+4;floor++)
            {
                Tile footing=Main.tile[sx,floor];
                if(!footing.HasTile || footing.IsActuated || !Main.tileSolid[footing.TileType] || !ClearBody(sx,floor))continue;
                double dist=Vector2.Distance(Stand(sx,floor),new Vector2(x*16+8,y*16+8));
                if(dist<=88)candidates.Add((x,y,sx,floor,dist));
            }
        }
        foreach(var candidate in candidates.OrderBy(c=>c.distance))
        {
            var visited=new HashSet<(int x,int y)>();var pending=new Queue<(int x,int y)>();
            pending.Enqueue((candidate.x,candidate.y));
            while(pending.Count>0 && visited.Count<256)
            {
                var p=pending.Dequeue();
                if(!WorldGen.InWorld(p.x,p.y,2) || visited.Contains(p))continue;
                Tile tile=Main.tile[p.x,p.y];if(!tile.HasTile || tile.TileType!=directedTileType || tile.IsActuated)continue;
                visited.Add(p);pending.Enqueue((p.x+1,p.y));pending.Enqueue((p.x-1,p.y));pending.Enqueue((p.x,p.y+1));pending.Enqueue((p.x,p.y-1));
            }
            if(visited.Count<directedRequested)continue;
            directedX=candidate.x;directedY=candidate.y;directedStart=Stand(candidate.standX,candidate.floor);
            directedSource.UnionWith(visited);break;
        }
        if(directedSource.Count<directedRequested)
        {
            directedMineFinished=true;
            File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-directed-result.json"),
                JsonSerializer.Serialize(new{status="failed_setup",error="No existing natural source and clear standing position found.",target=directedTarget}));return;
        }
        for(int x=Math.Max(2,directedX-100);x<=Math.Min(Main.maxTilesX-3,directedX+100);x++)
            for(int y=Math.Max(2,directedY-40);y<=Math.Min(Main.maxTilesY-3,directedY+40);y++)
        {Tile t=Main.tile[x,y];directedBefore[(x,y)]=(t.HasTile,t.HasTile?(int)t.TileType:-1,(int)t.WallType);}
        NPC npc=CompanionNpc!;Player player=Main.LocalPlayer;Stop();Executor.Clear();Executor.ForgetBlockedGoal();
        npc.Center=directedStart;npc.velocity=Vector2.Zero;player.Center=Stand(3173,395);player.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();directedInitialStone=Loadout.CountItem(directedItemType);
        Executor.ResetNavigationTelemetry();directedAt=DateTime.UtcNow;directedMineStarted=true;
        Executor.Apply(new AIPlan{Action=AIActionType.Mine,Target=directedTarget,Count=directedRequested,X=directedX,Y=directedY});
        TestLog($"directed {directedTarget} setup seed={directedX},{directedY} sourceCells={directedSource.Count} npc={directedStart/16}");
        SaveDirectedMine("running","");
    }
    private void TickDirectedMineAcceptance()
    {
        if(!HouseMode || Main.gameMenu || !directedMineStarted || directedMineFinished || CompanionNpc==null)return;
        directedFrames++;NPC npc=CompanionNpc;Player player=Main.LocalPlayer;
        player.immune=true;player.immuneTime=600;player.statLife=player.statLifeMax2;
        foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        foreach(var pair in directedBefore)
        {
            Tile t=Main.tile[pair.Key.x,pair.Key.y];
            if(!directedSource.Contains(pair.Key) && !directedOutsideReported.Contains(pair.Key) &&
                (t.HasTile!=pair.Value.has || (t.HasTile && t.TileType!=pair.Value.type) || t.WallType!=pair.Value.wall))
            {
                directedOutsideReported.Add(pair.Key);
                directedOutsideChanges.Add(new{frame=directedFrames,x=pair.Key.x,y=pair.Key.y,
                    beforeType=pair.Value.type,afterType=t.HasTile?(int)t.TileType:-1,beforeWall=pair.Value.wall,afterWall=(int)t.WallType});
            }
            if(!pair.Value.has || directedReported.Contains(pair.Key))continue;
            bool selectedGrassBecameDirt=directedTarget=="grass" && pair.Key==(directedX,directedY) &&
                pair.Value.type==TileID.Grass && t.HasTile && t.TileType==TileID.Dirt;
            if(selectedGrassBecameDirt)directedSeedGrassStripped=true;
            if((!t.HasTile || t.TileType!=pair.Value.type) && !selectedGrassBecameDirt)
            {
                directedReported.Add(pair.Key);
                bool seed=pair.Key==(directedX,directedY);
                if(seed && directedFirstRemovedFrame<0)directedFirstRemovedFrame=directedFrames;
                directedRemoved.Add(new{frame=directedFrames,x=pair.Key.x,y=pair.Key.y,beforeType=pair.Value.type,
                    afterType=t.HasTile?(int)t.TileType:-1,seed,inInitialSource=directedSource.Contains(pair.Key)});
            }
        }
        if(directedFrames%60==0)
        {
            var path=ContinuousField("navPath") as System.Collections.IEnumerable;
            directedTrace.Add(new{frame=directedFrames,utc=DateTime.UtcNow,x=npc.Center.X/16,y=npc.Center.Y/16,bottom=npc.Bottom.Y/16,
                vx=npc.velocity.X,vy=npc.velocity.Y,action=Executor.CurrentAction.ToString(),status=Executor.StatusText(),
                path=path?.Cast<object>().Take(20).Select(p=>p.ToString()).ToArray(),pathIndex=ContinuousField("navIndex"),
                failure=Executor.NavigationFailureReason,blocked=Executor.BlockedReason,last=Executor.LastStepResult,
                actualInventory=Loadout.CountItem(directedItemType),teleports=Executor.NavigationTeleportCount});
            SaveDirectedMine("running","");
        }
        if(Executor.NavigationTeleportCount>0){FinishDirectedMine(false,"teleport during directed mining");return;}
        if(Executor.BlockedPlan?.Action==AIActionType.Mine){FinishDirectedMine(false,"directed mining blocked: "+Executor.LastStepResult);return;}
        if(Loadout.CountItem(directedItemType)-directedInitialStone>=directedRequested)
        {
            bool first=directedRemoved.Count>0 && !Main.tile[directedX,directedY].HasTile && directedReported.Contains((directedX,directedY)) &&
                directedRemoved[0].GetType().GetProperty("seed")?.GetValue(directedRemoved[0]) is true;
            bool onlySource=directedReported.All(p=>directedSource.Contains(p)) && directedOutsideChanges.Count==0;
            FinishDirectedMine(first&&onlySource,first&&onlySource?"":"Seed was not first removed or unrelated terrain was removed.");return;
        }
        if((DateTime.UtcNow-directedAt).TotalSeconds>120)FinishDirectedMine(false,"directed mining deadline");
    }
    private void FinishDirectedMine(bool pass,string error)
    {
        directedMineFinished=true;SaveDirectedMine(pass?"passed":"failed",error);Stop();
        TestLog($"directed {directedTarget} {(pass?"passed":"failed")}: {error}");
    }
    private void SaveDirectedMine(string status,string error)
    {
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-directed-result.json"),JsonSerializer.Serialize(new{
            status,error,world=Main.worldName,worldId=Main.worldID,seed=new{x=directedX,y=directedY},
            start=new{x=directedStart.X/16,y=directedStart.Y/16},target=directedTarget,requested=directedRequested,source=directedSource.Select(p=>new{x=p.x,y=p.y}),
            seedHasTile=Main.tile[directedX,directedY].HasTile,seedTile=Main.tile[directedX,directedY].HasTile?(int)Main.tile[directedX,directedY].TileType:-1,
            grassStrippedBeforeRemoval=directedSeedGrassStripped,
            firstSeedRemovedFrame=directedFirstRemovedFrame,actualGain=Loadout.CountItem(directedItemType)-directedInitialStone,
            teleports=Executor.NavigationTeleportCount,frames=directedFrames,removed=directedRemoved,outsideChanges=directedOutsideChanges,trace=directedTrace,
            snapshotBounds=new{left=directedX-100,right=directedX+100,top=directedY-40,bottom=directedY+40},scope="Existing native source; setup reposition only; no generated terrain; hostiles removed."
        },new JsonSerializerOptions{WriteIndented=true}));
    }

    private sealed class ContinuousCase
    {
        public string id="",status="running",error="",phase="follow";
        public string recordingSmokeError="";
        public Vector2 start,playerStart;
        public DateTime started;
        public int frames,jumps,turns,teleports,stable,phaseFrames;
        public int closeMovingFrames;
        public int playerFloorCandidate=int.MinValue,playerFloorStable;
        public float maxDisplacement;
        public bool movingRight=true;
        public bool waitHoldVerified;
        public bool waitResumeVerified;
        public float minPlayerBottom=float.MaxValue,maxPlayerBottom=float.MinValue;
        public float minPlayerX=float.MaxValue,maxPlayerX=float.MinValue;
        public readonly List<object> trace=new();
        public readonly List<object> detailedTrace=new();
        public readonly HashSet<int> playerStandingFloors=new();
    }
    private readonly List<ContinuousCase> continuousCases=new();
    private readonly List<(string id,Vector2 npc,Vector2 player,string kind)> continuousRoutes=new();
    private ContinuousCase? continuousCase;
    private int continuousIndex,continuousWoodStart,continuousStoneStart;
    private bool continuousLlmStarted,continuousLlmFinished;
    private int continuousLlmRequests;
    private int continuousLlmActiveFrames;
    private object? minePreviewBefore, minePreviewAfter, minePreviewMidBefore, minePreviewMidAfter;
    private string? minePreviewValid, minePreviewMissing, minePreviewUnknown, minePreviewProtected, minePreviewMid;
    private string continuousLlmRecordingError="";
    private bool continuousLlmPhysicalEverMet;
    private DateTime continuousLlmAt;
    private readonly List<object> continuousLlmTrace=new();
    private readonly List<object> continuousMaterialDeltas=new();
    private bool continuousReplayedFirst;
    private JsonElement? continuousReplayPlanning;
    private long continuousMaterialLastSequence;
    private int continuousMaterialWood,continuousMaterialStone;
    private bool MaterialsResumeCase=>Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="autonomous_materials_resume";
    private bool NaturalTravelCase=>Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="long_travel_natural";
    private bool naturalFarVerified,naturalComeVerified;
    private long naturalLastSequence;
    private readonly List<object> naturalTravelOutcomes=new();
    private bool AutonomousMaterialsCase=>Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") is "autonomous_materials" or "world_materials" || MaterialsResumeCase;
    private int ContinuousLlmRequestLimit=>int.TryParse(Environment.GetEnvironmentVariable("AITEAMMATE_RESOURCE_REQUEST_LIMIT"),out int limit)?Math.Clamp(limit,1,NaturalTravelCase?8:MaterialsResumeCase?11:15):NaturalTravelCase?8:MaterialsResumeCase?11:15;
    private bool ContinuousLlmWithinTime=>DateTime.UtcNow-continuousLlmAt<TimeSpan.FromMinutes(3);
    private bool ContinuousLlmAllowed=>continuousLlmStarted && !continuousLlmFinished &&
        PrimaryDecisionRequestCount-continuousLlmRequests<ContinuousLlmRequestLimit && ContinuousLlmWithinTime;
    private object? ContinuousField(string name)=>typeof(AIExecutor).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(Executor);
    private void BeginContinuousHouse()
    {
        if((Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="resource_only" || AutonomousMaterialsCase || NaturalTravelCase) &&
            Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CONTINUOUS_LLM")=="1")
        {BeginContinuousLlm();return;}
        continuousRoutes.Clear();continuousCases.Clear();continuousIndex=0;
        continuousRoutes.Add(("idle_wait",Stand(3173,395),Stand(3177,395),"idle_wait"));
        continuousRoutes.Add(("idle_player_turns",Stand(3173,395),Stand(3177,395),"idle_turn"));
        continuousRoutes.Add(("wait_for_player_stays",Stand(3173,395),Stand(3187,395),"waiting"));
        foreach(int floor in new[]{401,393,387})
        {
            if(!ClearBody(3220,floor))throw new InvalidOperationException($"Room setup obstructed at 3220,{floor}.");
            continuousRoutes.Add(($"room_{floor}_outside",Stand(3220,floor),Stand(3173,395),"follow"));
            continuousRoutes.Add(($"room_{floor}_roof",Stand(3220,floor),Stand(3173,381),"follow"));
        }
        if(!ClearBody(3219,378))throw new InvalidOperationException("Roof room setup obstructed.");
        continuousRoutes.Add(("roof_room_exit",Stand(3219,378),Stand(3173,381),"follow"));
        continuousRoutes.Add(("moving_player_follow",Stand(3173,395),Stand(3177,395),"moving"));
        continuousRoutes.Add(("moving_player_platforms",Stand(3173,395),Stand(3174,395),"moving_platform"));
        continuousRoutes.Add(("mine_wood_stone_return",Stand(3220,401),Stand(3173,395),"mine"));
        continuousRoutes.Add(("halfblock_3270_diagnostic",new Vector2(3270.6123f*16,400.5f*16-21),Stand(3173,395),"diagnostic"));
        Vector2 longStart=new(3357.9612f*16,402f*16-21);
        if(!Collision.SolidCollision(longStart-new Vector2(10,21),20,42))
            continuousRoutes.Add(("long_return_3357_diagnostic",longStart,Stand(3173,395),"diagnostic"));
        continuousRoutes.Add(("mine_projection_3319",new Vector2(3319.4028f*16,400.46533f*16),Stand(3173,395),"diagnostic"));
        continuousRoutes.Add(("mine_preview_native",new Vector2(3319.4028f*16,400.46533f*16),Stand(3173,395),"diagnostic"));
        string? selectedCase=Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE");
        if(string.IsNullOrWhiteSpace(selectedCase))continuousRoutes.RemoveAll(route=>route.kind=="diagnostic");
        if(!string.IsNullOrWhiteSpace(selectedCase))
        {
            continuousRoutes.RemoveAll(route=>route.id!=selectedCase);
            if(continuousRoutes.Count!=1)throw new InvalidOperationException("Unknown continuous diagnostic case.");
        }
        BeginContinuousCase();
    }
    private void BeginContinuousCase()
    {
        Stop();Executor.Clear();Executor.ForgetBlockedGoal();
        var r=continuousRoutes[continuousIndex];NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        npc.Center=r.npc;npc.velocity=Vector2.Zero;p.Center=r.player;p.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();Executor.ResetNavigationTelemetry();
        continuousWoodStart=Loadout.CountItem(ItemID.Wood);continuousStoneStart=Loadout.CountItem(ItemID.StoneBlock);
        continuousCase=new ContinuousCase{id=r.id,start=r.npc,playerStart=r.player,started=DateTime.UtcNow,phase=r.kind=="mine"?"mine_wood":"follow"};
        continuousCases.Add(continuousCase);
        if(r.kind=="waiting") {continuousCase.phase="waiting";ApplyPlan(new AIPlan{WaitForPlayer=true},null);}
        else if(r.id is "mine_projection_3319" or "mine_preview_native")
        {
            if(!Main.tile[3319,394].HasTile || Main.tile[3319,394].TileType!=TileID.Stone)
            {EndContinuousCase(false,"actual seed 3319,394 absent or not stone");return;}
            if(r.id=="mine_preview_native")
            {
                minePreviewBefore=CaptureMinePreviewState(npc,p);
                minePreviewValid=AgentKnowledge.Query("stone","mine",npc,p,Executor,3319,394);
                minePreviewMissing=AgentKnowledge.Query("stone","mine",npc,p,Executor);
                minePreviewUnknown=AgentKnowledge.Query("unknown_resource","mine",npc,p,Executor,3319,394);
                minePreviewProtected=AgentKnowledge.Query("dirt","mine",npc,p,Executor,3187,401);
                minePreviewAfter=CaptureMinePreviewState(npc,p);
            }
            continuousCase.phase="mine_projection";Executor.Apply(new AIPlan{Action=AIActionType.Mine,Target="stone",Count=1,X=3319,Y=394});
        }
        else Executor.Apply(r.kind=="diagnostic" && r.id=="long_return_3357_diagnostic"?new AIPlan{Action=AIActionType.Come}:
            r.kind=="mine"?new AIPlan{Action=AIActionType.Mine,Target="wood",Count=1}:
            r.kind=="idle_wait"?new AIPlan{Action=AIActionType.Wait}:new AIPlan{Action=AIActionType.Follow});
        TestLog($"continuous case {r.id} setup npc={r.npc/16} player={r.player/16} phase={continuousCase.phase}");
        SaveContinuousHouse("running");
    }
    private void TickContinuousHouse()
    {
        if(continuousLlmStarted && !continuousLlmFinished){TickContinuousLlm();return;}
        if(!HouseMode || Main.gameMenu || continuousCase==null || continuousCase.status!="running" || CompanionNpc==null)return;
        var c=continuousCase;var route=continuousRoutes[continuousIndex];NPC npc=CompanionNpc;Player p=Main.LocalPlayer;
        c.frames++;c.phaseFrames++;c.jumps=Executor.NavigationJumpCount;c.turns=Executor.NavigationFacingChanges;c.teleports=Executor.NavigationTeleportCount;
        if(c.id=="mine_preview_native" && c.frames==15)
        {minePreviewMidBefore=CaptureMinePreviewState(npc,p);minePreviewMid=AgentKnowledge.Query("stone","mine",npc,p,Executor,3319,394);minePreviewMidAfter=CaptureMinePreviewState(npc,p);}
        c.maxDisplacement=Math.Max(c.maxDisplacement,Vector2.Distance(npc.Center,c.start));
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;p.controlUseItem=p.controlUseTile=false;
        foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        c.minPlayerBottom=Math.Min(c.minPlayerBottom,p.Bottom.Y/16);c.maxPlayerBottom=Math.Max(c.maxPlayerBottom,p.Bottom.Y/16);
        c.minPlayerX=Math.Min(c.minPlayerX,p.Center.X/16);c.maxPlayerX=Math.Max(c.maxPlayerX,p.Center.X/16);
        int observedFloor=(int)Math.Round(p.Bottom.Y/16);
        if(Math.Abs(p.velocity.Y)<0.1f && observedFloor==c.playerFloorCandidate)c.playerFloorStable++;
        else {c.playerFloorCandidate=observedFloor;c.playerFloorStable=Math.Abs(p.velocity.Y)<0.1f?1:0;}
        if(c.playerFloorStable>=6)c.playerStandingFloors.Add(observedFloor);
        if(route.kind=="moving")
        {
            if(p.Center.X/16>=3188)c.movingRight=false;
            else if(p.Center.X/16<=3174)c.movingRight=true;
            bool right=c.movingRight;
            p.controlLeft=!right;p.controlRight=right;p.controlJump=false;
            p.velocity.X=c.frames<=600?(right?3f:-3f):0;
        }
        else if(route.kind=="moving_platform")
        {
            p.controlLeft=false;p.controlRight=false;
            p.controlJump=c.frames<=600 && c.frames%110<32;
            if(c.frames<=600 && c.frames%110==1 && Math.Abs(p.velocity.Y)<0.1f)p.velocity.Y=-10f;
        }
        else {p.controlLeft=false;p.controlRight=false;p.controlJump=false;}
        if(route.kind=="idle_turn")p.direction=c.frames%120<60?-1:1;
        if(c.frames%60==0)
        {
            var path=ContinuousField("navPath") as System.Collections.IEnumerable;
            var points=path?.Cast<object>().Take(20).Select(o=>o.ToString()).ToArray();
            c.trace.Add(new{frame=c.frames,utc=DateTime.UtcNow,phase=c.phase,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,bottom=npc.Bottom.Y/16,vx=npc.velocity.X,vy=npc.velocity.Y,direction=npc.direction},
                player=new{x=p.Center.X/16,y=p.Center.Y/16,bottom=p.Bottom.Y/16,vx=p.velocity.X,vy=p.velocity.Y},action=Executor.CurrentAction.ToString(),
                status=Executor.StatusText(),path=points,pathIndex=ContinuousField("navIndex"),navBuiltAt=ContinuousField("navBuiltAt"),
                navFailures=ContinuousField("navigationFailures"),navGoal=ContinuousField("navGoal"),
                failure=Executor.NavigationFailureReason,blocked=Executor.BlockedReason,last=Executor.LastStepResult});
            if(c.id=="long_return_3357_diagnostic")
            {
                try { JsonSerializer.Serialize(CaptureContinuousLlmTrace(npc,p)); }
                catch(Exception e) { c.recordingSmokeError=e.GetType().Name+": "+e.Message; }
            }
        }
        if((c.id.StartsWith("room_387") && npc.Center.X/16>=3233 && npc.Center.X/16<=3240 ||
            c.id=="moving_player_follow" && npc.Center.X/16>=3182 && npc.Center.X/16<=3185 ||
            c.id=="halfblock_3270_diagnostic" || c.id=="mine_projection_3319" ||
            c.id=="long_return_3357_diagnostic" && npc.Center.X/16>=3265 && npc.Center.X/16<=3273) && c.detailedTrace.Count<240)
        {
            var path=ContinuousField("navPath") as System.Collections.IList;
            int index=(int)(ContinuousField("navIndex")??-1);
            var grounded=typeof(AIExecutor).GetMethod("IsGrounded",BindingFlags.NonPublic|BindingFlags.Static)?.Invoke(null,new object[]{npc});
            var slope=typeof(AIExecutor).GetMethod("HasSlopedSupport",BindingFlags.NonPublic|BindingFlags.Static)?.Invoke(null,new object[]{npc});
            var support=new List<object>();
            int supportLeft=c.id=="mine_projection_3319"?3316:c.id=="long_return_3357_diagnostic"?3267:(int)(npc.Left.X/16);
            int supportRight=c.id=="mine_projection_3319"?3322:c.id=="long_return_3357_diagnostic"?3272:(int)((npc.Right.X-0.01f)/16);
            int supportTop=c.id=="mine_projection_3319"?398:c.id=="long_return_3357_diagnostic"?398:(int)(npc.Bottom.Y/16)-1;
            int supportBottom=c.id=="mine_projection_3319"?404:c.id=="long_return_3357_diagnostic"?402:(int)(npc.Bottom.Y/16)+1;
            for(int tx=supportLeft;tx<=supportRight;tx++)
                for(int ty=supportTop;ty<=supportBottom;ty++)
                {Tile t=Main.tile[tx,ty];support.Add(new{x=tx,y=ty,type=t.HasTile?(int)t.TileType:-1,
                    frameX=(int)t.TileFrameX,frameY=(int)t.TileFrameY,slope=(int)t.Slope,half=t.IsHalfBlock,actuated=t.IsActuated});}
            c.detailedTrace.Add(new{frame=c.frames,tick=Main.GameUpdateCount,x=npc.Center.X,y=npc.Center.Y,bottom=npc.Bottom.Y,
                vx=npc.velocity.X,vy=npc.velocity.Y,oldVx=npc.oldVelocity.X,oldVy=npc.oldVelocity.Y,npc.collideX,npc.collideY,
                npcStairFall=npc.stairFall,collisionStairFall=Collision.stairFall,grounded,slopedSupport=slope,support,
                dropTicks=ContinuousField("platformDropTicks"),sourceBottom=ContinuousField("platformDropSourceBottom"),clearPlane=ContinuousField("platformDropClearPlane"),
                canFallThrough=Executor.DropThroughPlatforms,index,waypoint=path!=null&&index>=0&&index<path.Count?path[index]?.ToString():null,
                targetDistance=c.id=="mine_projection_3319"?Vector2.Distance(npc.Center,new Vector2(3319*16+8,394*16+8)):0,
                projectedTargetDistance=c.id=="mine_projection_3319" && TilePathfinder.TryResolveStand(npc.Center,out Point projected)?
                    Vector2.Distance(TilePathfinder.StandCenter(projected,npc.height),new Vector2(3319*16+8,394*16+8)):(float?)null,
                miningStand=ContinuousField("miningStand")?.ToString()});
        }
        if(c.teleports>0){EndContinuousCase(false,"teleport during continuous route");return;}
        if((c.id is "mine_projection_3319" or "mine_preview_native") && !Main.tile[3319,394].HasTile && Loadout.CountItem(ItemID.StoneBlock)>0)
        {EndContinuousCase(true,"");return;}
        if(route.kind=="waiting" && c.phase=="waiting" && c.frames>=480)
        {
            c.waitHoldVerified=Executor.HoldPosition && c.jumps==0 && c.turns==0 && c.maxDisplacement<8;
            if(!c.waitHoldVerified){EndContinuousCase(false,"wait_for_player moved, turned, jumped or released hold");return;}
            int requestsBefore=PrimaryDecisionRequestCount;
            HandlePlayerMessage(p,"继续");
            c.waitResumeVerified=!Executor.HoldPosition && !goalWaitingForPlayer && PrimaryDecisionRequestCount==requestsBefore;
            EndContinuousCase(c.waitResumeVerified,"new message did not release wait hold without a request");return;
        }
        if(route.kind=="waiting" && c.phase=="waiting")return;
        if((route.kind!="mine" || c.phase=="return") && Executor.BlockedPlan!=null)
        {EndContinuousCase(false,(c.id=="mine_projection_3319"?"mine blocked: ":"follow blocked: ")+Executor.BlockedReason);return;}
        if(route.kind=="mine")
        {
            if(c.phase=="mine_wood" && Loadout.CountItem(ItemID.Wood)>continuousWoodStart)
            {c.phase="mine_stone";c.phaseFrames=0;Executor.Apply(new AIPlan{Action=AIActionType.Mine,Target="stone",Count=1});}
            else if(c.phase=="mine_stone" && Loadout.CountItem(ItemID.StoneBlock)>continuousStoneStart)
            {c.phase="return";c.phaseFrames=0;Executor.Apply(new AIPlan{Action=AIActionType.Follow});}
            else if(Executor.BlockedPlan?.Action==AIActionType.Mine){EndContinuousCase(false,"mine blocked: "+Executor.LastStepResult);return;}
        }
        bool close=Vector2.Distance(npc.Center,p.Center)<72 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<6;
        bool dynamicRoute=route.kind is "moving" or "moving_platform";
        bool settled=dynamicRoute
            ? (bool)(ContinuousField("followPaused")??false) && Math.Abs(npc.Center.X-p.Center.X)<=112 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<6 &&
                Math.Abs(npc.velocity.Y)<=0.6f && Math.Abs(p.velocity.Y)<=0.6f
            : close;
        c.stable=settled?c.stable+1:0;
        if(route.kind is "idle_wait" or "idle_turn")
        {
            if(c.frames>=480){EndContinuousCase(c.jumps==0 && c.turns==0 && c.teleports==0 && c.maxDisplacement<8,"idle motion or facing change");return;}
            if(c.frames%60==0)SaveContinuousHouse("running");
            return;
        }
        if(c.frames<=600 && Vector2.Distance(npc.Center,p.Center)<128 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<24)c.closeMovingFrames++;
        if(route.kind=="moving" || route.kind=="moving_platform")
        {
            bool moved=route.kind=="moving" ? c.maxPlayerX-c.minPlayerX>=10 : c.playerStandingFloors.Count>=2 && c.playerStandingFloors.Max()-c.playerStandingFloors.Min()>=3;
            if(c.frames>=606 && c.closeMovingFrames>=360 && moved && c.stable>=6) {EndContinuousCase(true,"");return;}
        }
        else if((route.kind!="mine" || c.phase=="return") && c.stable>=6){EndContinuousCase(true,"");return;}
        if((DateTime.UtcNow-c.started).TotalSeconds>90)EndContinuousCase(false,"deadline: "+Executor.NavigationFailureReason+" "+Executor.LastStepResult);
        else if(c.frames%60==0)SaveContinuousHouse("running");
    }
    private void EndContinuousCase(bool pass,string error)
    {
        continuousCase!.status=pass?"passed":"failed";continuousCase.error=pass?"":error;
        if(continuousCase.id=="mine_preview_native")
            File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","mine-preview-native-result.json"),JsonSerializer.Serialize(new {
                status=continuousCase.status,error,frames=continuousCase.frames,teleports=Executor.NavigationTeleportCount,
                seedAfterHasTile=Main.tile[3319,394].HasTile,stoneBefore=continuousStoneStart,stoneAfter=Loadout.CountItem(ItemID.StoneBlock),
                valid=minePreviewValid,missing=minePreviewMissing,unknown=minePreviewUnknown,protectedSeed=minePreviewProtected,
                before=minePreviewBefore,after=minePreviewAfter,mid=minePreviewMid,midBefore=minePreviewMidBefore,midAfter=minePreviewMidAfter,
                initialSnapshotEqual=JsonSerializer.Serialize(minePreviewBefore)==JsonSerializer.Serialize(minePreviewAfter),
                midSnapshotEqual=minePreviewMidBefore!=null && JsonSerializer.Serialize(minePreviewMidBefore)==JsonSerializer.Serialize(minePreviewMidAfter),
                scope="Fresh copied world; setup once; preview only then native mine; hostile NPCs removed and player protected; no LLM."
            },new JsonSerializerOptions{WriteIndented=true}));
        TestLog($"continuous case {continuousCase.id} {continuousCase.status}: {error}");
        continuousIndex++;
        if(continuousIndex<continuousRoutes.Count)BeginContinuousCase();
        else
        {
            Stop();houseFinished=true;bool passed=continuousCases.All(c=>c.status=="passed");
            SaveContinuousHouse(passed?"passed":"failed");
            bool navigationPassed=continuousCases.Where(c=>c.id!="mine_wood_stone_return").All(c=>c.status=="passed");
            if(navigationPassed && Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CONTINUOUS_LLM")=="1")BeginContinuousLlm();
        }
    }
    private object CaptureMinePreviewState(NPC npc,Player p)
    {
        var tiles=new List<object>();
        for(int x=3170;x<=3335;x++)for(int y=380;y<=420;y++)
        {Tile t=Main.tile[x,y];tiles.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.LiquidAmount,t.LiquidType,t.Slope,t.IsHalfBlock,t.IsActuated,t.TileFrameX,t.TileFrameY});}
        string[] fields={"navPath","navIndex","navGoal","navBuiltAt","navigationNoProgressTicks","navigationBestDistance","navigationRetryAt","navigationFailures","miningStand","miningObservedQuantity","miningObservedHits","miningSourceType"};
        var live=fields.ToDictionary(name=>name,name=> {
            object? value=ContinuousField(name);
            return value is System.Collections.IEnumerable sequence && value is not string
                ? string.Join("|",sequence.Cast<object>().Select(v=>v?.ToString())) : value?.ToString(); });
        var budget=ContinuousField("miningBudget") as MiningProgressBudget;
        var approach=ContinuousField("miningApproach") as MiningApproachBudget;
        var stands=ContinuousField("miningWorkStands") as MiningWorkStandSelection;
        return new {npc=new{x=npc.position.X,y=npc.position.Y,vx=npc.velocity.X,vy=npc.velocity.Y},
            player=new{x=p.position.X,y=p.position.Y,vx=p.velocity.X,vy=p.velocity.Y},tiles,
            inventory=Loadout.Inventory.Select(i=>new{i.type,i.stack,i.prefix}).ToArray(),
            playerInventory=p.inventory.Select(i=>new{i.type,i.stack,i.prefix}).ToArray(),
            armor=Loadout.Armor.Select(i=>new{i.type,i.stack,i.prefix}).ToArray(),
            accessories=Loadout.Accessories.Select(i=>new{i.type,i.stack,i.prefix}).ToArray(),
            action=Executor.CurrentAction.ToString(),status=Executor.StatusText(),live,
            budget=new{idleTicks=budget?.IdleTicks,failedCandidates=budget?.FailedCandidates,approachIdle=approach?.IdleTicks,
                approachFields=approach==null?null:typeof(MiningApproachBudget).GetFields(BindingFlags.Instance|BindingFlags.NonPublic).ToDictionary(f=>f.Name,f=> {
                    object? value=f.GetValue(approach);return value is System.Collections.IEnumerable sequence && value is not string
                        ? string.Join("|",sequence.Cast<object>().Select(v=>v?.ToString())):value?.ToString();}),
                standProbes=stands?.ProbeCount}};
    }
    private void BeginContinuousLlm()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();
        NPC npc=CompanionNpc!;Player player=Main.LocalPlayer;
        npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;player.Center=Stand(3173,395);player.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();Executor.ResetNavigationTelemetry();
        continuousMaterialDeltas.Clear();continuousMaterialLastSequence=agentJournal.LastSequence;
        continuousReplayedFirst=false;continuousReplayPlanning=null;
        continuousMaterialWood=Loadout.CountItem(ItemID.Wood);continuousMaterialStone=Loadout.CountItem(ItemID.StoneBlock);
        continuousLlmAt=DateTime.UtcNow;continuousLlmRequests=PrimaryDecisionRequestCount;continuousLlmStarted=true;
        string request=NaturalTravelCase?"从屋内出发，自行选择一个距我256到360格的真实资源地点，先查询位置，走过去观察，再走回我身边；不要采挖或建造，遇阻调整路线，不能传送。":"请从屋里出发，收集20个木材和10个石块，然后带着材料回到我身边。按实际情况行动，遇到无法到达或资源不足请如实告诉我。";
        naturalFarVerified=naturalComeVerified=false;naturalLastSequence=agentJournal.LastSequence;naturalTravelOutcomes.Clear();
        if(MaterialsResumeCase)
        {
            string? path=Environment.GetEnvironmentVariable("AITEAMMATE_REPLAY_PUBLIC_DECISION_PATH");
            if(string.IsNullOrWhiteSpace(path) || !File.Exists(path)){FinishContinuousLlm(false,"Explicit replay decision path is missing.");return;}
            try
            {
                AIPlan replay=AIPlanParser.Parse(File.ReadAllText(path));
                if(!ApplyPlan(replay,null,source:"chat",sourcePlayerRequest:request)){FinishContinuousLlm(false,"Replayed public decision was rejected.");return;}
                continuousReplayedFirst=true;continuousReplayPlanning=JsonSerializer.SerializeToElement(PlanningContext);
            }
            catch(Exception e){FinishContinuousLlm(false,"Replay parse/apply failed: "+e.Message);return;}
        }
        else HandlePlayerMessage(player,request);
        SaveContinuousLlm("running","");
    }
    private void TickContinuousLlm()
    {
        if(CompanionNpc==null){FinishContinuousLlm(false,"companion missing");return;}
        NPC npc=CompanionNpc;Player p=Main.LocalPlayer;
        continuousLlmActiveFrames++;
        if(NaturalTravelCase)
        foreach(var outcome in agentJournal.Outcomes.Where(o=>o.Sequence>naturalLastSequence).OrderBy(o=>o.Sequence))
        {
            double distance=Vector2.Distance(npc.Center,p.Center)/16d;
            bool grounded=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width);
            bool priorWorld=agentJournal.Outcomes.Any(o=>o.Sequence<outcome.Sequence && o.Status=="succeeded" && o.Task.Action==AIActionType.Inspect && o.Task.Target=="world");
            bool resourceSample=false;
            if(outcome.Task.X.HasValue && outcome.Task.Y.HasValue)
                foreach(var observation in agentJournal.Outcomes.Where(o=>o.Sequence<outcome.Sequence && o.Status=="succeeded" && o.Task.Action==AIActionType.Inspect && o.Task.Target=="world"))
                try {
                    using var doc=JsonDocument.Parse(observation.Result); var body=doc.RootElement.GetProperty("data");
                    if(!body.TryGetProperty("entries",out var entries) || entries.ValueKind!=JsonValueKind.Array)continue;
                    foreach(var group in entries.EnumerateArray())if(group.TryGetProperty("samples",out var samples))foreach(var sample in samples.EnumerateArray())
                        if(sample.GetProperty("x").GetInt32()==outcome.Task.X && sample.GetProperty("y").GetInt32()==outcome.Task.Y)resourceSample=true;
                } catch(JsonException){} catch(InvalidOperationException){} catch(KeyNotFoundException){}
            bool isFar=priorWorld && outcome.Status=="succeeded" && outcome.Task.Action==AIActionType.Explore && outcome.Task.X.HasValue && outcome.Task.Y.HasValue && distance>=256 && distance<=360;
            if(isFar)naturalFarVerified=true;
            bool isCome=naturalFarVerified && outcome.Status=="succeeded" && outcome.Task.Action==AIActionType.Come && Math.Abs(npc.Center.X-p.Center.X)<=48 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<=24 && grounded;
            if(isCome)naturalComeVerified=true;
            naturalTravelOutcomes.Add(new{outcome.Sequence,outcome.Task,outcome.Status,outcome.Result,distanceTiles=distance,grounded,priorWorld,resourceSample,
                actor=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},player=new{x=p.Center.X/16,y=p.Center.Y/16,feet=p.Bottom.Y/16},isFar,isCome});
            naturalLastSequence=outcome.Sequence;
        }
        if(AutonomousMaterialsCase)
        {
            foreach(var outcome in agentJournal.Outcomes.Where(o=>o.Sequence>continuousMaterialLastSequence).OrderBy(o=>o.Sequence))
            {
                int wood=Loadout.CountItem(ItemID.Wood),stone=Loadout.CountItem(ItemID.StoneBlock);
                continuousMaterialDeltas.Add(new{sequence=outcome.Sequence,action=outcome.Task.Action.ToString(),outcome.Status,
                    woodBefore=continuousMaterialWood,woodAfter=wood,woodDelta=wood-continuousMaterialWood,
                    stoneBefore=continuousMaterialStone,stoneAfter=stone,stoneDelta=stone-continuousMaterialStone,
                    note="Inventory observed after outcome; a delta can include other events since prior observation."});
                continuousMaterialWood=wood;continuousMaterialStone=stone;continuousMaterialLastSequence=outcome.Sequence;
            }
        }
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;p.controlLeft=p.controlRight=p.controlJump=false;
        foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(decisionFailures>0){FinishContinuousLlm(false,"A model request failed; test stopped before automatic retry.");return;}
        if(Main.GameUpdateCount%60==0)
        {
            continuousLlmTrace.Add(CaptureContinuousLlmTrace(npc,p));
            try { SaveContinuousLlm("running",""); }
            catch(Exception e) { continuousLlmRecordingError=e.GetType().Name+": "+e.Message; }
        }
        if(Executor.NavigationTeleportCount>0){FinishContinuousLlm(false,"teleport during task");return;}
        bool physical=NaturalTravelCase ? naturalFarVerified && naturalComeVerified && Math.Abs(npc.Center.X-p.Center.X)<=48 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<=24 && TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width) : Loadout.CountItem(ItemID.Wood)>=20 && Loadout.CountItem(ItemID.StoneBlock)>=10 &&
            Math.Abs(npc.Center.X-p.Center.X)<=48 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<6;
        continuousLlmPhysicalEverMet|=physical;
        bool returned=agentJournal.Outcomes.Any(o=>o.Status=="succeeded" && o.Task.Action==AIActionType.Come);
        if(physical && returned && goalStatus=="completed"){FinishContinuousLlm(true,"");return;}
        bool budgetDone=PrimaryDecisionRequestCount-continuousLlmRequests>=ContinuousLlmRequestLimit && primaryCancellation==null &&
            !Executor.IsBusy && Executor.SnapshotTasks().Count==0;
        if(!ContinuousLlmWithinTime || budgetDone || goalWaitingForPlayer || goalStatus=="completed")
            FinishContinuousLlm(false,continuousLlmPhysicalEverMet?
                "Physical target was met, but truthful completed goal and successful return evidence were not both present.":
                "Did not complete actual collection and return within bounded task.");
    }
    private object CaptureContinuousLlmTrace(NPC npc,Player p)
    {
        var path=ContinuousField("navPath") as System.Collections.IEnumerable;
        var nearbyTiles=new List<object>();int centerX=(int)(npc.Center.X/16),centerY=(int)(npc.Bottom.Y/16);
        for(int tx=centerX-3;tx<=centerX+3;tx++)for(int ty=centerY-3;ty<=centerY+3;ty++)
        {
            Tile t=Main.tile[tx,ty];nearbyTiles.Add(new{x=tx,y=ty,type=t.HasTile?(int)t.TileType:-1,
                solid=t.HasTile&&Main.tileSolid[t.TileType],solidTop=t.HasTile&&Main.tileSolidTop[t.TileType],
                platform=t.HasTile&&TileID.Sets.Platforms[t.TileType],half=t.IsHalfBlock,slope=(int)t.Slope,actuated=t.IsActuated});
        }
        var grounded=typeof(AIExecutor).GetMethod("IsGrounded",BindingFlags.NonPublic|BindingFlags.Static)?.Invoke(null,new object[]{npc});
        return new{utc=DateTime.UtcNow,activeFrames=continuousLlmActiveFrames,x=npc.Center.X/16,y=npc.Center.Y/16,bottom=npc.Bottom.Y/16,
            pixelX=npc.position.X,pixelY=npc.position.Y,npc.width,npc.height,vx=npc.velocity.X,vy=npc.velocity.Y,
            npc.collideX,npc.collideY,grounded,solidCollision=Collision.SolidCollision(npc.position,npc.width,npc.height),nearbyTiles,
            playerX=p.Center.X/16,playerY=p.Center.Y/16,action=Executor.CurrentAction.ToString(),status=Executor.StatusText(),
            path=path?.Cast<object>().Take(20).Select(o=>o.ToString()).ToArray(),pathIndex=ContinuousField("navIndex"),
            navBuiltAt=ContinuousField("navBuiltAt"),navFailures=ContinuousField("navigationFailures"),blocked=Executor.BlockedReason,
            last=Executor.LastStepResult,wood=Loadout.CountItem(ItemID.Wood),stone=Loadout.CountItem(ItemID.StoneBlock),
            teleports=Executor.NavigationTeleportCount};
    }
    private void FinishContinuousLlm(bool success,string error)
    {
        continuousLlmFinished=true;
        try { SaveContinuousLlm(success?"passed":"failed",error); }
        catch(Exception e) { continuousLlmRecordingError=e.GetType().Name+": "+e.Message; TestLog("continuous LLM recording failed: "+continuousLlmRecordingError); }
        finally { Stop(); }
        TestLog($"continuous real LLM collection {(success?"passed":"failed")}: {error}");
    }
    private void SaveContinuousLlm(string status,string error)
    {
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-continuous-llm-result.json"),JsonSerializer.Serialize(new{
            status,error,recordingError=continuousLlmRecordingError,world=Main.worldName,worldId=Main.worldID,startedUtc=continuousLlmAt,updatedUtc=DateTime.UtcNow,
            requests=PrimaryDecisionRequestCount-continuousLlmRequests,activeFrames=continuousLlmActiveFrames,wood=Loadout.CountItem(ItemID.Wood),
            replayedFirstCalls=continuousReplayedFirst?1:0,cumulativeApiCalls=(MaterialsResumeCase?1:0)+PrimaryDecisionRequestCount-continuousLlmRequests,
            replaySource=MaterialsResumeCase?Environment.GetEnvironmentVariable("AITEAMMATE_REPLAY_PUBLIC_DECISION_PATH"):null,replayPlanning=continuousReplayPlanning,
            stone=Loadout.CountItem(ItemID.StoneBlock),teleports=Executor.NavigationTeleportCount,reply=LastReply,
            physicalEverMet=continuousLlmPhysicalEverMet,goalStatus,
            finalNpc=CompanionNpc==null?null:new{x=CompanionNpc.Center.X/16,y=CompanionNpc.Center.Y/16,bottom=CompanionNpc.Bottom.Y/16},
            finalPlayer=new{x=Main.LocalPlayer.Center.X/16,y=Main.LocalPlayer.Center.Y/16,bottom=Main.LocalPlayer.Bottom.Y/16},
            outcomes=agentJournal.Outcomes,materialDeltas=continuousMaterialDeltas,naturalFarVerified,naturalComeVerified,naturalTravelOutcomes,trace=continuousLlmTrace,
            scope=MaterialsResumeCase?"Original-house copy; first previously paid public decision replayed, later decisions use LLM; not a fresh initial model sample; no seeded resources or mid-task resets; hostiles removed/player protected.":AutonomousMaterialsCase?"Original-house copy; autonomous material acquisition including real chest withdrawal; no seeded resources or mid-task resets; hostiles removed/player protected; production execution policy applies.":"One setup inside copied original house; no position resets or seeded resources during task; hostile NPCs removed and player protected."
        },new JsonSerializerOptions{WriteIndented=true}));
    }
    private void SaveContinuousHouse(string status)
    {
        var caveGeometry=new List<object>();
        int npcX=CompanionNpc==null?3250:(int)(CompanionNpc.Center.X/16),npcY=CompanionNpc==null?410:(int)(CompanionNpc.Center.Y/16);
        int candidateX=npcX,candidateY=npcY;
        try
        {
            using var result=JsonDocument.Parse(string.IsNullOrWhiteSpace(Executor.LastStepResult)?"{}":Executor.LastStepResult);
            if(result.RootElement.TryGetProperty("currentCandidate",out var candidate) &&
                candidate.TryGetProperty("x",out var cx) && candidate.TryGetProperty("y",out var cy))
            {candidateX=cx.GetInt32();candidateY=cy.GetInt32();}
        }
        catch(JsonException) { }
        int left=Math.Max(2,Math.Min(npcX,candidateX)-20),top=Math.Max(2,Math.Min(npcY,candidateY)-20);
        int right=Math.Min(Main.maxTilesX-3,Math.Min(left+119,Math.Max(npcX,candidateX)+20));
        int bottom=Math.Min(Main.maxTilesY-3,Math.Min(top+89,Math.Max(npcY,candidateY)+20));
        for(int x=left;x<=right;x++)for(int y=top;y<=bottom;y++)
        {
            Tile t=Main.tile[x,y];caveGeometry.Add(new{x,y,tile=t.HasTile?(int)t.TileType:-1,wall=(int)t.WallType,
                solid=t.HasTile&&Main.tileSolid[t.TileType],solidTop=t.HasTile&&Main.tileSolidTop[t.TileType],
                platform=t.HasTile&&TileID.Sets.Platforms[t.TileType],actuated=t.IsActuated,slope=(int)t.Slope,halfBlock=t.IsHalfBlock});
        }
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-continuous-result.json"),JsonSerializer.Serialize(new {
            status,world=Main.worldName,worldId=Main.worldID,updatedUtc=DateTime.UtcNow,setupOnlyReposition=true,
            hostileNpcsRemoved=true,terrainRebuilt=false,caseFilter=Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE"),initialGeometry=houseGeometry,
            caveBounds=new{left,top,right,bottom,npcX,npcY,candidateX,candidateY},caveGeometry,
            npcNative=CompanionNpc==null?null:new{x=CompanionNpc.position.X,y=CompanionNpc.position.Y,width=CompanionNpc.width,height=CompanionNpc.height,
                solidCollision=Collision.SolidCollision(CompanionNpc.position,CompanionNpc.width,CompanionNpc.height)},
            movingPlayerFixture="Player velocity scripted; native collision and gravity; no position writes during case.",cases=continuousCases.Select(c=>new{c.id,c.status,c.error,c.phase,
                start=new{x=c.start.X/16,y=c.start.Y/16},playerStart=new{x=c.playerStart.X/16,y=c.playerStart.Y/16},
                c.started,c.frames,c.jumps,c.turns,c.teleports,c.waitHoldVerified,c.waitResumeVerified,c.recordingSmokeError,c.maxDisplacement,c.closeMovingFrames,c.minPlayerX,c.maxPlayerX,c.minPlayerBottom,c.maxPlayerBottom,c.playerStandingFloors,c.trace,c.detailedTrace})},new JsonSerializerOptions{WriteIndented=true}));
    }
    private void EndHouseCase(bool pass,string error)
    {
        houseCase!.Status=pass?"passed":"failed";houseCase.Error=pass?"":error;
        TestLog($"house case {houseCase.Id} {houseCase.Status}: {houseCase.Error}");
        houseRouteIndex++;
        if(houseRouteIndex<houseRoutes.Count) BeginHouseCase();
        else { Stop(); houseFinished=true;houseRunning=false;SaveHouseReport(houseCases.All(c=>c.Status=="passed")?"passed":"failed",""); }
    }
    private void SaveHouseReport(string status,string error)
    {
        if(!HouseMode)return;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-acceptance-result.json"),JsonSerializer.Serialize(new {
            status,error,world=Main.worldName,worldId=Main.worldID,updatedUtc=DateTime.UtcNow,geometryNotRebuiltByFixture=true,
            hostileNpcsRemovedForNavigation=true,terrainGenerated=false,geometry=houseGeometry,
            cases=houseCases.Select(c=>new {id=c.Id,status=c.Status,error=c.Error,start=new{x=c.Start.X/16,y=c.Start.Y/16},
                target=new{x=c.Target.X/16,y=c.Target.Y/16},frames=c.Frames,jumps=c.Jumps,turns=c.FacingChanges,teleports=c.Warps,trace=c.Trace})
        },new JsonSerializerOptions {WriteIndented=true}));
    }
}
