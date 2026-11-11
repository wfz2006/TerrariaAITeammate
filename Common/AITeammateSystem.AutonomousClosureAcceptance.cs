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

internal sealed record ClosureDirtSpawn(ulong tick,int slot,int type,int stack,string source,int? x,int? y,string action);
public sealed class ClosureDirtWitness : GlobalItem
{
    internal static bool Active;
    internal static readonly List<ClosureDirtSpawn> Spawns=new();
    public override void OnSpawn(Item item,IEntitySource source)
    {
        if(!Active || Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")!="autonomous_closure_sources_native" || Main.netMode!=NetmodeID.SinglePlayer || !Main.SavePath.Contains("agent-acceptance",StringComparison.OrdinalIgnoreCase) || item.type!=ItemID.DirtBlock)return;
        Point? p=source is EntitySource_TileBreak b?b.TileCoords:null;
        Spawns.Add(new(Main.GameUpdateCount,item.whoAmI,item.type,item.stack,source.GetType().FullName??source.GetType().Name,p?.X,p?.Y,AITeammateSystem.Instance.Executor.CurrentAction.ToString()));
    }
}

// Test-only: a human inspects the complete source page and terrain before selecting an observed seed.
public partial class AITeammateSystem
{
    private bool closureSourcesStarted, closureSourcesFinished, closurePartialRecovery, closureGroupPageProbe;
    private int closureRecoveryPlayer=-1, closureRecoveryStone;
    private int closurePhase, closureFrames, closurePrimary, closureCombat, closureX, closureY;
    private long closureSequence;
    private DateTime closureAt;
    private ShelterWorldAudit? closureAudit;
    private JsonElement closurePage;
    private readonly List<JsonElement> closurePages = new();
    private readonly HashSet<(string target,int x,int y)> closureTerrainObserved = new();
    private string closureQuery="stone;maxDistance=400;limit=8";
    private int? closureQueryX=3220, closureQueryY=401;
    private int closureAutomaticPages=3, closureWorkCount, closureWorkBefore, closureExploreX, closureExploreY;
    private readonly List<object> closureMotionTrace=new();
    private string closureWorkTarget="";
    private readonly List<object> closureEvents = new(), closureTrace = new(), closureRemovedLoose = new();
    private readonly List<(int x,int y)> closureRemovedTiles = new();
    private string ClosureFile(string name) => Path.Combine(Main.SavePath,"AITeammate",$"autonomous-closure-sources-native-{name}");
    private void DispatchClosureInspect(JsonElement task,string source)
    {
        string publicJson=JsonSerializer.Serialize(new{tasks=new[]{task}});
        try
        {
            AIPlan envelope=AIPlanParser.Parse(publicJson);
            if(envelope.Steps.Count!=1)throw new JsonException("expected exactly one parsed task");
            AIPlan parsed=envelope.Steps[0];
            string item=task.GetProperty("item").GetString()??"";
            int? x=task.TryGetProperty("x",out var xNode) && xNode.ValueKind==JsonValueKind.Number?xNode.GetInt32():null;
            int? y=task.TryGetProperty("y",out var yNode) && yNode.ValueKind==JsonValueKind.Number?yNode.GetInt32():null;
            if(parsed.Action!=AIActionType.Inspect || parsed.Target!="sources" || parsed.Item!=item || parsed.X!=x || parsed.Y!=y)
                throw new JsonException("parsed source task did not preserve item and paired origin");
            closureQuery=item; closureQueryX=x; closureQueryY=y;
            closureEvents.Add(new{kind="source_parser_dispatch",source,nativeTask=task,publicJson,accepted=true,parsed});
            Executor.Apply(parsed);
        }
        catch(JsonException ex)
        {
            closureEvents.Add(new{kind="source_parser_dispatch",source,nativeTask=task,publicJson,accepted=false,error=ex.Message});
            FinishAutonomousClosureSources("failed_or_uncovered","public source task parser rejected or changed native task");
        }
    }
    private JsonElement ClosureInspectTask()=>JsonSerializer.SerializeToElement(new{type="inspect",target="sources",item=closureQuery,x=closureQueryX,y=closureQueryY});
    private void BeginAutonomousClosureSources()
    {
        Stop(); Memory.Clear(); LlmConversationStore.ResetAll(); agentJournal.Clear(); Executor.Clear(true); Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!; Player player=Main.LocalPlayer;
        closurePlatformMode=HouseMode && Environment.GetEnvironmentVariable("AITEAMMATE_CLOSURE_PLATFORM")=="1";
        closureGroupPageProbe=!closurePlatformMode && Environment.GetEnvironmentVariable("AITEAMMATE_CLOSURE_GROUP_PAGES")=="1";
        closurePrimary=PrimaryDecisionRequestCount; closureCombat=CombatDecisionRequestCount;
        if((closurePlatformMode || closureGroupPageProbe) && !ClosurePlatformConfigSafe())
        { FinishAutonomousClosureSources("uncovered","controlled acceptance requires UseLlm=false and AllowTeleportRecovery=false"); return; }
        if (closureGroupPageProbe)
        {
            closureQueryX = closureQueryY = null;
            closureEvents.Add(new { kind = "group_page_probe_setup", maximumGroupPages = 8, query = closureQuery,
                scope = "Follow complete native paging.nextTask only. No selected mine seed, actions, materials or model requests." });
        }
        npc.Center=Stand(3220,401); npc.velocity=Vector2.Zero; player.Center=Stand(3173,395); player.velocity=Vector2.Zero;
        Loadout.Reset(); Loadout.EnsureStarterTools();
        foreach(Item item in Main.item) if(item.active && item.type is ItemID.StoneBlock or ItemID.DirtBlock)
        { closureRemovedLoose.Add(new{item.whoAmI,item.type,item.stack,x=item.Center.X,y=item.Center.Y}); item.TurnToAir(); }
        LongGoalDropWitness.Spawns.Clear(); LongGoalDropWitness.Active=true; ClosureDirtWitness.Spawns.Clear(); ClosureDirtWitness.Active=true;
        closureAudit=ShelterWorldAudit.Capture(3220,390); closurePrimary=PrimaryDecisionRequestCount; closureCombat=CombatDecisionRequestCount;
        closureSequence=agentJournal.LastSequence; closureAt=DateTime.UtcNow; closureSourcesStarted=true;
        foreach(string name in new[]{"select.txt","query.txt","terrain.txt","explore.txt","work.txt","ready.txt","result.json"}) if(File.Exists(ClosureFile(name))) File.Delete(ClosureFile(name));
        if(closurePlatformMode && !BeginClosurePlatform(player)) return;
        SaveAutonomousClosureSources("running","");
    }
    private void TickAutonomousClosureSources()
    {
        if(Main.gameMenu || CompanionNpc==null)return;
        NPC npc=CompanionNpc; Player player=Main.LocalPlayer; closureFrames++;
        player.immune=true; player.immuneTime=600; player.statLife=player.statLifeMax2;
        foreach(NPC hostile in Main.npc) if(hostile.active&&!hostile.friendly) hostile.active=false;
        if(closurePhase is 3 or 4 or 5 or ClosurePlatformStandPhase) closureMotionTrace.Add(new{frame=closureFrames,tick=Main.GameUpdateCount,phase=closurePhase,action=Executor.CurrentAction.ToString(),centerPixels=new{x=npc.Center.X,y=npc.Center.Y},feetPixels=npc.Bottom.Y,x=npc.Center.X/16,y=npc.Center.Y/16,bottom=npc.Bottom.Y/16,vx=npc.velocity.X,vy=npc.velocity.Y,npc.collideX,npc.collideY,grounded=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width),dropThrough=Executor.DropThroughPlatforms,navigation=Executor.NavigationState});
        if(closureFrames%30==0) closureTrace.Add(new{frame=closureFrames,phase=closurePhase,x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16,stone=Loadout.CountItem(ItemID.StoneBlock),action=Executor.CurrentAction.ToString()});
        if(PrimaryDecisionRequestCount!=closurePrimary || CombatDecisionRequestCount!=closureCombat || Executor.NavigationTeleportCount!=0)
        { FinishAutonomousClosureSources("failed","API or teleport observed"); return; }
        if((closurePlatformMode || closureGroupPageProbe) && !ClosurePlatformConfigSafe())
        { FinishAutonomousClosureSources("failed","isolated no-API/no-teleport settings changed"); return; }
        if(DateTime.UtcNow-closureAt>TimeSpan.FromMinutes(10)) { FinishAutonomousClosureSources("uncovered","deadline"); return; }
        if(closurePlatformMode && (closurePhase is 4 or ClosurePlatformStandPhase)) RecordClosurePlatformContact(npc);
        if(closurePhase==0)
        {
            JsonElement status=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Status());
            if(!status.GetProperty("ready").GetBoolean())return;
            closurePhase=1; DispatchClosureInspect(ClosureInspectTask(),"initial_query"); return;
        }
        if(closurePhase==2)
        {
            if(closurePlatformMode && !Executor.HoldPosition) Executor.SetHold(true);
            string queryFile=ClosureFile("query.txt");
            if(File.Exists(queryFile))
            {
                string[] q=File.ReadAllText(queryFile).Trim().Split('|'); File.Delete(queryFile);
                bool supportedResource=new[]{"stone;","dirt;","grass;"}.Any(prefix=>q[0].StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) ||
                    closurePlatformMode && q[0].StartsWith("wood;",StringComparison.OrdinalIgnoreCase);
                if(closurePages.Count>=8 || q.Length!=3 || !int.TryParse(q[1],out int qx) || !int.TryParse(q[2],out int qy) || !supportedResource || !WorldGen.InWorld(qx,qy,10))
                { closureEvents.Add(new{kind="query_rejected",reason="eight page limit or invalid resource item/origin"}); SaveAutonomousClosureSources("awaiting_visual_review","query rejected"); return; }
                closureQuery=q[0]; closureQueryX=qx; closureQueryY=qy; closureAutomaticPages=0;
                closureEvents.Add(new{kind="controlled_query_requested",item=closureQuery,x=qx,y=qy,npcX=npc.Center.X/16,npcY=npc.Center.Y/16});
                closurePhase=1; DispatchClosureInspect(ClosureInspectTask(),"controlled_query"); return;
            }
            string terrainFile=ClosureFile("terrain.txt");
            if(File.Exists(terrainFile))
            {
                string query=File.ReadAllText(terrainFile).Trim(); File.Delete(terrainFile);
                if(closurePlatformMode) closurePlatformObservedAir.Clear();
                string[] terrainParts=query.Split(',');
                if(terrainParts.Length!=4 || !int.TryParse(terrainParts[0],out int tx) || !int.TryParse(terrainParts[1],out int ty) || !int.TryParse(terrainParts[2],out int tw) || !int.TryParse(terrainParts[3],out int th) || tw<1 || th<1 || tw>97 || th>81 || !WorldGen.InWorld(tx,ty,10) || !WorldGen.InWorld(tx+tw-1,ty+th-1,10))
                { closureEvents.Add(new{kind="terrain_rejected",query,reason="invalid bounded window"}); SaveAutonomousClosureSources("awaiting_visual_review","terrain rejected"); return; }
                object[] SnapshotCells()=>Enumerable.Range(tx,tw).SelectMany(x=>Enumerable.Range(ty,th).Select(y=>{Tile t=Main.tile[x,y];return (object)new{x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsHalfBlock,t.Slope,t.IsActuated,t.LiquidAmount,t.LiquidType,solid=t.HasTile&&Main.tileSolid[t.TileType],solidTop=t.HasTile&&Main.tileSolidTop[t.TileType],platform=t.HasTile&&TileID.Sets.Platforms[t.TileType]};})).ToArray();
                var beforeCells=SnapshotCells(); var beforeActor=new{x=npc.Center.X,y=npc.Center.Y,vx=npc.velocity.X,vy=npc.velocity.Y}; var beforeInventory=ShelterCounts(Loadout.Inventory);
                ulong tick=Main.GameUpdateCount; JsonElement world=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Status());
                JsonElement terrain=JsonSerializer.SerializeToElement(NavigationWorldObserver.Terrain(query,npc));
                var afterCells=SnapshotCells(); var afterActor=new{x=npc.Center.X,y=npc.Center.Y,vx=npc.velocity.X,vy=npc.velocity.Y}; var afterInventory=ShelterCounts(Loadout.Inventory);
                bool unchanged=JsonSerializer.Serialize(beforeCells)==JsonSerializer.Serialize(afterCells) && JsonSerializer.Serialize(beforeActor)==JsonSerializer.Serialize(afterActor) && JsonSerializer.Serialize(beforeInventory)==JsonSerializer.Serialize(afterInventory);
                int eligible=0;
                if(closurePlatformMode && unchanged && terrain.TryGetProperty("available",out var terrainAvailable) && terrainAvailable.ValueKind==JsonValueKind.True)
                    ObserveClosurePlatformAir(tx,ty,tw,th,tick);
                if(unchanged && terrain.TryGetProperty("available",out var available) && available.ValueKind==JsonValueKind.True && terrain.TryGetProperty("miningFacts",out var facts))
                {
                    var classes=facts.GetProperty("resourceClasses").EnumerateArray().Select(v=>v.GetString()??"").ToArray();
                    var allowed=new HashSet<(int x,int y)>();
                    foreach(var row in facts.GetProperty("miningProtectionRuns").EnumerateArray())foreach(var run in row.GetProperty("runs").EnumerateArray())
                        if(run[2].GetInt32()==0 && run[3].GetInt32()!=0 && run[4].GetInt32()==0)
                            for(int x=run[0].GetInt32();x<run[0].GetInt32()+run[1].GetInt32();x++)allowed.Add((x,row.GetProperty("y").GetInt32()));
                    foreach(var row in facts.GetProperty("resourceTargetRuns").EnumerateArray())foreach(var run in row.GetProperty("runs").EnumerateArray())
                    {
                        int index=run[2].GetInt32(); if(index<0 || index>=classes.Length || classes[index] is not ("grass" or "dirt"))continue;
                        for(int x=run[0].GetInt32();x<run[0].GetInt32()+run[1].GetInt32();x++)if(allowed.Contains((x,row.GetProperty("y").GetInt32())))
                        {closureTerrainObserved.Add((classes[index],x,row.GetProperty("y").GetInt32()));eligible++;}
                    }
                }
                closureEvents.Add(new{kind="controlled_terrain_observation",query,tick,world,terrain,beforeCells,afterCells,beforeActor,afterActor,beforeInventory,afterInventory,unchanged,eligible,scope="read-only; observation is not permission or proof of reachability; fresh native preflight required"});
                SaveAutonomousClosureSources("awaiting_visual_review",unchanged?"terrain observed":"terrain snapshot changed; no terrain candidates admitted"); return;
            }
            if(closurePlatformMode && DispatchClosurePlatformDirective(npc,player)) return;
            string exploreFile=ClosureFile("explore.txt");
            if(File.Exists(exploreFile))
            {
                string[] p=File.ReadAllText(exploreFile).Trim().Split(','); File.Delete(exploreFile);
                if(p.Length!=2 || !int.TryParse(p[0],out int sx) || !int.TryParse(p[1],out int sy)) { closureEvents.Add(new{kind="explore_rejected",reason="invalid stand"}); return; }
                bool MatchStand(JsonElement stand)=>stand.ValueKind==JsonValueKind.Object && stand.TryGetProperty("x",out var x) && x.GetInt32()==sx && stand.TryGetProperty("standRow",out var y) && y.GetInt32()==sy;
                bool seen=closurePages.Any(page=>page.TryGetProperty("candidates",out var cs) && cs.EnumerateArray().Any(c=>
                {
                    if(!c.TryGetProperty("preflight",out var pre) || !pre.TryGetProperty("seed",out var seed) ||
                       !seed.TryGetProperty("structureProtected",out var protectedFlag) || protectedFlag.ValueKind!=JsonValueKind.False ||
                       !seed.TryGetProperty("nativeCanKill",out var kill) || kill.ValueKind!=JsonValueKind.True ||
                       !pre.TryGetProperty("tool",out var tool) || !tool.TryGetProperty("canBreak",out var canBreak) || canBreak.ValueKind!=JsonValueKind.True ||
                       !pre.TryGetProperty("actualDistancePixels",out var distance) || distance.ValueKind!=JsonValueKind.Number ||
                       !pre.TryGetProperty("approach",out var approach)) return false;
                    bool distant=distance.GetDouble()>88;
                    bool routeUnproven=!approach.TryGetProperty("status",out var status) || status.GetString()!="work_stand_route_found";
                    if(!distant && !routeUnproven)return false;
                    bool selected=approach.TryGetProperty("selectedStand",out var stand) && MatchStand(stand);
                    bool geometry=c.TryGetProperty("geometry",out var geom) && geom.ValueKind==JsonValueKind.Object &&
                        geom.TryGetProperty("standSamples",out var samples) && samples.ValueKind==JsonValueKind.Array && samples.EnumerateArray().Any(MatchStand);
                    return selected || geometry;
                }));
                if(!seen || closurePages.Count>=8) { closureEvents.Add(new{kind="explore_rejected",reason="legal candidate stand not observed, movement unnecessary, or page budget exhausted",sx,sy}); SaveAutonomousClosureSources("awaiting_visual_review","explore rejected"); return; }
                closureEvents.Add(new{kind="controlled_observed_stand_explore",sx,sy,query=closureQuery,x=closureQueryX,y=closureQueryY,naturalIdentity="requires independent visual review"});
                closureExploreX=sx; closureExploreY=sy; closurePhase=5; Executor.Apply(new AIPlan{Action=AIActionType.Explore,X=sx,Y=sy}); return;
            }
            string workFile=ClosureFile("work.txt");
            if(File.Exists(workFile))
            {
                string raw=File.ReadAllText(workFile); File.Delete(workFile);
                try
                {
                    using var workDoc=JsonDocument.Parse(raw); var w=workDoc.RootElement;
                    string target=w.GetProperty("target").GetString()??""; int wx=w.GetProperty("x").GetInt32(),wy=w.GetProperty("y").GetInt32(),count=w.GetProperty("count").GetInt32();
                    bool seen=closurePages.Any(page=>page.TryGetProperty("resource",out var resource) && resource.GetString()==target && page.TryGetProperty("candidates",out var candidates) && candidates.EnumerateArray().Any(c=>c.GetProperty("seed").GetProperty("x").GetInt32()==wx && c.GetProperty("seed").GetProperty("y").GetInt32()==wy && c.GetProperty("preflight").GetProperty("canAttempt").ValueKind==JsonValueKind.True));
                    bool terrainSeen=closureTerrainObserved.Contains((target,wx,wy));
                    bool platformWood=closurePlatformMode && target=="wood";
                    bool workAllowed=platformWood
                        ? !closurePlatformWoodTried && count is >=1 and <=4 && seen
                        : (target is "dirt" or "grass") && count is >=1 and <=8 && (seen || terrainSeen) &&
                            (!closurePlatformMode || closureWorkCount-(closurePlatformWoodTried?1:0)<3);
                    if(!workAllowed || closureWorkCount>=4 || closurePlatformMode && closurePlatformMinedStone>0)
                    { closureEvents.Add(new{kind="work_rejected",target,wx,wy,count,reason="requires same-resource sources canAttempt or eligible observed terrain; count 1..8 and at most four segments"}); SaveAutonomousClosureSources("awaiting_visual_review","work rejected"); return; }
                    JsonElement fresh=JsonSerializer.SerializeToElement(Executor.InspectMiningInteraction(npc,target,wx,wy));
                    closureEvents.Add(new{kind="controlled_work_fresh_preflight",target,wx,wy,count,observationSource=seen?"sources":"terrain",fresh,naturalIdentity="independent visual review required"});
                    if(!fresh.TryGetProperty("canAttempt",out var permitted) || permitted.ValueKind!=JsonValueKind.True || !fresh.TryGetProperty("seed",out var freshSeed) || !freshSeed.TryGetProperty("targetMatches",out var matches) || matches.ValueKind!=JsonValueKind.True || !freshSeed.TryGetProperty("structureProtected",out var protectedFlag) || protectedFlag.ValueKind!=JsonValueKind.False || !freshSeed.TryGetProperty("nativeCanKill",out var kill) || kill.ValueKind!=JsonValueKind.True || !fresh.TryGetProperty("tool",out var freshTool) || !freshTool.TryGetProperty("canBreak",out var canBreak) || canBreak.ValueKind!=JsonValueKind.True)
                    { closureEvents.Add(new{kind="work_rejected",reason="fresh native preflight denied"}); SaveAutonomousClosureSources("awaiting_visual_review","work fresh preflight rejected"); return; }
                    if(platformWood)
                    { DispatchClosurePlatformWood(wx,wy,count,fresh); return; }
                    closureWorkTarget=target; closureWorkBefore=Loadout.CountItem(ItemID.DirtBlock); closureWorkCount++; closurePhase=6;
                    closureEvents.Add(new{kind="controlled_work_dispatch",target,wx,wy,count,beforeDirt=closureWorkBefore,x=npc.Center.X/16,y=npc.Center.Y/16,segment=closureWorkCount});
                    Executor.Apply(new AIPlan{Action=AIActionType.Mine,Target=target,X=wx,Y=wy,Count=count}); return;
                }
                catch(Exception ex) when(ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                { closureEvents.Add(new{kind="work_rejected",reason="invalid JSON work request",error=ex.Message}); SaveAutonomousClosureSources("awaiting_visual_review","invalid work request"); return; }
            }
            string selection=ClosureFile("select.txt"); if(!File.Exists(selection))return;
            string[] parts=File.ReadAllText(selection).Trim().Split(','); File.Delete(selection);
            if(closurePlatformMode && (closurePlatformCrafted==0 || closurePlatformMinedStone>0 || Loadout.CountItem(ItemID.StoneBlock)!=0))
            { RejectClosurePlatform("select","requires attributable crafted platforms and zero prior stone"); return; }
            if(parts.Length!=2 || !int.TryParse(parts[0],out closureX) || !int.TryParse(parts[1],out closureY)) { FinishAutonomousClosureSources("uncovered","invalid reviewed selection"); return; }
            bool observed=closurePages.Any(page=>(!closurePlatformMode || page.TryGetProperty("resource",out var resource) && resource.GetString()=="stone") && page.TryGetProperty("candidates",out var candidates) && candidates.EnumerateArray().Any(c=>c.GetProperty("seed").GetProperty("x").GetInt32()==closureX && c.GetProperty("seed").GetProperty("y").GetInt32()==closureY && c.GetProperty("preflight").GetProperty("canAttempt").ValueKind==JsonValueKind.True));
            if(!observed) { FinishAutonomousClosureSources("uncovered","selected seed was not an attemptable source-page candidate"); return; }
            JsonElement current=JsonSerializer.SerializeToElement(Executor.InspectMiningInteraction(npc,"stone",closureX,closureY));
            closureEvents.Add(new{kind="reviewed_candidate_fresh_preflight",x=closureX,y=closureY,current});
            if(!current.TryGetProperty("canAttempt",out var can) || can.ValueKind!=JsonValueKind.True) { FinishAutonomousClosureSources("uncovered","fresh preflight does not permit attempted mining"); return; }
            if(closurePlatformMode) closurePlatformSpawnStart=LongGoalDropWitness.Spawns.Count;
            closurePhase=3; Executor.Apply(new AIPlan{Action=AIActionType.Mine,Target="stone",X=closureX,Y=closureY,Count=5}); return;
        }
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>closureSequence); if(outcome==null)return;
        closureSequence=outcome.Sequence; closureEvents.Add(new{kind="outcome",phase=closurePhase,outcome,stone=Loadout.CountItem(ItemID.StoneBlock),x=npc.Center.X/16,y=npc.Center.Y/16,preservation=closureAudit?.CheckPreservation()});
        if(closurePlatformMode && CompleteClosurePlatformAction(outcome,npc,player)) return;
        if(closurePhase==1)
        {
            using var doc=JsonDocument.Parse(outcome.Result); closurePage=doc.RootElement.Clone();
            closurePages.Add(closurePage);
            closureEvents.Add(new{kind="source_page_raw",query=new{item=closureQuery,x=closureQueryX,y=closureQueryY},outcomeSequence=outcome.Sequence,raw=outcome.Result,observedAtTick=Main.GameUpdateCount});
            var cells=new List<object>();
            if(closurePage.TryGetProperty("candidates",out var candidates))foreach(var c in candidates.EnumerateArray())
            {
                int sx=c.GetProperty("seed").GetProperty("x").GetInt32(),sy=c.GetProperty("seed").GetProperty("y").GetInt32();
                for(int x=sx-7;x<=sx+7;x++)for(int y=sy-7;y<=sy+7;y++)if(WorldGen.InWorld(x,y,2))
                { Tile t=Main.tile[x,y]; cells.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsHalfBlock,t.Slope,t.IsActuated,t.LiquidAmount,solid=t.HasTile&&Main.tileSolid[t.TileType]}); }
            }
            closureEvents.Add(new{kind="source_page_and_candidate_neighborhoods",page=closurePage,cells});
            string? next=null;
            bool complete=outcome.Status=="succeeded" && closurePage.TryGetProperty("status",out var pageStatus) && pageStatus.GetString()=="complete" && closurePage.TryGetProperty("complete",out var done) && done.ValueKind==JsonValueKind.True;
            if (closureGroupPageProbe)
            {
                if (!complete || closureAudit?.CheckPreservation() is not { preserved: true, coverageComplete: true })
                { FinishAutonomousClosureSources("failed_or_uncovered", "The original house or complete native source page was unavailable."); return; }
                if (candidates.ValueKind == JsonValueKind.Array && candidates.EnumerateArray().Any(candidate =>
                    candidate.TryGetProperty("preflight", out var preflight) &&
                    preflight.TryGetProperty("canAttempt", out var attempt) && attempt.ValueKind == JsonValueKind.True))
                { FinishAutonomousClosureSources("passed_source_discovery", ""); return; }
                if (closurePages.Count < 8 && closurePage.TryGetProperty("paging", out var paging) &&
                    paging.TryGetProperty("nextTask", out var nextGroup) && nextGroup.ValueKind == JsonValueKind.Object)
                { DispatchClosureInspect(nextGroup, "native_next_group_task"); return; }
                FinishAutonomousClosureSources("uncovered", "The bounded different-group pages contained no currently attemptable source.");
                return;
            }
            if(complete && closurePage.TryGetProperty("refinement",out var refinement) && refinement.TryGetProperty("nextQuery",out var nextValue) && nextValue.ValueKind==JsonValueKind.String) next=nextValue.GetString();
            if(complete && !string.IsNullOrWhiteSpace(next) && closurePages.Count<closureAutomaticPages && closurePages.Count<8)
            {
                if(!closurePage.TryGetProperty("refinement",out var nextRefinement) || !nextRefinement.TryGetProperty("nextTask",out var nativeNextTask) || nativeNextTask.ValueKind!=JsonValueKind.Object)
                { FinishAutonomousClosureSources("failed_or_uncovered","native continuation task missing"); return; }
                DispatchClosureInspect(nativeNextTask.Clone(),"native_nextTask"); return;
            }
            closureEvents.Add(new{kind="page_collection_end",pages=closurePages.Count,lastComplete=complete,nextQuery=next,remaining="unread scope remains unknown"});
            if(closurePlatformMode) { ReviewClosurePlatform("source page recorded; review observed tree, terrain or stone before selecting work"); return; }
            closurePhase=2; SaveAutonomousClosureSources("awaiting_visual_review","select an observed canAttempt seed only after reviewing natural origin; write x,y to select.txt");
            File.WriteAllText(ClosureFile("ready.txt"),"awaiting_visual_review"); return;
        }
        if(closurePhase==6)
        {
            closureEvents.Add(new{kind="controlled_work_completed",target=closureWorkTarget,segment=closureWorkCount,status=outcome.Status,result=outcome.Result,beforeDirt=closureWorkBefore,afterDirt=Loadout.CountItem(ItemID.DirtBlock),actualPosition=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},dirtSpawns=ClosureDirtWitness.Spawns,preservation=closureAudit?.CheckPreservation()});
            if(closurePlatformMode) { ReviewClosurePlatform("work segment ended; review actual result before the next directive"); return; }
            closurePhase=2; SaveAutonomousClosureSources("awaiting_visual_review","work segment ended; no automatic continuation"); return;
        }
        if(closurePhase==5)
        {
            Point actual=new((int)(npc.Center.X/16),(int)(npc.Bottom.Y/16)-1);
            bool resolved=TilePathfinder.TryResolveStand(npc.Center,out Point actualStand);
            var stableMethod=typeof(TilePathfinder).GetMethod("IsStableStand",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static,null,new[]{typeof(int),typeof(int)},null);
            bool? Stable(int x,int y)=>stableMethod?.Invoke(null,new object[]{x,y}) is bool result?result:null;
            var neighborhood=new List<object>();
            for(int x=closureExploreX-4;x<=closureExploreX+4;x++)for(int y=closureExploreY-4;y<=closureExploreY+6;y++)if(WorldGen.InWorld(x,y,2))
            { Tile t=Main.tile[x,y]; neighborhood.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.IsHalfBlock,t.Slope,t.IsActuated,solid=t.HasTile&&Main.tileSolid[t.TileType],solidTop=t.HasTile&&Main.tileSolidTop[t.TileType]}); }
            closureEvents.Add(new{kind="explore_terminal_physics",requested=new{x=closureExploreX,standRow=closureExploreY,safe=TilePathfinder.IsSafeStand(closureExploreX,closureExploreY),stable=Stable(closureExploreX,closureExploreY)},actual=new{x=npc.Center.X/16,y=npc.Center.Y/16,bottom=npc.Bottom.Y/16,vx=npc.velocity.X,vy=npc.velocity.Y,npc.collideY,grounded=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width),dropThrough=Executor.DropThroughPlatforms,cell=actual,resolvedStand=resolved?(Point?)actualStand:null,resolvedSafe=resolved?TilePathfinder.IsSafeStand(actualStand.X,actualStand.Y):(bool?)null,resolvedStable=resolved?Stable(actualStand.X,actualStand.Y):null},navigation=Executor.NavigationState,neighborhood});
            if(outcome.Status!="succeeded") { FinishAutonomousClosureSources("uncovered","controlled Explore did not reach observed stand"); return; }
            closureEvents.Add(new{kind="explore_completed_reinspect_same_query",item=closureQuery,x=closureQueryX,y=closureQueryY,actor=new{x=npc.Center.X/16,y=npc.Center.Y/16}});
            closureAutomaticPages=0; closurePhase=1;
            DispatchClosureInspect(ClosureInspectTask(),"post_explore_reinspect"); return;
        }
        if(closurePhase==3)
        {
            using var doc=JsonDocument.Parse(outcome.Result); var root=doc.RootElement;
            if(root.TryGetProperty("removedTilesTruncated",out var truncated) && truncated.ValueKind==JsonValueKind.True) { FinishAutonomousClosureSources("uncovered","removed tiles truncated"); return; }
            int thisMineRemovedCount=0;
            if(root.TryGetProperty("removedTiles",out var tiles) && tiles.ValueKind==JsonValueKind.Array)foreach(var tile in tiles.EnumerateArray())
            {closureRemovedTiles.Add((tile.GetProperty("x").GetInt32(),tile.GetProperty("y").GetInt32()));thisMineRemovedCount++;}
            bool attribution=LongGoalDropWitness.Spawns.Where(s=>s.type==ItemID.StoneBlock).All(s=>s.stack>0 && s.sourceType==typeof(EntitySource_TileBreak).FullName && s.action=="Mine" && s.tileX.HasValue && s.tileY.HasValue && closureRemovedTiles.Contains((s.tileX.Value,s.tileY.Value)));
            int spawned=LongGoalDropWitness.Spawns.Where(s=>s.type==ItemID.StoneBlock).Sum(s=>s.stack);
            int actualStone=Loadout.CountItem(ItemID.StoneBlock);
            int yielded=0;
            if(root.TryGetProperty("actualYields",out var yields) && yields.ValueKind==JsonValueKind.Array)
                yielded=yields.EnumerateArray().Where(v=>v.GetProperty("itemType").GetInt32()==ItemID.StoneBlock).Sum(v=>v.GetProperty("amount").GetInt32());
            bool removedComplete=root.TryGetProperty("removedTilesTruncated",out var removedTruncated) && removedTruncated.ValueKind==JsonValueKind.False;
            bool partial=outcome.Status=="blocked" && actualStone>0 && actualStone<5 && yielded==actualStone && spawned>=actualStone && attribution && removedComplete && thisMineRemovedCount>0;
            if(partial)
            {
                closurePartialRecovery=true; closureRecoveryPlayer=player.whoAmI; closureRecoveryStone=actualStone;
                closureEvents.Add(new{kind="partialReturnRecovery_start",mineOutcome=outcome,playerId=player.whoAmI,worldId=Main.worldID,stone=actualStone,spawned,yielded,start=new{x=npc.Center.X,y=npc.Center.Y,feet=npc.Bottom.Y},player=new{x=player.Center.X,y=player.Center.Y,feet=player.Bottom.Y},scope="controlled recovery only; strict five-stone acceptance remains failed"});
                closurePhase=4; Executor.Apply(new AIPlan{Action=AIActionType.Come}); return;
            }
            if(outcome.Status!="succeeded" || actualStone<5 || spawned<5 || !attribution) { FinishAutonomousClosureSources("failed_or_uncovered","mine did not yield five attributable native stone"); return; }
            closurePhase=4; Executor.Apply(new AIPlan{Action=AIActionType.Come}); return;
        }
        bool near=Math.Abs(npc.Center.X-player.Center.X)<=48 && Math.Abs(npc.Bottom.Y-player.Bottom.Y)<=24 && TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width);
        var preservation=closureAudit?.CheckPreservation();
        if(closurePartialRecovery)
        {
            float horizontal=Math.Abs(npc.Center.X-player.Center.X), vertical=Math.Abs(npc.Bottom.Y-player.Bottom.Y);
            closureEvents.Add(new{kind="partialReturnRecovery_result",outcome,playerId=player.whoAmI,samePlayer=player.whoAmI==closureRecoveryPlayer,worldId=Main.worldID,stoneAtStart=closureRecoveryStone,stoneNow=Loadout.CountItem(ItemID.StoneBlock),actual=new{x=npc.Center.X,y=npc.Center.Y,feet=npc.Bottom.Y},player=new{x=player.Center.X,y=player.Center.Y,feet=player.Bottom.Y},horizontalPixels=horizontal,verticalFeetPixels=vertical,grounded=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width),near,preservation,recoverySucceeded=outcome.Status=="succeeded" && near && player.whoAmI==closureRecoveryPlayer && Loadout.CountItem(ItemID.StoneBlock)>=closureRecoveryStone && preservation is {preserved:true,coverageComplete:true} && Executor.NavigationTeleportCount==0});
            FinishAutonomousClosureSources("failed_or_uncovered","strict five-stone goal incomplete; partial return recovery recorded separately"); return;
        }
        bool pass=outcome.Status=="succeeded" && near && Loadout.CountItem(ItemID.StoneBlock)>=5 && preservation is {preserved:true,coverageComplete:true};
        FinishAutonomousClosureSources(pass?"passed_controlled":"failed_or_uncovered",pass?"":"physical return or bounded preservation failed");
    }
    private void FinishAutonomousClosureSources(string status,string error)
    { closureSourcesFinished=true; LongGoalDropWitness.Active=false; ClosureDirtWitness.Active=false; SaveAutonomousClosureSources(status,error); Stop(); Executor.Clear(true); }
    private void SaveAutonomousClosureSources(string status,string error)
    {
        File.WriteAllText(ClosureFile("result.json"),JsonSerializer.Serialize(new{status,error,frames=closureFrames,phase=closurePhase,primaryRequests=PrimaryDecisionRequestCount-closurePrimary,combatRequests=CombatDecisionRequestCount-closureCombat,teleports=Executor.NavigationTeleportCount,removedPreexistingLooseStoneAndDirt=closureRemovedLoose,controlledWorkSegments=closureWorkCount,dirtSpawns=ClosureDirtWitness.Spawns,removedTiles=closureRemovedTiles.Select(p=>new{p.x,p.y}),dropSpawns=LongGoalDropWitness.Spawns,preservation=closureAudit?.CheckPreservation(),events=closureEvents,trace=closureTrace,motionTrace=closureMotionTrace,platform=closurePlatformMode?new{earnedWood=closurePlatformEarnedWood,crafted=closurePlatformCrafted,minedStone=closurePlatformMinedStone,placed=closurePlatformPlaced.Select(p=>new{x=p.X,y=p.Y}),contacted=closurePlatformContacts.Select(p=>new{x=p.X,y=p.Y}),inventory=ShelterCounts(Loadout.Inventory)}:null,scope=closurePlatformMode?"Controlled zero-API platform return in an isolated original-house copy; observed tree, native Craft, stone, legal one-cell placements and production travel. Natural origin requires independent visual review. No autonomy claim.":"Controlled skill test; source selection requires visual review of actual source page and terrain; at most eight bounded pages with per-page raw and query preserved for offline archive integration verification; unread scope remains unknown. No autonomous-planner claim. Original-house copy, starter tools, one actor setup, preexisting loose stone and dirt removed with records, hostiles removed/player protected. Never manufacture terrain or resources; path query alone is not a physical return. Natural origin is a separate human review obligation."}));
    }
}

// Zero-API acceptance: continuation is taken only from the immediately preceding native page.
public partial class AITeammateSystem
{
    private bool movingSourceStarted, movingSourceFinished;
    private int movingSourcePhase, movingSourceFrames, movingSourcePrimary, movingSourceCombat, movingSourceX, movingSourceY;
    private long movingSourceSequence;
    private DateTime movingSourceAt;
    private Point movingSourceInitialTile, movingSourceOrigin, movingSourceBeforeExploreTile;
    private string movingSourceScope="";
    private JsonElement movingSourceNext;
    private ShelterWorldAudit? movingSourceAudit;
    private string movingSourceInventory="";
    private readonly List<object> movingSourceEvents=new();
    private string MovingSourceFile(string name)=>Path.Combine(Main.SavePath,"AITeammate",$"autonomous-closure-moving-sources-{name}");
    private static Point MovingSourceTile(NPC npc)=>new((int)(npc.Center.X/16f),(int)(npc.Center.Y/16f));
    private void BeginMovingSourceAcceptance()
    {
        Stop(); Memory.Clear(); LlmConversationStore.ResetAll(); agentJournal.Clear(); Executor.Clear(true); Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!; Player player=Main.LocalPlayer;
        npc.Center=Stand(3220,401); npc.velocity=Vector2.Zero; player.Center=Stand(3173,395); player.velocity=Vector2.Zero;
        Loadout.Reset(); Loadout.EnsureStarterTools();
        movingSourceAudit=ShelterWorldAudit.Capture(3220,390); movingSourcePrimary=PrimaryDecisionRequestCount; movingSourceCombat=CombatDecisionRequestCount;
        movingSourceSequence=agentJournal.LastSequence; movingSourceAt=DateTime.UtcNow; movingSourceStarted=true;
        movingSourceInitialTile=MovingSourceTile(npc); movingSourceInventory=JsonSerializer.Serialize(ShelterCounts(Loadout.Inventory));
        movingSourceEvents.Add(new{kind="setup",actor=new{x=npc.Center.X,y=npc.Center.Y,tile=movingSourceInitialTile},inventory=ShelterCounts(Loadout.Inventory),preservation=movingSourceAudit.CheckPreservation(),scope="one setup only; no injected materials or subsequent position/velocity assignments"});
        SaveMovingSource("running","");
    }
    private void MovingSourceDispatch(JsonElement task,string source)
    {
        string json=JsonSerializer.Serialize(new{tasks=new[]{task}});
        try
        {
            AIPlan envelope=AIPlanParser.Parse(json);
            if(envelope.Steps.Count!=1)throw new JsonException("expected one task");
            AIPlan parsed=envelope.Steps[0];
            int? x=task.TryGetProperty("x",out var xn)&&xn.ValueKind==JsonValueKind.Number?xn.GetInt32():null;
            int? y=task.TryGetProperty("y",out var yn)&&yn.ValueKind==JsonValueKind.Number?yn.GetInt32():null;
            if(parsed.Action!=AIActionType.Inspect || parsed.Target!="sources" || parsed.Item!=task.GetProperty("item").GetString() || parsed.X!=x || parsed.Y!=y)throw new JsonException("native task changed");
            var npc=CompanionNpc!;
            movingSourceEvents.Add(new{kind="source_parser_dispatch",source,nativeTask=task,publicJson=json,accepted=true,parsed,actualObserver=new{x=npc.Center.X/16f,y=npc.Center.Y/16f,tile=MovingSourceTile(npc)}});
            Executor.Apply(parsed);
        }
        catch(JsonException ex){movingSourceEvents.Add(new{kind="source_parser_dispatch",source,nativeTask=task,publicJson=json,accepted=false,error=ex.Message}); FinishMovingSource("failed","parser rejected native task");}
    }
    private void TickMovingSourceAcceptance()
    {
        if(Main.gameMenu || CompanionNpc==null)return;
        NPC npc=CompanionNpc; Player player=Main.LocalPlayer; movingSourceFrames++;
        player.immune=true; player.immuneTime=600; player.statLife=player.statLifeMax2;
        foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(PrimaryDecisionRequestCount!=movingSourcePrimary || CombatDecisionRequestCount!=movingSourceCombat || Executor.NavigationTeleportCount!=0){FinishMovingSource("failed","API or teleport observed");return;}
        if(DateTime.UtcNow-movingSourceAt>TimeSpan.FromMinutes(10)){FinishMovingSource("uncovered","deadline");return;}
        if(movingSourcePhase==0)
        {
            JsonElement status=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Status()); if(!status.GetProperty("ready").GetBoolean())return;
            movingSourcePhase=1; MovingSourceDispatch(JsonSerializer.SerializeToElement(new{type="inspect",target="sources",item="stone;maxDistance=400;limit=8"}),"initial_query"); return;
        }
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>movingSourceSequence); if(outcome==null)return;
        movingSourceSequence=outcome.Sequence;
        movingSourceEvents.Add(new{kind="outcome",phase=movingSourcePhase,outcome,actualObserver=new{x=npc.Center.X/16f,y=npc.Center.Y/16f,tile=MovingSourceTile(npc)},inventory=ShelterCounts(Loadout.Inventory),preservation=movingSourceAudit?.CheckPreservation()});
        if(movingSourcePhase is 1 or 4 or 5)
        {
            using var doc=JsonDocument.Parse(outcome.Result); JsonElement page=doc.RootElement;
            movingSourceEvents.Add(new{kind="source_page_raw",phase=movingSourcePhase,raw=outcome.Result,actualObserver=new{x=npc.Center.X/16f,y=npc.Center.Y/16f,tile=MovingSourceTile(npc)}});
            bool complete=outcome.Status=="succeeded" && page.TryGetProperty("available",out var av)&&av.ValueKind==JsonValueKind.True && page.TryGetProperty("inputValid",out var iv)&&iv.ValueKind==JsonValueKind.True && page.TryGetProperty("callSucceeded",out var cs)&&cs.ValueKind==JsonValueKind.True && page.TryGetProperty("complete",out var cp)&&cp.ValueKind==JsonValueKind.True;
            if(!complete){FinishMovingSource("failed","source inspect incomplete or invalid");return;}
            if(!page.TryGetProperty("origin",out var origin) || !page.TryGetProperty("query",out var query) || !query.TryGetProperty("refineScope",out var scopeNode)) {FinishMovingSource("failed","source origin or scope absent");return;}
            Point queryOrigin=new(origin.GetProperty("x").GetInt32(),origin.GetProperty("y").GetInt32()); string scope=scopeNode.GetString()??"";
            if(movingSourcePhase==1){movingSourceOrigin=queryOrigin;movingSourceScope=scope;}
            else if(queryOrigin!=movingSourceOrigin || scope!=movingSourceScope){FinishMovingSource("failed","continuation changed frozen query origin or scope");return;}
            movingSourceEvents.Add(new{kind="origin_scope_check",queryOrigin,scope,query=query.Clone(),actualObserverTile=MovingSourceTile(npc),sameOrigin=queryOrigin==movingSourceOrigin,sameScope=scope==movingSourceScope});
            if(movingSourcePhase==5){FinishMovingSource("awaiting_visual_review","");return;}
            if(!page.TryGetProperty("refinement",out var refinement)||!refinement.TryGetProperty("nextTask",out var next)||next.ValueKind!=JsonValueKind.Object){FinishMovingSource("uncovered","native nextTask absent");return;}
            movingSourceNext=next.Clone();
            if(!movingSourceNext.TryGetProperty("x",out var nextX) || nextX.ValueKind!=JsonValueKind.Number || nextX.GetInt32()!=movingSourceOrigin.X || !movingSourceNext.TryGetProperty("y",out var nextY) || nextY.ValueKind!=JsonValueKind.Number || nextY.GetInt32()!=movingSourceOrigin.Y){FinishMovingSource("failed","native continuation omitted resolved origin");return;}
            if(movingSourcePhase==4){movingSourcePhase=5;MovingSourceDispatch(movingSourceNext,"native_nextTask_2");return;}
            int nx=(int)(npc.Center.X/16f), row=(int)(npc.Bottom.Y/16f)-1;
            int? dx=Enumerable.Range(-12,25).Where(d=>Math.Abs(d)>=5 && TilePathfinder.IsSafeStand(nx+d,row) && new Point((int)(TilePathfinder.StandCenter(new Point(nx+d,row),npc.height).X/16f),(int)(TilePathfinder.StandCenter(new Point(nx+d,row),npc.height).Y/16f))!=movingSourceOrigin).OrderBy(d=>Math.Abs(d)).Select(d=>(int?)d).FirstOrDefault();
            if(!dx.HasValue){FinishMovingSource("uncovered","no nearby native safe stand");return;}
            movingSourceX=nx+dx.Value; movingSourceY=row;
            movingSourceEvents.Add(new{kind="route_candidate",x=movingSourceX,y=movingSourceY,safe=TilePathfinder.IsSafeStand(movingSourceX,movingSourceY)});
            movingSourcePhase=2; Executor.Apply(new AIPlan{Action=AIActionType.Inspect,Target="route",X=movingSourceX,Y=movingSourceY});return;
        }
        if(movingSourcePhase==2)
        {
            using var doc=JsonDocument.Parse(outcome.Result); var route=doc.RootElement;
            bool found=outcome.Status=="succeeded" && route.TryGetProperty("measurement",out var m) && m.TryGetProperty("pathFound",out var foundNode) && foundNode.ValueKind==JsonValueKind.True && m.TryGetProperty("resolvedDestination",out var dst) && dst.ValueKind==JsonValueKind.Object && dst.GetProperty("x").GetInt32()==movingSourceX && dst.GetProperty("standRow").GetInt32()==movingSourceY;
            movingSourceEvents.Add(new{kind="native_route_raw",raw=outcome.Result,found});
            if(!found){FinishMovingSource("uncovered","nearby stand lacks native route proof");return;}
            movingSourceBeforeExploreTile=MovingSourceTile(npc); movingSourceEvents.Add(new{kind="explore_dispatch",setupTile=movingSourceInitialTile,queryOrigin=movingSourceOrigin,beforeExploreTile=movingSourceBeforeExploreTile,target=new{x=movingSourceX,y=movingSourceY}});
            movingSourcePhase=3; Executor.Apply(new AIPlan{Action=AIActionType.Explore,X=movingSourceX,Y=movingSourceY});return;
        }
        if(movingSourcePhase==3)
        {
            Point after=MovingSourceTile(npc); bool moved=after!=movingSourceBeforeExploreTile; bool awayFromOrigin=after!=movingSourceOrigin;
            movingSourceEvents.Add(new{kind="physical_movement",outcome,setupTile=movingSourceInitialTile,queryOrigin=movingSourceOrigin,beforeExploreTile=movingSourceBeforeExploreTile,afterExploreTile=after,continuationObserverTile=after,moved,awayFromOrigin,grounded=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width),actual=new{x=npc.Center.X,y=npc.Center.Y,feet=npc.Bottom.Y}});
            if(outcome.Status!="succeeded" || !moved || !awayFromOrigin){FinishMovingSource("uncovered","native movement did not change explore-start tile or remains at query origin");return;}
            movingSourcePhase=4;MovingSourceDispatch(movingSourceNext,"native_nextTask_1_after_movement");return;
        }
    }
    private void FinishMovingSource(string status,string error){movingSourceFinished=true;SaveMovingSource(status,error);Stop();Executor.Clear(true);}
    private void SaveMovingSource(string status,string error)
    {
        bool inventoryUnchanged=movingSourceInventory==JsonSerializer.Serialize(ShelterCounts(Loadout.Inventory));
        File.WriteAllText(MovingSourceFile("result.json"),JsonSerializer.Serialize(new{status,error,frames=movingSourceFrames,phase=movingSourcePhase,primaryRequests=PrimaryDecisionRequestCount-movingSourcePrimary,combatRequests=CombatDecisionRequestCount-movingSourceCombat,teleports=Executor.NavigationTeleportCount,inventoryUnchanged,preservation=movingSourceAudit?.CheckPreservation(),events=movingSourceEvents,scope="zero API; original nullable initial query; each continuation is native previous page task; unread source coverage remains unknown; no mining, placement or autonomy claim"}));
        if(status=="awaiting_visual_review")File.WriteAllText(MovingSourceFile("ready.txt"),status);
    }
}
