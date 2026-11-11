using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private static string ShelterPreviewBoundedTiles()
    {
        var cells=new System.Collections.Generic.List<object>();
        foreach(var r in new[]{(left:3196,top:400,right:3261,bottom:423),(left:3100,top:383,right:3109,bottom:413),(left:3246,top:383,right:3255,bottom:413)})
            for(int x=r.left;x<=r.right;x++)for(int y=r.top;y<=r.bottom;y++)
            {Tile t=Main.tile[x,y];cells.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsActuated,t.IsHalfBlock,t.Slope,t.LiquidAmount,t.LiquidType});}
        return JsonSerializer.Serialize(cells);
    }
    private void RunShelterBuildPreviewNative()
    {
        Stop();Executor.Clear(true);Executor.ResetNavigationTelemetry();NPC npc=CompanionNpc!;Player player=Main.LocalPlayer;
        npc.Center=Stand(3229,410);npc.velocity=Vector2.Zero;player.Center=Stand(3173,395);player.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();
        var audit=ShelterWorldAudit.Capture(3220,390);var preservationBefore=audit.CheckPreservation();
        ulong tick=Main.GameUpdateCount;int requests=PrimaryDecisionRequestCount;
        string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));string tilesBefore=ShelterPreviewBoundedTiles();
        object automatic=Executor.InspectShelterBuild(npc,"shelter");
        object openSite=Executor.InspectShelterBuild(npc,"ebonwood",3100,393);
        object occupiedSite=Executor.InspectShelterBuild(npc,"ebonwood",3246,390);
        string standardEntry=AgentKnowledge.Query("ebonwood","build",npc,player,Executor,3100,393);
        string after=JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));string tilesAfter=ShelterPreviewBoundedTiles();
        var preservationAfter=audit.CheckPreservation();
        JsonElement autoJson=JsonSerializer.SerializeToElement(automatic),openJson=JsonSerializer.SerializeToElement(openSite),occupiedJson=JsonSerializer.SerializeToElement(occupiedSite);
        var scan=autoJson.GetProperty("siteSearch");var openScan=openJson.GetProperty("siteSearch");var occupiedScan=occupiedJson.GetProperty("siteSearch");
        var requested=openScan.GetProperty("requestedTopLeft");var openCandidate=openScan.GetProperty("candidates")[0];var occupiedCandidate=occupiedScan.GetProperty("candidates")[0];
        bool selfCosts=autoJson.GetProperty("options").EnumerateArray().SelectMany(o=>o.GetProperty("items").EnumerateArray().ToArray()).All(item=>{
            int id=item.GetProperty("id").GetInt32(),required=item.GetProperty("required").GetInt32(),owned=Loadout.CountItem(id);
            return item.GetProperty("ownedSelf").GetInt32()==owned && item.GetProperty("missing").GetInt32()==Math.Max(0,required-owned) && !item.TryGetProperty("ownedPlayer",out _);});
        int playerTorch=player.inventory.Where(i=>!i.IsAir && i.type==ItemID.Torch).Sum(i=>i.stack);
        bool torchMissing=playerTorch>0 && autoJson.GetProperty("options").EnumerateArray().All(o=>o.GetProperty("items").EnumerateArray().Any(i=>i.GetProperty("id").GetInt32()==ItemID.Torch && i.GetProperty("ownedSelf").GetInt32()==0 && i.GetProperty("missing").GetInt32()>0));
        bool autoBounds=scan.GetProperty("candidateCount").GetInt32()==20 && scan.GetProperty("groundScan").GetProperty("fromY").GetInt32()==407 && scan.GetProperty("groundScan").GetProperty("toY").GetInt32()==423 &&
            scan.GetProperty("candidates").EnumerateArray().Select(c=>c.GetProperty("centerX").GetInt32()).SequenceEqual(ShelterSiteProbe.AutomaticCenters(3229,npc.direction));
        bool openExpected=requested.GetProperty("x").GetInt32()==3100 && requested.GetProperty("y").GetInt32()==393 && openCandidate.GetProperty("geometryStatus").GetString()=="geometry_passed" && openCandidate.GetProperty("resolvedTopLeft").GetProperty("x").GetInt32()==3100 && openCandidate.GetProperty("resolvedTopLeft").GetProperty("y").GetInt32()==393;
        bool occupiedRejected=occupiedScan.GetProperty("requestedTopLeft").GetProperty("x").GetInt32()==3246 && occupiedScan.GetProperty("requestedTopLeft").GetProperty("y").GetInt32()==390 && occupiedCandidate.GetProperty("geometryStatus").GetString()!="geometry_passed";
        bool entryEqual=JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(openSite)),JsonNode.Parse(standardEntry));
        bool sameTick=tick==Main.GameUpdateCount,snapshotEqual=before==after,queryTilesEqual=tilesBefore==tilesAfter,zeroApi=PrimaryDecisionRequestCount==requests,zeroWarp=Executor.NavigationTeleportCount==0;
        bool passed=sameTick && snapshotEqual && queryTilesEqual && zeroApi && zeroWarp && autoBounds && openExpected && occupiedRejected && entryEqual && selfCosts && torchMissing && preservationBefore.available && preservationBefore.preserved && preservationAfter.available && preservationAfter.preserved;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","shelter-build-preview-native-result.json"),JsonSerializer.Serialize(new{
            status=passed?"passed":"failed",tick,finishedTick=Main.GameUpdateCount,sameTick,snapshotEqual,queryTilesEqual,
            checks=new{autoBounds,openExpected,occupiedRejected,entryEqual,selfCosts,torchMissing,zeroApi,zeroWarp,preservationBefore=preservationBefore.available && preservationBefore.preserved,preservationAfter=preservationAfter.available && preservationAfter.preserved,rng="unknown_not_captured"},
            tileSnapshotBounds=new[]{new{left=3196,top=400,right=3261,bottom=423},new{left=3100,top=383,right=3109,bottom=413},new{left=3246,top=383,right=3255,bottom=413}},
            requests=PrimaryDecisionRequestCount-requests,teleports=Executor.NavigationTeleportCount,
            actor=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},selfInventory=ShelterCounts(Loadout.Inventory),playerInventory=ShelterCounts(player.inventory),
            automatic,openSite,occupiedSite,standardEntry=JsonSerializer.Deserialize<JsonElement>(standardEntry),preservationBefore,preservationAfter,
            scope="Fresh original house, controlled actor setup and starter tools only. Same-tick read-only auto and explicit shelter build previews; player inventory shown only for auditing, not supplied to companion. Bounded snapshot and original-house audit, no build, route, housing certification or LLM call. RNG not directly captured by this fixture."
        },new JsonSerializerOptions{WriteIndented=false}));
        houseFinished=true;Stop();Executor.Clear(true);
    }
}
