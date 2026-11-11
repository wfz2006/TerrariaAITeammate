using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private bool basicStarted,basicFinished,basicNatural;
    private int basicPhase,basicFrames,basicPhaseFrames,basicRequestStart,basicStable;
    private long basicSequence,basicStageStartSequence;
    private DateTime basicStartedAt,basicPhaseAt;
    private Vector2 basicInitialNpc,basicInitialPlayer,basicInspectNpc,basicInspectPlayer;
    private int basicHoldJumps,basicHoldTurns,basicStableJumps,basicStableTurns,basicPlatformBefore,basicWoodBefore,basicSettleFrames,basicSettleStable;
    private Vector2 basicStableNpc; private string basicRouteWorldBefore="";
    private string basicStageRequest="";
    private readonly List<object> basicEvents=new(),basicTrace=new();
    private bool basicReplayStarted,basicReplayFinished,basicSkills;
    private long basicReplaySequence; private int basicReplayFrames,basicReplayRequests,basicReplayPlatformBefore,basicReplayWoodBeforeCraft=-1;
    private DateTime basicReplayAt; private readonly List<object> basicReplayEvents=new(),basicReplayTrace=new();

    private void BeginBasicSkills()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear();Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();basicSkills=true;
        var steps=new[]{new AIPlan{Action=AIActionType.Mine,Target="wood",Count=10},new AIPlan{Action=AIActionType.Mine,Target="stone",Count=5},new AIPlan{Action=AIActionType.Come},new AIPlan{Action=AIActionType.Craft,Item="94",Count=2}};
        basicReplayPlatformBefore=Loadout.CountItem(94);basicReplayRequests=PrimaryDecisionRequestCount;basicReplaySequence=agentJournal.LastSequence;
        basicReplayAt=DateTime.UtcNow;basicReplayStarted=true;
        basicReplayEvents.Add(new{phase="dispatch",steps,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},player=new{x=p.Center.X/16,y=p.Center.Y/16,feet=p.Bottom.Y/16}});
        Executor.ApplySequence(steps);
    }

    private void BeginBasicReplay()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear();Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        npc.Center=new Vector2(3174.8755f*16,395f*16-21);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();
        string? source=Environment.GetEnvironmentVariable("AITEAMMATE_REPLAY_PUBLIC_DECISION_PATH");
        if(string.IsNullOrWhiteSpace(source) || !File.Exists(source))throw new InvalidOperationException("Basic replay requires explicit public decision file.");
        using var doc=JsonDocument.Parse(File.ReadAllText(source));AIPlan parsed=AIPlanParser.Parse(doc.RootElement[6].GetRawText());
        if(parsed.Steps.Count!=3 || parsed.Steps[0].Action!=AIActionType.Mine || parsed.Steps[0].Target!="stone" || parsed.Steps[0].Count!=5 || parsed.Steps[0].X.HasValue || parsed.Steps[0].Y.HasValue ||
            parsed.Steps[1].Action!=AIActionType.Explore || parsed.Steps[1].X!=3252 || parsed.Steps[1].Y!=398 || parsed.Steps[2].Action!=AIActionType.Mine || parsed.Steps[2].Target!="wood" || parsed.Steps[2].Count!=10 || parsed.Steps[2].X!=3262 || parsed.Steps[2].Y!=398)
            throw new InvalidOperationException("Public decision physical prefix does not match the recorded 3 tasks.");
        var steps=parsed.Steps.ToList();steps.Add(new AIPlan{Action=AIActionType.Come});steps.Add(new AIPlan{Action=AIActionType.Craft,Item="94",Count=2});
        basicReplayPlatformBefore=Loadout.CountItem(94);basicReplayRequests=PrimaryDecisionRequestCount;basicReplaySequence=agentJournal.LastSequence;
        basicReplayAt=DateTime.UtcNow;basicReplayStarted=true;
        basicReplayEvents.Add(new{phase="dispatch",source,publicDecisionIndex=6,steps,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},player=new{x=p.Center.X/16,y=p.Center.Y/16,feet=p.Bottom.Y/16}});
        Executor.ApplySequence(steps);
    }
    private void TickBasicReplay()
    {
        if(Main.gameMenu || CompanionNpc==null)return;NPC npc=CompanionNpc;Player p=Main.LocalPlayer;basicReplayFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(basicReplayFrames%30==0)basicReplayTrace.Add(new{frame=basicReplayFrames,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16,vx=npc.velocity.X,vy=npc.velocity.Y},
            action=Executor.CurrentAction.ToString(),wood=Loadout.CountItem(ItemID.Wood),stone=Loadout.CountItem(ItemID.StoneBlock),platform=Loadout.CountItem(94),pathIndex=ContinuousField("navIndex"),warps=Executor.NavigationTeleportCount});
        if(Executor.NavigationTeleportCount>0 || DateTime.UtcNow-basicReplayAt>TimeSpan.FromSeconds(240)){FinishBasicReplay("failed","teleport or deadline");return;}
        foreach(var outcome in agentJournal.Outcomes.Where(o=>o.Sequence>basicReplaySequence).OrderBy(o=>o.Sequence))
        {
            basicReplaySequence=outcome.Sequence;int wood=Loadout.CountItem(ItemID.Wood),stone=Loadout.CountItem(ItemID.StoneBlock),platform=Loadout.CountItem(94);
            basicReplayEvents.Add(new{phase="outcome",outcome,wood,stone,platform,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},player=new{x=p.Center.X/16,y=p.Center.Y/16,feet=p.Bottom.Y/16}});
            if(outcome.Status!="succeeded"){FinishBasicReplay("failed","actual replay action blocked");return;}
            if(outcome.Task.Action==AIActionType.Come)
            {
                if(Math.Abs(npc.Center.X-p.Center.X)>48 || Math.Abs(npc.Bottom.Y-p.Bottom.Y)>24){FinishBasicReplay("failed","Come proximity");return;}
                basicReplayWoodBeforeCraft=wood;
            }
            if(outcome.Task.Action==AIActionType.Craft)
            {
                bool passed=wood>=9 && stone>=5 && platform-basicReplayPlatformBefore>=2 && basicReplayWoodBeforeCraft-wood==1 && PrimaryDecisionRequestCount==basicReplayRequests;
                FinishBasicReplay(passed?"passed":"failed",passed?"":"inventory or API evidence missing");return;
            }
        }
    }
    private void FinishBasicReplay(string status,string error)
    {
        basicReplayFinished=true;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate",basicSkills?"basic-skills-result.json":"basic-replay-result.json"),JsonSerializer.Serialize(new{
            status,error,frames=basicReplayFrames,requests=PrimaryDecisionRequestCount-basicReplayRequests,teleports=Executor.NavigationTeleportCount,
            platformBefore=basicReplayPlatformBefore,woodBeforeCraft=basicReplayWoodBeforeCraft,events=basicReplayEvents,trace=basicReplayTrace,
            scope=basicSkills?"Controlled basic skill chain via Executor.ApplySequence: auto wood10, auto stone5, Come, Craft platform2; not a Flash decision or autonomous plan. Original-house fresh copy, starter tools only, one actor setup, no seeded material or mid-case reposition; hostiles removed/player protected.":"Previously paid Flash physical prefix replayed directly via Executor.ApplySequence, followed by controlled Come/Craft; no new autonomous planning. Original-house fresh copy, starter tools only, one actor setup, no seeded material or mid-case reposition; hostiles removed/player protected."
        },new JsonSerializerOptions{WriteIndented=true}));
        Stop();Executor.Clear(true);
    }
    private int BasicRequestLimit=>int.TryParse(Environment.GetEnvironmentVariable("AITEAMMATE_RESOURCE_REQUEST_LIMIT"),out int limit)?Math.Clamp(limit,1,16):16;
    private bool BasicPlanningAllowed=>basicStarted && !basicFinished && basicNatural && PrimaryDecisionRequestCount-basicRequestStart<BasicRequestLimit && DateTime.UtcNow-basicPhaseAt<TimeSpan.FromSeconds(basicPhase==2?120:240);

    private void BeginBasicAcceptance()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear();Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        npc.Center=Stand(3170,395);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();basicInitialNpc=npc.Center;basicInitialPlayer=p.Center;
        basicNatural=Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="basic_natural";
        basicStarted=true;basicStartedAt=basicPhaseAt=DateTime.UtcNow;basicRequestStart=PrimaryDecisionRequestCount;basicSequence=agentJournal.LastSequence;
        if(basicNatural){npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;basicInitialNpc=npc.Center;StartBasicNaturalStage();}
        else {basicPhase=-1;Executor.Apply(new AIPlan{Action=AIActionType.Stop});basicEvents.Add(new{eventType="initial_setup",npc=new{x=npc.Center.X,y=npc.Center.Y,feet=npc.Bottom.Y},player=new{x=p.Center.X,y=p.Center.Y,feet=p.Bottom.Y}});}
    }
    private void StartBasicNaturalStage()
    {
        basicPhaseAt=DateTime.UtcNow;basicStageStartSequence=agentJournal.LastSequence;
        if(basicPhase==2){basicPlatformBefore=Loadout.CountItem(94);basicWoodBefore=Loadout.CountItem(ItemID.Wood);}
        string request=basicPhase switch{
            0=>"请去看看坐标（3219，377）附近，然后回到我身边。",
            1=>"请从这里收集10个木材和5个石块，带着材料回到我身边。",
            _=>"请用你刚采集的材料制作两个木平台。"};
        basicStageRequest=request;
        basicEvents.Add(new{phase=basicPhase,eventType="request",request,requests=PrimaryDecisionRequestCount-basicRequestStart});
        HandlePlayerMessage(Main.LocalPlayer,request);
    }
    private void TickBasicAcceptance()
    {
        if(Main.gameMenu || CompanionNpc==null)return;NPC npc=CompanionNpc;Player p=Main.LocalPlayer;basicFrames++;basicPhaseFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;
        foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(basicFrames%30==0)basicTrace.Add(new{phase=basicPhase,frame=basicFrames,npc=new{x=npc.Center.X,y=npc.Center.Y,feet=npc.Bottom.Y,vx=npc.velocity.X,vy=npc.velocity.Y,npc.direction},player=new{x=p.Center.X,y=p.Center.Y,feet=p.Bottom.Y,vx=p.velocity.X,vy=p.velocity.Y},jumps=Executor.NavigationJumpCount,turns=Executor.NavigationFacingChanges,warps=Executor.NavigationTeleportCount});
        if(Executor.NavigationTeleportCount>0){FinishBasicAcceptance("failed","teleport");return;}
        if(basicNatural){TickBasicNatural(npc,p);return;}
        if(DateTime.UtcNow-basicStartedAt>TimeSpan.FromSeconds(180)){FinishBasicAcceptance("failed","native deadline");return;}
        if(basicPhase==-1)
        {
            basicSettleFrames++;
            bool grounded=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width);
            if(grounded && Math.Abs(npc.velocity.Y)<0.6f)basicSettleStable++;else basicSettleStable=0;
            if(basicSettleStable>=10)
            {
                basicInitialNpc=npc.Center;basicHoldJumps=Executor.NavigationJumpCount;basicHoldTurns=Executor.NavigationFacingChanges;
                basicEvents.Add(new{eventType="settled",frames=basicSettleFrames,grounded,npc=new{x=npc.Center.X,y=npc.Center.Y,feet=npc.Bottom.Y,vy=npc.velocity.Y}});
                basicPhase=0;basicPhaseFrames=0;
            }
            else if(basicSettleFrames>=300){FinishBasicAcceptance("failed","initial gravity settle timed out");return;}
        }
        else if(basicPhase==0 && basicPhaseFrames>=60)
        {
            float displacement=Vector2.Distance(npc.Center,basicInitialNpc);int jumps=Executor.NavigationJumpCount-basicHoldJumps,turns=Executor.NavigationFacingChanges-basicHoldTurns;
            basicEvents.Add(new{eventType="hold",displacement,held=Executor.HoldPosition,jumps,turns});
            if(displacement>3 || !Executor.HoldPosition || jumps!=0 || turns!=0){FinishBasicAcceptance("failed","hold moved or turned");return;}
            basicPhase=1;basicPhaseFrames=0;Executor.Apply(new AIPlan{Action=AIActionType.Follow});
        }
        else if(basicPhase==1)
        {
            p.controlRight=basicPhaseFrames<=120;p.controlLeft=false;p.velocity.X=basicPhaseFrames<=120?3f:0;
            if(basicPhaseFrames>120 && Math.Abs(npc.velocity.X)<0.2 && Math.Abs(npc.velocity.Y)<0.6 && Math.Abs(npc.Center.X-p.Center.X)<=112)basicStable++;else basicStable=0;
            if(basicStable>=30)
            {
                p.controlRight=false;basicEvents.Add(new{eventType="follow_settled",playerDistanceMoved=Vector2.Distance(p.Center,basicInitialPlayer),horizontal=Math.Abs(npc.Center.X-p.Center.X),jumps=Executor.NavigationJumpCount,turns=Executor.NavigationFacingChanges});
                if(Vector2.Distance(p.Center,basicInitialPlayer)<112){FinishBasicAcceptance("failed","player did not walk far enough");return;}
                basicStableNpc=npc.Center;basicStableJumps=Executor.NavigationJumpCount;basicStableTurns=Executor.NavigationFacingChanges;
                basicPhase=2;basicPhaseFrames=0;
            }
        }
        else if(basicPhase==2 && basicPhaseFrames>=60)
        {
            float displacement=Vector2.Distance(npc.Center,basicStableNpc);int jumps=Executor.NavigationJumpCount-basicStableJumps,turns=Executor.NavigationFacingChanges-basicStableTurns;
            basicEvents.Add(new{eventType="follow_stability",displacement,jumps,turns,frames=basicPhaseFrames});
            if(displacement>3 || jumps!=0 || turns>1){FinishBasicAcceptance("failed","follow not stable");return;}
            Executor.Clear(true);basicInspectNpc=npc.Center;basicInspectPlayer=p.Center;basicSequence=agentJournal.LastSequence;basicRouteWorldBefore=BasicWorldSnapshot();
            string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));int ox=(int)(p.Center.X/16),oy=(int)(p.Center.Y/16);
            JsonElement world=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Query("stone;minDistance=256;maxDistance=360;limit=8",npc,ox,oy));
            bool unchanged=before==JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
            var samples=world.GetProperty("data").GetProperty("entries").EnumerateArray().SelectMany(e=>e.GetProperty("samples").EnumerateArray()).ToArray();
            bool band=samples.All(s=>{int x=s.GetProperty("x").GetInt32(),y=s.GetProperty("y").GetInt32();double d=Math.Sqrt((x-ox)*(double)(x-ox)+(y-oy)*(double)(y-oy));return d>=256 && d<=360;});
            basicEvents.Add(new{eventType="world_distance_filter",origin=new{x=ox,y=oy},samples=samples.Length,band,unchanged,world});
            if(!world.GetProperty("available").GetBoolean() || !band || !unchanged){FinishBasicAcceptance("failed","world distance query");return;}
            basicPhase=3;basicPhaseFrames=0;Executor.Apply(new AIPlan{Action=AIActionType.Inspect,Target="route",Item="2880,405"});
        }
        else if(basicPhase>=3)
        {
            var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>basicSequence);
            if(outcome==null)return;basicSequence=outcome.Sequence;
            basicEvents.Add(new{eventType="outcome",phase=basicPhase,outcome,npc=new{x=npc.Center.X,y=npc.Center.Y,feet=npc.Bottom.Y},player=new{x=p.Center.X,y=p.Center.Y,feet=p.Bottom.Y}});
            if(outcome.Status!="succeeded"){FinishBasicAcceptance("failed","native action blocked");return;}
            if(basicPhase==3)
            {
                using var doc=JsonDocument.Parse(outcome.Result);JsonElement root=doc.RootElement;
                bool terminal=root.GetProperty("available").GetBoolean() && root.GetProperty("measurement").GetProperty("searchStatus").GetString()=="found";
                bool unchanged=Vector2.Distance(npc.Center,basicInspectNpc)<0.1f && Vector2.Distance(p.Center,basicInspectPlayer)<0.1f;
                bool environmentChangedDuringQuery=basicRouteWorldBefore!=BasicWorldSnapshot();
                basicEvents.Add(new{eventType="route_terminal",terminal,unchanged,environmentChangedDuringQuery,frames=basicPhaseFrames,note="Cross-frame world changes can be natural; they are not attributed to the query."});if(!terminal || !unchanged){FinishBasicAcceptance("failed","route incomplete or actors moved");return;}
                basicPhase=4;basicPhaseFrames=0;Executor.Apply(new AIPlan{Action=AIActionType.Explore,X=3219,Y=377});
            }
            else if(basicPhase==4){basicPhase=5;Executor.Apply(new AIPlan{Action=AIActionType.Come});}
            else if(basicPhase==5){bool near=Math.Abs(npc.Center.X-p.Center.X)<=48 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<=24;if(!near){FinishBasicAcceptance("failed","Come proximity");return;}FinishBasicAcceptance("passed","");}
        }
    }
    private void TickBasicNatural(NPC npc,Player p)
    {
        p.controlLeft=p.controlRight=p.controlJump=false;
        if(decisionFailures>0){FinishBasicAcceptance("failed","first API/parse failure");return;}
        if(DateTime.UtcNow-basicPhaseAt>TimeSpan.FromSeconds(basicPhase==2?120:240)){FinishBasicAcceptance("failed","stage deadline");return;}
        var stage=agentJournal.Outcomes.Where(o=>o.Sequence>basicStageStartSequence).ToArray();
        bool returned=stage.Any(o=>o.Task.Action==AIActionType.Come && o.Status=="succeeded") && Math.Abs(npc.Center.X-p.Center.X)<=48 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<=24;
        bool task=basicPhase switch{
            0=>stage.Any(o=>o.Task.Action==AIActionType.Explore && o.Task.X==3219 && o.Task.Y==377 && o.Status=="succeeded") && returned,
            1=>Loadout.CountItem(ItemID.Wood)>=10 && Loadout.CountItem(ItemID.StoneBlock)>=5 && returned,
            _=>stage.Any(o=>o.Task.Action==AIActionType.Craft && o.Status=="succeeded") && Loadout.CountItem(94)-basicPlatformBefore>=2 && Loadout.CountItem(ItemID.Wood)<basicWoodBefore};
        JsonElement proof=JsonSerializer.SerializeToElement(lastCompletedGoal);
        bool currentProof=proof.ValueKind==JsonValueKind.Object && proof.TryGetProperty("sourcePlayerRequest",out var source) && source.GetString()==basicStageRequest;
        if(goalStatus=="completed" && currentProof)
        {
            basicEvents.Add(new{eventType="stage_end",phase=basicPhase,task,goalStatus,outcomes=stage,wood=Loadout.CountItem(ItemID.Wood),stone=Loadout.CountItem(ItemID.StoneBlock),platform=Loadout.CountItem(94),basicPlatformBefore,basicWoodBefore,planning=JsonSerializer.SerializeToElement(PlanningContext)});
            if(!task){FinishBasicAcceptance("failed","completion lacked physical stage evidence");return;}
            if(++basicPhase==3){FinishBasicAcceptance("passed","");return;}
            if(PrimaryDecisionRequestCount-basicRequestStart>=BasicRequestLimit){FinishBasicAcceptance("failed","shared request budget exhausted");return;}
            StartBasicNaturalStage();return;
        }
        if(goalWaitingForPlayer || PrimaryDecisionRequestCount-basicRequestStart>=BasicRequestLimit && primaryCancellation==null && !Executor.IsBusy && Executor.SnapshotTasks().Count==0)
            FinishBasicAcceptance("failed","waiting or shared request budget exhausted");
    }
    private static string BasicWorldSnapshot()
    {
        var cells=new List<object>();for(int x=3150;x<=3230;x++)for(int y=375;y<=410;y++)
        {Tile t=Main.tile[x,y];cells.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.LiquidAmount,t.LiquidType,t.Slope,t.IsHalfBlock,t.IsActuated,t.TileFrameX,t.TileFrameY});}
        return JsonSerializer.Serialize(cells);
    }
    private void FinishBasicAcceptance(string status,string error)
    {
        basicFinished=true;Main.LocalPlayer.controlRight=false;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate",basicNatural?"basic-natural-result.json":"basic-native-result.json"),JsonSerializer.Serialize(new{
            status,error,basicNatural,phase=basicPhase,frames=basicFrames,requests=PrimaryDecisionRequestCount-basicRequestStart,requestLimit=BasicRequestLimit,stageRequest=basicStageRequest,teleports=Executor.NavigationTeleportCount,
            reply=LastReply,goalStatus,events=basicEvents,trace=basicTrace,outcomes=agentJournal.Outcomes,
            scope="Fresh copied original house, one initial actor setup, no terrain/resources seeded or mid-case reposition; hostile NPCs removed/player protected. Materials remain in companion inventory."
        },new JsonSerializerOptions{WriteIndented=true}));
        Stop();Executor.Clear(true);
    }
}
