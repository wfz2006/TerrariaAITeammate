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
    private const string AdvancedWorkstationRequest="请自己采集木材和石块，使用家里已有的工作台制作1把木椅和20个石墙，做好后带着它们回到我身边。材料要自己采，不从箱子拿，不拆现有房屋，也不要另外放工作台。";
    private const string AdvancedSuppliesRequest="请自己采一些木材，从家里已有的箱子取3个凝胶，制作9个普通火把和4个木平台，做好后带着它们回到我身边。不要拆现有房屋，不要从箱子直接拿成品。";
    private sealed record AdvancedChest(int Index,int X,int Y,bool Locked,int Gel,int Torch,int Platform,string OtherContents);
    private sealed record AdvancedSnapshot(int Wood,int Stone,int Gel,int Chair,int Wall,int Torch,int Platform,Vector2 Actor,float Feet,object[] Stations,Dictionary<int,int> Inventory,Dictionary<int,AdvancedChest> Chests);
    private bool advancedStarted,advancedFinished,advancedSupplies,advancedChairCraft,advancedWallCraft,advancedTorchCraft,advancedPlatformCraft,advancedReturned,advancedEvidenceAmbiguous;
    private int advancedFrames,advancedRequestStart,advancedMineWood,advancedMineStone,advancedGelWithdrawn,advancedChairProduced,advancedWallProduced,advancedTorchProduced,advancedPlatformProduced;private long advancedSequence,advancedLastCraftSequence;
    private int advancedWoodConsumed,advancedStoneConsumed,advancedGelConsumed;
    private int advancedCraftCompletedFrame=-1;private ulong advancedCraftCompletedTick;
    private ulong advancedStartedTick;private DateTime advancedAt;private string advancedRequest="";private AdvancedSnapshot? advancedPrevious;
    private readonly List<object> advancedEvents=new(),advancedTrace=new();
    private readonly HashSet<(int x,int y)> advancedOriginalBenches=new();
    private bool advancedConstraintViolation;private int advancedSlotIndex=-1;private object[]? advancedSlotBefore;
    private int AdvancedLimit=>int.TryParse(Environment.GetEnvironmentVariable("AITEAMMATE_RESOURCE_REQUEST_LIMIT"),out int n)?Math.Clamp(n,1,12):12;
    private bool AdvancedWithinTime=>DateTime.UtcNow-advancedAt<TimeSpan.FromMinutes(6);
    private bool AdvancedPlanningAllowed=>advancedStarted && !advancedFinished && PrimaryDecisionRequestCount-advancedRequestStart<AdvancedLimit && AdvancedWithinTime;
    private static object[] AdvancedSlots(Chest c)=>c.item.Select((item,slot)=>new{slot,item.type,item.stack,item.prefix}).Where(i=>i.type>0 && i.stack>0).Cast<object>().ToArray();
    private static object AdvancedSnapshotView(AdvancedSnapshot s,bool includeChests=false)=>new{s.Wood,s.Stone,s.Gel,s.Chair,s.Wall,s.Torch,s.Platform,Actor=new{x=s.Actor.X,y=s.Actor.Y},s.Feet,s.Stations,s.Inventory,Chests=includeChests?s.Chests:null};
    private AdvancedSnapshot CaptureAdvanced(NPC npc)
    {
        var stations=CompanionCrafting.NearbyStations(npc).Select(s=>(object)new{type=s.Type,x=s.X,y=s.Y}).ToArray();
        var chests=Main.chest.Select((c,i)=>(c,i)).Where(v=>v.c!=null && CompanionChests.IsChestBuilt(v.c)).ToDictionary(v=>v.i,v=>{
            Chest c=v.c!;int gel=0,torch=0,platform=0;var other=new SortedDictionary<int,int>();
            foreach(Item item in c.item)if(!item.IsAir){if(item.type==ItemID.Gel)gel+=item.stack;else{other[item.type]=other.GetValueOrDefault(item.type)+item.stack;if(item.type==ItemID.Torch)torch+=item.stack;if(item.type==ItemID.WoodPlatform)platform+=item.stack;}}
            return new AdvancedChest(v.i,c.x,c.y,Chest.IsLocked(c.x,c.y),gel,torch,platform,string.Join(",",other.Select(pair=>$"{pair.Key}:{pair.Value}")));});
        var inventory=Loadout.Inventory.Where(i=>!i.IsAir).GroupBy(i=>i.type).ToDictionary(g=>g.Key,g=>g.Sum(i=>i.stack));
        return new(Loadout.CountItem(ItemID.Wood),Loadout.CountItem(ItemID.StoneBlock),Loadout.CountItem(ItemID.Gel),Loadout.CountItem(ItemID.WoodenChair),Loadout.CountItem(ItemID.StoneWall),Loadout.CountItem(ItemID.Torch),Loadout.CountItem(ItemID.WoodPlatform),npc.Center,npc.Bottom.Y,stations,inventory,chests);
    }
    private void BeginAdvancedAcceptance()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear();Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;Loadout.Reset();Loadout.EnsureStarterTools();
        advancedSupplies=Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="advanced_supplies";advancedRequest=advancedSupplies?AdvancedSuppliesRequest:AdvancedWorkstationRequest;
        advancedPrevious=CaptureAdvanced(npc);advancedSequence=agentJournal.LastSequence;advancedRequestStart=PrimaryDecisionRequestCount;advancedStartedTick=Main.GameUpdateCount;advancedAt=DateTime.UtcNow;advancedStarted=true;
        var houseStations=new List<object>();for(int x=3160;x<=3270;x++)for(int y=350;y<=420;y++)if(Main.tile[x,y].HasTile && Main.tile[x,y].TileType==TileID.WorkBenches){houseStations.Add(new{x,y,type=(int)Main.tile[x,y].TileType});advancedOriginalBenches.Add((x,y));}
        var initialChestSlots=advancedPrevious.Chests.Values.Select(c=>new{c.Index,c.X,c.Y,c.Locked,items=AdvancedSlots(Main.chest[c.Index])}).ToArray();
        advancedEvents.Add(new{kind="initial",snapshot=AdvancedSnapshotView(advancedPrevious,true),houseStations,initialChestSlots,scope="audit only; not supplied to model"});
        if(advancedPrevious.Wood!=0 || advancedPrevious.Stone!=0 || advancedPrevious.Gel!=0 || advancedPrevious.Chair!=0 || advancedPrevious.Wall!=0 || advancedPrevious.Torch!=0 || advancedPrevious.Platform!=0){FinishAdvancedAcceptance("failed","initial materials not empty");return;}
        HandlePlayerMessage(p,advancedRequest);SaveAdvancedAcceptance("running","");
    }
    private void TickAdvancedAcceptance()
    {
        if(Main.gameMenu || CompanionNpc==null)return;NPC npc=CompanionNpc;Player p=Main.LocalPlayer;advancedFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        AdvancedSnapshot now=CaptureAdvanced(npc);var events=agentJournal.Outcomes.Where(o=>o.Sequence>advancedSequence).OrderBy(o=>o.Sequence).ToArray();
        int mutatingCount=events.Count(o=>o.Task.Action is AIActionType.Mine or AIActionType.Craft or AIActionType.Withdraw or AIActionType.Deposit or AIActionType.Pickup or AIActionType.Drop or AIActionType.Place or AIActionType.Build);
        if(mutatingCount>1)advancedEvidenceAmbiguous=true;
        foreach(var o in events)
        {
            advancedSequence=o.Sequence;AdvancedSnapshot before=advancedPrevious??now;
            if(o.Task.Action==AIActionType.Mine)try{using var result=JsonDocument.Parse(o.Result);if(result.RootElement.TryGetProperty("actualGained",out var v) && v.TryGetInt32(out int n)){if(o.Task.Target=="wood")advancedMineWood+=n;if(o.Task.Target=="stone")advancedMineStone+=n;}}catch(JsonException){}
            var chestDeltas=before.Chests.Values.Where(c=>now.Chests.TryGetValue(c.Index,out var after) && (c.Gel!=after.Gel || c.OtherContents!=after.OtherContents)).Select(c=>new{before=c,after=now.Chests[c.Index],distancePixels=Vector2.Distance(before.Actor,new Vector2(c.X*16+16,c.Y*16+16)),beforeSlots=advancedSlotIndex==c.Index?advancedSlotBefore:null,afterSlots=AdvancedSlots(Main.chest[c.Index])}).ToArray();
            if(!advancedSupplies && o.Task.Action==AIActionType.Withdraw && o.Status=="succeeded")advancedConstraintViolation=true;
            if(!advancedSupplies && o.Status=="succeeded" && o.Task.Action is AIActionType.Place or AIActionType.Build)
            {
                var old=before.Stations.Select(s=>JsonSerializer.SerializeToElement(s)).Where(s=>s.GetProperty("type").GetInt32()==TileID.WorkBenches).Select(s=>(s.GetProperty("x").GetInt32(),s.GetProperty("y").GetInt32())).ToHashSet();
                bool added=now.Stations.Select(s=>JsonSerializer.SerializeToElement(s)).Where(s=>s.GetProperty("type").GetInt32()==TileID.WorkBenches).Any(s=>!old.Contains((s.GetProperty("x").GetInt32(),s.GetProperty("y").GetInt32())) && !advancedOriginalBenches.Contains((s.GetProperty("x").GetInt32(),s.GetProperty("y").GetInt32())));
                if(added)advancedConstraintViolation=true;
                if(mutatingCount>1)advancedEvidenceAmbiguous=true;
            }
            if(o.Task.Action==AIActionType.Withdraw && o.Status=="succeeded")
            {
                int fromChest=chestDeltas.Sum(d=>d.before.Gel-d.after.Gel),toActor=now.Gel-before.Gel;
                bool onlyGel=chestDeltas.All(d=>d.before.OtherContents==d.after.OtherContents) && now.Inventory.Keys.Union(before.Inventory.Keys).Where(id=>id!=ItemID.Gel).All(id=>now.Inventory.GetValueOrDefault(id)==before.Inventory.GetValueOrDefault(id));
                bool transferPhysical=chestDeltas.All(d=>!d.before.Locked && d.before.X==d.after.X && d.before.Y==d.after.Y && d.distancePixels<=80 && Main.chest[d.before.Index]!=null && CompanionChests.IsChestBuilt(Main.chest[d.before.Index]));
                if(!onlyGel)advancedConstraintViolation=true;
                if(mutatingCount==1 && fromChest>0 && fromChest==toActor && onlyGel && transferPhysical)advancedGelWithdrawn+=fromChest;
                else advancedEvidenceAmbiguous=true;
            }
            object? recipes=null;
            if(o.Task.Action==AIActionType.Craft && o.Status=="succeeded")
            {
                var candidates=CompanionCrafting.FindRecipes(o.Task.Item).Where(r=>!o.Task.RecipeId.HasValue || r.RecipeIndex==o.Task.RecipeId.Value).ToArray();
                recipes=candidates.Select(r=>new{r.RecipeIndex,output=r.createItem.type,outputCount=r.createItem.stack,requirements=CompanionCrafting.Requirements(r),stations=r.requiredTile.ToArray()}).ToArray();
                bool workbench=before.Stations.Concat(now.Stations).Select(s=>JsonSerializer.SerializeToElement(s)).Any(s=>s.GetProperty("type").GetInt32()==TileID.WorkBenches && advancedOriginalBenches.Contains((s.GetProperty("x").GetInt32(),s.GetProperty("y").GetInt32())));
                bool RecipeMatches(int output,int delta,bool requireBench)
                {
                    if(delta<=0)return false;
                    return candidates.Any(r=>{
                        if(r.createItem.type!=output || r.createItem.stack<=0 || delta%r.createItem.stack!=0 || requireBench && (!workbench || !r.requiredTile.Any(t=>CompanionCrafting.StationMatches(TileID.WorkBenches,t))))return false;
                        var needs=CompanionCrafting.Requirements(r,delta/r.createItem.stack);
                        int Need(int id)=>needs.Where(q=>q.AcceptedItems.Contains(id)).Sum(q=>q.Count);
                        return before.Wood-now.Wood==Need(ItemID.Wood) && before.Stone-now.Stone==Need(ItemID.StoneBlock) && before.Gel-now.Gel==Need(ItemID.Gel);
                    });
                }
                bool chair=mutatingCount==1 && RecipeMatches(ItemID.WoodenChair,now.Chair-before.Chair,true);
                bool wall=mutatingCount==1 && RecipeMatches(ItemID.StoneWall,now.Wall-before.Wall,true);
                bool torch=mutatingCount==1 && RecipeMatches(ItemID.Torch,now.Torch-before.Torch,false);
                bool platform=mutatingCount==1 && RecipeMatches(ItemID.WoodPlatform,now.Platform-before.Platform,false);
                if(chair)advancedChairProduced+=now.Chair-before.Chair;
                if(wall)advancedWallProduced+=now.Wall-before.Wall;
                if(torch)advancedTorchProduced+=now.Torch-before.Torch;
                if(platform)advancedPlatformProduced+=now.Platform-before.Platform;
                if(chair || wall || torch || platform){advancedWoodConsumed+=before.Wood-now.Wood;advancedStoneConsumed+=before.Stone-now.Stone;advancedGelConsumed+=before.Gel-now.Gel;}
                advancedChairCraft=advancedChairProduced>=1;advancedWallCraft=advancedWallProduced>=20;advancedTorchCraft=advancedTorchProduced>=9;advancedPlatformCraft=advancedPlatformProduced>=4;
                if(advancedCraftCompletedFrame<0 && (advancedSupplies?advancedTorchCraft&&advancedPlatformCraft:advancedChairCraft&&advancedWallCraft))
                {advancedCraftCompletedFrame=advancedFrames;advancedCraftCompletedTick=Main.GameUpdateCount;}
                advancedLastCraftSequence=o.Sequence;
            }
            bool grounded=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width);
            advancedEvents.Add(new{kind="outcome",frame=advancedFrames,tick=Main.GameUpdateCount,outcome=o,before=AdvancedSnapshotView(before),after=AdvancedSnapshotView(now),chestDeltas,recipes,grounded,advancedReturned});
        }
        bool groundedNow=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width);
        if(!advancedReturned && advancedCraftCompletedFrame>=0 && advancedFrames>advancedCraftCompletedFrame && Main.GameUpdateCount>advancedCraftCompletedTick && groundedNow && Math.Abs(npc.Center.X-p.Center.X)<=48 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<=24)
        {advancedReturned=true;advancedEvents.Add(new{kind="returned_with_products",frame=advancedFrames,tick=Main.GameUpdateCount,craftFrame=advancedCraftCompletedFrame,craftTick=advancedCraftCompletedTick,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},player=new{x=p.Center.X/16,y=p.Center.Y/16,feet=p.Bottom.Y/16},grounded=groundedNow,now.Chair,now.Wall,now.Torch,now.Platform});}
        advancedPrevious=now;advancedSlotIndex=-1;advancedSlotBefore=null;
        if(Executor.CurrentAction==AIActionType.Withdraw && ContinuousField("chestIndex") is int selected && selected>=0 && selected<Main.chest.Length && Main.chest[selected]!=null)
        {advancedSlotIndex=selected;advancedSlotBefore=AdvancedSlots(Main.chest[selected]);}
        if(advancedFrames%30==0){advancedTrace.Add(new{frame=advancedFrames,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},player=new{x=p.Center.X/16,y=p.Center.Y/16,feet=p.Bottom.Y/16},now.Wood,now.Stone,now.Gel,now.Chair,now.Wall,now.Torch,now.Platform,action=Executor.CurrentAction.ToString(),warps=Executor.NavigationTeleportCount});SaveAdvancedAcceptance("running","");}
        if(Executor.NavigationTeleportCount>0){FinishAdvancedAcceptance("failed","teleport");return;}
        if(decisionFailures>0 || dispatchRepairs.LastCode=="protocol_parse_rejected"){FinishAdvancedAcceptance("failed","first API or parse failure");return;}
        JsonElement proof=JsonSerializer.SerializeToElement(lastCompletedGoal);bool ownProof=proof.ValueKind==JsonValueKind.Object && proof.TryGetProperty("sourcePlayerRequest",out var req) && req.GetString()==advancedRequest && proof.TryGetProperty("observedAtTick",out var tick) && tick.GetUInt64()>=advancedStartedTick && proof.TryGetProperty("worldId",out var world) && world.GetInt32()==Main.worldID;
        bool physical=advancedSupplies?advancedMineWood>=advancedWoodConsumed && advancedWoodConsumed>0 && advancedGelWithdrawn==3 && advancedGelWithdrawn>=advancedGelConsumed && advancedGelConsumed>0 && advancedTorchCraft && advancedPlatformCraft && now.Torch>=9 && now.Platform>=4:advancedMineWood>=advancedWoodConsumed && advancedWoodConsumed>0 && advancedMineStone>=advancedStoneConsumed && advancedStoneConsumed>0 && advancedChairCraft && advancedWallCraft && now.Chair>=1 && now.Wall>=20;
        bool nearNow=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width) && Math.Abs(npc.Center.X-p.Center.X)<=48 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<=24;
        if(goalStatus=="completed" && ownProof){FinishAdvancedAcceptance(physical && advancedReturned && nearNow && !advancedEvidenceAmbiguous && !advancedConstraintViolation?"passed":"failed",physical && advancedReturned && nearNow && !advancedEvidenceAmbiguous && !advancedConstraintViolation?"":"completion lacks unambiguous native ordered evidence or violates task constraints");return;}
        bool reviewTail=goalReviewPending && ContinuousField("currentPlan") is not AIPlan && !Executor.IsInCombat && !Executor.IsAutoDefending;
        bool exhausted=PrimaryDecisionRequestCount-advancedRequestStart>=AdvancedLimit && primaryCancellation==null && (!Executor.IsBusy && Executor.SnapshotTasks().Count==0 || reviewTail);
        if(!AdvancedWithinTime || exhausted || goalWaitingForPlayer)FinishAdvancedAcceptance("failed",!AdvancedWithinTime?"wall_deadline":goalWaitingForPlayer?"goal_waiting":"request_budget_exhausted");
    }
    private void FinishAdvancedAcceptance(string status,string error){advancedFinished=true;SaveAdvancedAcceptance(status,error);Stop();Executor.Clear(true);}
    private void SaveAdvancedAcceptance(string status,string error)
    {
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate",advancedSupplies?"advanced-supplies-result.json":"advanced-workstation-result.json"),JsonSerializer.Serialize(new{
            status,error,request=advancedRequest,startedUtc=advancedAt,startedTick=advancedStartedTick,updatedUtc=DateTime.UtcNow,frames=advancedFrames,requests=PrimaryDecisionRequestCount-advancedRequestStart,requestLimit=AdvancedLimit,teleports=Executor.NavigationTeleportCount,
            goalStatus,reply=LastReply,lastCompletedGoal,planning=JsonSerializer.SerializeToElement(PlanningContext),goalReviewPending,executorIsBusy=Executor.IsBusy,currentPlan=ContinuousField("currentPlan"),primaryInFlight=primaryCancellation!=null,remainingTasks=Executor.SnapshotTasks(),finalSnapshot=advancedPrevious==null?null:AdvancedSnapshotView(advancedPrevious),
            advancedMineWood,advancedMineStone,advancedGelWithdrawn,advancedWoodConsumed,advancedStoneConsumed,advancedGelConsumed,advancedChairProduced,advancedWallProduced,advancedTorchProduced,advancedPlatformProduced,advancedChairCraft,advancedWallCraft,advancedTorchCraft,advancedPlatformCraft,advancedCraftCompletedFrame,advancedCraftCompletedTick,advancedReturned,advancedEvidenceAmbiguous,advancedConstraintViolation,events=advancedEvents,trace=advancedTrace,
            scope="Fresh original-house copy, starter tools only, one actor setup, no injected materials or mid-case reposition; hostiles removed/player protected. Chest and station observations are audit-only, never supplied as fixture answers. Multiple inventory-mutating outcomes in one tick are ambiguous. NearbyStations is a native ±5×4 tile proximity test without line-of-sight or room-entry proof; new-workbench detection is limited to that observed station window."
        },new JsonSerializerOptions{WriteIndented=false}));
    }
}

