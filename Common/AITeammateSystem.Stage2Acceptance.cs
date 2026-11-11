using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private bool stage2NativeStarted,stage2NativeFinished;
    private int stage2Phase,stage2Frame,stage2SeedX,stage2SeedY,stage2InspectCount;
    private DateTime stage2Started;
    private readonly List<object> stage2Events=new();
    private List<GoalCondition> stage2Contract=new();
    private JsonElement Stage2Planning(){SynchronizeGoalQueue();return JsonSerializer.SerializeToElement(PlanningContext);}
    private void BeginStage2NativeAcceptance()
    {
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Loadout.Reset();Loadout.EnsureStarterTools();Executor.ResetNavigationTelemetry();
        // One wood is injected solely to exercise a real craft; this is not a natural resource success fixture.
        if(!Loadout.AddItem(ItemID.Wood,1))throw new InvalidOperationException("Could not set up craft ingredient.");
        stage2Contract=new(){new(){Type="inventory_at_least",ItemId=ItemID.Wood,Count=20},new(){Type="inventory_at_least",ItemId=ItemID.StoneBlock,Count=10},new(){Type="near_player",HorizontalPixels=48,VerticalPixels=24}};
        var plan=new WorkingPlanProposal{Summary="先验证制作为旁证，再采石和返回",Steps=new(){
            new(){Id="craft",Objective="制作平台",SuccessCriteria="一次真实制作"},
            new(){Id="mine",Objective="采石",SuccessCriteria="采石完成",DependsOn=new(){"craft"}},
            new(){Id="return",Objective="返回",SuccessCriteria="到玩家身边",DependsOn=new(){"mine"}}}};
        Recipe recipe=CompanionCrafting.FindRecipes("94").First();
        stage2Started=DateTime.UtcNow;stage2NativeStarted=true;
        ApplyPlan(new AIPlan{Goal="收集木材石块并返回",CompletionConditions=stage2Contract,WorkingPlan=plan,
            Action=AIActionType.Craft,Item="94",Count=1,RecipeId=recipe.RecipeIndex,PlanStep="craft"},null,sourcePlayerRequest:"收集20木10石并回来");
        stage2Events.Add(new{phase="initial",planning=Stage2Planning(),tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks()),recipeId=recipe.RecipeIndex});
        int ax=(int)(npc.Center.X/16),ay=(int)(npc.Center.Y/16);
        var seed=Enumerable.Range(-10,21).SelectMany(dx=>Enumerable.Range(-10,21).Select(dy=>(x:ax+dx,y:ay+dy)))
            .Where(v=>WorldGen.InWorld(v.x,v.y,3)&&Main.tile[v.x,v.y].HasTile&&Main.tile[v.x,v.y].TileType!=TileID.Stone)
            .OrderBy(v=>Math.Abs(v.x-ax)+Math.Abs(v.y-ay)).First();
        stage2SeedX=seed.x;stage2SeedY=seed.y;
        var tile=Main.tile[seed.x,seed.y];
        var nativeConditions=new List<GoalCondition>{new(){Type="area_matches",X=seed.x,Y=seed.y,Width=1,Height=1,TileType=tile.TileType},new(){Type="housing_valid"}};
        object Scratch()=>typeof(WorldGen).GetFields(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)
            .Where(f=>!f.IsLiteral&&!f.IsInitOnly&&(f.Name.StartsWith("room",StringComparison.Ordinal)||f.Name is "numRoomTiles" or "canSpawn" or "sharedRoomX"))
            .ToDictionary(f=>f.Name,f=>{object? v=f.GetValue(null);return v is IEnumerable a && v is not string?string.Join("|",a.Cast<object>().Select(x=>x?.ToString())):v?.ToString();});
        string observationBefore=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
        string scratchBefore=JsonSerializer.Serialize(Scratch());
        var native=GoalWorldObserver.Capture(this,nativeConditions,0);
        string scratchAfter=JsonSerializer.Serialize(Scratch());
        stage2Events.Add(new{phase="native_observation",areaSeed=new{x=seed.x,y=seed.y,tile=(int)tile.TileType},native,scratchEqual=scratchBefore==scratchAfter,
            note="Single actual tile; unbound housing must be unknown. No room success or construction claim."});
        var reading=StructureWorldObserver.Query("3200,390",npc);var room=reading.observation?.rooms.FirstOrDefault();
        if(room!=null)
        {
            var b=room.bounds;var housing=new GoalCondition{Type="housing_valid",X=b.x+b.width/2,Y=b.y+b.height/2};
            string roomScratchBefore=JsonSerializer.Serialize(Scratch());var roomResult=GoalWorldObserver.Capture(this,new[]{housing},0);
            string roomScratchAfter=JsonSerializer.Serialize(Scratch());
            stage2Events.Add(new{phase="native_housing",point=new{x=housing.X,y=housing.Y},result=roomResult,scratchEqual=roomScratchBefore==roomScratchAfter,
                note="Existing interior native check only; false or unknown is not construction success."});
        }
        else stage2Events.Add(new{phase="native_housing",uncovered="No sampled room found."});
        stage2Events.Add(new{phase="observation_window",snapshotEqual=observationBefore==JsonSerializer.Serialize(CaptureMinePreviewState(npc,p)),
            note="Actor, inventory and recorded local tiles only; not a whole-world proof."});
    }
    private void TickStage2NativeAcceptance()
    {
        if(Main.gameMenu || CompanionNpc==null)return;
        stage2Frame++;Player p=Main.LocalPlayer;p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;
        foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(Executor.NavigationTeleportCount>0 || stage2Frame>3600 || DateTime.UtcNow-stage2Started>TimeSpan.FromMinutes(2))
        {FinishStage2Native("failed","teleport or deadline");return;}
        if(stage2Phase==0 && agentJournal.Outcomes.Any(o=>o.Task.Action==AIActionType.Craft))
        {
            bool accepted=ApplyPlan(new AIPlan{GoalStatus="completed",GoalDone=true},null,source:"goal");
            stage2Events.Add(new{phase="false_completion",accepted,planning=Stage2Planning(),outcomes=JsonSerializer.SerializeToElement(agentJournal.Outcomes)});
            var weaker=new List<GoalCondition>{new(){Type="inventory_at_least",ItemId=ItemID.Wood,Count=1},new(){Type="inventory_at_least",ItemId=ItemID.StoneBlock,Count=10}};
            bool revised=ApplyPlan(new AIPlan{CompletionConditions=weaker,ContractRevisionReason="fixture attempts weakening"},null,source:"goal");
            stage2Events.Add(new{phase="weaken_contract",accepted=revised,planning=Stage2Planning()});
            var inspect=new AIPlan{Action=AIActionType.Inspect,Target="nearby"};
            var mine=new AIPlan{Action=AIActionType.Mine,Target="stone",Count=1,X=stage2SeedX,Y=stage2SeedY,PlanStep="mine"};
            var tail=new AIPlan{Action=AIActionType.Come,PlanStep="return"};
            ApplyPlan(new AIPlan{ReplaceTasks=true,ReplanAfterStep=false,Steps=new(){inspect,mine,tail}},null,source:"goal");
            stage2Events.Add(new{phase="inspect_started",tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks()),planning=Stage2Planning()});stage2Phase=1;
        }
        else if(stage2Phase==1 && agentJournal.Outcomes.Any(o=>o.Task.Action==AIActionType.Inspect && o.Status=="succeeded"))
        {
            stage2Events.Add(new{phase="inspect_checkpoint",mineOutcomes=agentJournal.Outcomes.Count(o=>o.Task.Action==AIActionType.Mine),
                hasCurrentPlan=Executor.HasCurrentPlan,tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks()),planning=Stage2Planning()});
            bool accepted=ApplyPlan(new AIPlan{KeepTasks=true},null,source:"goal");
            stage2Events.Add(new{phase="inspect_continue",accepted,tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks()),planning=Stage2Planning()});stage2Phase=2;
        }
        else if(stage2Phase==2 && agentJournal.Outcomes.Any(o=>o.Task.Action==AIActionType.Mine && o.Status=="blocked"))
        {
            stage2Events.Add(new{phase="mine_blocked",tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks()),planning=Stage2Planning(),outcomes=JsonSerializer.SerializeToElement(agentJournal.Outcomes)});
            LlmConversationStore.ResetAll();
            stage2Events.Add(new{phase="history_reset",tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks()),planning=Stage2Planning()});
            bool cancelled=ApplyPlan(new AIPlan{GoalStatus="cancelled",CompletionConditions=new()},null,source:"goal");
            stage2Events.Add(new{phase="cancelled",accepted=cancelled,planning=Stage2Planning()});
            ApplyPlan(new AIPlan{Goal="全新验收目标"},null,sourcePlayerRequest:"全新验收目标");
            stage2Events.Add(new{phase="new_goal",planning=Stage2Planning()});
            ApplyPlan(new AIPlan{GoalStatus="cancelled",CompletionConditions=new()},null,source:"goal");
            stage2InspectCount=agentJournal.Outcomes.Count(o=>o.Task.Action==AIActionType.Inspect);
            Executor.ApplyTasks(new[]{new AIPlan{Action=AIActionType.Inspect,Target="nearby"},new AIPlan{Action=AIActionType.Wait}});
            stage2Events.Add(new{phase="no_goal_batch_started",goal=activeGoal,tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks())});stage2Phase=3;
        }
        else if(stage2Phase==3 && agentJournal.Outcomes.Count(o=>o.Task.Action==AIActionType.Inspect)>stage2InspectCount)
        {
            stage2Events.Add(new{phase="no_goal_checkpoint",goal=activeGoal,hasCurrentPlan=Executor.HasCurrentPlan,
                tasks=JsonSerializer.SerializeToElement(Executor.SnapshotTasks()),waitOutcomes=agentJournal.Outcomes.Count(o=>o.Task.Action==AIActionType.Wait),planning=Stage2Planning()});
            ApplyPlan(new AIPlan{GoalStatus="cancelled",CompletionConditions=new()},null,source:"goal");
            var positive=new List<GoalCondition>{new(){Type="inventory_at_least",ItemId=94,Count=2},new(){Type="near_player",HorizontalPixels=48,VerticalPixels=24}};
            bool goalAccepted=ApplyPlan(new AIPlan{Goal="确认已有平台并靠近玩家",CompletionAction="build",CompletionConditions=positive},null,sourcePlayerRequest:"确认背包已有2个平台并在玩家附近");
            var actor=CompanionNpc!;var player=Main.LocalPlayer;
            stage2Events.Add(new{phase="positive_before",goalAccepted,platform=Loadout.CountItem(94),horizontalPixels=Math.Abs(actor.Center.X-player.Center.X),
                verticalPixels=Math.Abs(actor.Bottom.Y-player.Bottom.Y),planning=Stage2Planning(),note="Two platforms came from this case's real craft using one fixture-injected wood."});
            ApplyPlan(new AIPlan{Action=AIActionType.Come},null,source:"goal");stage2Phase=4;
        }
        else if(stage2Phase==4 && agentJournal.Outcomes.Any(o=>o.Task.Action==AIActionType.Come && o.Status=="succeeded"))
        {
            var actor=CompanionNpc!;var player=Main.LocalPlayer;
            stage2Events.Add(new{phase="positive_after_come",platform=Loadout.CountItem(94),horizontalPixels=Math.Abs(actor.Center.X-player.Center.X),
                verticalPixels=Math.Abs(actor.Bottom.Y-player.Bottom.Y),planning=Stage2Planning()});
            bool completed=ApplyPlan(new AIPlan{GoalStatus="completed",GoalDone=true},null,source:"goal");
            stage2Events.Add(new{phase="positive_completion",accepted=completed,planning=Stage2Planning(),note="Current native inventory and position; prior completion_action metadata is build."});
            FinishStage2Native("review","");
        }
    }
    private void FinishStage2Native(string status,string error)
    {
        stage2NativeFinished=true;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","stage2-native-result.json"),JsonSerializer.Serialize(new{status,error,frames=stage2Frame,
            teleports=Executor.NavigationTeleportCount,events=stage2Events,outcomes=agentJournal.Outcomes,
            uncovered=new[]{"positive completion with naturally acquired resources","real world switch"},
            scope="Real copied house; 1 wood injected solely for actual craft; inspect checkpoint before physical tail, actual directed mine failure; player protected and hostiles removed; no LLM."
        },new JsonSerializerOptions{WriteIndented=true}));
        Stop();TestLog($"stage2 native {status}: {error}");
    }
}
