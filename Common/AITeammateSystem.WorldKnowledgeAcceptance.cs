using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private bool worldKnowledgeNativeStarted, worldKnowledgeNativeFinished;
    private DateTime worldKnowledgeNativeStart;
    private int worldKnowledgeFrames, worldKnowledgeMaxReads, worldKnowledgeReadViolations, worldKnowledgeStartRequests;
    private double worldKnowledgeMaxMs;
    private readonly List<object> worldKnowledgeScanSamples = new();

    private void BeginWorldKnowledgeNativeAcceptance()
    {
        NPC npc=CompanionNpc!; Player player=Main.LocalPlayer;
        Stop(); Executor.Clear(); Executor.ForgetBlockedGoal();
        npc.Center=Stand(3220,401); npc.velocity=Vector2.Zero;
        player.Center=Stand(3173,395); player.velocity=Vector2.Zero;
        Loadout.Reset(); Loadout.EnsureStarterTools(); Executor.ResetNavigationTelemetry();
        worldKnowledgeStartRequests=PrimaryDecisionRequestCount;
        worldKnowledgeNativeStart=DateTime.UtcNow; worldKnowledgeNativeStarted=true;
    }

    private void TickWorldKnowledgeNativeAcceptance()
    {
        if(Main.gameMenu || CompanionNpc==null)return;
        worldKnowledgeFrames++;
        JsonElement status=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Status());
        JsonElement scan=status.GetProperty("scan");
        int reads=scan.GetProperty("lastTileReads").GetInt32();
        worldKnowledgeMaxReads=Math.Max(worldKnowledgeMaxReads,reads);
        worldKnowledgeMaxMs=Math.Max(worldKnowledgeMaxMs,scan.GetProperty("lastMilliseconds").GetDouble());
        if(reads>scan.GetProperty("maximumTileReadsPerTick").GetInt32())worldKnowledgeReadViolations++;
        if(worldKnowledgeFrames%60==1)worldKnowledgeScanSamples.Add(status);
        if(status.GetProperty("ready").GetBoolean() || DateTime.UtcNow-worldKnowledgeNativeStart>TimeSpan.FromSeconds(120))
            FinishWorldKnowledgeNativeAcceptance(status);
    }

    private void FinishWorldKnowledgeNativeAcceptance(JsonElement status)
    {
        worldKnowledgeNativeFinished=true; NPC npc=CompanionNpc!; Player player=Main.LocalPlayer;
        string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));
        var chest=Main.chest.FirstOrDefault(c=>c!=null && CompanionChests.IsChestBuilt(c));
        var locked=Main.chest.FirstOrDefault(c=>c!=null && CompanionChests.IsChestBuilt(c) && Chest.IsLocked(c.x,c.y));
        NPC? realNpc=Main.npc.FirstOrDefault(n=>n.active && n.whoAmI!=npc.whoAmI);
        int boss=NPC.AnyNPCs(4)?50:4;
        var results=new Dictionary<string,JsonElement>();
        void Query(string name,string q,int? x=null,int? y=null) => results[name]=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Query(q,npc,x,y));
        Query("stone_far","stone;limit=8",0,0); Query("wood_far","wood;limit=8",0,0);
        Query("stations","stations;limit=8",0,0); Query("chests","chests;limit=8");
        Query("item9","item:9;limit=8"); Query("boss",$"boss:{boss}");
        if(chest!=null) Query("chest_detail","chest",chest.x,chest.y);
        if(locked!=null) Query("locked_chest_detail","chest",locked.x,locked.y);
        if(realNpc!=null) Query("npc",$"npc:{realNpc.type};limit=8",(int)(realNpc.Center.X/16),(int)(realNpc.Center.Y/16));
        Query("stone_repeat","stone;limit=8",0,0);
        string after=JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));
        var tileChecks=new List<object>(); var timeChecks=new List<object>();
        foreach(string key in new[]{"stone_far","wood_far","stations"})
        {
            JsonElement data=results[key].GetProperty("data");
            foreach(JsonElement group in data.GetProperty("entries").EnumerateArray())
            {
            ulong start=group.GetProperty("scanStartedAtTick").GetUInt64(), end=group.GetProperty("scanCompletedAtTick").GetUInt64(), verified=group.GetProperty("sampleVerifiedAtTick").GetUInt64();
            timeChecks.Add(new{query=key,start,end,verified,valid=start<=end && end<=verified});
            foreach(JsonElement sample in group.GetProperty("samples").EnumerateArray())
            {
                int x=sample.GetProperty("x").GetInt32(), y=sample.GetProperty("y").GetInt32(), type=group.GetProperty("tileType").GetInt32();
                Tile tile=Main.tile[x,y]; double distance=Vector2.Distance(npc.Center,new Vector2(x*16+8,y*16+8))/16;
                tileChecks.Add(new{query=key,x,y,type,actualType=(int)tile.TileType,tile.HasTile,distanceTiles=distance,match=tile.HasTile && tile.TileType==type});
            }
            }
        }
        bool ChestMatches(Chest c,JsonElement result)
        {
            JsonElement data=result.GetProperty("data"); if(!data.GetProperty("found").GetBoolean())return false;
            JsonElement detail=data.GetProperty("chest");
            var actual=c.item.Where(i=>!i.IsAir).Select(i=>(i.type,i.stack)).ToArray();
            var reported=detail.GetProperty("items").EnumerateArray().Select(i=>(i.GetProperty("id").GetInt32(),i.GetProperty("count").GetInt32())).ToArray();
            return detail.GetProperty("x").GetInt32()==c.x && detail.GetProperty("y").GetInt32()==c.y &&
                detail.GetProperty("locked").GetBoolean()==Chest.IsLocked(c.x,c.y) && actual.SequenceEqual(reported);
        }
        var parsedChecks=tileChecks.Select(v=>JsonSerializer.SerializeToElement(v)).ToArray();
        bool farStone=parsedChecks.Any(v=>v.GetProperty("query").GetString()=="stone_far" && v.GetProperty("distanceTiles").GetDouble()>55 && v.GetProperty("match").GetBoolean());
        bool farWood=parsedChecks.Any(v=>v.GetProperty("query").GetString()=="wood_far" && v.GetProperty("distanceTiles").GetDouble()>55 && v.GetProperty("match").GetBoolean());
        bool tilesMatch=parsedChecks.All(v=>v.GetProperty("match").GetBoolean());
        bool timesMatch=timeChecks.Select(v=>JsonSerializer.SerializeToElement(v)).All(v=>v.GetProperty("valid").GetBoolean());
        bool? chestMatch=chest==null?null:ChestMatches(chest,results["chest_detail"]);
        bool? lockedMatch=locked==null?null:ChestMatches(locked,results["locked_chest_detail"]);
        JsonElement bossData=results["boss"].GetProperty("data");
        bool bossMatches=bossData.GetProperty("active").GetBoolean()==NPC.AnyNPCs(boss) &&
            (NPC.AnyNPCs(boss) || bossData.GetProperty("locations").GetProperty("entries").GetArrayLength()==0);
        bool? npcMatch=realNpc==null?null:results["npc"].GetProperty("data").GetProperty("entries").EnumerateArray().Any(e=>e.GetProperty("index").GetInt32()==realNpc.whoAmI && e.GetProperty("npcType").GetInt32()==realNpc.type);
        bool noMutation=before==after, noApi=PrimaryDecisionRequestCount==worldKnowledgeStartRequests;
        object? controlledStale=null; bool stalePassed=false;
        JsonElement far=parsedChecks.FirstOrDefault(v=>v.GetProperty("query").GetString()=="stone_far" && v.GetProperty("distanceTiles").GetDouble()>55 && v.GetProperty("match").GetBoolean());
        if(far.ValueKind!=JsonValueKind.Undefined)
        {
            int x=far.GetProperty("x").GetInt32(), y=far.GetProperty("y").GetInt32(); Tile tile=Main.tile[x,y]; ushort original=tile.TileType;
            string LocalSnapshot()=>JsonSerializer.Serialize(Enumerable.Range(-1,3).SelectMany(dx=>Enumerable.Range(-1,3).Select(dy=>{
                Tile t=Main.tile[x+dx,y+dy]; return new{dx,dy,t.HasTile,t.TileType,t.WallType,t.LiquidAmount,t.LiquidType,t.Slope,t.IsHalfBlock,t.IsActuated,t.TileFrameX,t.TileFrameY};})).ToArray());
            string localBefore=LocalSnapshot(); ulong tickBefore=Main.GameUpdateCount; JsonElement changed=default, dirty=default;
            try
            {
                tile.TileType=original==0?(ushort)1:(ushort)0;
                changed=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Query("stone;limit=8",npc,0,0));
                dirty=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Status());
            }
            finally { tile.TileType=original; WorldKnowledgeObserver.Invalidate(x,y); }
            JsonElement body=changed.GetProperty("data");
            bool removed=!body.GetProperty("entries").EnumerateArray().Any(g=>{
                JsonElement b=g.GetProperty("bounds"); int bx=b.GetProperty("x").GetInt32(),by=b.GetProperty("y").GetInt32();
                return x>=bx && x<bx+b.GetProperty("width").GetInt32() && y>=by && y<by+b.GetProperty("height").GetInt32();});
            bool restored=localBefore==LocalSnapshot(), sameTick=tickBefore==Main.GameUpdateCount;
            bool partial=!body.GetProperty("coverageComplete").GetBoolean() && dirty.GetProperty("dirtyChunks").GetInt32()>0;
            stalePassed=removed && restored && sameTick && partial && body.GetProperty("changedChunksInvalidated").GetInt32()>0;
            controlledStale=new{x,y,original,changed,dirty,removed,restored,sameTick,partial,scope="Controlled same-tick temporary TileType change, restored in finally; separate from readonly query window; chunk invalidated after restore."};
        }
        bool passed=status.GetProperty("ready").GetBoolean() && worldKnowledgeReadViolations==0 && farStone && farWood && tilesMatch && timesMatch && stalePassed && chestMatch==true && bossMatches && npcMatch==true && noMutation && noApi;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","world-knowledge-native-result.json"),JsonSerializer.Serialize(new{
            status=passed?"passed":"failed_or_incomplete",coverage=status,frames=worldKnowledgeFrames,elapsedSeconds=(DateTime.UtcNow-worldKnowledgeNativeStart).TotalSeconds,
            maxReads=worldKnowledgeMaxReads,readBudgetViolations=worldKnowledgeReadViolations,maxObservedMilliseconds=worldKnowledgeMaxMs,scanSamples=worldKnowledgeScanSamples,
            farStone,farWood,tilesMatch,tileChecks,timesMatch,timeChecks,stalePassed,controlledStale,chestMatch,lockedChestMatch=lockedMatch,lockedChestCoverage=locked!=null,npcMatch,bossMatches,
            snapshotEqual=noMutation,primaryRequests=PrimaryDecisionRequestCount-worldKnowledgeStartRequests,teleports=Executor.NavigationTeleportCount,
            queryBytes=results.ToDictionary(p=>p.Key,p=>Encoding.UTF8.GetByteCount(p.Value.GetRawText())),results,
            scope="Fresh original-house copy; one actor setup; no seeded resources. Readonly same-tick local actor/inventory/task and bounded house tiles snapshot, not whole-world mutation proof. Separate controlled stale-sample test temporarily changes and restores one remote tile in the same tick. Tile existence is not a route, interaction permission or harvested yield; no LLM."
        },new JsonSerializerOptions{WriteIndented=true}));
    }
}
