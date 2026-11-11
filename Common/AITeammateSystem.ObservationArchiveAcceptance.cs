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
    private void RunObservationArchiveNative()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear(true);Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();
        SetActiveGoal("观测档案只读诊断", "零 API 观测档案诊断");Executor.SetHold(true);
        int requests=PrimaryDecisionRequestCount;ulong tick=Main.GameUpdateCount;
        string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
        const string query="wood;limit=8";
        string original=AgentKnowledge.Query(query,"world",npc,p,Executor);
        var task=new AIPlan{Action=AIActionType.Inspect,Target="world",Item=query};
        NotifyTaskResult(task,original,false,false);
        JsonElement planningBefore=JsonSerializer.SerializeToElement(PlanningContext);
        JsonElement journalOutcome=JsonSerializer.SerializeToElement(agentJournal.Outcomes.Last());
        JsonElement planningOutcome=planningBefore.GetProperty("outcomes").EnumerateArray().Last();
        string id=planningOutcome.GetProperty("observationId").GetString()??"";
        long acknowledgedOutcomeSequence=planningBefore.GetProperty("acknowledgedOutcomeSequence").GetInt64();
        object direct=ReadWorldObservation(id);
        string inspect=AgentKnowledge.Query(id,"observation",npc,p,Executor);
        JsonElement directJson=JsonSerializer.SerializeToElement(direct);
        bool available=directJson.TryGetProperty("available",out var av) && av.GetBoolean();
        bool sameOriginal=available && directJson.TryGetProperty("observation",out var archived) && JsonNode.DeepEquals(JsonNode.Parse(original),JsonNode.Parse(archived.GetRawText()));
        bool sameEntry=JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(direct)),JsonNode.Parse(inspect));
        LlmSettings settings=GetLlmSettings() ?? throw new InvalidOperationException("Native archive fixture requires available sanitized LLM settings to construct the actual planning frame; no API is called.");
        string Frame()=>PlanningRequest.Build("controlled native test","goal","controlled test",AIPlanner.BuildState(p,Executor,Memory),
            Array.Empty<LlmMessage>(),null,settings.UsePhysicsStructureView,settings.UseStructureOverview)[^1].Content;
        string frameBefore=Frame();
        var receipt=WorldObservationReceipt.FromPlanningFrame(frameBefore);
        var accepted=new AIPlan{NativeObservationReceipt=receipt};
        bool validated=goalPlanningState.ValidateDecision(accepted,out string validationError);
        if(validated)goalPlanningState.AdoptDecision(accepted,agentJournal.LastSequence,Executor.SnapshotTasks());
        long acknowledgedAfter=goalPlanningState.AcknowledgedOutcomeSequence;
        string frameAfter=Frame();
        const string secondQuery="stone;limit=8";
        string secondOriginal=AgentKnowledge.Query(secondQuery,"world",npc,p,Executor);
        NotifyTaskResult(new AIPlan{Action=AIActionType.Inspect,Target="world",Item=secondQuery},secondOriginal,false,false);
        string frameWithSecond=Frame();
        JsonNode? FindResult(string frame,string observationId)=>JsonNode.Parse(frame)?["state"]?["planning"]?["outcomes"]?.AsArray()
            .FirstOrDefault(o=>o?["observationId"]?.GetValue<string>()==observationId)?["result"];
        JsonElement secondPlanning=JsonSerializer.SerializeToElement(PlanningContext);
        string secondId=secondPlanning.GetProperty("outcomes").EnumerateArray().Last().GetProperty("observationId").GetString()??"";
        JsonNode? oldAfter=FindResult(frameAfter,id),oldWithSecond=FindResult(frameWithSecond,id),newWithSecond=FindResult(frameWithSecond,secondId);
        bool oldSummarized=oldAfter?["wireView"]?.GetValue<string>()==WorldObservationWire.View && oldWithSecond?["wireView"]?.GetValue<string>()==WorldObservationWire.View;
        bool newFull=newWithSecond?["topic"]?.GetValue<string>()=="world" && newWithSecond?["wireView"]==null;
        object directAfterReceipt=ReadWorldObservation(id);
        JsonElement directAfterJson=JsonSerializer.SerializeToElement(directAfterReceipt);
        bool originalStillRetrievable=directAfterJson.TryGetProperty("observation",out var originalAfterReceipt) && JsonNode.DeepEquals(JsonNode.Parse(original),JsonNode.Parse(originalAfterReceipt.GetRawText()));
        string after=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
        SetActiveGoal("新目标档案隔离诊断", "零 API 新目标诊断");
        object previousIdInNewGoal=ReadWorldObservation(id);
        JsonElement cross=JsonSerializer.SerializeToElement(previousIdInNewGoal);
        bool crossRejected=cross.TryGetProperty("available",out var crossAvailable) && !crossAvailable.GetBoolean();
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","observation-archive-native-result.json"),JsonSerializer.Serialize(new{
            requests=PrimaryDecisionRequestCount-requests,teleports=Executor.NavigationTeleportCount,tick,finishedTick=Main.GameUpdateCount,sameTick=tick==Main.GameUpdateCount,
            snapshotEqual=before==after,query,id,original=JsonNode.Parse(original),journalOutcome,planningOutcome,planningBefore,acknowledgedOutcomeSequence,acknowledgementUnchanged=acknowledgedOutcomeSequence==0,direct,inspect=JsonNode.Parse(inspect),sameOriginal,sameEntry,
            syntheticReceipt=new{validated,validationError,receipt,acknowledgedAfter,frameBefore,frameAfter,secondQuery,secondId,secondOriginal=JsonNode.Parse(secondOriginal),frameWithSecond,oldSummarized,newFull,originalStillRetrievable,directAfterReceipt},crossRejected,previousIdInNewGoal,
            scope="Fresh original-house copy, zero API. Native BuildState and PlanningRequest produce a synthetic accepted receipt for controlled adoption; no request was sent to an LLM. Two real world queries and NotifyTaskResult archive; bounded same-tick actor/inventory/world snapshot covers the read window. A new goal tests archive scope, not physical execution."
        },new JsonSerializerOptions{WriteIndented=false}));
        houseFinished=true;Stop();Executor.Clear(true);
    }
}
