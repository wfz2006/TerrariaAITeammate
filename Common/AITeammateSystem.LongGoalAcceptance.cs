using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

internal sealed record LongGoalDropSpawn(ulong tick,int slot,int type,int stack,string sourceType,int? tileX,int? tileY,string action);

// Acceptance-only witness: it observes the actual native spawn source and never changes drops.
public sealed class LongGoalDropWitness : GlobalItem
{
    internal static bool Active;
    internal static readonly List<LongGoalDropSpawn> Spawns=new();
    public override void OnSpawn(Item item,IEntitySource source)
    {
        if(!Active || Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE") is not ("long_goal_supplies" or "long_goal_supplies_native" or "autonomous_closure_natural" or "autonomous_closure_sources_native") || Main.netMode!=NetmodeID.SinglePlayer || !Main.SavePath.Contains("agent-acceptance",StringComparison.OrdinalIgnoreCase) || item.type is not (ItemID.Wood or ItemID.StoneBlock))return;
        Point? tile=source is EntitySource_TileBreak broken?broken.TileCoords:null;
        Spawns.Add(new(Main.GameUpdateCount,item.whoAmI,item.type,item.stack,source.GetType().FullName??source.GetType().Name,tile?.X,tile?.Y,AITeammateSystem.Instance.Executor.CurrentAction.ToString()));
    }
}

public partial class AITeammateSystem
{
    private const string LongGoalRequest="请自己筹集材料，使用家里已有的工作台，做1把木椅、20个石墙、6个普通火把和4个木平台，做好后带着成品回到我身边。木材和石块要自己采，凝胶可以从家里现有箱子取；不要拿箱里的成品，不要拆现有建筑或另放工作台。遇到受阻自己换路线或材料来源，保留已经完成的部分继续做。";
    private sealed record LongGoalChest(int Index,int X,int Y,bool Locked,int Gel,int Torch,int Platform,string OtherContents,object[] Slots);
    private sealed record LongGoalSnapshot(int Wood,int Stone,int Gel,int Chair,int Wall,int Torch,int Platform,Vector2 Actor,float Feet,object[] Stations,Dictionary<int,int> Inventory,Dictionary<int,LongGoalChest> Chests);
    private sealed record LongGoalReturnDeparture(ulong Tick, int PlayerId, float ActorX, float ActorFeet, float PlayerX, float PlayerFeet);
    private LongGoalReturnDeparture? longGoalReturnDeparture;
    private int longGoalPlacedNavigationCells;
    private bool longGoalStarted,longGoalFinished,longGoalNative,longGoalChairCraft,longGoalWallCraft,longGoalTorchCraft,longGoalPlatformCraft,longGoalReturned,longGoalEvidenceAmbiguous;
    private int longGoalFrames,longGoalRequestStart,longGoalCombatStart,longGoalMineWood,longGoalMineStone,longGoalGelWithdrawn,longGoalChairProduced,longGoalWallProduced,longGoalTorchProduced,longGoalPlatformProduced;private long longGoalSequence;
    private int longGoalWoodConsumed,longGoalStoneConsumed,longGoalGelConsumed;
    private int longGoalCraftCompletedFrame=-1;private ulong longGoalCraftCompletedTick;
    private ulong longGoalStartedTick;private DateTime longGoalAt;private string longGoalRequest="";private LongGoalSnapshot? longGoalPrevious;
    private readonly List<object> longGoalEvents=new(),longGoalTrace=new();
    private readonly HashSet<(int x,int y)> longGoalOriginalBenches=new();
    private readonly HashSet<(int x,int y)> longGoalRemovedMineTiles=new();
    private readonly List<object> longGoalRemovedLoose=new();
    private bool gelProbeStarted,gelProbeFinished,gelProbeSubmitted;
    private int gelProbeFrames,gelProbeChest=-1,gelProbePrimary,gelProbeCombat;
    private long gelProbeSequence;
    private LongGoalSnapshot? gelProbePrevious;
    private ShelterWorldAudit? gelProbeAudit;
    private readonly List<object> gelProbeEvents=new();
    private bool longGoalConstraintViolation,longGoalSubmitted,longGoalSourceEvidenceIncomplete,longGoalNativeFinalCome,longGoalPriorHideUI;private float longGoalPriorZoom;private ShelterWorldAudit? longGoalAudit;private ShelterPreservationReport? longGoalPreservation;private string longGoalPackageHash="";private int longGoalFailuresStart;
    private bool ClosureNatural => Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="autonomous_closure_natural";
    private int LongGoalLimit=>ClosureReplay?longGoalReplayLimit:ClosureNatural?20:16;
    private string LongGoalFile(string name)=>Path.Combine(Main.SavePath,"AITeammate",$"{(ClosureNatural?"autonomous-closure-natural":longGoalNative?"long-goal-native":"long-goal-supplies")}-{name}");
    private bool LongGoalWithinTime=>DateTime.UtcNow-longGoalAt<TimeSpan.FromMinutes(ClosureNatural?10:8);
    private bool LongGoalPlanningAllowed=>longGoalStarted && longGoalSubmitted && !longGoalNative && !longGoalFinished && (!ClosureReplay || longGoalReplayLive) && decisionFailures==longGoalFailuresStart && dispatchRepairs.LastCode!="protocol_parse_rejected" && PrimaryDecisionRequestCount-longGoalRequestStart<LongGoalLimit && LongGoalWithinTime;
    private bool LongGoalClosurePlanActive => HouseMode && ClosureNatural && longGoalSubmitted && !longGoalFinished;

    private static bool LongGoalNamesItem(string query, int itemType)
    {
        string name = query.Trim();
        return int.TryParse(name, out int id) ? id == itemType :
            name.Equals(Lang.GetItemNameValue(itemType), StringComparison.OrdinalIgnoreCase) ||
            name.Equals(ItemID.Search.GetName(itemType), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsLongGoalNavigationPlacement(AIPlan task)
    {
        int allowedType = LongGoalNamesItem(task.Item, ItemID.WoodPlatform) ? ItemID.WoodPlatform :
            LongGoalNamesItem(task.Item, ItemID.DirtBlock) ? ItemID.DirtBlock : ItemID.None;
        if (allowedType == ItemID.None || task is not { X: int x, Y: int y, Width: 1, Height: 1, Count: <= 1 } ||
            !WorldGen.InWorld(x, y, 3) || longGoalAudit?.BaselineReport.primaryComponentBounds is not { } house ||
            house.Contains(x, y) || !ClosurePlatformDryAir(new Point(x, y))) return false;
        int index = Loadout.FindInventoryIndex(task.Item);
        return (index < 0 || Loadout.Inventory[index].type == allowedType) &&
            longGoalAudit.ReadConstructionProtection(x, y, 1, 1) is { Available: true, Covered: true, Allowed: true };
    }

    private bool ValidateLongGoalClosurePlan(AIPlan plan)
    {
        IReadOnlyList<AIPlan> tasks = plan.Steps.Count > 0 ? plan.Steps : new[] { plan };
        bool Allowed(AIPlan task) => task.Action switch
        {
            AIActionType.None or AIActionType.Inspect or AIActionType.Explore or AIActionType.Follow or
                AIActionType.Come or AIActionType.Wait or AIActionType.Stop or AIActionType.Craft => true,
            AIActionType.Mine => task.Target is "wood" or "stone" or "dirt" or "grass",
            AIActionType.Withdraw => LongGoalNamesItem(task.Item, ItemID.Gel) && !Main.chest.Any(chest =>
                chest != null && CompanionChests.IsChestBuilt(chest) && !Chest.IsLocked(chest.x, chest.y) &&
                CompanionChests.MatchesExplicitTarget(chest, task.X, task.Y) && chest.item.Any(item =>
                    item != null && !item.IsAir && item.type != ItemID.Gel && CompanionChests.Matches(item, task.Item.Trim()))),
            AIActionType.Place => IsLongGoalNavigationPlacement(task),
            _ => false
        };
        int uncountedPlacements = agentJournal.Outcomes.Count(outcome => outcome.Sequence > longGoalSequence &&
            outcome.Task.Action == AIActionType.Place && outcome.Status == "succeeded");
        if (longGoalPlacedNavigationCells + uncountedPlacements + tasks.Count(task => task.Action == AIActionType.Place) <= ClosurePlatformLimit &&
            tasks.All(Allowed)) return true;
        longGoalConstraintViolation = true;
        longGoalEvents.Add(new { kind = "forbidden_plan", tasks, executed = false, longGoalPlacedNavigationCells });
        FinishLongGoalAcceptance("instruction_violation", "Only self-gathered wood/stone, native gel withdrawal and small, protected outdoor navigation placement are allowed.");
        return false;
    }

    private bool ObserveLongGoalCarriedArrival(NPC npc, Player player, LongGoalSnapshot now, bool grounded)
    {
        if (longGoalCraftCompletedFrame < 0 || now.Chair < 1 || now.Wall < 20 || now.Torch < 6 || now.Platform < 4)
        {
            longGoalReturnDeparture = null;
            return false;
        }
        float horizontal = Math.Abs(now.Actor.X - player.Center.X), vertical = Math.Abs(now.Feet - player.Bottom.Y);
        if (horizontal > 48f || vertical > 24f)
        {
            longGoalReturnDeparture ??= new(Main.GameUpdateCount, player.whoAmI, now.Actor.X, now.Feet, player.Center.X, player.Bottom.Y);
            return false;
        }
        if (longGoalReturnDeparture is not { } departure || departure.PlayerId != player.whoAmI ||
            Math.Abs(player.Center.X - departure.PlayerX) > 4f || Math.Abs(player.Bottom.Y - departure.PlayerFeet) > 4f ||
            !grounded || !OrderedGoalPassage(npc, player)) return false;
        return Vector2.DistanceSquared(new(now.Actor.X, now.Feet), new(departure.ActorX, departure.ActorFeet)) >= 1f;
    }

    private static object[] LongGoalSlots(Chest c)=>c.item.Select((item,slot)=>(object)new{slot,item.type,item.stack,item.prefix}).ToArray();
    private static object LongGoalSnapshotView(LongGoalSnapshot s,bool includeChests=false)=>new{s.Wood,s.Stone,s.Gel,s.Chair,s.Wall,s.Torch,s.Platform,Actor=new{x=s.Actor.X,y=s.Actor.Y},s.Feet,s.Stations,s.Inventory,Chests=includeChests?s.Chests:null};
    private LongGoalSnapshot CaptureLongGoal(NPC npc)
    {
        var stations=CompanionCrafting.NearbyStations(npc).Select(s=>(object)new{type=s.Type,x=s.X,y=s.Y}).ToArray();
        var chests=Main.chest.Select((c,i)=>(c,i)).Where(v=>v.c!=null && CompanionChests.IsChestBuilt(v.c)).ToDictionary(v=>v.i,v=>{
            Chest c=v.c!;int gel=0,torch=0,platform=0;var other=new List<string>();
            for(int slot=0;slot<c.item.Length;slot++){Item item=c.item[slot];if(item.IsAir)continue;if(item.type==ItemID.Gel)gel+=item.stack;else{other.Add($"{slot}:{item.type}:{item.stack}:{item.prefix}");if(item.type==ItemID.Torch)torch+=item.stack;if(item.type==ItemID.WoodPlatform)platform+=item.stack;}}
            return new LongGoalChest(v.i,c.x,c.y,Chest.IsLocked(c.x,c.y),gel,torch,platform,string.Join(",",other),LongGoalSlots(c));});
        var inventory=Loadout.Inventory.Where(i=>!i.IsAir).GroupBy(i=>i.type).ToDictionary(g=>g.Key,g=>g.Sum(i=>i.stack));
        return new(Loadout.CountItem(ItemID.Wood),Loadout.CountItem(ItemID.StoneBlock),Loadout.CountItem(ItemID.Gel),Loadout.CountItem(ItemID.WoodenChair),Loadout.CountItem(ItemID.StoneWall),Loadout.CountItem(ItemID.Torch),Loadout.CountItem(ItemID.WoodPlatform),npc.Center,npc.Bottom.Y,stations,inventory,chests);
    }
    private void BeginLongGoalAcceptance()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear();Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;Loadout.Reset();Loadout.EnsureStarterTools();
        longGoalNative=Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="long_goal_supplies_native";
        longGoalRequest=longGoalNative?"Controlled zero-API primitive chain: Mine wood, Mine stone, Come, Craft wood platform, Come.":LongGoalRequest+(ClosureNatural?"允许在原屋外用自己筹集的材料搭少量临时平台或土阶，用于通行和返程；保留原建筑和物品，不恢复自主建屋。":"");longGoalSourceEvidenceIncomplete=true;
        longGoalAudit=ShelterWorldAudit.Capture(3220,390);longGoalPreservation=longGoalAudit.CheckPreservation();longGoalFailuresStart=decisionFailures;CombatPlannerEnabled=false;
        LongGoalDropWitness.Active=false;LongGoalDropWitness.Spawns.Clear();longGoalRemovedMineTiles.Clear();longGoalRemovedLoose.Clear();
        foreach(Item item in Main.item)if(item.active && item.type is ItemID.Wood or ItemID.StoneBlock)
        {longGoalRemovedLoose.Add(new{item.whoAmI,item.type,item.stack,item.prefix,x=item.Center.X,y=item.Center.Y});item.TurnToAir();}
        LongGoalDropWitness.Active=true;
        string package=Path.Combine(Main.SavePath,"Mods","TerrariaAITeammate.tmod");using(var hash=System.Security.Cryptography.SHA256.Create())using(var stream=File.OpenRead(package))longGoalPackageHash=Convert.ToHexString(hash.ComputeHash(stream));
        longGoalPrevious=CaptureLongGoal(npc);longGoalSequence=agentJournal.LastSequence;longGoalRequestStart=PrimaryDecisionRequestCount;longGoalCombatStart=CombatDecisionRequestCount;longGoalStartedTick=Main.GameUpdateCount;longGoalAt=DateTime.UtcNow;longGoalStarted=true;
        var houseStations=new List<object>();for(int x=3160;x<=3270;x++)for(int y=350;y<=420;y++)if(Main.tile[x,y].HasTile && Main.tile[x,y].TileType==TileID.WorkBenches){houseStations.Add(new{x,y,type=(int)Main.tile[x,y].TileType});longGoalOriginalBenches.Add((x,y));}
        var initialChestSlots=longGoalPrevious.Chests.Values.Select(c=>new{c.Index,c.X,c.Y,c.Locked,items=LongGoalSlots(Main.chest[c.Index])}).ToArray();
        longGoalEvents.Add(new{kind="pre_dispatch",snapshot=LongGoalSnapshotView(longGoalPrevious,true),houseStations,initialChestSlots,removedPreexistingLooseWoodStone=longGoalRemovedLoose,scope="audit only; not supplied to model; only preexisting loose wood/stone removed from isolated copy; actor setup occurs once when start gate is consumed"});
        if(longGoalPrevious.Wood!=0 || longGoalPrevious.Stone!=0 || longGoalPrevious.Gel!=0 || longGoalPrevious.Chair!=0 || longGoalPrevious.Wall!=0 || longGoalPrevious.Torch!=0 || longGoalPrevious.Platform!=0){FinishLongGoalAcceptance("failed","initial materials not empty");return;}
        var settings=GetLlmSettings();var config=Terraria.ModLoader.ModContent.GetInstance<AIConfig>();
        if (ClosureReplay && !LoadLongGoalReplay(settings)) return;
        if(!longGoalNative && (!ClosureReplay || LongGoalLimit > 0) && (settings==null || settings.Model!="deepseek-flash" || settings.ThinkingMode!=LlmThinkingMode.Enabled || settings.ReasoningEffort!=LlmReasoningEffort.Low || !config.DebugLog))
        {FinishLongGoalAcceptance("uncovered_config","Requires Flash/Enabled/Low and DebugLog for public response content; no API request made");return;}
        if (ClosureNatural && (config.AllowTeleportRecovery || longGoalPreservation is not { preserved: true, coverageComplete: true } ||
            longGoalOriginalBenches.Count == 0 || !longGoalPrevious.Chests.Values.Any(chest => !chest.Locked && chest.Gel > 0)))
        { FinishLongGoalAcceptance("uncovered_fixture", "Requires a complete original house, an existing bench and unlocked gel, with teleport recovery disabled."); return; }
        if(!longGoalNative && settings!=null)longGoalEvents.Add(new{kind="sanitized_model_settings",settings.Model,settings.ThinkingMode,settings.ReasoningEffort,settings.MaxTokens,debugPublicContent=config.DebugLog,publicResponseLog="isolated runtime stdout / tML log: planner raw, usage and actualModel lines"});
        string ack=LongGoalFile("start.txt");if(File.Exists(ack))File.Delete(ack);
        File.WriteAllText(LongGoalFile("ready.txt"),"waiting_for_explicit_start");SaveLongGoalAcceptance("ready","");
    }
    private void TickLongGoalAcceptance()
    {
        if(Main.gameMenu || CompanionNpc==null)return;NPC npc=CompanionNpc;Player p=Main.LocalPlayer;
        if(!longGoalSubmitted){string ack=LongGoalFile("start.txt");if(!File.Exists(ack)||File.ReadAllText(ack).Trim()!=(longGoalNative?"START_NATIVE":ClosureReplay?"START_REPLAY":"START_NATURAL"))return;
            if (ClosureNatural && !JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Status()).GetProperty("ready").GetBoolean()) return;
            File.Delete(ack);
            npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;longGoalPrevious=CaptureLongGoal(npc);
            longGoalEvents.Add(new{kind="one_time_actor_setup_at_dispatch",npc=new{x=npc.Center.X,y=npc.Center.Y,feet=npc.Bottom.Y},player=new{x=p.Center.X,y=p.Center.Y,feet=p.Bottom.Y},snapshot=LongGoalSnapshotView(longGoalPrevious)});
            longGoalPriorHideUI=Main.hideUI;longGoalPriorZoom=Main.GameZoomTarget;Main.hideUI=true;Main.GameZoomTarget=1.6f;
            longGoalSubmitted=true;longGoalAt=DateTime.UtcNow;longGoalStartedTick=Main.GameUpdateCount;
            if(longGoalNative){var steps=new[]{new AIPlan{Action=AIActionType.Mine,Target="wood",Count=10},new AIPlan{Action=AIActionType.Mine,Target="stone",Count=5},new AIPlan{Action=AIActionType.Come},new AIPlan{Action=AIActionType.Craft,Item=ItemID.WoodPlatform.ToString(),Count=4},new AIPlan{Action=AIActionType.Come}};longGoalEvents.Add(new{kind="controlled_primitive_dispatch",steps});Executor.ApplySequence(steps);}
            else if(!ClosureReplay) HandlePlayerMessage(p,longGoalRequest);SaveLongGoalAcceptance("running","");return;}
        longGoalFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(ClosureReplay && longGoalReplayReady && !longGoalReplayLive){AdvanceLongGoalReplay();return;}
        LongGoalSnapshot now=CaptureLongGoal(npc);var events=agentJournal.Outcomes.Where(o=>o.Sequence>longGoalSequence).OrderBy(o=>o.Sequence).ToArray();
        int mutatingCount=events.Count(o=>o.Task.Action is AIActionType.Mine or AIActionType.Craft or AIActionType.Withdraw or AIActionType.Deposit or AIActionType.Pickup or AIActionType.Drop or AIActionType.Place or AIActionType.Build);
        if(longGoalPrevious!=null)
        {
            foreach(var beforeChest in longGoalPrevious.Chests.Values)
            {
                if(!now.Chests.TryGetValue(beforeChest.Index,out var afterChest)){longGoalEvidenceAmbiguous=true;continue;}
                if(beforeChest.OtherContents!=afterChest.OtherContents)longGoalConstraintViolation=true;
                if(beforeChest.Gel!=afterChest.Gel && !events.Any(o=>o.Task.Action==AIActionType.Withdraw && o.Status=="succeeded"))longGoalEvidenceAmbiguous=true;
            }
        }
        if(mutatingCount>1)longGoalEvidenceAmbiguous=true;
        foreach(var o in events)
        {
            longGoalSequence=o.Sequence;LongGoalSnapshot before=longGoalPrevious??now;
            if(o.Task.Action==AIActionType.Mine)try{using var result=JsonDocument.Parse(o.Result);var root=result.RootElement;
                if(root.TryGetProperty("actualYields",out var yields)&&yields.ValueKind==JsonValueKind.Array && root.TryGetProperty("removedTilesTotal",out var removed))
                {
                    if(root.TryGetProperty("removedTilesTruncated",out var truncated)&&truncated.ValueKind==JsonValueKind.True)longGoalEvidenceAmbiguous=true;
                    if(root.TryGetProperty("removedTiles",out var tiles)&&tiles.ValueKind==JsonValueKind.Array)
                        foreach(var tile in tiles.EnumerateArray())longGoalRemovedMineTiles.Add((tile.GetProperty("x").GetInt32(),tile.GetProperty("y").GetInt32()));
                    foreach(var row in yields.EnumerateArray()){int id=row.GetProperty("itemType").GetInt32(),n=row.GetProperty("amount").GetInt32();if(id==ItemID.Wood)longGoalMineWood+=n;if(id==ItemID.StoneBlock)longGoalMineStone+=n;if(n>0&&removed.GetInt32()==0)longGoalEvidenceAmbiguous=true;}
                }
                else longGoalEvidenceAmbiguous=true;
            }catch(JsonException){longGoalEvidenceAmbiguous=true;}
            var chestDeltas=before.Chests.Values.Where(c=>now.Chests.TryGetValue(c.Index,out var after) && (c.Gel!=after.Gel || c.OtherContents!=after.OtherContents)).Select(c=>new{before=c,after=now.Chests[c.Index],distancePixels=Vector2.Distance(before.Actor,new Vector2(c.X*16+16,c.Y*16+16)),beforeSlots=c.Slots,afterSlots=now.Chests[c.Index].Slots}).ToArray();
            
            if(ClosureNatural && (o.Task.Action is AIActionType.Place or AIActionType.Build))
            {
                if (o.Task.Action == AIActionType.Place && o.Status == "succeeded") longGoalPlacedNavigationCells++;
                var consumption=before.Inventory.Keys.Union(now.Inventory.Keys).Select(id=>new{id,amount=before.Inventory.GetValueOrDefault(id)-now.Inventory.GetValueOrDefault(id)}).Where(v=>v.amount>0).Select(v=>{
                    var item=new Item();item.SetDefaults(v.id);return new{itemType=v.id,v.amount,item.createTile,item.createWall,item.placeStyle};}).ToArray();
                var cells=new List<object>(); int? sx=o.Task.X,sy=o.Task.Y; int width=Math.Max(1,o.Task.Width),height=Math.Max(1,o.Task.Height);
                bool bounded=sx.HasValue && sy.HasValue && width<=97 && height<=81 && WorldGen.InWorld(sx.Value,sy.Value,2) && WorldGen.InWorld(sx.Value+width-1,sy.Value+height-1,2);
                if(bounded)for(int x=sx!.Value;x<sx.Value+width;x++)for(int y=sy!.Value;y<sy.Value+height;y++)
                {Tile t=Main.tile[x,y];cells.Add(new{x,y,t.HasTile,t.TileType,t.TileFrameX,t.TileFrameY,t.Slope,t.IsHalfBlock,t.IsActuated,t.WallType});}
                longGoalEvents.Add(new{kind="placed_navigation_observation",outcome=o,consumption,beforeActor=new{x=before.Actor.X,y=before.Actor.Y,feet=before.Feet},afterActor=new{x=now.Actor.X,y=now.Actor.Y,feet=now.Feet},requestedBounds=new{x=sx,y=sy,width,height},bounded,cells,preservation=longGoalAudit?.CheckPreservation(),scope="Observed outcome and current cells only; placement purpose, outdoor location, quantity and any partial changes require raw result and original-mask human review. This event does not authorize or classify the action."});
                if(o.Task.Action==AIActionType.Build)longGoalConstraintViolation=true;
            }
            if(o.Status=="succeeded" && (o.Task.Action is AIActionType.Place or AIActionType.Build))
            {
                var old=before.Stations.Select(s=>JsonSerializer.SerializeToElement(s)).Where(s=>s.GetProperty("type").GetInt32()==TileID.WorkBenches).Select(s=>(s.GetProperty("x").GetInt32(),s.GetProperty("y").GetInt32())).ToHashSet();
                bool added=now.Stations.Select(s=>JsonSerializer.SerializeToElement(s)).Where(s=>s.GetProperty("type").GetInt32()==TileID.WorkBenches).Any(s=>!old.Contains((s.GetProperty("x").GetInt32(),s.GetProperty("y").GetInt32())) && !longGoalOriginalBenches.Contains((s.GetProperty("x").GetInt32(),s.GetProperty("y").GetInt32())));
                if(added)longGoalConstraintViolation=true;
                if(mutatingCount>1)longGoalEvidenceAmbiguous=true;
            }
            if(o.Task.Action==AIActionType.Withdraw && o.Status=="succeeded")
            {
                int fromChest=chestDeltas.Sum(d=>d.before.Gel-d.after.Gel),toActor=now.Gel-before.Gel;
                bool onlyGel=chestDeltas.All(d=>d.before.OtherContents==d.after.OtherContents) && now.Inventory.Keys.Union(before.Inventory.Keys).Where(id=>id!=ItemID.Gel).All(id=>now.Inventory.GetValueOrDefault(id)==before.Inventory.GetValueOrDefault(id));
                bool transferPhysical=chestDeltas.All(d=>!d.before.Locked && d.before.X==d.after.X && d.before.Y==d.after.Y && d.distancePixels<=80 && Main.chest[d.before.Index]!=null && CompanionChests.IsChestBuilt(Main.chest[d.before.Index]));
                if(!onlyGel)longGoalConstraintViolation=true;
                if(mutatingCount==1 && fromChest>0 && fromChest==toActor && onlyGel && transferPhysical)longGoalGelWithdrawn+=fromChest;
                else longGoalEvidenceAmbiguous=true;
            }
            object? recipes=null;
            if(o.Task.Action==AIActionType.Craft && o.Status=="succeeded")
            {
                var candidates=CompanionCrafting.FindRecipes(o.Task.Item).Where(r=>!o.Task.RecipeId.HasValue || r.RecipeIndex==o.Task.RecipeId.Value).ToArray();
                recipes=candidates.Select(r=>new{r.RecipeIndex,output=r.createItem.type,outputCount=r.createItem.stack,requirements=CompanionCrafting.Requirements(r),stations=r.requiredTile.ToArray()}).ToArray();
                bool workbench=before.Stations.Concat(now.Stations).Select(s=>JsonSerializer.SerializeToElement(s)).Any(s=>s.GetProperty("type").GetInt32()==TileID.WorkBenches && longGoalOriginalBenches.Contains((s.GetProperty("x").GetInt32(),s.GetProperty("y").GetInt32())));
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
                if(chair)longGoalChairProduced+=now.Chair-before.Chair;
                if(wall)longGoalWallProduced+=now.Wall-before.Wall;
                if(torch)longGoalTorchProduced+=now.Torch-before.Torch;
                if(platform)longGoalPlatformProduced+=now.Platform-before.Platform;
                if(chair || wall || torch || platform){longGoalWoodConsumed+=before.Wood-now.Wood;longGoalStoneConsumed+=before.Stone-now.Stone;longGoalGelConsumed+=before.Gel-now.Gel;}
                longGoalChairCraft=longGoalChairProduced>=1;longGoalWallCraft=longGoalWallProduced>=20;longGoalTorchCraft=longGoalTorchProduced>=6;longGoalPlatformCraft=longGoalPlatformProduced>=4;
                if(longGoalCraftCompletedFrame<0 && (longGoalNative?longGoalPlatformCraft:longGoalChairCraft&&longGoalWallCraft&&longGoalTorchCraft&&longGoalPlatformCraft))
                {longGoalCraftCompletedFrame=longGoalFrames;longGoalCraftCompletedTick=Main.GameUpdateCount;}
            }
            if(longGoalNative && o.Task.Action==AIActionType.Come && o.Status=="succeeded" && longGoalCraftCompletedFrame>=0)longGoalNativeFinalCome=true;
            if(longGoalNative && o.Status!="succeeded"){longGoalEvents.Add(new{kind="native_primitive_blocked",outcome=o});FinishLongGoalAcceptance("failed","controlled primitive blocked");return;}
            bool grounded=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width);
            longGoalEvents.Add(new{kind="outcome",frame=longGoalFrames,tick=Main.GameUpdateCount,outcome=o,before=LongGoalSnapshotView(before),after=LongGoalSnapshotView(now),chestDeltas,recipes,grounded,longGoalReturned});
        }
        bool groundedNow=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width);
        if (!longGoalReturned)
        {
            bool carriedArrival = !ClosureNatural || ObserveLongGoalCarriedArrival(npc, p, now, groundedNow);
            if (carriedArrival && longGoalCraftCompletedFrame >= 0 && longGoalFrames > longGoalCraftCompletedFrame &&
                Main.GameUpdateCount > longGoalCraftCompletedTick && groundedNow &&
                Math.Abs(npc.Center.X - p.Center.X) <= 48 && Math.Abs(npc.Bottom.Y - p.Bottom.Y) <= 24)
            {
                longGoalReturned = true;
                longGoalEvents.Add(new { kind = "returned_with_products", frame = longGoalFrames, tick = Main.GameUpdateCount,
                    craftFrame = longGoalCraftCompletedFrame, craftTick = longGoalCraftCompletedTick, departure = longGoalReturnDeparture,
                    npc = new { x = npc.Center.X / 16, y = npc.Center.Y / 16, feet = npc.Bottom.Y / 16 },
                    player = new { x = p.Center.X / 16, y = p.Center.Y / 16, feet = p.Bottom.Y / 16 },
                    grounded = groundedNow, passage = OrderedGoalPassage(npc, p), now.Chair, now.Wall, now.Torch, now.Platform });
            }
        }
        longGoalPrevious=now;
        if(longGoalFrames%60==0 && longGoalAudit!=null){longGoalPreservation=longGoalAudit.CheckPreservation();if(!longGoalPreservation.preserved){longGoalConstraintViolation=true;FinishLongGoalAcceptance("failed","bounded house preservation changed");return;}}
        if(events.Length>0)SaveLongGoalAcceptance("running","");
        if(longGoalFrames%30==0){longGoalTrace.Add(new{frame=longGoalFrames,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},player=new{x=p.Center.X/16,y=p.Center.Y/16,feet=p.Bottom.Y/16},now.Wood,now.Stone,now.Gel,now.Chair,now.Wall,now.Torch,now.Platform,action=Executor.CurrentAction.ToString(),warps=Executor.NavigationTeleportCount});SaveLongGoalAcceptance("running","");}
        if(Executor.NavigationTeleportCount>0){FinishLongGoalAcceptance("failed","teleport");return;}
        if(longGoalNative && PrimaryDecisionRequestCount!=longGoalRequestStart){FinishLongGoalAcceptance("failed","unexpected primary API request");return;}
        if(CombatDecisionRequestCount!=longGoalCombatStart){FinishLongGoalAcceptance("failed","unexpected combat API request");return;}
        if(decisionFailures>longGoalFailuresStart || dispatchRepairs.LastCode=="protocol_parse_rejected"){FinishLongGoalAcceptance("failed","first API or parse failure");return;}
        if(ClosureReplay && !longGoalReplayLive){AdvanceLongGoalReplay();return;}
        JsonElement proof=JsonSerializer.SerializeToElement(lastCompletedGoal);bool ownProof=proof.ValueKind==JsonValueKind.Object && proof.TryGetProperty("sourcePlayerRequest",out var req) && req.GetString()==longGoalRequest && proof.TryGetProperty("observedAtTick",out var tick) && tick.GetUInt64()>=longGoalStartedTick && proof.TryGetProperty("worldId",out var world) && world.GetInt32()==Main.worldID;
        bool physical=longGoalMineWood>=longGoalWoodConsumed && longGoalWoodConsumed>0 && longGoalMineStone>=longGoalStoneConsumed && longGoalStoneConsumed>0 && longGoalGelWithdrawn>=longGoalGelConsumed && longGoalGelConsumed>0 && longGoalChairCraft && longGoalWallCraft && longGoalTorchCraft && longGoalPlatformCraft && now.Chair>=1 && now.Wall>=20 && now.Torch>=6 && now.Platform>=4;
        bool nearNow=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width) && Math.Abs(npc.Center.X-p.Center.X)<=48 && Math.Abs(npc.Bottom.Y-p.Bottom.Y)<=24 && (!ClosureNatural || OrderedGoalPassage(npc, p));
        if(longGoalNative && longGoalNativeFinalCome && longGoalReturned && nearNow){longGoalSourceEvidenceIncomplete=!LongGoalSourcesProven();longGoalPreservation=longGoalAudit?.CheckPreservation();bool passed=longGoalMineWood>0&&longGoalMineStone>0&&longGoalPlatformCraft&&!longGoalEvidenceAmbiguous&&!longGoalConstraintViolation&&!longGoalSourceEvidenceIncomplete&&longGoalPreservation is {preserved:true,coverageComplete:true}&&PrimaryDecisionRequestCount==longGoalRequestStart;FinishLongGoalAcceptance(passed?"passed":"uncovered",passed?"":"native chain completed but source or preservation evidence incomplete");return;}
        if(!longGoalNative && goalStatus=="completed" && ownProof){longGoalSourceEvidenceIncomplete=!LongGoalSourcesProven();longGoalPreservation=longGoalAudit?.CheckPreservation();bool passed=physical && longGoalReturned && nearNow && !longGoalEvidenceAmbiguous && !longGoalConstraintViolation && !longGoalSourceEvidenceIncomplete && longGoalPreservation is { preserved:true, coverageComplete:true };FinishLongGoalAcceptance(passed?"passed":"failed",passed?"":"completion lacks unambiguous native ordered evidence or violates task constraints");return;}
        bool reviewTail=goalReviewPending && ContinuousField("currentPlan") is not AIPlan && !Executor.IsInCombat && !Executor.IsAutoDefending;
        bool exhausted=PrimaryDecisionRequestCount-longGoalRequestStart>=LongGoalLimit && primaryCancellation==null && (!Executor.IsBusy && Executor.SnapshotTasks().Count==0 || reviewTail);
        if(!LongGoalWithinTime || exhausted || goalWaitingForPlayer)FinishLongGoalAcceptance("failed",!LongGoalWithinTime?"wall_deadline":goalWaitingForPlayer?"goal_waiting":"request_budget_exhausted");
    }
    private bool LongGoalSourcesProven()
    {
        var spawns=LongGoalDropWitness.Spawns;
        if(longGoalMineWood<=0||longGoalMineStone<=0||spawns.Count==0)return false;
        if(spawns.Any(s=>s.stack<=0 || s.sourceType!=typeof(EntitySource_TileBreak).FullName || s.action!="Mine" || !s.tileX.HasValue || !s.tileY.HasValue || !longGoalRemovedMineTiles.Contains((s.tileX.Value,s.tileY.Value))))return false;
        return spawns.Where(s=>s.type==ItemID.Wood).Sum(s=>s.stack)>=longGoalMineWood && spawns.Where(s=>s.type==ItemID.StoneBlock).Sum(s=>s.stack)>=longGoalMineStone;
    }
    private void RunLongGoalStoneReadonly()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear(true);Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player player=Main.LocalPlayer;
        npc.Center=new Vector2(3274.6387f*16f,400.6875f*16f);npc.velocity=Vector2.Zero;player.Center=Stand(3173,395);player.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();int primary=PrimaryDecisionRequestCount,combat=CombatDecisionRequestCount;ulong tick=Main.GameUpdateCount;
        string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));
        var seeds=new[]{(3307,394),(3308,394),(3309,394),(3310,394),(3311,394),(3319,394),(3316,408),(3239,417),(3265,416)};
        var reports=new List<object>();
        foreach(var (sx,sy) in seeds)
        {
            Tile tile=Main.tile[sx,sy];Vector2 target=new(sx*16f+8,sy*16f+8);var stands=new List<object>();var localCells=new List<object>();
            for(int x=sx-6;x<=sx+6;x++)for(int y=sy-7;y<=sy+7;y++)
                if(TilePathfinder.IsSafeStand(x,y)){Point node=new(x,y);Vector2 center=TilePathfinder.StandCenter(node,npc.height);stands.Add(new{x,standRow=y,center=new{x=center.X,y=center.Y},bottom=TilePathfinder.StandBottom(node),distance=Vector2.Distance(center,target),inToolRange=Vector2.Distance(center,target)<=MiningApproachProbe.ToolReachPixels});}
            for(int x=sx-7;x<=sx+7;x++)for(int y=sy-9;y<=sy+9;y++){Tile t=Main.tile[x,y];localCells.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsHalfBlock,t.Slope,t.IsActuated,t.LiquidAmount,t.LiquidType,solid=t.HasTile&&Main.tileSolid[t.TileType],solidTop=t.HasTile&&Main.tileSolidTop[t.TileType],platform=t.HasTile&&TileID.Sets.Platforms[t.TileType]});}
            reports.Add(new{x=sx,y=sy,tile=new{tile.HasTile,tile.TileType,tile.WallType,tile.TileFrameX,tile.TileFrameY,tile.IsHalfBlock,tile.Slope,tile.IsActuated},geometry=MiningAccessWorldObserver.Observe(npc,sx,sy),inspection=Executor.InspectMiningInteraction(npc,"stone",sx,sy),safeStands=stands,localCells});
        }
        var cells=new List<object>();for(int x=3296;x<=3323;x++)for(int y=386;y<=410;y++)
        {Tile t=Main.tile[x,y];cells.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsHalfBlock,t.Slope,t.IsActuated,t.LiquidAmount,t.LiquidType,solid=t.HasTile&&Main.tileSolid[t.TileType],solidTop=t.HasTile&&Main.tileSolidTop[t.TileType],platform=t.HasTile&&TileID.Sets.Platforms[t.TileType]});}
        string after=JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","long-goal-stone-readonly-result.json"),JsonSerializer.Serialize(new{status="observed",tick,finishedTick=Main.GameUpdateCount,sameTick=tick==Main.GameUpdateCount,snapshotEqual=before==after,requests=PrimaryDecisionRequestCount-primary,combatRequests=CombatDecisionRequestCount-combat,warps=Executor.NavigationTeleportCount,
            actor=new{x=npc.Center.X,y=npc.Center.Y,feet=npc.Bottom.Y},reports,cells,scope="Fresh original-house copy, one-time controlled setup matching v2 stone-blocked actor position; read-only native mining preview and stand geometry only. No mining, terrain edits, material injection, actor movement after setup, or API. Bounded seed checks and nearby cells do not establish global source availability or physical reachability."}));
        Stop();Executor.Clear(true);
    }
    private void BeginLongGoalGelNative()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear(true);Executor.ResetNavigationTelemetry();
        Loadout.Reset();Loadout.EnsureStarterTools();autoMode=false;CombatPlannerEnabled=false;
        gelProbePrimary=PrimaryDecisionRequestCount;gelProbeCombat=CombatDecisionRequestCount;gelProbeSequence=agentJournal.LastSequence;
        gelProbeAudit=ShelterWorldAudit.Capture(3220,390);gelProbeStarted=true;
        string gate=Path.Combine(Main.SavePath,"AITeammate","long-goal-gel-native-start.txt");if(File.Exists(gate))File.Delete(gate);
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","long-goal-gel-native-ready.txt"),"waiting_for_explicit_start");
    }
    private void TickLongGoalGelNative()
    {
        if(Main.gameMenu||CompanionNpc==null)return;NPC npc=CompanionNpc;Player player=Main.LocalPlayer;
        player.immune=true;player.immuneTime=600;
        if(!gelProbeSubmitted)
        {
            string gate=Path.Combine(Main.SavePath,"AITeammate","long-goal-gel-native-start.txt");if(!File.Exists(gate)||File.ReadAllText(gate).Trim()!="START_GEL")return;File.Delete(gate);
            npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;player.Center=Stand(3173,395);player.velocity=Vector2.Zero;
            foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
            gelProbePrevious=CaptureLongGoal(npc);
            var candidate=gelProbePrevious.Chests.Values.Where(c=>c.X>=3160&&c.X<=3270&&c.Y>=350&&c.Y<=420&&!c.Locked&&c.Gel>=2)
                .OrderBy(c=>Vector2.Distance(npc.Center,new Vector2(c.X*16+16,c.Y*16+16))).FirstOrDefault();
            if(candidate==null||gelProbePrevious.Gel!=0){FinishLongGoalGelNative("uncovered","no unlocked original-house chest with two gel, or actor initially has gel");return;}
            gelProbeChest=candidate.Index;gelProbeSubmitted=true;
            gelProbeEvents.Add(new{phase="controlled_setup",npc=new{x=npc.Center.X,y=npc.Center.Y,feet=npc.Bottom.Y},player=new{x=player.Center.X,y=player.Center.Y,feet=player.Bottom.Y},chest=candidate,inventory=gelProbePrevious.Inventory,scope="observed chest selected by fixture; no materials supplied"});
            Executor.Apply(new AIPlan{Action=AIActionType.Withdraw,Item=ItemID.Gel.ToString(),Count=2,X=candidate.X,Y=candidate.Y});return;
        }
        gelProbeFrames++;foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        LongGoalSnapshot now=CaptureLongGoal(npc);var outcomes=agentJournal.Outcomes.Where(o=>o.Sequence>gelProbeSequence).OrderBy(o=>o.Sequence).ToArray();
        if(outcomes.Length>0)
        {
            LongGoalSnapshot before=gelProbePrevious??now;var outcome=outcomes[0];
            bool hasBefore=before.Chests.TryGetValue(gelProbeChest,out var chestBefore),hasAfter=now.Chests.TryGetValue(gelProbeChest,out var chestAfter);
            bool stableOtherChests=before.Chests.Count==now.Chests.Count&&before.Chests.Where(c=>c.Key!=gelProbeChest).All(c=>now.Chests.TryGetValue(c.Key,out var a)&&c.Value.Gel==a.Gel&&c.Value.OtherContents==a.OtherContents);
            bool nonGelInventoryStable=now.Inventory.Keys.Union(before.Inventory.Keys).Where(id=>id!=ItemID.Gel).All(id=>now.Inventory.GetValueOrDefault(id)==before.Inventory.GetValueOrDefault(id));
            float? distance=hasBefore?Vector2.Distance(before.Actor,new Vector2(chestBefore!.X*16+16,chestBefore.Y*16+16)):null;
            bool onlyGel=hasBefore&&hasAfter&&chestBefore!.OtherContents==chestAfter!.OtherContents;
            bool sameChest=hasBefore&&hasAfter&&chestBefore!.X==chestAfter!.X&&chestBefore.Y==chestAfter.Y&&!chestBefore.Locked;
            bool physical=distance.HasValue&&distance.Value<=80f;
            bool exact=hasBefore&&hasAfter&&chestBefore!.Gel-chestAfter!.Gel==2&&now.Gel-before.Gel==2;
            var preservation=gelProbeAudit?.CheckPreservation();
            bool pass=outcomes.Length==1&&outcome.Task.Action==AIActionType.Withdraw&&outcome.Status=="succeeded"&&sameChest&&physical&&exact&&onlyGel&&nonGelInventoryStable&&stableOtherChests&&preservation is {preserved:true,coverageComplete:true}&&PrimaryDecisionRequestCount==gelProbePrimary&&CombatDecisionRequestCount==gelProbeCombat&&Executor.NavigationTeleportCount==0;
            gelProbeEvents.Add(new{phase="withdraw_outcome",outcome,beforeSlots=chestBefore?.Slots,afterSlots=chestAfter?.Slots,beforeGel=chestBefore?.Gel,afterGel=chestAfter?.Gel,actorBeforeGel=before.Gel,actorAfterGel=now.Gel,distance,onlyGel,nonGelInventoryStable,stableOtherChests,sameChest,physical,exact,preservation});
            gelProbePrevious=now;FinishLongGoalGelNative(pass?"passed":"failed",pass?"":"gel transfer or preservation evidence incomplete");return;
        }
        gelProbePrevious=now;
        if(PrimaryDecisionRequestCount!=gelProbePrimary||CombatDecisionRequestCount!=gelProbeCombat||Executor.NavigationTeleportCount!=0||gelProbeFrames>=900)
            FinishLongGoalGelNative("failed","unexpected API, warp, or 900-frame deadline");
    }
    private void FinishLongGoalGelNative(string status,string error)
    {
        gelProbeFinished=true;File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","long-goal-gel-native-result.json"),JsonSerializer.Serialize(new{status,error,frames=gelProbeFrames,requests=PrimaryDecisionRequestCount-gelProbePrimary,combatRequests=CombatDecisionRequestCount-gelProbeCombat,warps=Executor.NavigationTeleportCount,events=gelProbeEvents,
            scope="Independent zero-API controlled Withdraw of two gel from an existing unlocked original-house chest. Full slot arrays come from the previous and current actual per-frame snapshots, not reconstructed from after state. No material injection, no mining, no autonomous goal or return claim; hostiles removed and player protected."}));Stop();Executor.Clear(true);
    }
    private void FinishLongGoalAcceptance(string status,string error){longGoalFinished=true;if(longGoalSubmitted){Main.hideUI=longGoalPriorHideUI;Main.GameZoomTarget=longGoalPriorZoom;}longGoalSourceEvidenceIncomplete=!LongGoalSourcesProven();LongGoalDropWitness.Active=false;longGoalPreservation=longGoalAudit?.CheckPreservation();SaveLongGoalAcceptance(status,error);Stop();Executor.Clear(true);}
    private void SaveLongGoalAcceptance(string status,string error)
    {
        string path=LongGoalFile("result.json"),temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(new{
            status,error,packageSha256=longGoalPackageHash,sourceAttributionIncomplete=longGoalSourceEvidenceIncomplete,preservation=longGoalPreservation,auditBaseline=longGoalAudit?.BaselineReport,request=longGoalRequest,startedUtc=longGoalAt,startedTick=longGoalStartedTick,updatedUtc=DateTime.UtcNow,frames=longGoalFrames,requests=PrimaryDecisionRequestCount-longGoalRequestStart,combatRequests=CombatDecisionRequestCount-longGoalCombatStart,requestLimit=LongGoalLimit,teleports=Executor.NavigationTeleportCount,
            replay=ClosureReplay?new{sha256=longGoalReplayHash,decisions=longGoalReplay.Length,replayed=longGoalReplayIndex,suffixLive=longGoalReplayLive}:null,
            goalStatus,reply=LastReply,lastCompletedGoal,planning=JsonSerializer.SerializeToElement(PlanningContext),goalReviewPending,executorIsBusy=Executor.IsBusy,currentPlan=ContinuousField("currentPlan"),primaryInFlight=primaryCancellation!=null,remainingTasks=Executor.SnapshotTasks(),finalSnapshot=longGoalPrevious==null?null:LongGoalSnapshotView(longGoalPrevious),
            longGoalMineWood,longGoalMineStone,longGoalGelWithdrawn,longGoalWoodConsumed,longGoalStoneConsumed,longGoalGelConsumed,longGoalChairProduced,longGoalWallProduced,longGoalTorchProduced,longGoalPlatformProduced,longGoalChairCraft,longGoalWallCraft,longGoalTorchCraft,longGoalPlatformCraft,longGoalCraftCompletedFrame,longGoalCraftCompletedTick,longGoalReturned,longGoalPlacedNavigationCells,longGoalEvidenceAmbiguous,longGoalConstraintViolation,removedMineTiles=longGoalRemovedMineTiles.Select(p=>new{x=p.x,y=p.y}).ToArray(),dropSpawns=LongGoalDropWitness.Spawns,events=longGoalEvents,trace=longGoalTrace,
            scope="Fresh original-house copy, starter tools only, one actor setup, no injected materials or mid-case reposition; test camera follows companion at 1.6 zoom with UI hidden and restored on finish; initial loose wood/stone removed with before records, hostiles removed/player protected. GlobalItem records actual source class/tile/action for all new wood/stone drops. Provenance requires only Mine-time TileBreak sources matching untruncated actual removed tiles and sufficient native spawn counts; any unknown source or unmatched tile prevents pass. Controlled native case uses a fixed primitive queue, not autonomous planning. Chest and station observations are audit-only, never supplied as fixture answers. Multiple inventory-mutating outcomes in one tick are ambiguous. NearbyStations is a native ±5×4 tile proximity test without line-of-sight or room-entry proof; bounded preservation coverageComplete is required. Public message content and usage are in the isolated DebugLog; no key or hidden reasoning is persisted by this fixture."
        },new JsonSerializerOptions{WriteIndented=false}));File.Move(temp,path,true);
    }
}

