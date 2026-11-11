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
    private bool stage1NativeStarted,stage1NativeFinished;
    private int stage1Frame,stage1Phase,stage1PlayerSlot,stage1PlayerWood,stage1TargetX,stage1TargetY,stage1CraftOutcomes,stage1RecipeId;
    private string stage1BeforePause="",stage1Paused="",stage1AfterResume="",stage1RetryBefore="",stage1FailureHash1="",stage1FailureHash2="";
    private Vector2 stage1InitialNpc;
    private readonly List<object> stage1Events=new();
    private DateTime stage1Started;
    private void BeginStage1NativeAcceptance()
    {
        NPC npc=CompanionNpc!;Player player=Main.LocalPlayer;
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();
        Loadout.Reset();Loadout.EnsureStarterTools();Executor.ResetNavigationTelemetry();
        stage1PlayerSlot=Array.FindIndex(player.inventory,i=>i.IsAir);
        if(stage1PlayerSlot<0){stage1NativeFinished=true;TestLog("stage1 setup failed: player has no empty inventory slot");return;}
        player.inventory[stage1PlayerSlot].SetDefaults(ItemID.Wood);player.inventory[stage1PlayerSlot].stack=5;
        stage1PlayerWood=player.inventory.Where(i=>i.type==ItemID.Wood).Sum(i=>i.stack);
        int nx=(int)(npc.Center.X/16),ny=(int)(npc.Bottom.Y/16)-1;
        var stand=Enumerable.Range(-12,25).Where(dx=>Math.Abs(dx)>=5 && TilePathfinder.IsSafeStand(nx+dx,ny))
            .OrderBy(dx=>Math.Abs(dx)).Select(dx=>(int?)(nx+dx)).FirstOrDefault();
        if(!stand.HasValue){stage1NativeFinished=true;TestLog("stage1 setup failed: no nearby existing stand");return;}
        stage1TargetX=stand.Value;stage1TargetY=ny;
        stage1Started=DateTime.UtcNow;stage1NativeStarted=true;
        Executor.ApplyTasks(new[]{new AIPlan{Action=AIActionType.Attack,Target="50"}});
        Executor.ApplyTasks(new[]{new AIPlan{Action=AIActionType.Attack,Target="4"}});
        stage1Events.Add(new{phase="attack_identity",frame=stage1Frame,tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks()),executorTarget=ContinuousField("target")?.ToString(),note="Synchronous ApplyTasks only; no game tick or combat."});
        var walk=new AIPlan{Action=AIActionType.Explore,X=stage1TargetX,Y=stage1TargetY};
        Recipe? recipe=CompanionCrafting.FindRecipes("94").FirstOrDefault();
        if(recipe==null){stage1NativeFinished=true;TestLog("stage1 setup failed: platform recipe missing");return;}
        stage1RecipeId=recipe.RecipeIndex;
        var craft=new AIPlan{Action=AIActionType.Craft,Item="94",Count=1,RecipeId=stage1RecipeId};
        stage1InitialNpc=npc.Center;stage1RetryBefore=Executor.CaptureRetryConditions(craft);
        ApplyPlan(new AIPlan{Goal="验收：走到附近后制作平台",ReplaceTasks=true,ReplanAfterStep=false,Steps=new(){walk,new AIPlan{Action=AIActionType.Wait},craft}},null,sourcePlayerRequest:"验收：走到附近后制作平台");
        stage1BeforePause=JsonSerializer.Serialize(Executor.SnapshotTasks());
        Executor.Apply(new AIPlan{Action=AIActionType.Stop});
        stage1Events.Add(new{phase="direct_stop",frame=stage1Frame,hold=Executor.HoldPosition,tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks()),snapshotEqual=stage1BeforePause==JsonSerializer.Serialize(Executor.SnapshotTasks())});
        ApplyPlan(new AIPlan{Hold=true,KeepTasks=true},null);
        stage1Paused=JsonSerializer.Serialize(Executor.SnapshotTasks());
        stage1Events.Add(new{phase="paused",frame=stage1Frame,hold=Executor.HoldPosition,tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks()),goal=activeGoal,planning=JsonSerializer.SerializeToElement(PlanningContext)});
        ApplyPlan(new AIPlan{KeepTasks=true,Reply="普通聊天"},null);
        stage1Events.Add(new{phase="ordinary_keep_tasks",frame=stage1Frame,hold=Executor.HoldPosition,tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks()),planning=JsonSerializer.SerializeToElement(PlanningContext)});
    }
    private void TickStage1NativeAcceptance()
    {
        if(Main.gameMenu || CompanionNpc==null)return;
        stage1Frame++;Player p=Main.LocalPlayer;NPC npc=CompanionNpc;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;
        foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(Executor.NavigationTeleportCount>0){FinishStage1Native("failed","teleport");return;}
        if(stage1Frame>3600 || DateTime.UtcNow-stage1Started>TimeSpan.FromMinutes(2))
        {FinishStage1Native("failed","case deadline");return;}
        if(stage1Phase==0 && stage1Frame>=30)
        {
            ApplyPlan(new AIPlan{Resume=true,KeepTasks=true},null);
            stage1AfterResume=JsonSerializer.Serialize(Executor.SnapshotTasks());
            stage1Events.Add(new{phase="resumed",frame=stage1Frame,hold=Executor.HoldPosition,tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks()),goal=activeGoal,planning=JsonSerializer.SerializeToElement(PlanningContext)});
            stage1Phase=1;
        }
        else if(stage1Phase==1 && agentJournal.Outcomes.Any(o=>o.Task.Action==AIActionType.Craft))
        {
            stage1Events.Add(new{phase="craft_outcome",frame=stage1Frame,outcomes=JsonSerializer.SerializeToElement(agentJournal.Outcomes),planning=JsonSerializer.SerializeToElement(PlanningContext),
                playerWood=p.inventory.Where(i=>i.type==ItemID.Wood).Sum(i=>i.stack),companionWood=Loadout.CountItem(ItemID.Wood),platform=Loadout.CountItem(94),
                startX=stage1InitialNpc.X,startY=stage1InitialNpc.Y,endX=npc.Center.X,endY=npc.Center.Y,
                retryBefore=stage1RetryBefore,retryAfter=Executor.CaptureRetryConditions(new AIPlan{Action=AIActionType.Craft,Item="94",Count=1,RecipeId=stage1RecipeId})});
            stage1CraftOutcomes=agentJournal.Outcomes.Count(o=>o.Task.Action==AIActionType.Craft);
            var retryTask=new AIPlan{Action=AIActionType.Craft,Item="94",Count=1,RecipeId=stage1RecipeId};
            stage1FailureHash1=Executor.CaptureRetryConditions(retryTask);
            stage1Events.Add(new{phase="before_same_retry_1",frame=stage1Frame,conditions=Executor.CaptureRetryConditions(retryTask),ledger=taskRetries.Evaluate(TaskFingerprint(retryTask),Executor.CaptureRetryConditions(retryTask)),npcX=npc.Center.X,npcBottom=npc.Bottom.Y});
            ApplyPlan(retryTask,null,source:"goal");
            stage1Events.Add(new{phase="same_retry_1",frame=stage1Frame,planning=JsonSerializer.SerializeToElement(PlanningContext),tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks())});stage1Phase=2;
        }
        else if(stage1Phase==2 && agentJournal.Outcomes.Count(o=>o.Task.Action==AIActionType.Craft)>stage1CraftOutcomes)
        {
            var retryTask=new AIPlan{Action=AIActionType.Craft,Item="94",Count=1,RecipeId=stage1RecipeId};
            stage1FailureHash2=Executor.CaptureRetryConditions(retryTask);
            stage1Events.Add(new{phase="before_same_retry_2",frame=stage1Frame,conditions=Executor.CaptureRetryConditions(retryTask),ledger=taskRetries.Evaluate(TaskFingerprint(retryTask),Executor.CaptureRetryConditions(retryTask)),npcX=npc.Center.X,npcBottom=npc.Bottom.Y});
            ApplyPlan(retryTask,null,source:"goal");
            stage1Events.Add(new{phase="same_retry_2",frame=stage1Frame,planning=JsonSerializer.SerializeToElement(PlanningContext),tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks())});
            ApplyPlan(new AIPlan{Action=AIActionType.Craft,Item="93",Count=1},null,source:"goal");
            stage1Events.Add(new{phase="changed_target",frame=stage1Frame,planning=JsonSerializer.SerializeToElement(PlanningContext),tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks())});
            FinishStage1Native("review","Case ended; evaluate receipts and coverage.");
        }
    }
    private void FinishStage1Native(string status,string error)
    {
        stage1NativeFinished=true;Player p=Main.LocalPlayer;NPC? npc=CompanionNpc;
        var craft=agentJournal.Outcomes.LastOrDefault(o=>o.Task.Action==AIActionType.Craft);
        bool noBorrow=craft!=null && craft.Status!="succeeded" && Loadout.CountItem(94)==0 &&
            p.inventory.Where(i=>i.type==ItemID.Wood).Sum(i=>i.stack)==stage1PlayerWood;
        bool pausePreserved=stage1BeforePause==stage1Paused && stage1BeforePause==stage1AfterResume;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","stage1-native-result.json"),JsonSerializer.Serialize(new{
            status,error,frames=stage1Frame,pausePreserved,noBorrow,recipeId=stage1RecipeId,sameHash=stage1FailureHash1.Length>0 && stage1FailureHash1==stage1FailureHash2,stage1BeforePause,stage1Paused,stage1AfterResume,
            playerWoodBefore=stage1PlayerWood,playerWoodAfter=p.inventory.Where(i=>i.type==ItemID.Wood).Sum(i=>i.stack),
            companionWood=Loadout.CountItem(ItemID.Wood),companionPlatform=Loadout.CountItem(94),
            teleports=Executor.NavigationTeleportCount,finalNpc=npc==null?null:new{x=npc.Center.X,y=npc.Center.Y,bottom=npc.Bottom.Y},
            outcomes=JsonSerializer.SerializeToElement(agentJournal.Outcomes),planning=JsonSerializer.SerializeToElement(PlanningContext),events=stage1Events,
            uncovered=new[]{"door change retry"},
            scope="Copied original house, single initial setup, player wood injected for self-material isolation, hostiles removed and player protected; no LLM."
        },new JsonSerializerOptions{WriteIndented=true}));
        Stop();TestLog($"stage1 native {status}: {error}");
    }
}

