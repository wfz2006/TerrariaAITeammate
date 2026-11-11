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
    private bool sourcesNativeStarted,sourcesNativeFinished;private int sourcesNativePhase,sourcesNativeFrames,sourcesNativeRequests;private long sourcesNativeSequence;private DateTime sourcesNativeAt;
    private ShelterWorldAudit? sourcesNativeAudit;private string sourcesNativeBefore="",sourcesNativeCompatBefore="",sourcesNativeCompatAfter="";private object? sourcesNativeCompatFirst,sourcesNativeCompatSecond;private readonly List<object> sourcesNativeProgress=new();
    private readonly List<(int x,int y)> sourcesNativeCells=new();private string sourcesNativeCellsBefore="";
    private const string SourcesNativeQuery="wood;maxDistance=400;limit=8";
    private string SourcesNativeCellsSnapshot()=>JsonSerializer.Serialize(sourcesNativeCells.Select(pos=>{Tile t=Main.tile[pos.x,pos.y];return new{pos.x,pos.y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsHalfBlock,t.Slope,t.IsActuated,t.LiquidAmount};}).ToArray());
    private void BeginSourcesNative()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear(true);Executor.ResetNavigationTelemetry();NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;Loadout.Reset();Loadout.EnsureStarterTools();
        sourcesNativeAudit=ShelterWorldAudit.Capture(3220,390);sourcesNativeRequests=PrimaryDecisionRequestCount;sourcesNativeSequence=agentJournal.LastSequence;sourcesNativeAt=DateTime.UtcNow;sourcesNativeStarted=true;
    }
    private void TickSourcesNative()
    {
        if(Main.gameMenu || CompanionNpc==null)return;NPC npc=CompanionNpc;Player p=Main.LocalPlayer;sourcesNativeFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(DateTime.UtcNow-sourcesNativeAt>TimeSpan.FromSeconds(150) || Executor.NavigationTeleportCount>0){FinishSourcesNative("failed","deadline or warp",null);return;}
        if(sourcesNativePhase==0)
        {
            JsonElement world=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Status());
            if(!world.GetProperty("ready").GetBoolean())return;
            sourcesNativeBefore=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
            JsonElement page=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Query(SourcesNativeQuery,npc,3200,390));
            if(page.TryGetProperty("data",out var data) && data.TryGetProperty("entries",out var groups))foreach(var group in groups.EnumerateArray())
                if(group.TryGetProperty("samples",out var samples))foreach(var sample in samples.EnumerateArray()){
                    int sx=sample.GetProperty("x").GetInt32(),sy=sample.GetProperty("y").GetInt32();
                    for(int x=sx-1;x<=sx+1;x++)for(int y=sy-1;y<=sy+1;y++)if(WorldGen.InWorld(x,y,2) && !sourcesNativeCells.Contains((x,y)))sourcesNativeCells.Add((x,y));}
            sourcesNativeCellsBefore=SourcesNativeCellsSnapshot();
            sourcesNativeCompatBefore=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
            sourcesNativeCompatFirst=Executor.InspectMiningSources(npc,SourcesNativeQuery,3200,390);
            sourcesNativeCompatSecond=Executor.InspectMiningSources(npc,SourcesNativeQuery,3200,390);
            sourcesNativeCompatAfter=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
            sourcesNativePhase=1;return;
        }
        if(sourcesNativePhase==1)
        {
            Executor.Apply(new AIPlan{Action=AIActionType.Inspect,Target="sources",Item=SourcesNativeQuery,X=3200,Y=390});sourcesNativePhase=2;return;
        }
        if(Executor.MiningSourcesInspectionProgress is object progress)sourcesNativeProgress.Add(new{tick=Main.GameUpdateCount,frame=sourcesNativeFrames,progress});
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>sourcesNativeSequence);if(outcome==null)return;sourcesNativeSequence=outcome.Sequence;
        FinishSourcesNative("observed","",outcome);
    }
    private void FinishSourcesNative(string status,string error,AgentOutcome? outcome)
    {
        sourcesNativeFinished=true;NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;string after=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
        JsonElement first=JsonSerializer.SerializeToElement(sourcesNativeCompatFirst),second=JsonSerializer.SerializeToElement(sourcesNativeCompatSecond);
        var comparisons=new List<object>();JsonElement? result=null;bool candidateFieldsMatch=true,uniqueTicks=true,normalComplete=false;
        if(outcome!=null){using var doc=JsonDocument.Parse(outcome.Result);result=doc.RootElement.Clone();var ticks=new HashSet<ulong>();
            normalComplete=result.Value.GetProperty("status").GetString()=="complete" && result.Value.GetProperty("complete").GetBoolean();
            foreach(var c in result.Value.GetProperty("candidates").EnumerateArray()){
                int x=c.GetProperty("seed").GetProperty("x").GetInt32(),y=c.GetProperty("seed").GetProperty("y").GetInt32();ulong tick=c.GetProperty("observedAtTick").GetUInt64();uniqueTicks &= ticks.Add(tick);
                JsonElement full=JsonSerializer.SerializeToElement(Executor.InspectMiningInteraction(npc,"wood",x,y));JsonElement shortView=c.GetProperty("preflight");
                bool matches=shortView.GetProperty("canAttempt").GetRawText()==full.GetProperty("canAttempt").GetRawText() && shortView.GetProperty("seed").GetProperty("structureProtected").GetRawText()==full.GetProperty("seed").GetProperty("structureProtected").GetRawText() && shortView.GetProperty("seed").GetProperty("nativeCanKill").GetRawText()==full.GetProperty("seed").GetProperty("nativeCanKill").GetRawText();candidateFieldsMatch &= matches;
                comparisons.Add(new{x,y,observedAtTick=tick,source=c.Clone(),full,criticalFieldsMatch=matches});}}
        bool sameTickBudget=second.ValueKind==JsonValueKind.Object && second.GetProperty("tickWorkDeferred").GetBoolean() && second.GetProperty("fullPreflightChecks").GetInt32()==0;
        var preserved=sourcesNativeAudit?.CheckPreservation();bool noApi=PrimaryDecisionRequestCount==sourcesNativeRequests,noWarp=Executor.NavigationTeleportCount==0;
        JsonNode? beforeNode=JsonNode.Parse(sourcesNativeBefore),afterNode=JsonNode.Parse(after),compatBefore=JsonNode.Parse(sourcesNativeCompatBefore),compatAfter=JsonNode.Parse(sourcesNativeCompatAfter);
        string[] fields={"npc","player","inventory","playerInventory","armor","accessories","action","status","live","budget"};
        var componentChanges=fields.Select(field=>new{field,equal=JsonNode.DeepEquals(beforeNode?[field],afterNode?[field]),before=beforeNode?[field]?.DeepClone(),after=afterNode?[field]?.DeepClone()}).Where(x=>!x.equal).ToArray();
        bool inventoryEqual=new[]{"inventory","playerInventory","armor","accessories"}.All(field=>JsonNode.DeepEquals(beforeNode?[field],afterNode?[field]));
        bool compatPhysicalEqual=new[]{"npc","player","inventory","playerInventory","armor","accessories","tiles"}.All(field=>JsonNode.DeepEquals(compatBefore?[field],compatAfter?[field]));
        JsonArray beforeTiles=beforeNode?["tiles"]?.AsArray()??new JsonArray(),afterTiles=afterNode?["tiles"]?.AsArray()??new JsonArray();
        var tileDifferences=Enumerable.Range(0,Math.Min(beforeTiles.Count,afterTiles.Count)).Where(i=>!JsonNode.DeepEquals(beforeTiles[i],afterTiles[i])).Select(i=>new{before=beforeTiles[i]?.DeepClone(),after=afterTiles[i]?.DeepClone()}).ToArray();
        bool pass=status=="observed" && normalComplete && comparisons.Count>0 && sameTickBudget && candidateFieldsMatch && uniqueTicks && inventoryEqual && compatPhysicalEqual && preserved is {available:true,preserved:true} && noApi && noWarp;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","sources-native-result.json"),JsonSerializer.Serialize(new{
            status=pass?"passed":"failed_or_uncovered",error,frames=sourcesNativeFrames,requests=PrimaryDecisionRequestCount-sourcesNativeRequests,teleports=Executor.NavigationTeleportCount,
            checks=new{normalComplete,sameTickBudget,candidateFieldsMatch,uniqueTicks,inventoryEqual,compatPhysicalEqual,preservation=preserved?.preserved,zeroApi=noApi,zeroWarp=noWarp,boundedAcrossFramesEqual=sourcesNativeBefore==after,candidateNeighborhoodEqual=sourcesNativeCellsBefore==SourcesNativeCellsSnapshot(),rng="unknown_not_captured"},componentChanges,tileDifferenceCount=tileDifferences.Length,tileDifferenceSamples=tileDifferences.Take(16),
            query=SourcesNativeQuery,queryOrigin=new{x=3200,y=390},compatFirst=first,compatSecond=second,normalResult=result,progress=sourcesNativeProgress,comparisons,preservation=preserved,
            scope="Fresh original-house copy; index readiness awaited, compatibility called twice in one tick then retained Executor Inspect sources on later frames. Query origin differs from actual observer. Candidate full preflights rechecked after terminal only as current-facts comparison; multi-tick observations are not atomic. No mining, placement, LLM, or teleport. Hostiles removed/player protected; bounded cross-frame snapshot is reported, not a global-world or RNG equality claim."
        },new JsonSerializerOptions{WriteIndented=false}));Stop();Executor.Clear(true);
    }
}
