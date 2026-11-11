using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private bool shelterGuardStarted,shelterGuardFinished;private int shelterGuardPhase,shelterGuardRequests,shelterGuardFrames;private long shelterGuardSequence;private DateTime shelterGuardAt;
    private ShelterWorldAudit? shelterGuardAudit;private string shelterGuardBefore="",shelterGuardInventoryBefore="";private bool shelterGuardPreflightOk,shelterGuardDeniedOk;private readonly List<object> shelterGuardEvents=new();
    private static string ShelterGuardArea()
    {
        var cells=new List<object>();for(int x=3098;x<=3107;x++)for(int y=393;y<=399;y++){Tile t=Main.tile[x,y];cells.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsHalfBlock,t.Slope,t.IsActuated,t.LiquidAmount});}
        return JsonSerializer.Serialize(cells);
    }
    private void BeginShelterGuardNative()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear(true);Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;npc.Center=new Vector2(3096.339f*16,398.6875f*16);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();var supplied=CompanionConstruction.ShelterMaterials("ebonwood");foreach(var pair in supplied)if(!Loadout.AddItem(pair.Key,pair.Value))throw new InvalidOperationException("Controlled guard materials do not fit.");
        bool accepted=ApplyPlan(new AIPlan{Goal="受控原屋保护建造诊断",CompletionConditions=new(){new GoalCondition{Type="structure_preserved"}}},null,sourcePlayerRequest:"零 API 受控保护合同诊断");
        shelterGuardAudit=ShelterWorldAudit.Capture(3220,390);shelterGuardBefore=ShelterGuardArea();shelterGuardRequests=PrimaryDecisionRequestCount;shelterGuardSequence=agentJournal.LastSequence;shelterGuardAt=DateTime.UtcNow;shelterGuardStarted=true;
        ulong tick=Main.GameUpdateCount;string snapshot=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
        object denied=Executor.InspectShelterBuild(npc,"ebonwood",3098,393),allowed=Executor.InspectShelterBuild(npc,"ebonwood",3100,393);
        string standard=AgentKnowledge.Query("ebonwood","build",npc,p,Executor,3098,393);
        JsonElement deniedJson=JsonSerializer.SerializeToElement(denied),allowedJson=JsonSerializer.SerializeToElement(allowed);
        JsonElement dc=deniedJson.GetProperty("siteSearch").GetProperty("candidates")[0],ac=allowedJson.GetProperty("siteSearch").GetProperty("candidates")[0];
        JsonElement protection=dc.GetProperty("protection");
        bool containsProtectedCell=protection.GetProperty("protectedHits").EnumerateArray().Any(h=>h.GetProperty("x").GetInt32()==3098 && h.GetProperty("y").GetInt32()==399);
        shelterGuardPreflightOk=accepted && dc.GetProperty("geometryStatus").GetString()=="geometry_passed" && !dc.GetProperty("allowedByCurrentChecks").GetBoolean() && containsProtectedCell &&
            ac.GetProperty("geometryStatus").GetString()=="geometry_passed" && ac.GetProperty("allowedByCurrentChecks").GetBoolean() && JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(denied)),JsonNode.Parse(standard)) &&
            tick==Main.GameUpdateCount && snapshot==JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
        shelterGuardInventoryBefore=JsonSerializer.Serialize(ShelterCounts(Loadout.Inventory));
        shelterGuardEvents.Add(new{phase="baseline",goalAccepted=accepted,tick,sameTick=tick==Main.GameUpdateCount,snapshotEqual=snapshot==JsonSerializer.Serialize(CaptureMinePreviewState(npc,p)),
            supplied,productionBaseline=PreservationEvidence.DebugSnapshot(),fixtureBaseline=shelterGuardAudit.BaselineReport,explanation=shelterGuardAudit.ExplainProtection(3098,399),denied,allowed,standard=JsonSerializer.Deserialize<JsonElement>(standard),inventory=ShelterCounts(Loadout.Inventory)});
        if(!shelterGuardPreflightOk){FinishShelterGuardNative("failed","guard preflight checks failed; no build dispatched");return;}DispatchShelterGuardNative();
    }
    private void DispatchShelterGuardNative()
    {
        var task=new AIPlan{Action=AIActionType.Build,Blueprint="shelter",Target="ebonwood",X=shelterGuardPhase==0?3098:3100,Y=393};
        shelterGuardEvents.Add(new{phase="dispatch",step=shelterGuardPhase,task,inventory=ShelterCounts(Loadout.Inventory)});Executor.Apply(task);
    }
    private void TickShelterGuardNative()
    {
        if(Main.gameMenu || CompanionNpc==null)return;NPC npc=CompanionNpc;Player p=Main.LocalPlayer;shelterGuardFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(Executor.NavigationTeleportCount>0 || DateTime.UtcNow-shelterGuardAt>TimeSpan.FromMinutes(5)){FinishShelterGuardNative("stopped","teleport or deadline");return;}
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>shelterGuardSequence);if(outcome==null)return;shelterGuardSequence=outcome.Sequence;
        var conditions=new[]{new GoalCondition{Type="structure_preserved"},new GoalCondition{Type="new_housing_valid",X=3100,Y=393,Width=10,Height=7}};
        var native=GoalWorldObserver.Capture(this,conditions,goalBossDeathBoundary);
        shelterGuardEvents.Add(new{phase="outcome",step=shelterGuardPhase,outcome,frame=shelterGuardFrames,tick=Main.GameUpdateCount,
            areaUnchanged=shelterGuardBefore==ShelterGuardArea(),inventory=ShelterCounts(Loadout.Inventory),nativeConditions=native.NativeConditions,
            production=PreservationEvidence.DebugSnapshot(),fixturePreservation=shelterGuardAudit!.CheckPreservation()});
        if(shelterGuardPhase++==0){shelterGuardDeniedOk=outcome.Status=="blocked" && shelterGuardBefore==ShelterGuardArea() && shelterGuardInventoryBefore==JsonSerializer.Serialize(ShelterCounts(Loadout.Inventory));
            if(!shelterGuardDeniedOk){FinishShelterGuardNative("failed","protected build was not rejected without changes");return;}DispatchShelterGuardNative();return;}
        bool pass=outcome.Status=="succeeded" && native.NativeConditions.TryGetValue(0,out var preserved) && preserved.Met==true &&
            native.NativeConditions.TryGetValue(1,out var housing) && housing.Met==true && shelterGuardAudit!.CheckPreservation().preserved && PrimaryDecisionRequestCount==shelterGuardRequests && Executor.NavigationTeleportCount==0;
        FinishShelterGuardNative(pass?"passed":"failed",pass?"":"allowed build, housing, preservation or zero-call checks failed");
    }
    private void FinishShelterGuardNative(string status,string error)
    {
        shelterGuardFinished=true;File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","shelter-guard-native-result.json"),JsonSerializer.Serialize(new{
            status,error,preflightOk=shelterGuardPreflightOk,deniedOk=shelterGuardDeniedOk,frames=shelterGuardFrames,requests=PrimaryDecisionRequestCount-shelterGuardRequests,teleports=Executor.NavigationTeleportCount,events=shelterGuardEvents,
            scope="Controlled zero-API synthetic structure-preservation goal: original immutable baseline; supplied ebonwood template materials only at setup. Denied 3098 then allowed 3100 uses production Build, not an autonomous plan. Hostiles removed/player protected; no mid-case reset or reposition."
        },new JsonSerializerOptions{WriteIndented=false}));Stop();Executor.Clear(true);
    }
}
