using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private bool housePlanningStarted, housePlanningFinished;
    private DateTime housePlanningAt;
    private int housePlanningRequestStart;
    private StructureReading? houseNativeReference;
    private bool houseSmallBuildStarted, houseSmallBuildFinished, housePlanningPassed;
    private int houseSmallBuildX, houseSmallBuildY, houseSmallBuildRequestStart;
    private DateTime houseSmallBuildAt;
    private readonly Dictionary<(int x,int y),(bool has,int type,int wall)> houseSmallBuildBefore=new();
    private readonly Dictionary<(int x,int y),(bool has,int type,int wall)> houseSmallBuildBeforePlace=new();
    private readonly Dictionary<(int x,int y),(bool has,int type,int wall)> houseSmallBuildAfterPlace=new();
    private DateTime houseSmallBuildPlaceAt;
    private DateTime houseSmallBuildAfterPlaceAt;
    private bool houseSurveyOnlyStarted,houseSurveyOnlyFinished;
    private DateTime houseSurveyOnlyAt;
    private int houseSurveyOnlyRequests,houseSurveyOnlyActiveFrames;
    private string houseSurveyOnlyRecordError="";
    private StructureSurvey? houseSurveyOnlyNative;
    private readonly Dictionary<(int x,int y),(bool has,int tile,int wall,int tilePaint,int wallPaint)> houseSurveyOnlyBefore=new();
    private bool HouseDesignSurvey=>Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_DESIGN_SURVEY")=="1";
    private int HouseSurveyRequestLimit
    {
        get {int cap=HouseDesignSurvey?12:10;return int.TryParse(Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_SURVEY_LIMIT"),out int requested)?Math.Clamp(requested,1,cap):cap;}
    }
    private bool HouseSurveyOnlyAllowed=>houseSurveyOnlyStarted && !houseSurveyOnlyFinished &&
        PrimaryDecisionRequestCount-houseSurveyOnlyRequests<HouseSurveyRequestLimit &&
        DateTime.UtcNow-houseSurveyOnlyAt<TimeSpan.FromSeconds(HouseDesignSurvey?240:120);
    private bool HousePlanningAllowed => HouseMode && (
        housePlanningStarted && !housePlanningFinished && PrimaryDecisionRequestCount-housePlanningRequestStart<10 && DateTime.UtcNow-housePlanningAt<TimeSpan.FromSeconds(90) ||
        houseSmallBuildStarted && !houseSmallBuildFinished && PrimaryDecisionRequestCount-houseSmallBuildRequestStart<8 && DateTime.UtcNow-houseSmallBuildAt<TimeSpan.FromSeconds(90) ||
        ContinuousLlmAllowed || HouseSurveyOnlyAllowed || BasicPlanningAllowed || AutonomousResourcePlanningAllowed || AdvancedPlanningAllowed || LongGoalPlanningAllowed || ShelterPlanningAllowed || NaturalFlashCanPlan);

    private void BeginHouseSurveyOnly()
    {
        // Fixture-only serialization smoke before a paid request.
        string smoke=JsonSerializer.Serialize(new{status="smoke",activeFrames=0,requests=0,
            actor=new{pixelX=0f,pixelY=0f,vx=0f,vy=0f},worldChanges=Array.Empty<object>()});
        using(JsonDocument.Parse(smoke)) { }
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();
        autoMode=CombatPlannerEnabled=false;
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();Executor.ResetNavigationTelemetry();
        var sample=typeof(StructureWorldObserver).GetMethod("Sample",BindingFlags.Static|BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Native structure sampler unavailable.");
        houseSurveyOnlyNative=(StructureSurvey?)sample.Invoke(null,new object[]{3220,397})
            ?? throw new InvalidOperationException("Native reference unavailable.");
        for(int x=3140;x<=3290;x++)for(int y=350;y<=425;y++)
        {Tile t=Main.tile[x,y];houseSurveyOnlyBefore[(x,y)]=(t.HasTile,t.HasTile?t.TileType:-1,t.WallType,t.TileColor,t.WallColor);}
        houseSurveyOnlyAt=DateTime.UtcNow;houseSurveyOnlyRequests=PrimaryDecisionRequestCount;houseSurveyOnlyStarted=true;
        string request="请按这座房屋原有样式，在实测主楼全宽加完整一层，保留现有屋顶房间和家具，不是只给屋顶小房加间。加盖形式、必要临时支撑、工位和施工顺序由你自主选择。先只读勘察并设计，精确核算材料和缺口，自行预检并根据反馈修订；不要真实施工、拆除或采集。常规方案选择由你决定，只有确实无法自行解决的外部依赖才等待我；不能核实的事实请如实说明。";
        HandlePlayerMessage(p,request);
        SaveHouseSurveyOnly("running","");
    }
    private void TickHouseSurveyOnly()
    {
        if(!houseSurveyOnlyStarted || houseSurveyOnlyFinished || Main.gameMenu)return;
        houseSurveyOnlyActiveFrames++;
        Player player=Main.LocalPlayer;
        player.immune=true;player.immuneTime=600;player.statLife=player.statLifeMax2;
        foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(decisionFailures>0)
        {FinishHouseSurveyOnly("failed","A model request failed; test stopped before automatic retry.");return;}
        if(Main.GameUpdateCount%60==0)
        {
            try {SaveHouseSurveyOnly("running","");}
            catch(Exception e){houseSurveyOnlyRecordError=e.GetType().Name+": "+e.Message;}
        }
        if(goalWaitingForPlayer || goalStatus=="completed")
        {
            bool queried=agentJournal.Outcomes.Any(o=>o.Status=="succeeded" && o.Task.Action==AIActionType.Inspect &&
                o.Task.Target is "structure" or "house");
            FinishHouseSurveyOnly(queried && goalStatus!="completed"?"ready_for_manual_review":"failed",
                queried?"":"No successful model-initiated structure query.");
        }
        else if(DateTime.UtcNow-houseSurveyOnlyAt>=TimeSpan.FromSeconds(HouseDesignSurvey?240:120))
            FinishHouseSurveyOnly("failed","Survey wall-time limit reached.");
        else if(PrimaryDecisionRequestCount-houseSurveyOnlyRequests>=HouseSurveyRequestLimit && primaryCancellation==null && !Executor.IsBusy)
            FinishHouseSurveyOnly("budget_exhausted","Request limit reached after the final response and its tasks finished.");
    }
    private void FinishHouseSurveyOnly(string status,string error)
    {
        houseSurveyOnlyFinished=true;
        try {SaveHouseSurveyOnly(status,error);}
        catch(Exception e){houseSurveyOnlyRecordError=e.GetType().Name+": "+e.Message;TestLog("survey recording failed: "+houseSurveyOnlyRecordError);}
        finally {Stop();}
        TestLog($"house survey only {status}: {error}");
    }
    private void SaveHouseSurveyOnly(string status,string error)
    {
        var changes=new List<object>();
        foreach(var entry in houseSurveyOnlyBefore)
        {
            Tile t=Main.tile[entry.Key.x,entry.Key.y];var v=entry.Value;
            if(t.HasTile!=v.has || (t.HasTile && t.TileType!=v.tile) || t.WallType!=v.wall ||
                t.TileColor!=v.tilePaint || t.WallColor!=v.wallPaint)
                changes.Add(new{x=entry.Key.x,y=entry.Key.y,beforeTile=v.tile,afterTile=t.HasTile?(int)t.TileType:-1,
                    beforeWall=v.wall,afterWall=(int)t.WallType,beforeTilePaint=v.tilePaint,afterTilePaint=(int)t.TileColor,
                    beforeWallPaint=v.wallPaint,afterWallPaint=(int)t.WallColor});
        }
        var designReviews=agentJournal.Outcomes.Where(o=>o.Task.Action==AIActionType.Inspect && o.Task.Target=="design").Select(o=>new {
            sequence=o.Sequence,status=o.Status,
            designSha256=o.Task.Design.HasValue?Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(o.Task.Design.Value.GetRawText()))):"",
            design=o.Task.Design,result=o.Result}).ToArray();
        var runtimeWorkingState=new { sourcePlayerRequest=goalPlanningState.SourcePlayerRequest, latestDesign=goalPlanningState.LatestDesign };
        var settings=GetLlmSettings();
        var compact=PlanningRequest.CompactState(JsonSerializer.Serialize(new { planning=PlanningContext }),
            usePhysicsStructureView:settings?.UsePhysicsStructureView??false,useStructureOverview:settings?.UseStructureOverview??false);
        var wirePlanning=compact["planning"];
        long? reviewSequence=goalPlanningState.LatestDesign?.reviewSequence;
        var wireWorkingState=new {
            sourcePlayerRequest=wirePlanning?["sourcePlayerRequest"]?.DeepClone(),
            latestDesign=wirePlanning?["latestDesign"]?.DeepClone(),
            reviewOutcome=(wirePlanning?["outcomes"] as System.Text.Json.Nodes.JsonArray)?
                .OfType<System.Text.Json.Nodes.JsonObject>()
                .Where(o=>reviewSequence.HasValue && o["sequence"]?.GetValue<long>()==reviewSequence.Value)
                .Select(o=>new { sequence=o["sequence"]?.DeepClone(), result=o["result"]?.DeepClone() }).FirstOrDefault()
        };
        var payload=new{status,error,recordingError=houseSurveyOnlyRecordError,world=Main.worldName,worldId=Main.worldID,
            startedUtc=houseSurveyOnlyAt,updatedUtc=DateTime.UtcNow,activeFrames=houseSurveyOnlyActiveFrames,
            requests=PrimaryDecisionRequestCount-houseSurveyOnlyRequests,nativeReference=houseSurveyOnlyNative,
            modelReply=LastReply,goalStatus,decisionFailures,outcomes=agentJournal.Outcomes,designReviews,runtimeWorkingState,wireWorkingState,teleports=Executor.NavigationTeleportCount,
            worldChanges=changes,scope="Planning acceptance with hostile NPCs removed/player protected; read-only model survey for manual review; no construction success claim."};
        string json=JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true});
        using(JsonDocument.Parse(json)) { }
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-survey-only-result.json"),json);
    }

    private void RunHouseNativeSurveyAudit()
    {
        var sample=typeof(StructureWorldObserver).GetMethod("Sample",BindingFlags.Static|BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Native structure sampler unavailable.");
        var survey=(StructureSurvey?)sample.Invoke(null,new object[]{3220,397})
            ?? throw new InvalidOperationException("Native structure sampler returned no result.");
        var b=survey.bounds ?? throw new InvalidOperationException("No main-house bounds in native survey.");
        var houseTiles=new Dictionary<(int type,int paint),int>();
        var houseWalls=new Dictionary<(int type,int paint),int>();
        var roofCells=new List<object>();
        for(int x=b.x;x<b.x+b.width;x++)
            for(int y=Math.Max(2,b.y-12);y<b.y+b.height;y++)
            {
                Tile tile=Main.tile[x,y];
                if(y>=b.y && tile.HasTile)
                {
                    var key=((int)tile.TileType,(int)tile.TileColor);
                    houseTiles[key]=houseTiles.GetValueOrDefault(key)+1;
                }
                if(y>=b.y && tile.WallType>WallID.None)
                {
                    var key=((int)tile.WallType,(int)tile.WallColor);
                    houseWalls[key]=houseWalls.GetValueOrDefault(key)+1;
                }
                if(y<b.y && (tile.HasTile || tile.WallType>WallID.None))
                    roofCells.Add(new{x,y,tile=tile.HasTile?(int)tile.TileType:-1,wall=(int)tile.WallType,
                        tileColor=(int)tile.TileColor,wallColor=(int)tile.WallColor,slope=(int)tile.Slope,half=tile.IsHalfBlock});
            }
        var payload=new{status="passed",world=Main.worldName,worldId=Main.worldID,observedUtc=DateTime.UtcNow,
            bounds=b,observed=survey,houseTilePaint=houseTiles.OrderByDescending(kv=>kv.Value).Select(kv=>new{type=kv.Key.type,paint=kv.Key.paint,count=kv.Value}),
            houseWallPaint=houseWalls.OrderByDescending(kv=>kv.Value).Select(kv=>new{type=kv.Key.type,paint=kv.Key.paint,count=kv.Value}),
            roofScan=new{left=b.x,right=b.x+b.width-1,top=b.y-12,bottom=b.y-1,cells=roofCells},
            scope="Native reference only; no model query or world mutation; tile colors are paint IDs."};
        string json=JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true});
        using var smoke=JsonDocument.Parse(json);
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-native-survey-audit.json"),json);
        TestLog("house native survey audit completed");houseFinished=true;Stop();
    }
    private void RunHouseDesignNativeAcceptance()
    {
        NPC npc=CompanionNpc!;Player player=Main.LocalPlayer;
        npc.Center=Stand(3190,381);npc.velocity=Vector2.Zero;player.Center=Stand(3173,395);player.velocity=Vector2.Zero;
        var initialNpc=npc.Center;var initialPlayer=player.Center;
        var inventoryBefore=Loadout.Inventory.Select(item=>(item.type,item.stack,item.prefix)).ToArray();
        var before=new Dictionary<(int x,int y),(bool has,int tile,int wall,int slope,bool half,int tilePaint,int wallPaint)>();
        for(int x=3160;x<=3270;x++)for(int y=350;y<=420;y++)
        {Tile t=Main.tile[x,y];before[(x,y)]=(t.HasTile,t.HasTile?t.TileType:-1,t.WallType,(int)t.Slope,t.IsHalfBlock,t.TileColor,t.WallColor);}
        var invalidExample=new{bounds=new[]{3191,364,53,8},
            clearSpaces=new[]{new[]{3192,365,51,5}},
            placements=new[]{new[]{ItemID.Wood,3191,371,53,1,3190,381},new[]{ItemID.Wood,3191,364,27,7,3190,381}}};
        var dedupExample=new{bounds=new[]{3192,380,1,1},clearSpaces=Array.Empty<int[]>(),
            placements=new[]{new[]{ItemID.Wood,3192,380,1,1,3190,381},new[]{ItemID.Wood,3192,380,1,1,3190,381}}};
        var platformSingle=new{bounds=new[]{3192,380,1,1},clearSpaces=Array.Empty<int[]>(),
            placements=new[]{new[]{94,3192,380,1,1,3190,381}}};
        var platformArea=new{bounds=new[]{3192,380,2,1},clearSpaces=Array.Empty<int[]>(),
            placements=new[]{new[]{94,3192,380,2,1,3190,381}}};
        using var malformed=JsonDocument.Parse("{\"bounds\":\"invalid\",\"placements\":[]}");
        using var invalidResponse=JsonDocument.Parse(AgentKnowledge.Query("","design",npc,player,Executor,
            design:JsonSerializer.SerializeToElement(invalidExample)));
        using var dedupResponse=JsonDocument.Parse(AgentKnowledge.Query("","design",npc,player,Executor,
            design:JsonSerializer.SerializeToElement(dedupExample)));
        using var malformedResponse=JsonDocument.Parse(AgentKnowledge.Query("","design",npc,player,Executor,design:malformed.RootElement));
        using var platformSingleResponse=JsonDocument.Parse(AgentKnowledge.Query("","design",npc,player,Executor,
            design:JsonSerializer.SerializeToElement(platformSingle)));
        using var platformAreaResponse=JsonDocument.Parse(AgentKnowledge.Query("","design",npc,player,Executor,
            design:JsonSerializer.SerializeToElement(platformArea)));
        var catalog=PlacementCatalogWorldObserver.Read(3191,380,53,23);
        var changes=new List<object>();
        foreach(var pair in before)
        {
            Tile t=Main.tile[pair.Key.x,pair.Key.y];var v=pair.Value;
            if(t.HasTile!=v.has || (t.HasTile && t.TileType!=v.tile) || t.WallType!=v.wall ||
                (int)t.Slope!=v.slope || t.IsHalfBlock!=v.half || t.TileColor!=v.tilePaint || t.WallColor!=v.wallPaint)
                changes.Add(new{x=pair.Key.x,y=pair.Key.y,beforeTile=v.tile,afterTile=t.HasTile?(int)t.TileType:-1});
        }
        bool inventorySame=inventoryBefore.SequenceEqual(Loadout.Inventory.Select(item=>(item.type,item.stack,item.prefix)));
        bool actorsSame=npc.Center==initialNpc && player.Center==initialPlayer;
        bool noMutation=changes.Count==0 && inventorySame && actorsSame;
        Item nativePlatform=Terraria.ID.ContentSamples.ItemsByType[94];
        var platformMetadata=ConstructionDesignWorldObserver.ReadItem(94);
        var platformObject=Terraria.ObjectData.TileObjectData.GetTileData(nativePlatform.createTile,nativePlatform.placeStyle);
        var woodCatalog=catalog.tileTypes.Where(t=>t.tileType==30).ToArray();
        var stoneWallCatalog=catalog.wallTypes.Where(w=>w.wallType==1).ToArray();
        Item itemThirty=Terraria.ID.ContentSamples.ItemsByType[30];
        bool woodCandidate=woodCatalog.Any(t=>t.styleKnown && t.placementCandidates.Any(c=>c.itemId==9)) &&
            woodCatalog.All(t=>t.placementCandidates.All(c=>c.itemId!=30));
        bool stoneWallCandidate=stoneWallCatalog.Any(w=>w.placementCandidates.Any(c=>c.itemId==26));
        bool itemThirtyIsDirtWall=itemThirty.createWall==16;
        var payload=new{status=noMutation?"ready_for_manual_review":"failed",world=Main.worldName,worldId=Main.worldID,
            nativeWoodItem=new{itemId=ItemID.Wood,tileType=(int)Terraria.ID.ContentSamples.ItemsByType[ItemID.Wood].createTile,
                expectedTileType=TileID.WoodBlock},
            nativePlatform=new{itemId=94,tileType=nativePlatform.createTile,placeStyle=nativePlatform.placeStyle,
                expectedTileType=TileID.Platforms,nativeSolid=Main.tileSolid[nativePlatform.createTile],
                nativeSolidTop=Main.tileSolidTop[nativePlatform.createTile],nativePlatform=TileID.Sets.Platforms[nativePlatform.createTile],
                nativeObject=platformObject==null?null:new{width=platformObject.Width,height=platformObject.Height,originX=platformObject.Origin.X,originY=platformObject.Origin.Y},
                reviewerMetadata=platformMetadata},
            catalogAudit=new{woodCandidate,stoneWallCandidate,itemThirtyIsDirtWall,
                nativeItem30=new{itemId=30,createTile=itemThirty.createTile,createWall=itemThirty.createWall},
                woodEntries=woodCatalog,stoneWallEntries=stoneWallCatalog},
            invalidDesign=invalidExample,invalidReview=invalidResponse.RootElement,
            dedupDesign=dedupExample,dedupReview=dedupResponse.RootElement,malformedReview=malformedResponse.RootElement,
            platformSingleDesign=platformSingle,platformSingleReview=platformSingleResponse.RootElement,
            platformAreaDesign=platformArea,platformAreaReview=platformAreaResponse.RootElement,
            worldChanges=changes,inventorySame,actorsSame,
            scope="Read-only native static preflight fixture; no world placement, travel, or housing validation."};
        string json=JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true});
        using(JsonDocument.Parse(json)) { }
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-design-native-result.json"),json);
        TestLog($"house design native ready, unchanged={noMutation}");houseFinished=true;Stop();
    }

    private bool ValidateHousePlanningPlan(AIPlan plan)
    {
        if (NaturalFlashSelected) return ValidateNaturalFlashPlan(plan);
        if (LongGoalClosurePlanActive) return ValidateLongGoalClosurePlan(plan);
        if(HouseMode && shelterAcceptanceStarted && !shelterAcceptanceFinished)return true;
        if(HouseMode && advancedStarted && !advancedFinished)return true;
        if(HouseMode && autonomousResourceStarted && !autonomousResourceFinished)return true;
        if(HouseMode && basicStarted && !basicFinished && basicNatural)
        {
            bool Allowed(AIPlan t)=>t.Action is AIActionType.None or AIActionType.Inspect or AIActionType.Explore or AIActionType.Come or AIActionType.Follow or AIActionType.Wait or AIActionType.Mine or AIActionType.Craft or AIActionType.Place;
            if(Allowed(plan) && plan.Steps.All(Allowed))return true;
            FinishBasicAcceptance("failed","Model attempted action outside bounded basic test.");return false;
        }
        if(HouseMode && houseSurveyOnlyStarted && !houseSurveyOnlyFinished)
        {
            bool Allowed(AIPlan t)=>t.Action is AIActionType.None or AIActionType.Inspect or AIActionType.Wait;
            if(Allowed(plan) && plan.Steps.All(Allowed))return true;
            FinishHouseSurveyOnly("failed","Model attempted an action outside the read-only survey.");return false;
        }
        if(HouseMode && continuousLlmStarted && !continuousLlmFinished)
        {
            if(NaturalTravelCase)
            {
                bool TravelAllowed(AIPlan t)=>t.Action is AIActionType.None or AIActionType.Inspect or AIActionType.Explore or AIActionType.Come or AIActionType.Follow or AIActionType.Wait;
                if(TravelAllowed(plan) && plan.Steps.All(TravelAllowed))return true;
                FinishContinuousLlm(false,"Travel case rejected a world-modifying or unsupported action.");return false;
            }
            if(AutonomousMaterialsCase)return true;
            bool Allowed(AIPlan t)=>t.Action is AIActionType.None or AIActionType.Inspect or AIActionType.Mine or
                AIActionType.Follow or AIActionType.Come or AIActionType.Wait or AIActionType.Pickup or
                AIActionType.Explore or AIActionType.Craft or AIActionType.Place;
            if(Allowed(plan) && plan.Steps.All(Allowed))return true;
            FinishContinuousLlm(false,"Rejected action outside bounded collection and return.");return false;
        }
        if(HouseMode && houseSmallBuildStarted && !houseSmallBuildFinished)
        {
            bool siteDone=agentJournal.Outcomes.Any(IsSafeSmallBuildSite);
            bool Allowed(AIPlan t) => t.Action is AIActionType.None or AIActionType.Wait ||
                t.Action==AIActionType.Inspect && t.Target=="site" ||
                siteDone && t.Action==AIActionType.Place && t.X==houseSmallBuildX && t.Y==houseSmallBuildY && t.Width==6 && t.Height==1;
            if(Allowed(plan) && plan.Steps.All(Allowed))
            {
                if(houseSmallBuildBeforePlace.Count==0 && (plan.Action==AIActionType.Place || plan.Steps.Any(t=>t.Action==AIActionType.Place)))
                {
                    houseSmallBuildPlaceAt=DateTime.UtcNow;
                    foreach(var kv in houseSmallBuildBefore)
                    { Tile tile=Main.tile[kv.Key.x,kv.Key.y];houseSmallBuildBeforePlace[kv.Key]=(tile.HasTile,tile.HasTile?tile.TileType:-1,tile.WallType); }
                }
                return true;
            }
            File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-small-build-result.json"),JsonSerializer.Serialize(new {
                status="failed",error="Rejected unsafe or unverified action.",x=houseSmallBuildX,y=houseSmallBuildY,
                remainingWood=Loadout.CountItem(ItemID.Wood),outcomes=agentJournal.Outcomes,attempted=plan},new JsonSerializerOptions{WriteIndented=true}));
            houseSmallBuildFinished=true;Stop();TestLog("house small build rejected unsafe or unverified action");return false;
        }
        if(!HouseMode || !housePlanningStarted || housePlanningFinished) return true;
        if(plan.Action is AIActionType.None or AIActionType.Inspect or AIActionType.Wait &&
            plan.Steps.All(t=>t.Action is AIActionType.Inspect or AIActionType.Wait))return true;
        FinishHousePlanning(false,"Model attempted a world mutation during an explicitly read-only survey phase.");
        return false;
    }
    private void TickHousePlanningAcceptance()
    {
        if(HouseMode && Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_SURVEY_ONLY")=="1")
        {TickHouseSurveyOnly();return;}
        if(!HouseMode || Main.gameMenu || Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_PLANNING")!="1")return;
        if(housePlanningFinished) { if(housePlanningPassed) TickHouseSmallBuild(); return; }
        if(!housePlanningStarted)
        {
            if(!houseFinished || houseCases.Any(c=>c.Status!="passed")) return;
            Stop();Memory.Clear();
            Memory.Remember("旧记忆：原屋10x7，屋顶加盖已完成（验收注入的错误历史推断，必须以当前观察为准）。");
            LlmConversationStore.ResetAll();agentJournal.Clear();
            housePlanningStarted=true;housePlanningAt=DateTime.UtcNow;housePlanningRequestStart=PrimaryDecisionRequestCount;
            var sample=typeof(StructureWorldObserver).GetMethod("Sample",BindingFlags.Static|BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Native structure sampler unavailable.");
            var survey=(StructureSurvey?)sample.Invoke(null,new object[]{3220,397})
                ?? throw new InvalidOperationException("Native structure sampler returned no survey.");
            houseNativeReference=new StructureReading("structure",true,"",Main.worldID,Main.GameUpdateCount,survey,null);
            HandlePlayerMessage(Main.LocalPlayer,"请按下面这座多层主屋的样式加盖一层。先进行只读勘察和规划：用structure读取原屋，明确说明实际范围和宽度格数、层高、门和平台通路、屋顶已有小房对扩建的影响。再用inspect target=site、item=\"3191,376,53,6\"核查主屋顶附近的占用，并依据结果给出拟施工位置、材料和步骤；如果未能确认安全位置就说明无法确定，不要把占用空间当作空地。这个阶段不要施工、拆除或采集；如果当前能力无法安全完成整层加盖，如实说明限制并明确等待，不得拿默认10x7模板当原屋，也不能说已经建好。");
            SaveHousePlanning("running","");
            return;
        }
        if(housePlanningFinished)return;
        bool queried=agentJournal.Outcomes.Any(IsActualHouseQuery) && agentJournal.Outcomes.Any(IsOccupiedRoofQuery);
        if(goalWaitingForPlayer)
        {
            var b=houseNativeReference?.observation?.bounds;
            bool measured=houseNativeReference is {available:true,observation.found:true} && b!=null &&
                b.width>10 && LastReply.Contains(b.width.ToString(),StringComparison.Ordinal);
            FinishHousePlanning(queried && measured && goalStatus!="completed",queried&&measured?"":"Missing actual structure query or measured main-house width in the model report.");
        }
        else if(!HousePlanningAllowed)FinishHousePlanning(false,"Bounded real-LLM survey did not reach an honest planning checkpoint.");
        else if(Main.GameUpdateCount%60==0)SaveHousePlanning("running","");
    }
    private bool IsOccupiedRoofQuery(AgentOutcome outcome)
    {
        if(outcome.Status!="succeeded" || outcome.Task.Action!=AIActionType.Inspect || outcome.Task.Target!="site")return false;
        try
        {
            using var document=JsonDocument.Parse(outcome.Result);var root=document.RootElement;
            var m=root.GetProperty("measurement");var b=m.GetProperty("bounds");
            return root.GetProperty("available").GetBoolean() && root.GetProperty("worldId").GetInt32()==Main.worldID &&
                b.GetProperty("x").GetInt32()==3191 && b.GetProperty("y").GetInt32()==376 &&
                b.GetProperty("width").GetInt32()==53 && b.GetProperty("height").GetInt32()==6 &&
                m.GetProperty("occupiedTileCells").GetInt32()>0;
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }
    private void TickHouseSmallBuild()
    {
        if(houseSmallBuildFinished)return;
        if(!houseSmallBuildStarted)
        {
            // This is a bounded fixture setup, not generated terrain or model-authored construction.
            int? start=Enumerable.Range(3192,18).Where(x=>Enumerable.Range(x,6).All(tx=>
                !Main.tile[tx,380].HasTile && Main.tile[tx,381].HasTile &&
                Main.tile[tx,381].TileType==TileID.WoodBlock && !Main.tile[tx,381].IsActuated)).Cast<int?>().FirstOrDefault();
            if(!start.HasValue || CompanionNpc==null || !ClearBody(3190,381))
            {
                houseSmallBuildFinished=true;
                File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-small-build-result.json"),
                    JsonSerializer.Serialize(new {status="failed_setup",error="No verified six-cell roof run or clear actor setup."}));
                TestLog("house small build unavailable: no verified six-cell roof run or actor setup"); return;
            }
            houseSmallBuildX=start.Value;houseSmallBuildY=380;
            Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();
            CompanionNpc.Center=Stand(3190,381);CompanionNpc.velocity=Vector2.Zero;
            Loadout.Reset();Loadout.EnsureStarterTools();
            Item first=Loadout.Inventory.First(i=>i.IsAir);first.SetDefaults(ItemID.Wood);first.stack=2;
            Item second=Loadout.Inventory.First(i=>i.IsAir);second.SetDefaults(ItemID.Wood);second.stack=4;
            for(int x=3160;x<=3270;x++)for(int y=350;y<=420;y++)
            { Tile t=Main.tile[x,y];houseSmallBuildBefore[(x,y)]=(t.HasTile,t.HasTile?t.TileType:-1,t.WallType); }
            houseSmallBuildStarted=true;houseSmallBuildAt=DateTime.UtcNow;houseSmallBuildRequestStart=PrimaryDecisionRequestCount;
            HandlePlayerMessage(Main.LocalPlayer,$"这是独立的小范围施工验证，不是整层扩建。先用inspect target=site、item=\"{houseSmallBuildX},{houseSmallBuildY},6,1;tile=30\"查询该范围的占用和支撑；如果安全，用我给你的6个木材在该范围放置一行普通木块，然后核对实际结果。不得修改其他地方。若无法安全执行，请如实等待。");
            return;
        }
        if(houseSmallBuildAfterPlace.Count==0 && agentJournal.Outcomes.Any(o=>o.Status=="succeeded" && o.Task.Action==AIActionType.Place))
        {
            houseSmallBuildAfterPlaceAt=DateTime.UtcNow;
            foreach(var kv in houseSmallBuildBefore)
            { Tile tile=Main.tile[kv.Key.x,kv.Key.y];houseSmallBuildAfterPlace[kv.Key]=(tile.HasTile,tile.HasTile?tile.TileType:-1,tile.WallType); }
        }
        if(goalWaitingForPlayer || goalStatus=="completed" || DateTime.UtcNow-houseSmallBuildAt>TimeSpan.FromSeconds(90) || PrimaryDecisionRequestCount-houseSmallBuildRequestStart>=8)
        {
            int placed=Enumerable.Range(houseSmallBuildX,6).Count(x=>Main.tile[x,houseSmallBuildY].HasTile && Main.tile[x,houseSmallBuildY].TileType==TileID.WoodBlock);
            var outsideChanges=houseSmallBuildBefore.Where(kv=>
            {
                if(kv.Key.y==houseSmallBuildY && kv.Key.x>=houseSmallBuildX && kv.Key.x<houseSmallBuildX+6)return false;
                Tile t=Main.tile[kv.Key.x,kv.Key.y]; return t.HasTile!=kv.Value.has || (t.HasTile && t.TileType!=kv.Value.type) || t.WallType!=kv.Value.wall;
            }).Select(kv=>new {x=kv.Key.x,y=kv.Key.y,beforeHas=kv.Value.has,beforeType=kv.Value.type,beforeWall=kv.Value.wall,
                afterHas=Main.tile[kv.Key.x,kv.Key.y].HasTile,afterType=Main.tile[kv.Key.x,kv.Key.y].HasTile?(int)Main.tile[kv.Key.x,kv.Key.y].TileType:-1,
                afterWall=(int)Main.tile[kv.Key.x,kv.Key.y].WallType}).ToArray();
            int changedOutside=outsideChanges.Length;
            var placementChanges=houseSmallBuildBeforePlace.Where(kv=>
            {
                if(kv.Key.y==houseSmallBuildY && kv.Key.x>=houseSmallBuildX && kv.Key.x<houseSmallBuildX+6)return false;
                if(!houseSmallBuildAfterPlace.TryGetValue(kv.Key,out var after))return true;
                return after.has!=kv.Value.has || (after.has && after.type!=kv.Value.type) || after.wall!=kv.Value.wall;
            }).Select(kv=>new {x=kv.Key.x,y=kv.Key.y,beforeType=kv.Value.type,beforeWall=kv.Value.wall,
                afterType=houseSmallBuildAfterPlace.TryGetValue(kv.Key,out var after)?after.type:-1,
                afterWall=houseSmallBuildAfterPlace.TryGetValue(kv.Key,out var afterWall)?afterWall.wall:-1}).ToArray();
            bool site=agentJournal.Outcomes.Any(IsSafeSmallBuildSite);
            bool pass=placed==6 && Loadout.CountItem(ItemID.Wood)==0 && houseSmallBuildBeforePlace.Count>0 && houseSmallBuildAfterPlace.Count>0 && placementChanges.Length==0 && site &&
                agentJournal.Outcomes.Any(o=>o.Status=="succeeded" && o.Task.Action==AIActionType.Place);
            File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-small-build-result.json"),JsonSerializer.Serialize(new {
                status=pass?"passed":"failed",x=houseSmallBuildX,y=houseSmallBuildY,width=6,initialWoodStacks=new[]{2,4},
                remainingWood=Loadout.CountItem(ItemID.Wood),placed,changedOutside,outsideChanges,placementChanges,
                startedUtc=houseSmallBuildAt,placeCommandUtc=houseSmallBuildPlaceAt,placeOutcomeObservedUtc=houseSmallBuildAfterPlaceAt,checkedUtc=DateTime.UtcNow,site,requests=PrimaryDecisionRequestCount-houseSmallBuildRequestStart,
                npc=new{x=CompanionNpc?.Center.X/16,y=CompanionNpc?.Center.Y/16},player=new{x=Main.LocalPlayer.Center.X/16,y=Main.LocalPlayer.Center.Y/16},
                modelReply=LastReply,outcomes=agentJournal.Outcomes,scope="Six real wood tiles only; no claim of a complete storey."},new JsonSerializerOptions{WriteIndented=true}));
            houseSmallBuildFinished=true;Stop();TestLog($"house small build {(pass?"passed":"failed")}: placed={placed} outside={changedOutside}");
        }
    }
    private bool IsSafeSmallBuildSite(AgentOutcome outcome)
    {
        if(outcome.Status!="succeeded" || outcome.Task.Action!=AIActionType.Inspect || outcome.Task.Target!="site")return false;
        try
        {
            using var document=JsonDocument.Parse(outcome.Result);
            var root=document.RootElement;
            if(!root.GetProperty("available").GetBoolean() || root.GetProperty("worldId").GetInt32()!=Main.worldID)return false;
            var m=root.GetProperty("measurement");var b=m.GetProperty("bounds");
            return b.GetProperty("x").GetInt32()==houseSmallBuildX && b.GetProperty("y").GetInt32()==houseSmallBuildY &&
                b.GetProperty("width").GetInt32()==6 && b.GetProperty("height").GetInt32()==1 &&
                m.GetProperty("allTileCellsEmpty").GetBoolean() && m.GetProperty("supportedBottomColumns").GetInt32()==6 &&
                m.GetProperty("actorConflicts").GetArrayLength()==0;
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }
    private bool IsActualHouseQuery(AgentOutcome outcome)
    {
        if(outcome.Status!="succeeded" || outcome.Task.Action!=AIActionType.Inspect ||
            outcome.Task.Target is not ("structure" or "house") || houseNativeReference?.observation?.bounds is not { } expected)
            return false;
        try
        {
            using var document=JsonDocument.Parse(outcome.Result);
            var root=document.RootElement;
            if(!root.GetProperty("available").GetBoolean() || root.GetProperty("worldId").GetInt32()!=Main.worldID)
                return false;
            var observation=root.GetProperty("observation");
            if(!observation.GetProperty("found").GetBoolean())return false;
            var bounds=observation.GetProperty("bounds");
            return bounds.GetProperty("x").GetInt32()==expected.x && bounds.GetProperty("y").GetInt32()==expected.y &&
                bounds.GetProperty("width").GetInt32()==expected.width && bounds.GetProperty("height").GetInt32()==expected.height;
        }
        catch (Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }
    private void FinishHousePlanning(bool success,string error)
    {
        SaveHousePlanning(success?"passed":"failed",error);
        housePlanningPassed=success;
        housePlanningFinished=true;Stop();
        TestLog($"house real-LLM planning {(success?"passed":"failed")}: {error}");
    }
    private void SaveHousePlanning(string status,string error)
    {
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","house-planning-result.json"),JsonSerializer.Serialize(new {
            status,error,world=Main.worldName,worldId=Main.worldID,updatedUtc=DateTime.UtcNow,
            elapsedSeconds=(DateTime.UtcNow-housePlanningAt).TotalSeconds,requests=PrimaryDecisionRequestCount-housePlanningRequestStart,
            nativeReference=houseNativeReference,modelReply=LastReply,goalStatus,outcomes=agentJournal.Outcomes,
            injectedStaleMemory="旧记忆：原屋10x7，屋顶加盖已完成（验收注入的错误历史推断，必须以当前观察为准）。",
            scope="Actual user's copied multi-storey house: native geometry observation and real-LLM preconstruction planning; no claim of full-storey construction or demolition success."
        },new JsonSerializerOptions{WriteIndented=true}));
    }
}
