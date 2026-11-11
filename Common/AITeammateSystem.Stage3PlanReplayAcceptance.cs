using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private bool stage3RevisionStarted,stage3RevisionFinished;
    private int stage3RevisionFrame,stage3RevisionPhase,stage3RevisionX,stage3RevisionY;
    private DateTime stage3RevisionAt;
    private readonly List<object> stage3RevisionEvents=new();
    private void BeginStage3RevisionNativeAcceptance()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear();Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;int x=(int)(npc.Center.X/16),y=(int)(npc.Bottom.Y/16)-1;
        int? target=Enumerable.Range(-16,33).Where(dx=>Math.Abs(dx)>=8 && TilePathfinder.IsSafeStand(x+dx,y))
            .OrderBy(dx=>Math.Abs(dx)).Select(dx=>(int?)(x+dx)).FirstOrDefault();
        if(!target.HasValue)throw new InvalidOperationException("No nearby native stand for revision case.");
        stage3RevisionX=target.Value;stage3RevisionY=y;
        WorkingPlanProposal Graph(string objective)=>new(){Summary=objective,Steps=new(){new(){Id="work",Objective=objective,SuccessCriteria="执行成功"}}};
        bool accepted=ApplyPlan(new AIPlan{Goal="原屋行走版本验收",WorkingPlan=Graph("原目的"),ReplaceTasks=true,ReplanAfterStep=false,
            Steps=new(){new AIPlan{Action=AIActionType.Explore,X=stage3RevisionX,Y=stage3RevisionY,PlanStep="work"}}},null,sourcePlayerRequest:"原屋行走版本验收");
        stage3RevisionEvents.Add(new{phase="original_dispatch",accepted,planning=JsonSerializer.SerializeToElement(PlanningContext),tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks())});
        stage3RevisionAt=DateTime.UtcNow;stage3RevisionStarted=true;
    }
    private void TickStage3RevisionNativeAcceptance()
    {
        if(Main.gameMenu || CompanionNpc==null)return;stage3RevisionFrame++;Player p=Main.LocalPlayer;p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;
        foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(Executor.NavigationTeleportCount>0 || stage3RevisionFrame>1800 || DateTime.UtcNow-stage3RevisionAt>TimeSpan.FromSeconds(60))
        {FinishStage3RevisionNative("failed","teleport or deadline");return;}
        if(stage3RevisionPhase==0 && stage3RevisionFrame>=10)
        {
            int? ticksBefore=ContinuousField("stepTicks") as int?;
            var currentBefore=Executor.SnapshotTasks().FirstOrDefault();
            var graph=new WorkingPlanProposal{Summary="修订目的",Steps=new(){new(){Id="work",Objective="修订目的",SuccessCriteria="执行成功"}}};
            bool accepted=ApplyPlan(new AIPlan{WorkingPlan=graph,ReplaceTasks=true,ReplanAfterStep=false,
                Steps=new(){new AIPlan{Action=AIActionType.Explore,X=stage3RevisionX,Y=stage3RevisionY,PlanStep="work"}}},null,source:"goal");
            stage3RevisionEvents.Add(new{phase="same_task_new_purpose",accepted,ticksBefore,ticksAfter=ContinuousField("stepTicks"),
                sameCurrentReference=ReferenceEquals(currentBefore,Executor.SnapshotTasks().FirstOrDefault()),
                planning=JsonSerializer.SerializeToElement(PlanningContext),tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks())});stage3RevisionPhase=1;
        }
        else if(stage3RevisionPhase==1 && agentJournal.Outcomes.Any(o=>o.Task.Action==AIActionType.Explore))
        {
            stage3RevisionEvents.Add(new{phase="original_outcome",planning=JsonSerializer.SerializeToElement(PlanningContext),outcomes=JsonSerializer.SerializeToElement(agentJournal.Outcomes)});
            bool accepted=ApplyPlan(new AIPlan{Action=AIActionType.Inspect,Target="nearby",PlanStep="work"},null,source:"goal");
            stage3RevisionEvents.Add(new{phase="new_task_dispatch",accepted,planning=JsonSerializer.SerializeToElement(PlanningContext),tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks())});stage3RevisionPhase=2;
        }
        else if(stage3RevisionPhase==2 && agentJournal.Outcomes.Any(o=>o.Task.Action==AIActionType.Inspect))
        {
            stage3RevisionEvents.Add(new{phase="new_task_outcome",planning=JsonSerializer.SerializeToElement(PlanningContext),outcomes=JsonSerializer.SerializeToElement(agentJournal.Outcomes)});
            FinishStage3RevisionNative("review","");
        }
    }
    private void FinishStage3RevisionNative(string status,string error)
    {
        stage3RevisionFinished=true;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","stage3-revision-native-result.json"),JsonSerializer.Serialize(new{
            status,error,frames=stage3RevisionFrame,teleports=Executor.NavigationTeleportCount,events=stage3RevisionEvents,
            scope="Existing original-house copy; actual executor walk and inspect, one initial setup, no LLM; hostiles removed/player protected."
        },new JsonSerializerOptions{WriteIndented=true}));
        Stop();TestLog($"stage3 revision native {status}: {error}");
    }
    private void RunStage3MaterialsParseAcceptance()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear();
        string? path=Environment.GetEnvironmentVariable("AITEAMMATE_REPLAY_PUBLIC_DECISION_PATH");
        if(string.IsNullOrWhiteSpace(path) || !File.Exists(path))throw new InvalidOperationException("Explicit replay decision path is missing.");
        int before=PrimaryDecisionRequestCount;
        AIPlan plan=AIPlanParser.Parse(File.ReadAllText(path));
        bool accepted=ApplyPlan(plan,null,source:"chat",sourcePlayerRequest:"请从屋里出发，收集20个木材和10个石块，然后带着材料回到我身边。按实际情况行动，遇到无法到达或资源不足请如实告诉我。");
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","stage3-materials-parse-result.json"),JsonSerializer.Serialize(new{
            accepted,planning=PlanningContext,tasks=Executor.SnapshotTasks(),requestsBefore=before,requestsAfter=PrimaryDecisionRequestCount,
            scope="Captured public response parsed and dispatched synchronously; no game tick, physical execution or LLM request."
        },new JsonSerializerOptions{WriteIndented=true}));
        houseFinished=true;Stop();TestLog("stage3 materials parser replay completed; dispatch only");
    }
    private void RunStage3PlanReplayAcceptance()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear();
        string? path = Environment.GetEnvironmentVariable("AITEAMMATE_REPLAY_PUBLIC_DECISION_PATH");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("Explicit replay decision path is missing.");
        using JsonDocument input=JsonDocument.Parse(File.ReadAllText(path));
        var rows=input.RootElement.EnumerateArray().Select(x=>x.GetRawText()).ToArray();
        if(rows.Length!=3)throw new InvalidOperationException("Expected exactly three captured public decisions.");
        var events=new List<object>();int requestsBefore=PrimaryDecisionRequestCount;
        for(int i=0;i<rows.Length;i++)
        {
            AIPlan plan=AIPlanParser.Parse(rows[i]);bool accepted=ApplyPlan(plan,null,source:i==0?"chat":"goal",sourcePlayerRequest:i==0?"请从屋里出发，收集20个木材和10个石块，然后带着材料回到我身边。按实际情况行动，遇到无法到达或资源不足请如实告诉我。":null);
            events.Add(new{phase=$"replay_{i+1}",accepted,planning=JsonSerializer.SerializeToElement(PlanningContext),tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks())});
        }
        Executor.Clear();goalReviewPending=false;
        for(int i=0;i<3;i++)
        {
            bool accepted=ApplyPlan(new AIPlan{Action=AIActionType.Mine,Target="wood",Count=20,PlanStep="missing_step"},null,source:"goal");
            events.Add(new{phase=$"invalid_{i+1}",accepted,planning=JsonSerializer.SerializeToElement(PlanningContext),
                awaitingReview=goalReviewPending,lastResult=lastGoalResult,tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks())});
        }
        bool repaired=ApplyPlan(new AIPlan{Action=AIActionType.Inspect,Target="nearby"},null,source:"goal");
        events.Add(new{phase="valid_adopted",accepted=repaired,planning=JsonSerializer.SerializeToElement(PlanningContext)});
        Executor.Clear();
        for(int i=0;i<3;i++)
        {
            try{AIPlanParser.Parse("{");throw new InvalidOperationException("Malformed JSON unexpectedly parsed.");}
            catch(JsonException error){HandleProtocolRejection("goal","",error.Message);}
            events.Add(new{phase=$"parser_error_{i+1}",goal=activeGoal,awaitingReview=goalReviewPending,lastResult=lastGoalResult,
                planning=JsonSerializer.SerializeToElement(PlanningContext)});
        }
        bool parserRecovery=ApplyPlan(new AIPlan{Action=AIActionType.Inspect,Target="nearby"},null,source:"goal");
        events.Add(new{phase="parser_recovery_adopted",accepted=parserRecovery,planning=JsonSerializer.SerializeToElement(PlanningContext)});
        Executor.Clear();
        ApplyPlan(new AIPlan{Action=AIActionType.Mine,Target="wood",Count=20,PlanStep="missing_step"},null,source:"goal");
        events.Add(new{phase="new_rejection",planning=JsonSerializer.SerializeToElement(PlanningContext)});
        ApplyPlan(new AIPlan{Goal="全新重放目标"},null,sourcePlayerRequest:"全新重放目标");
        events.Add(new{phase="new_intent",planning=JsonSerializer.SerializeToElement(PlanningContext)});
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","stage3-plan-replay-result.json"),JsonSerializer.Serialize(new{
            events,requestsBefore,requestsAfter=PrimaryDecisionRequestCount,teleports=Executor.NavigationTeleportCount,
            sourcePath=path,scope="Three captured public decisions replayed synchronously; no game tick between dispatches, no physical execution or LLM request."
        },new JsonSerializerOptions{WriteIndented=true}));
        houseFinished=true;Stop();TestLog("stage3 public plan replay done; dispatch only");
    }
}
