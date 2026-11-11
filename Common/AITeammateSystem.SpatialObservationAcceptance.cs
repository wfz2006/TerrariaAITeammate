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
    private static string SpatialArchiveTiles()
    {
        var cells=new List<object>();foreach(var r in new[]{(x:3100,y:385),(x:3210,y:385)})
            for(int x=r.x;x<r.x+40;x++)for(int y=r.y;y<=r.y+30;y++)
            {Tile t=Main.tile[x,y];cells.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsActuated,t.IsHalfBlock,t.Slope,t.LiquidAmount,t.LiquidType});}
        return JsonSerializer.Serialize(cells);
    }
    private void RunSpatialObservationArchiveNative()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear(true);Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;Loadout.Reset();Loadout.EnsureStarterTools();
        SetActiveGoal("地形档案只读诊断","零 API 受控地形档案诊断");Executor.SetHold(true);
        ulong tick=Main.GameUpdateCount;int requests=PrimaryDecisionRequestCount;string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p)),tilesBefore=SpatialArchiveTiles();
        const string q1="3100,385,40,30",qShared="3100,385,40,29",q2="3210,385,40,30";
        string original=AgentKnowledge.Query(q1,"terrain",npc,p,Executor);NotifyTaskResult(new AIPlan{Action=AIActionType.Inspect,Target="terrain",Item=q1},original,false,false);
        JsonElement planning=JsonSerializer.SerializeToElement(PlanningContext),journal=JsonSerializer.SerializeToElement(agentJournal.Outcomes.Last());
        string id=planning.GetProperty("outcomes").EnumerateArray().Last().GetProperty("observationId").GetString()??"";
        long ackBefore=planning.GetProperty("acknowledgedOutcomeSequence").GetInt64();object direct=ReadWorldObservation(id);string standard=AgentKnowledge.Query(id,"observation",npc,p,Executor);
        JsonElement directJson=JsonSerializer.SerializeToElement(direct);
        bool originalAvailable=directJson.TryGetProperty("available",out var av) && av.GetBoolean() && directJson.TryGetProperty("originalTopic",out var topic) && topic.GetString()=="terrain" &&
            directJson.TryGetProperty("observation",out var body) && JsonNode.DeepEquals(JsonNode.Parse(original),JsonNode.Parse(body.GetRawText()));
        bool standardEqual=JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(direct)),JsonNode.Parse(standard));
        string shared=AgentKnowledge.Query(qShared,"terrain",npc,p,Executor);NotifyTaskResult(new AIPlan{Action=AIActionType.Inspect,Target="terrain",Item=qShared},shared,false,false);
        LlmSettings settings=GetLlmSettings()??throw new InvalidOperationException("Spatial fixture requires configured settings for a synthetic frame; no API is called.");
        string Frame()=>PlanningRequest.Build("controlled native test","goal","controlled test",AIPlanner.BuildState(p,Executor,Memory),Array.Empty<LlmMessage>(),null,settings.UsePhysicsStructureView,settings.UseStructureOverview)[^1].Content;
        string frameBefore=Frame();var receipt=WorldObservationReceipt.FromPlanningFrame(frameBefore);var decision=new AIPlan{NativeObservationReceipt=receipt};
        bool tablePresent=frameBefore.Contains("\"columns_and_rows_v1\"",StringComparison.Ordinal);
        bool sameFrameRefPresent=frameBefore.Contains("\"$ref\":\"#/state/planning/outcomes/",StringComparison.Ordinal);
        bool exactOriginalAcknowledged=receipt?.CompleteWorlds.Any(o=>o.Id==id && o.ContentHash==WorldObservationArchive.ContentHash(JsonSerializer.Deserialize<JsonElement>(original)))==true;
        bool validated=goalPlanningState.ValidateDecision(decision,out string error);if(validated)goalPlanningState.AdoptDecision(decision,agentJournal.LastSequence,Executor.SnapshotTasks());
        long ackAfter=goalPlanningState.AcknowledgedOutcomeSequence;string frameAfter=Frame();
        string second=AgentKnowledge.Query(q2,"terrain",npc,p,Executor);NotifyTaskResult(new AIPlan{Action=AIActionType.Inspect,Target="terrain",Item=q2},second,false,false);
        JsonElement planningSecond=JsonSerializer.SerializeToElement(PlanningContext);string secondId=planningSecond.GetProperty("outcomes").EnumerateArray().Last().GetProperty("observationId").GetString()??"";
        string frameSecond=Frame();
        JsonNode? Find(string frame,string observationId)=>JsonNode.Parse(frame)?["state"]?["planning"]?["outcomes"]?.AsArray().FirstOrDefault(o=>o?["observationId"]?.GetValue<string>()==observationId)?["result"];
        JsonNode? firstBefore=Find(frameBefore,id),firstAfter=Find(frameAfter,id),firstWithSecond=Find(frameSecond,id),secondWire=Find(frameSecond,secondId);
        bool fullBefore=firstBefore?["topic"]?.GetValue<string>()=="terrain" && firstBefore?["wireView"]==null;
        bool oldSummary=firstAfter?["wireView"]?.GetValue<string>()=="acknowledged_spatial_summary_v1" && firstWithSecond?["wireView"]?.GetValue<string>()=="acknowledged_spatial_summary_v1";
        bool newFull=secondWire?["topic"]?.GetValue<string>()=="terrain" && secondWire?["wireView"]==null;
        JsonElement reread=JsonSerializer.SerializeToElement(ReadWorldObservation(id));bool stillOriginal=reread.TryGetProperty("observation",out var again) && JsonNode.DeepEquals(JsonNode.Parse(original),JsonNode.Parse(again.GetRawText()));
        bool journalEqual=journal.TryGetProperty("Result",out var result) && JsonNode.DeepEquals(JsonNode.Parse(original),JsonNode.Parse(result.GetString()??"null"));
        string after=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p)),tilesAfter=SpatialArchiveTiles();bool sameTick=tick==Main.GameUpdateCount,snapshotEqual=before==after,tilesEqual=tilesBefore==tilesAfter;
        SetActiveGoal("地形档案新目标隔离","零 API 新目标诊断");JsonElement cross=JsonSerializer.SerializeToElement(ReadWorldObservation(id));bool crossRejected=cross.TryGetProperty("available",out var ca) && !ca.GetBoolean();
        bool pass=originalAvailable && standardEqual && ackBefore==0 && validated && receipt!=null && ackAfter>0 && tablePresent && sameFrameRefPresent && exactOriginalAcknowledged && fullBefore && oldSummary && newFull && stillOriginal && journalEqual && sameTick && snapshotEqual && tilesEqual && crossRejected && PrimaryDecisionRequestCount==requests && Executor.NavigationTeleportCount==0;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","spatial-observation-archive-native-result.json"),JsonSerializer.Serialize(new{
            status=pass?"passed":"failed",checks=new{originalAvailable,standardEqual,ackBefore,validated,error,receiptPresent=receipt!=null,ackAfter,tablePresent,sameFrameRefPresent,exactOriginalAcknowledged,fullBefore,oldSummary,newFull,stillOriginal,journalEqual,sameTick,snapshotEqual,tilesEqual,crossRejected,zeroApi=PrimaryDecisionRequestCount==requests,zeroWarp=Executor.NavigationTeleportCount==0,rng="unknown_not_captured"},
            tick,finishedTick=Main.GameUpdateCount,requests=PrimaryDecisionRequestCount-requests,teleports=Executor.NavigationTeleportCount,id,secondId,q1,qShared,q2,original=JsonNode.Parse(original),shared=JsonNode.Parse(shared),second=JsonNode.Parse(second),direct,standard=JsonNode.Parse(standard),journal,planning,receipt,frameBefore,frameAfter,frameSecond,cross,
            tileBounds=new[]{new{x=3100,y=385,width=40,height=31},new{x=3210,y=385,width=40,height=31}},
            scope="Controlled synthetic accepted receipt from actual BuildState/PlanningRequest; no LLM call. Two real same-tick terrain observations, bounded actor/inventory snapshot and both queried tile rectangles; RNG not captured. Summary is historical, not current world recheck."
        },new JsonSerializerOptions{WriteIndented=false}));houseFinished=true;Stop();Executor.Clear(true);
    }
}
