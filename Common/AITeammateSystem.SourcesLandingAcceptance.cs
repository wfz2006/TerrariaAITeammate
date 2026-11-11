using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private bool sourcesLandingStarted,sourcesLandingFinished;private int sourcesLandingPhase,sourcesLandingFrames,sourcesLandingRequests;private long sourcesLandingSequence;private DateTime sourcesLandingAt;
    private ShelterWorldAudit? sourcesLandingAudit;private readonly List<object> sourcesLandingEvents=new(),sourcesLandingTrace=new();
    private void BeginSourcesLandingReplay()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear(true);Executor.ResetNavigationTelemetry();NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;Loadout.Reset();Loadout.EnsureStarterTools();
        sourcesLandingAudit=ShelterWorldAudit.Capture(3220,390);sourcesLandingRequests=PrimaryDecisionRequestCount;sourcesLandingSequence=agentJournal.LastSequence;sourcesLandingAt=DateTime.UtcNow;sourcesLandingStarted=true;
        DispatchSourcesLandingReplay();
    }
    private void DispatchSourcesLandingReplay()
    {
        AIPlan task=sourcesLandingPhase switch{
            0=>new(){Action=AIActionType.Mine,Target="wood",Count=40,X=3262,Y=399},
            1=>new(){Action=AIActionType.Mine,Target="wood",Count=57,X=3276,Y=401},
            2=>new(){Action=AIActionType.Mine,Target="wood",Count=45},
            _=>new(){Action=AIActionType.Come}};
        sourcesLandingEvents.Add(new{phase="dispatch",step=sourcesLandingPhase,frame=sourcesLandingFrames,tick=Main.GameUpdateCount,task,inventory=ShelterCounts(Loadout.Inventory)});Executor.Apply(task);
    }
    private void TickSourcesLandingReplay()
    {
        if(Main.gameMenu || CompanionNpc==null)return;NPC npc=CompanionNpc;Player p=Main.LocalPlayer;sourcesLandingFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(sourcesLandingFrames%5==0)sourcesLandingTrace.Add(new{frame=sourcesLandingFrames,step=sourcesLandingPhase,tick=Main.GameUpdateCount,
            npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16,vx=npc.velocity.X,vy=npc.velocity.Y},grounded=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width),
            action=Executor.CurrentAction.ToString(),status=Executor.StatusText(),miningStand=ContinuousField("miningStand")?.ToString(),inventory=ShelterCounts(Loadout.Inventory),warps=Executor.NavigationTeleportCount});
        if(DateTime.UtcNow-sourcesLandingAt>TimeSpan.FromMinutes(6) || Executor.NavigationTeleportCount>0){FinishSourcesLandingReplay("stopped","deadline or warp");return;}
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>sourcesLandingSequence);if(outcome==null)return;sourcesLandingSequence=outcome.Sequence;
        sourcesLandingEvents.Add(new{phase="outcome",step=sourcesLandingPhase,frame=sourcesLandingFrames,tick=Main.GameUpdateCount,outcome,
            npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16,vx=npc.velocity.X,vy=npc.velocity.Y},inventory=ShelterCounts(Loadout.Inventory),preservation=sourcesLandingAudit!.CheckPreservation()});
        if(++sourcesLandingPhase>3){FinishSourcesLandingReplay("observed","");return;}DispatchSourcesLandingReplay();
    }
    private void FinishSourcesLandingReplay(string status,string error)
    {
        sourcesLandingFinished=true;File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","sources-landing-replay-native-result.json"),JsonSerializer.Serialize(new{
            status,error,frames=sourcesLandingFrames,requests=PrimaryDecisionRequestCount-sourcesLandingRequests,teleports=Executor.NavigationTeleportCount,events=sourcesLandingEvents,trace=sourcesLandingTrace,
            scope="Controlled zero-API replay of prior public Mine seq9/10/11 counts40/57/45, then Come; fresh original-house setup once, starter tools only. Failed segments retained and next segment dispatched to reproduce prior sequence. No mid-case positioning, velocity changes, source injection, teleport or LLM; actual outcome is not autonomous success. Hostiles removed/player protected."
        },new JsonSerializerOptions{WriteIndented=false}));Stop();Executor.Clear(true);
    }
}
