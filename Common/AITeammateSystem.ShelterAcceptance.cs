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
    private const string ShelterAcceptanceRequest="请在现有房屋附近的合适空地新建一间可供NPC入住的小屋。选址、材料和工作安排由你决定；需要用到的木材或石块请自己采集，凝胶等原料可以从家里的箱子获取，墙、门、工作台、椅子和光源请自己合成。保留原有建筑、家具和通路，不拆旧屋；遇到缺料或施工受阻就根据实际反馈调整，直到小屋建好并确认可住后告诉我。";
    private bool shelterAcceptanceStarted,shelterAcceptanceFinished,shelterLedgerAmbiguous,shelterForbiddenWithdrawal,shelterPlayerInventoryChanged,shelterActualConstruction;
    private int shelterFrames,shelterRequestStart,shelterMineWood,shelterMineStone,shelterRecoveredWood,shelterRecoveredStone;private long shelterSequence;private ulong shelterStartedTick;private DateTime shelterAt;
    private ShelterWorldAudit? shelterAudit;private object? shelterPreservation,shelterRoom;
    private Dictionary<int,int> shelterPreviousInventory=new(),shelterInitialPlayerInventory=new();
    private Dictionary<int,Dictionary<int,int>> shelterPreviousChests=new();private Dictionary<(int,int),string> shelterPreviousSite=new();
    private Dictionary<int,int> shelterPreviousGround=new();private readonly Dictionary<(int chest,int item),int> shelterDepositedOwnership=new();
    private readonly Dictionary<int,int> shelterCrafted=new(),shelterCraftConsumed=new(),shelterBuildConsumed=new(),shelterWithdrawn=new(),shelterPickup=new();
    private readonly Dictionary<int,int> shelterMinedByItem=new();
    private Dictionary<int,int> shelterInitialGround=new();
    private readonly List<object> shelterOutcomes=new(),shelterLedger=new(),shelterTrace=new(),shelterChanges=new(),shelterAudits=new();
    private readonly HashSet<(int x,int y,int w,int h)> shelterCandidates=new();
    private AIActionType shelterPreviousAction;
    private int ShelterLimit=>int.TryParse(Environment.GetEnvironmentVariable("AITEAMMATE_SHELTER_REQUEST_LIMIT"),out int n)?Math.Clamp(n,1,40):20;
    private int ShelterTimeLimitMinutes=>ShelterLimit>30?20:ShelterLimit>20?15:10;
    private bool ShelterWithinTime=>DateTime.UtcNow-shelterAt<TimeSpan.FromMinutes(ShelterTimeLimitMinutes);
    private bool ShelterPlanningAllowed=>shelterAcceptanceStarted && !shelterAcceptanceFinished && PrimaryDecisionRequestCount-shelterRequestStart<ShelterLimit && ShelterWithinTime;
    private static Dictionary<int,int> ShelterCounts(IEnumerable<Item> items)=>items.Where(i=>!i.IsAir).GroupBy(i=>i.type).ToDictionary(g=>g.Key,g=>g.Sum(i=>i.stack));
    private static Dictionary<int,Dictionary<int,int>> ShelterChests()=>Main.chest.Select((c,i)=>(c,i)).Where(v=>v.c!=null && CompanionChests.IsChestBuilt(v.c)).ToDictionary(v=>v.i,v=>ShelterCounts(v.c!.item));
    private static Dictionary<int,int> ShelterGround()=>ShelterCounts(Main.item.Where(i=>i.active));
    private static string ShelterTile(int x,int y){Tile t=Main.tile[x,y];return $"{t.HasTile}/{t.TileType}/{t.WallType}/{t.Slope}/{t.IsHalfBlock}/{t.IsActuated}/{t.TileFrameX}/{t.TileFrameY}";}
    private static bool ShelterDoorClosedEquivalent(string before,string after)
    {
        string[] a=before.Split('/'),b=after.Split('/');
        if(a.Length!=8 || b.Length!=8)return false;
        if(a[0]=="False" && b[0]=="False")return a.Skip(2).Take(4).SequenceEqual(b.Skip(2).Take(4));
        if(a[0]=="True" && b[0]=="True" && a[1]==TileID.ClosedDoor.ToString() && b[1]==a[1])return a.Take(6).SequenceEqual(b.Take(6)) && a[7]==b[7];
        return before==after;
    }
    private static bool ShelterFinishedMaterial(int id)
    {
        Item item=new();item.SetDefaults(id);return item.createWall>0 || item.createTile is TileID.ClosedDoor or TileID.WorkBenches or TileID.Chairs or TileID.Torches;
    }
    private static void ShelterAdd(Dictionary<int,int> map,int id,int count){if(count>0)map[id]=map.GetValueOrDefault(id)+count;}
    private static bool ShelterWoodFamily(int id)=>RecipeGroup.recipeGroups.TryGetValue(RecipeGroupID.Wood,out RecipeGroup? group) && group.ValidItems.Contains(id);
    private void RunShelterAuditNative()
    {
        Stop();Executor.Clear(true);NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;
        var audit=ShelterWorldAudit.Capture(3220,390);string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
        var preserved=audit.CheckPreservation();JsonElement baseline=JsonSerializer.SerializeToElement(audit.BaselineReport);
        JsonElement rooms=baseline.GetProperty("existingValidRooms");object? oldRoomReport=null;bool existingCovered=false,oldRoomRejected=false;
        if(rooms.GetArrayLength()>0)
        {JsonElement old=rooms[0];int x=old.GetProperty("queryX").GetInt32(),y=old.GetProperty("queryY").GetInt32();var report=audit.CheckNewRoom(x,y);oldRoomReport=report;existingCovered=true;oldRoomRejected=report.available && !report.validNew;}
        JsonElement area=baseline.GetProperty("sampleBounds");int airX=area.GetProperty("x").GetInt32()+8,airY=area.GetProperty("y").GetInt32()+8;
        var emptyRoom=audit.CheckNewRoom(airX,airY);bool emptyRejected=emptyRoom.available && !emptyRoom.validNew && !emptyRoom.nativeValid;
        string after=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
        bool doorOpened=false,doorRestored=false,openPreserved=false;object? doorObservation=null;
        for(int x=3160;x<=3270 && !doorOpened;x++)for(int y=350;y<=420 && !doorOpened;y++)
        {
            if(!Main.tile[x,y].HasTile || Main.tile[x,y].TileType!=TileID.ClosedDoor)continue;
            var beforeTiles=new Dictionary<(int,int),string>();for(int dx=-2;dx<=2;dx++)for(int dy=-3;dy<=3;dy++)beforeTiles[(x+dx,y+dy)]=ShelterTile(x+dx,y+dy);
            WorldGen.OpenDoor(x,y,1);
            bool opened=Enumerable.Range(-2,5).Any(dx=>Enumerable.Range(-3,7).Any(dy=>Main.tile[x+dx,y+dy].HasTile && Main.tile[x+dx,y+dy].TileType==TileID.OpenDoor));
            if(!opened)continue;doorOpened=true;var during=audit.CheckPreservation();openPreserved=during.available && during.preserved;
            for(int dx=-2;dx<=2;dx++)for(int dy=-3;dy<=3;dy++)if(Main.tile[x+dx,y+dy].HasTile && Main.tile[x+dx,y+dy].TileType==TileID.OpenDoor)WorldGen.CloseDoor(x+dx,y+dy,false);
            var afterTiles=beforeTiles.ToDictionary(kv=>kv.Key,kv=>ShelterTile(kv.Key.Item1,kv.Key.Item2));
            doorRestored=beforeTiles.All(kv=>ShelterDoorClosedEquivalent(kv.Value,afterTiles[kv.Key]));
            var changedFrames=beforeTiles.Where(kv=>kv.Value!=afterTiles[kv.Key]).Select(kv=>new{x=kv.Key.Item1,y=kv.Key.Item2,before=kv.Value,after=afterTiles[kv.Key],logicalEquivalent=ShelterDoorClosedEquivalent(kv.Value,afterTiles[kv.Key])}).ToArray();
            doorObservation=new{x,y,openPreserved,doorRestored,changedFrames,during,afterClose=audit.CheckPreservation()};
        }
        var final=audit.CheckPreservation();bool noMutation=before==after;
        bool pass=preserved.available && preserved.preserved && existingCovered && oldRoomRejected && emptyRejected && noMutation && doorOpened && openPreserved && doorRestored && final.available && final.preserved && PrimaryDecisionRequestCount==0;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","shelter-audit-native-result.json"),JsonSerializer.Serialize(new{status=pass?"passed":"failed_or_uncovered",baseline,preserved,existingCovered,oldRoomRejected,oldRoomReport,emptyPoint=new{x=airX,y=airY},emptyRoom,emptyRejected,noMutation,doorOpened,openPreserved,doorRestored,doorObservation,final,requests=PrimaryDecisionRequestCount,teleports=Executor.NavigationTeleportCount,scope="Fresh original house, zero API; bounded read-only snapshot covers actor/inventory/house tiles. Separate controlled native door open/close in one tick; same closed-door tile, frameY and non-frame attributes required, randomized closed-door frameX recorded. No new structure or resources."},new JsonSerializerOptions{WriteIndented=true}));
        houseFinished=true;Stop();Executor.Clear(true);
    }
    private void BeginShelterAcceptance()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear();Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;Loadout.Reset();Loadout.EnsureStarterTools();
        shelterPreviousInventory=ShelterCounts(Loadout.Inventory);shelterInitialPlayerInventory=ShelterCounts(p.inventory);shelterPreviousChests=ShelterChests();shelterPreviousGround=ShelterGround();shelterInitialGround=new(shelterPreviousGround);shelterAudit=ShelterWorldAudit.Capture(3220,390);
        shelterSequence=agentJournal.LastSequence;shelterRequestStart=PrimaryDecisionRequestCount;shelterStartedTick=Main.GameUpdateCount;shelterAt=DateTime.UtcNow;shelterAcceptanceStarted=true;
        shelterOutcomes.Add(new{kind="initial",npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},player=new{x=p.Center.X/16,y=p.Center.Y/16,feet=p.Bottom.Y/16},inventory=shelterPreviousInventory,houseBaseline=shelterAudit.BaselineReport});
        if(shelterPreviousInventory.Keys.Any(id=>id is ItemID.Wood or ItemID.StoneBlock or ItemID.Gel or ItemID.WoodWall or ItemID.StoneWall or ItemID.WoodenDoor or ItemID.WorkBench or ItemID.WoodenChair or ItemID.Torch)){FinishShelterAcceptance("failed","starter inventory contains materials");return;}
        HandlePlayerMessage(p,ShelterAcceptanceRequest);SaveShelterAcceptance("running","");
    }
    private void TickShelterAcceptance()
    {
        if(Main.gameMenu || CompanionNpc==null)return;NPC npc=CompanionNpc;Player p=Main.LocalPlayer;shelterFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        var now=ShelterCounts(Loadout.Inventory);var chests=ShelterChests();var ground=ShelterGround();var fresh=agentJournal.Outcomes.Where(o=>o.Sequence>shelterSequence).OrderBy(o=>o.Sequence).ToArray();
        int mutating=fresh.Count(o=>o.Task.Action is AIActionType.Mine or AIActionType.Withdraw or AIActionType.Craft or AIActionType.Place or AIActionType.Build or AIActionType.Pickup or AIActionType.Drop or AIActionType.Deposit);
        if(mutating>1)shelterLedgerAmbiguous=true;
        AIActionType action=mutating==1?fresh.First(o=>o.Task.Action is AIActionType.Mine or AIActionType.Withdraw or AIActionType.Craft or AIActionType.Place or AIActionType.Build or AIActionType.Pickup or AIActionType.Drop or AIActionType.Deposit).Task.Action:shelterPreviousAction;
        var deltas=now.Keys.Union(shelterPreviousInventory.Keys).Select(id=>new{id,delta=now.GetValueOrDefault(id)-shelterPreviousInventory.GetValueOrDefault(id)}).Where(d=>d.delta!=0).ToArray();
        if(deltas.Length>0)
        {
            if(action==AIActionType.Craft)
            {
                foreach(var d in deltas){if(d.delta>0)ShelterAdd(shelterCrafted,d.id,d.delta);else ShelterAdd(shelterCraftConsumed,d.id,-d.delta);}
                var recovered=deltas.Where(d=>d.delta>0 && d.id is ItemID.Wood or ItemID.StoneBlock).ToArray();
                if(recovered.Length>0)
                {
                    bool inputsProven=deltas.Any(d=>d.delta<0) && deltas.Where(d=>d.delta<0).All(d=>shelterCrafted.GetValueOrDefault(d.id)>=shelterCraftConsumed.GetValueOrDefault(d.id));
                    if(!inputsProven)shelterLedgerAmbiguous=true;
                    else foreach(var d in recovered){if(d.id==ItemID.Wood)shelterRecoveredWood+=d.delta;else shelterRecoveredStone+=d.delta;}
                }
            }
            else if(action is AIActionType.Build or AIActionType.Place){foreach(var d in deltas)if(d.delta<0)ShelterAdd(shelterBuildConsumed,d.id,-d.delta);}
            else if(action==AIActionType.Withdraw)
            {
                foreach(var d in deltas.Where(d=>d.delta>0))
                {
                    var sources=shelterPreviousChests.Where(c=>chests.ContainsKey(c.Key) && c.Value.GetValueOrDefault(d.id)!=chests[c.Key].GetValueOrDefault(d.id)).Select(c=>{
                        Chest? chest=Main.chest[c.Key];return new{index=c.Key,x=chest?.x,y=chest?.y,locked=chest==null || Chest.IsLocked(chest.x,chest.y),before=c.Value.GetValueOrDefault(d.id),after=chests[c.Key].GetValueOrDefault(d.id),distancePixels=chest==null?float.MaxValue:Vector2.Distance(npc.Center,CompanionChests.Center(chest))};}).ToArray();
                    int removed=sources.Sum(c=>c.before-c.after);
                    shelterLedger.Add(new{kind="withdraw_provenance",frame=shelterFrames,tick=Main.GameUpdateCount,item=d.id,actorDelta=d.delta,sources});
                    bool ownMaterial=ShelterWoodFamily(d.id) || d.id==ItemID.StoneBlock || ShelterFinishedMaterial(d.id);
                    bool ownDeposited=!ownMaterial || sources.All(c=>shelterDepositedOwnership.GetValueOrDefault((c.index,d.id))>=c.before-c.after);
                    if(removed!=d.delta || sources.Any(c=>c.locked || c.distancePixels>80) || !ownDeposited)shelterForbiddenWithdrawal=true;
                    else if(ownMaterial)foreach(var source in sources)shelterDepositedOwnership[(source.index,d.id)]-=source.before-source.after;
                    else ShelterAdd(shelterWithdrawn,d.id,d.delta);
                }
            }
            else if(action==AIActionType.Deposit)
            {
                foreach(var d in deltas.Where(d=>d.delta<0))
                {
                    var sinks=shelterPreviousChests.Where(c=>chests.ContainsKey(c.Key) && c.Value.GetValueOrDefault(d.id)!=chests[c.Key].GetValueOrDefault(d.id)).Select(c=>new{index=c.Key,added=chests[c.Key].GetValueOrDefault(d.id)-c.Value.GetValueOrDefault(d.id)}).ToArray();
                    if(sinks.Length==0 || sinks.Any(s=>s.added<=0) || sinks.Sum(s=>s.added)!=-d.delta)shelterLedgerAmbiguous=true;
                    else foreach(var sink in sinks)shelterDepositedOwnership[(sink.index,d.id)]=shelterDepositedOwnership.GetValueOrDefault((sink.index,d.id))+sink.added;
                    shelterLedger.Add(new{kind="deposit_sink",frame=shelterFrames,item=d.id,actorDelta=d.delta,sinks});
                }
            }
            else if(action==AIActionType.Drop)
            {
                foreach(var d in deltas.Where(d=>d.delta<0))
                {int added=ground.GetValueOrDefault(d.id)-shelterPreviousGround.GetValueOrDefault(d.id);if(added!=-d.delta)shelterLedgerAmbiguous=true;shelterLedger.Add(new{kind="ground_drop_sink",frame=shelterFrames,item=d.id,actorDelta=d.delta,groundDelta=added});}
            }
            else if(action==AIActionType.Pickup)foreach(var d in deltas.Where(d=>d.delta>0))ShelterAdd(shelterPickup,d.id,d.delta);
            else if(action!=AIActionType.Mine)shelterLedgerAmbiguous=true;
            shelterLedger.Add(new{frame=shelterFrames,tick=Main.GameUpdateCount,action=action.ToString(),deltas,actor=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},mutatingOutcomes=mutating});
        }
        foreach(var o in fresh)
        {
            shelterSequence=o.Sequence;
            if(o.Task.Action==AIActionType.Mine)try{using var result=JsonDocument.Parse(o.Result);if(result.RootElement.TryGetProperty("actualGained",out var v) && v.TryGetInt32(out int n)){if(o.Task.Target=="wood")shelterMineWood+=n;if(o.Task.Target=="stone")shelterMineStone+=n;}if(result.RootElement.TryGetProperty("actualYields",out var yields) && yields.ValueKind==JsonValueKind.Array)foreach(var item in yields.EnumerateArray())if(item.TryGetProperty("itemType",out var id) && id.TryGetInt32(out int itemId) && item.TryGetProperty("amount",out var amount) && amount.TryGetInt32(out int count))ShelterAdd(shelterMinedByItem,itemId,count);}catch(JsonException){}
            if(o.Task.Action is AIActionType.Place or AIActionType.Build)
            {
                if(o.Status=="succeeded")shelterActualConstruction=true;
                shelterPreservation=shelterAudit!.CheckPreservation();shelterAudits.Add(new{frame=shelterFrames,sequence=o.Sequence,kind="preservation_after_construction",report=shelterPreservation});
                if(o.Task.X.HasValue && o.Task.Y.HasValue)shelterCandidates.Add((o.Task.X.Value,o.Task.Y.Value,Math.Max(1,o.Task.Width),Math.Max(1,o.Task.Height)));
            }
            shelterOutcomes.Add(new{o.Sequence,o.Task,o.Status,o.Result,frame=shelterFrames,tick=Main.GameUpdateCount,actor=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},inventory=now});
        }
        JsonElement progress=JsonSerializer.SerializeToElement(Executor.ConstructionProgress);
        foreach(var condition in goalPlanningState.Completion.Conditions.Where(c=>(c.Type=="housing_valid" || c.Type=="new_housing_valid") && c.X.HasValue && c.Y.HasValue))
            shelterCandidates.Add((condition.X!.Value,condition.Y!.Value,condition.Width??1,condition.Height??1));
        if(progress.ValueKind==JsonValueKind.Object)
        {
            int left=progress.GetProperty("left").GetInt32(),top=progress.GetProperty("top").GetInt32(),w=progress.GetProperty("width").GetInt32(),h=progress.GetProperty("height").GetInt32();
            shelterCandidates.Add((left,top,w,h));
            for(int x=left;x<left+w;x++)for(int y=top;y<top+h;y++)if(WorldGen.InWorld(x,y,2))
            {string current=ShelterTile(x,y);if(shelterPreviousSite.TryGetValue((x,y),out string? prior) && prior!=current)shelterChanges.Add(new{frame=shelterFrames,tick=Main.GameUpdateCount,x,y,before=prior,after=current,actor=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},distancePixels=Vector2.Distance(npc.Center,new Vector2(x*16+8,y*16+8)),progress});shelterPreviousSite[(x,y)]=current;}
        }
        if(Executor.CurrentAction==AIActionType.Place && ContinuousField("currentPlan") is AIPlan place && place.X.HasValue && place.Y.HasValue)
        {
            int left=place.X.Value,top=place.Y.Value,w=Math.Clamp(place.Width,1,97),h=Math.Clamp(place.Height,1,81);
            for(int x=left;x<left+w;x++)for(int y=top;y<top+h;y++)if(WorldGen.InWorld(x,y,2))
            {string current=ShelterTile(x,y);if(shelterPreviousSite.TryGetValue((x,y),out string? prior) && prior!=current)shelterChanges.Add(new{frame=shelterFrames,tick=Main.GameUpdateCount,x,y,before=prior,after=current,actor=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},distancePixels=Vector2.Distance(npc.Center,new Vector2(x*16+8,y*16+8)),action="Place"});shelterPreviousSite[(x,y)]=current;}
        }
        if(!ShelterCounts(p.inventory).OrderBy(k=>k.Key).SequenceEqual(shelterInitialPlayerInventory.OrderBy(k=>k.Key)))shelterPlayerInventoryChanged=true;
        if(shelterFrames%30==0){shelterTrace.Add(new{frame=shelterFrames,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},player=new{x=p.Center.X/16,y=p.Center.Y/16,feet=p.Bottom.Y/16},inventory=now,action=Executor.CurrentAction.ToString(),constructionProgress=progress,warps=Executor.NavigationTeleportCount});SaveShelterAcceptance("running","");}
        shelterPreviousInventory=now;shelterPreviousChests=chests;shelterPreviousGround=ground;shelterPreviousAction=Executor.CurrentAction;
        if(Executor.NavigationTeleportCount>0){FinishShelterAcceptance("failed","teleport");return;}
        if(decisionFailures>0 || dispatchRepairs.LastCode=="protocol_parse_rejected"){FinishShelterAcceptance("failed","first API or parse failure");return;}
        JsonElement proof=JsonSerializer.SerializeToElement(lastCompletedGoal);bool ownProof=proof.ValueKind==JsonValueKind.Object && proof.TryGetProperty("sourcePlayerRequest",out var req) && req.GetString()==ShelterAcceptanceRequest && proof.TryGetProperty("observedAtTick",out var tick) && tick.GetUInt64()>=shelterStartedTick && proof.TryGetProperty("worldId",out var world) && world.GetInt32()==Main.worldID;
        if(goalStatus=="completed" && ownProof){FinishShelterAcceptance("review","model completed; native preservation, new housing, material provenance and reach require independent audit");return;}
        bool reviewTail=goalReviewPending && ContinuousField("currentPlan") is not AIPlan && !Executor.IsInCombat && !Executor.IsAutoDefending;
        bool exhausted=PrimaryDecisionRequestCount-shelterRequestStart>=ShelterLimit && primaryCancellation==null && (!Executor.IsBusy && Executor.SnapshotTasks().Count==0 || reviewTail);
        if(!ShelterWithinTime || exhausted || goalWaitingForPlayer)FinishShelterAcceptance("failed",!ShelterWithinTime?"wall_deadline":goalWaitingForPlayer?"goal_waiting":"request_budget_exhausted");
    }
    private void FinishShelterAcceptance(string status,string error)
    {
        shelterAcceptanceFinished=true;var preservation=shelterAudit!.CheckPreservation();shelterPreservation=preservation;bool validRoom=false;
        foreach(var c in shelterCandidates){var room=c.w>1 && c.h>1?shelterAudit.CheckNewRoom(c.x,c.y,c.w,c.h):shelterAudit.CheckNewRoom(c.x,c.y);shelterAudits.Add(new{kind="candidate_room",candidate=new{c.x,c.y,c.w,c.h},report=room});if(room.validNew){shelterRoom=room;validRoom=true;}}
        bool materialProven=shelterMinedByItem.Values.Sum()>0 &&
            shelterBuildConsumed.Where(p=>ShelterFinishedMaterial(p.Key)).All(p=>shelterCrafted.GetValueOrDefault(p.Key)>=p.Value) &&
            shelterCraftConsumed.Keys.Union(shelterBuildConsumed.Keys).All(id=>shelterMinedByItem.GetValueOrDefault(id)+shelterCrafted.GetValueOrDefault(id)+shelterWithdrawn.GetValueOrDefault(id)+((ShelterWoodFamily(id) || id==ItemID.StoneBlock)?0:shelterPickup.GetValueOrDefault(id))>=shelterCraftConsumed.GetValueOrDefault(id)+shelterBuildConsumed.GetValueOrDefault(id));
        if(status=="review"){bool pass=preservation.available && preservation.preserved && validRoom && materialProven && !shelterLedgerAmbiguous && !shelterForbiddenWithdrawal && !shelterPlayerInventoryChanged && (shelterChanges.Count>0 || shelterActualConstruction);
            status=pass?"passed":"failed";error=pass?"":"native preservation, new housing, provenance, or observed construction evidence not proven";}
        SaveShelterAcceptance(status,error);Stop();Executor.Clear(true);
    }
    private void SaveShelterAcceptance(string status,string error)
    {
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","autonomous-shelter-result.json"),JsonSerializer.Serialize(new{
            status,error,request=ShelterAcceptanceRequest,startedUtc=shelterAt,startedTick=shelterStartedTick,updatedUtc=DateTime.UtcNow,frames=shelterFrames,requests=PrimaryDecisionRequestCount-shelterRequestStart,requestLimit=ShelterLimit,timeLimitMinutes=ShelterTimeLimitMinutes,teleports=Executor.NavigationTeleportCount,
            goalStatus,reply=LastReply,lastCompletedGoal,planning=JsonSerializer.SerializeToElement(PlanningContext),goalReviewPending,executorIsBusy=Executor.IsBusy,currentPlan=ContinuousField("currentPlan"),remainingTasks=Executor.SnapshotTasks(),primaryInFlight=primaryCancellation!=null,
            preservation=shelterPreservation,productionPreservationDebug=status=="running"?null:PreservationEvidence.DebugSnapshot(),newRoom=shelterRoom,houseBaseline=shelterAudit?.BaselineReport,shelterMineWood,shelterMineStone,shelterMinedByItem,shelterInitialGround,shelterRecoveredWood,shelterRecoveredStone,shelterCrafted,shelterCraftConsumed,shelterBuildConsumed,shelterWithdrawn,shelterPickup,shelterActualConstruction,shelterLedgerAmbiguous,shelterForbiddenWithdrawal,shelterPlayerInventoryChanged,
            candidates=shelterCandidates.Select(c=>new{c.x,c.y,c.w,c.h}),outcomes=shelterOutcomes,ledger=shelterLedger,changes=shelterChanges,audits=shelterAudits,trace=shelterTrace,inventory=ShelterCounts(Loadout.Inventory),
            scope="One fresh natural Flash task on original-house copy; starter tools only, one setup; no injected materials or fixture-chosen site/layout/actions. Hostiles removed/player protected. Native shelter Build is 10x7 if model selects it. Mine yields are per-item inventory pickups and may include previously existing nearby drops; initial ground counts are retained, and yield alone does not prove each item was freshly cut. Review requires independent native housing, original-house preservation, item provenance and construction-reach audit."
        },new JsonSerializerOptions{WriteIndented=false}));
    }
}
