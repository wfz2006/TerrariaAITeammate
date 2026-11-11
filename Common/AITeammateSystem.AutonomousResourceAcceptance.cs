using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string AutonomousResourceRequest="请自己寻找材料，收集至少10个木材和5个石块，带回我身边后，用其中1个木材制作2个木平台。最后把至少9木材、5石块和2个平台留在你自己的背包里，完成后告诉我。";
    private bool autonomousResourceStarted,autonomousResourceFinished,autonomousResourceReturnedWithMaterials,autonomousResourceCraftVerified;
    private int autonomousResourceRequestStart,autonomousResourceFrames,autonomousResourcePriorWood,autonomousResourcePriorStone,autonomousResourcePriorPlatform,autonomousResourceMineWood,autonomousResourceMineStone;
    private long autonomousResourceSequence; private DateTime autonomousResourceAt;
    private int autonomousResourceReturnedFrame=-1; private ulong autonomousResourceStartedTick,autonomousResourceReturnedTick;
    private readonly List<object> autonomousResourceOutcomes=new(),autonomousResourceTrace=new(),autonomousResourceMilestones=new();
    private int AutonomousResourceLimit=>int.TryParse(Environment.GetEnvironmentVariable("AITEAMMATE_RESOURCE_REQUEST_LIMIT"),out int n)?Math.Clamp(n,1,12):12;
    private bool AutonomousResourceWithinTime=>DateTime.UtcNow-autonomousResourceAt<TimeSpan.FromMinutes(6);
    private bool AutonomousResourcePlanningAllowed=>autonomousResourceStarted && !autonomousResourceFinished && PrimaryDecisionRequestCount-autonomousResourceRequestStart<AutonomousResourceLimit && AutonomousResourceWithinTime;
    private void BeginAutonomousResourceAcceptance()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear();Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;npc.Center=Stand(3220,401);npc.velocity=Microsoft.Xna.Framework.Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Microsoft.Xna.Framework.Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();autonomousResourcePriorWood=Loadout.CountItem(ItemID.Wood);autonomousResourcePriorStone=Loadout.CountItem(ItemID.StoneBlock);autonomousResourcePriorPlatform=Loadout.CountItem(94);
        autonomousResourceSequence=agentJournal.LastSequence;autonomousResourceRequestStart=PrimaryDecisionRequestCount;autonomousResourceAt=DateTime.UtcNow;autonomousResourceStartedTick=Main.GameUpdateCount;autonomousResourceStarted=true;
        autonomousResourceMilestones.Add(new{kind="initial_setup",npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},player=new{x=p.Center.X/16,y=p.Center.Y/16,feet=p.Bottom.Y/16},wood=autonomousResourcePriorWood,stone=autonomousResourcePriorStone,platform=autonomousResourcePriorPlatform});
        if(autonomousResourcePriorWood!=0 || autonomousResourcePriorStone!=0 || autonomousResourcePriorPlatform!=0){FinishAutonomousResource("failed","initial inventory is not empty");return;}
        HandlePlayerMessage(p,AutonomousResourceRequest);SaveAutonomousResource("running","");
    }
    private void TickAutonomousResourceAcceptance()
    {
        if(Main.gameMenu || CompanionNpc==null)return;NPC npc=CompanionNpc;Player p=Main.LocalPlayer;autonomousResourceFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        int wood=Loadout.CountItem(ItemID.Wood),stone=Loadout.CountItem(ItemID.StoneBlock),platform=Loadout.CountItem(94);
        bool grounded=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width);
        bool near=Math.Abs(npc.Center.X-p.Center.X)<=48 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<=24 && grounded;
        var newOutcomes=agentJournal.Outcomes.Where(o=>o.Sequence>autonomousResourceSequence).OrderBy(o=>o.Sequence).ToArray();
        if(!autonomousResourceReturnedWithMaterials && !newOutcomes.Any(o=>o.Task.Action==AIActionType.Craft) && platform==0 && wood>=10 && stone>=5 && near)
        {autonomousResourceReturnedWithMaterials=true;autonomousResourceReturnedFrame=autonomousResourceFrames;autonomousResourceReturnedTick=Main.GameUpdateCount;autonomousResourceMilestones.Add(new{kind="returned_with_materials",frame=autonomousResourceReturnedFrame,tick=autonomousResourceReturnedTick,wood,stone,platform,grounded,horizontal=Math.Abs(npc.Center.X-p.Center.X),feetDelta=Math.Abs(npc.Bottom.Y-p.Bottom.Y)});}
        foreach(var o in newOutcomes)
        {
            autonomousResourceSequence=o.Sequence;
            if(o.Task.Action==AIActionType.Mine)
                try {using var result=JsonDocument.Parse(o.Result);if(result.RootElement.TryGetProperty("actualGained",out var gained) && gained.TryGetInt32(out int amount))
                    {if(o.Task.Target=="wood")autonomousResourceMineWood+=amount;else if(o.Task.Target=="stone")autonomousResourceMineStone+=amount;}}
                catch(JsonException){}
            bool craft=o.Task.Action==AIActionType.Craft && o.Status=="succeeded" && autonomousResourceReturnedWithMaterials && autonomousResourceReturnedFrame<autonomousResourceFrames && autonomousResourceReturnedTick<Main.GameUpdateCount && near && platform-autonomousResourcePriorPlatform>=2 && autonomousResourcePriorWood-wood==1;
            if(craft)autonomousResourceCraftVerified=true;
            autonomousResourceOutcomes.Add(new{o.Sequence,o.Task,o.Status,o.Result,frame=autonomousResourceFrames,woodBefore=autonomousResourcePriorWood,woodAfter=wood,stoneBefore=autonomousResourcePriorStone,stoneAfter=stone,platformBefore=autonomousResourcePriorPlatform,platformAfter=platform,near,grounded,craftVerified=craft,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16}});
            autonomousResourcePriorWood=wood;autonomousResourcePriorStone=stone;autonomousResourcePriorPlatform=platform;
        }
        if(autonomousResourceFrames%30==0)
        {
            autonomousResourceTrace.Add(new{frame=autonomousResourceFrames,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16,vx=npc.velocity.X,vy=npc.velocity.Y},player=new{x=p.Center.X/16,y=p.Center.Y/16,feet=p.Bottom.Y/16},wood,stone,platform,near,grounded,action=Executor.CurrentAction.ToString(),warps=Executor.NavigationTeleportCount});
            SaveAutonomousResource("running","");
        }
        if(Executor.NavigationTeleportCount>0){FinishAutonomousResource("failed","teleport");return;}
        if(decisionFailures>0 || dispatchRepairs.LastCode=="protocol_parse_rejected"){FinishAutonomousResource("failed","first API or parse failure");return;}
        JsonElement proof=JsonSerializer.SerializeToElement(lastCompletedGoal);
        bool ownProof=proof.ValueKind==JsonValueKind.Object && proof.TryGetProperty("sourcePlayerRequest",out var source) && source.GetString()==AutonomousResourceRequest && proof.TryGetProperty("observedAtTick",out var tick) && tick.GetUInt64()>=autonomousResourceStartedTick && proof.TryGetProperty("worldId",out var world) && world.GetInt32()==Main.worldID;
        bool physical=autonomousResourceMineWood>=10 && autonomousResourceMineStone>=5 && autonomousResourceReturnedWithMaterials && autonomousResourceCraftVerified && wood>=9 && stone>=5 && platform>=2 && near;
        if(goalStatus=="completed" && ownProof){FinishAutonomousResource(physical?"passed":"failed",physical?"":"completed without required real ordered evidence");return;}
        bool reviewTailOnly=goalReviewPending && ContinuousField("currentPlan") is not AIPlan && !Executor.IsInCombat && !Executor.IsAutoDefending;
        bool exhausted=PrimaryDecisionRequestCount-autonomousResourceRequestStart>=AutonomousResourceLimit && primaryCancellation==null &&
            (!Executor.IsBusy && Executor.SnapshotTasks().Count==0 || reviewTailOnly);
        if(!AutonomousResourceWithinTime || exhausted || goalWaitingForPlayer)
            FinishAutonomousResource("failed",!AutonomousResourceWithinTime?"wall_deadline":goalWaitingForPlayer?"goal_waiting":reviewTailOnly?"request_budget_exhausted_waiting_review":"request_budget_exhausted");
    }
    private void FinishAutonomousResource(string status,string error)
    {autonomousResourceFinished=true;SaveAutonomousResource(status,error);Stop();Executor.Clear(true);}
    private void SaveAutonomousResource(string status,string error)
    {
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","autonomous-resource-result.json"),JsonSerializer.Serialize(new{
            status,error,request=AutonomousResourceRequest,startedUtc=autonomousResourceAt,updatedUtc=DateTime.UtcNow,frames=autonomousResourceFrames,
            requests=PrimaryDecisionRequestCount-autonomousResourceRequestStart,requestLimit=AutonomousResourceLimit,teleports=Executor.NavigationTeleportCount,
            goalReviewPending,executorIsBusy=Executor.IsBusy,currentExecutorAction=Executor.CurrentAction.ToString(),currentExecutorPlan=ContinuousField("currentPlan"),remainingTasks=Executor.SnapshotTasks(),primaryInFlight=primaryCancellation!=null,
            goalStatus,reply=LastReply,planning=JsonSerializer.SerializeToElement(PlanningContext),lastCompletedGoal=lastCompletedGoal,startedTick=autonomousResourceStartedTick,mineWood=autonomousResourceMineWood,mineStone=autonomousResourceMineStone,returnedWithMaterials=autonomousResourceReturnedWithMaterials,returnedFrame=autonomousResourceReturnedFrame,returnedTick=autonomousResourceReturnedTick,craftVerified=autonomousResourceCraftVerified,
            milestones=autonomousResourceMilestones,outcomes=autonomousResourceOutcomes,trace=autonomousResourceTrace,
            inventory=new{wood=Loadout.CountItem(ItemID.Wood),stone=Loadout.CountItem(ItemID.StoneBlock),platform=Loadout.CountItem(94)},
            scope="Single fresh natural Flash request; original-house copy, starter tools, one setup, no injected materials or mid-task reposition. Production action policy applies; hostiles removed/player protected."
        },new JsonSerializerOptions{WriteIndented=true}));
    }
}
