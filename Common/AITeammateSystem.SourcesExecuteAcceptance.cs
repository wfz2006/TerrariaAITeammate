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
    private bool sourcesExecuteStarted,sourcesExecuteFinished;private int sourcesExecutePhase,sourcesExecuteFrames,sourcesExecuteRequests;private long sourcesExecuteSequence;private DateTime sourcesExecuteAt;
    private ShelterWorldAudit? sourcesExecuteAudit;private readonly List<object> sourcesExecuteEvents=new(),sourcesExecuteTrace=new();
    private void BeginSourcesExecuteNative()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear(true);Executor.ResetNavigationTelemetry();NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;Loadout.Reset();Loadout.EnsureStarterTools();
        sourcesExecuteAudit=ShelterWorldAudit.Capture(3220,390);sourcesExecuteRequests=PrimaryDecisionRequestCount;sourcesExecuteSequence=agentJournal.LastSequence;sourcesExecuteAt=DateTime.UtcNow;sourcesExecuteStarted=true;
    }
    private void TickSourcesExecuteNative()
    {
        if(Main.gameMenu || CompanionNpc==null)return;NPC npc=CompanionNpc;Player p=Main.LocalPlayer;sourcesExecuteFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(sourcesExecuteFrames%30==0)sourcesExecuteTrace.Add(new{frame=sourcesExecuteFrames,phase=sourcesExecutePhase,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},inventory=ShelterCounts(Loadout.Inventory),action=Executor.CurrentAction.ToString(),warps=Executor.NavigationTeleportCount});
        if(DateTime.UtcNow-sourcesExecuteAt>TimeSpan.FromMinutes(5) || Executor.NavigationTeleportCount>0){FinishSourcesExecuteNative("failed","deadline or warp");return;}
        if(sourcesExecutePhase==0){JsonElement world=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Status());if(!world.GetProperty("ready").GetBoolean())return;
            Executor.Apply(new AIPlan{Action=AIActionType.Inspect,Target="sources",Item=SourcesNativeQuery,X=3200,Y=390});sourcesExecutePhase=1;return;}
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>sourcesExecuteSequence);if(outcome==null)return;sourcesExecuteSequence=outcome.Sequence;
        sourcesExecuteEvents.Add(new{phase=sourcesExecutePhase,outcome,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},inventory=ShelterCounts(Loadout.Inventory),preservation=sourcesExecuteAudit!.CheckPreservation()});
        if(sourcesExecutePhase==1){using var doc=JsonDocument.Parse(outcome.Result);var root=doc.RootElement;
            bool candidate=root.GetProperty("status").GetString()=="complete" && root.GetProperty("candidates").EnumerateArray().Any(c=>c.GetProperty("seed").GetProperty("x").GetInt32()==3262 && c.GetProperty("seed").GetProperty("y").GetInt32()==399 && c.GetProperty("preflight").GetProperty("canAttempt").ValueKind==JsonValueKind.True);
            if(!candidate){FinishSourcesExecuteNative("failed_or_uncovered","previously observed candidate absent or not attemptable in fresh scene");return;}
            sourcesExecutePhase=2;Executor.Apply(new AIPlan{Action=AIActionType.Mine,Target="wood",X=3262,Y=399,Count=1});return;}
        if(sourcesExecutePhase==2){if(outcome.Status!="succeeded" || Loadout.CountItem(ItemID.Wood)<1){FinishSourcesExecuteNative("failed","actual mine did not yield a wood item");return;}
            sourcesExecutePhase=3;Executor.Apply(new AIPlan{Action=AIActionType.Come});return;}
        bool near=Math.Abs(npc.Center.X-p.Center.X)<=48 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<=24 && TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width);
        bool pass=outcome.Status=="succeeded" && near && sourcesExecuteAudit!.CheckPreservation().preserved && Executor.NavigationTeleportCount==0 && PrimaryDecisionRequestCount==sourcesExecuteRequests;
        FinishSourcesExecuteNative(pass?"passed":"failed",pass?"":"return, preservation or zero-call checks failed");
    }
    private void FinishSourcesExecuteNative(string status,string error)
    {
        sourcesExecuteFinished=true;File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","sources-execute-native-result.json"),JsonSerializer.Serialize(new{
            status,error,frames=sourcesExecuteFrames,requests=PrimaryDecisionRequestCount-sourcesExecuteRequests,teleports=Executor.NavigationTeleportCount,events=sourcesExecuteEvents,trace=sourcesExecuteTrace,
            scope="Controlled zero-API physical follow-up to a real sources page. Previously observed candidate 3262,399 must reappear canAttempt in fresh scene before Mine1 and Come; no LLM selection, no supplied resources, no reposition after initial setup. Hostiles removed/player protected; blocked is not success."
        },new JsonSerializerOptions{WriteIndented=false}));Stop();Executor.Clear(true);
    }
}
