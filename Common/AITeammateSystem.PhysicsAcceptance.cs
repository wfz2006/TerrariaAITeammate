using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private void RunPhysicsViewNativeAcceptance()
    {
        NPC npc=CompanionNpc!;Player player=Main.LocalPlayer;
        object Snapshot() => new {npcX=npc.position.X,npcY=npc.position.Y,npcVx=npc.velocity.X,npcVy=npc.velocity.Y,
            playerX=player.position.X,playerY=player.position.Y,playerVx=player.velocity.X,playerVy=player.velocity.Y,
            inventory=Loadout.Inventory.Select(i=>new{i.type,i.stack,i.prefix}).ToArray(),
            armor=Loadout.Armor.Select(i=>new{i.type,i.stack,i.prefix}).ToArray(),
            accessories=Loadout.Accessories.Select(i=>new{i.type,i.stack,i.prefix}).ToArray(),
            world=Enumerable.Range(3160,111).SelectMany(x=>Enumerable.Range(350,71).Select(y=> {
                Tile t=Main.tile[x,y];return new{x,y,t.HasTile,t.TileType,t.WallType,t.LiquidAmount,t.LiquidType,t.Slope,t.IsHalfBlock,t.IsActuated,t.TileFrameX,t.TileFrameY};})).ToArray()};
        string before=JsonSerializer.Serialize(Snapshot());
        string request="原玩家请求保持不变";
        string native=AgentKnowledge.Query("3200,390","structure",npc,player,Executor);
        JsonNode raw=JsonNode.Parse(native)!;
        string state=JsonSerializer.Serialize(new {knowledge=new {selectedStructure=JsonSerializer.Deserialize<JsonElement>(native)},planning=new {sourcePlayerRequest=request}});
        JsonObject wire=PlanningRequest.CompactState(state,usePhysicsStructureView:true);
        JsonNode projected=wire["knowledge"]!["selectedStructure"]!;
        JsonNode rawGrid=raw["observation"]!["grid"]!, viewGrid=projected["observation"]!["grid"]!;
        Dictionary<int,JsonObject> DecodeLegend(JsonNode grid,bool defaults)
        {
            var result=new Dictionary<int,JsonObject>();
            foreach(JsonObject entry in ((JsonArray)grid["legend"]!).OfType<JsonObject>())
            {
                var full=defaults?grid["legendDefaults"]!.DeepClone().AsObject():new JsonObject();
                int key=entry["key"]!.GetValue<int>();
                foreach(var pair in entry)if(pair.Key!="key" && pair.Key!="frameX" && pair.Key!="frameY")full[pair.Key]=pair.Value?.DeepClone();
                result[key]=full;
            }
            return result;
        }
        List<int> Expand(JsonNode grid) => ((JsonArray)grid["rows"]!).SelectMany(row=>row!.GetValue<string>().Split(',').SelectMany(run=> {
            string[] parts=run.Split(':');return Enumerable.Repeat(int.Parse(parts[0]),int.Parse(parts[1]));})).ToList();
        var oldLegend=DecodeLegend(rawGrid,false);var newLegend=DecodeLegend(viewGrid,true);
        var oldKeys=Expand(rawGrid);var newKeys=Expand(viewGrid);
        int width=rawGrid["width"]!.GetValue<int>(),height=rawGrid["height"]!.GetValue<int>();
        int left=rawGrid["x"]!.GetValue<int>(),top=rawGrid["y"]!.GetValue<int>();
        int mismatches=0,nativeShapeMismatches=0,halfCount=0,slopeCount=0,actuatedCount=0;
        if(oldKeys.Count!=width*height || newKeys.Count!=oldKeys.Count)mismatches++;
        for(int i=0;i<Math.Min(oldKeys.Count,newKeys.Count);i++)
        {
            JsonObject original=oldLegend[oldKeys[i]],decoded=newLegend[newKeys[i]];
            if(!JsonNode.DeepEquals(original,decoded))mismatches++;
            Tile tile=Main.tile[left+i%width,top+i/width];
            if(original["halfBlock"]?.GetValue<bool>()!=tile.IsHalfBlock || original["actuated"]?.GetValue<bool>()!=tile.IsActuated || original["slope"]?.GetValue<int>()!=(int)tile.Slope)nativeShapeMismatches++;
            if(tile.IsHalfBlock)halfCount++;if((int)tile.Slope!=0)slopeCount++;if(tile.IsActuated)actuatedCount++;
        }
        int centerX=raw["sampling"]!["sampledCenter"]!["x"]!.GetValue<int>(),centerY=raw["sampling"]!["sampledCenter"]!["y"]!.GetValue<int>();
        int radiusX=(int)typeof(StructureWorldObserver).GetField("RadiusX",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetRawConstantValue()!;
        int radiusY=(int)typeof(StructureWorldObserver).GetField("RadiusY",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetRawConstantValue()!;
        int scanLeft=Math.Max(2,centerX-radiusX),scanRight=Math.Min(Main.maxTilesX-3,centerX+radiusX);
        int scanTop=Math.Max(2,centerY-radiusY),scanBottom=Math.Min(Main.maxTilesY-3,centerY+radiusY);
        int standChecked=0,standMismatch=0,displayFootprintOutside=0;var standDiagnostics=new List<object>();var displayOutsidePoints=new List<object>();
        foreach(JsonNode? point in (JsonArray)raw["observation"]!["standingPositions"]!)
        {
            int x=point!["x"]!.GetValue<int>();double feet=point["feetY"]!.GetValue<double>();
            var candidates=Enumerable.Range(scanTop-1,scanBottom-scanTop+1).Select(row=>new {row,safe=TilePathfinder.IsSafeStand(x,row),nativeFeet=TilePathfinder.StandBottom(new Point(x,row))/16d,
                supportInScan=row+1>=scanTop && row+1<=scanBottom,bodyColumnsInScan=x-1>=scanLeft&&x+1<=scanRight}).Where(v=>Math.Abs(v.nativeFeet-feet)<0.001).ToArray();
            bool match=candidates.Any(v=>v.supportInScan && v.bodyColumnsInScan && v.safe && Math.Abs(v.nativeFeet-feet)<0.0001);
            bool displayContained=x-1>=left && x+1<left+width;
            if(!displayContained){displayFootprintOutside++;if(displayOutsidePoints.Count<16)displayOutsidePoints.Add(new{x,feet});}
            standChecked++;if(!match){standMismatch++;if(standDiagnostics.Count<16)standDiagnostics.Add(new{x,expectedFeet=feet,candidates,validation="support and three body columns inside native sample, IsSafeStand true, native feet exact"});}
        }
        string after=JsonSerializer.Serialize(Snapshot());
        bool snapshotEqual=before==after,sourceRequestEqual=wire["planning"]?["sourcePlayerRequest"]?.GetValue<string>()==request;
        bool pass=mismatches==0 && nativeShapeMismatches==0 && standMismatch==0 && snapshotEqual && sourceRequestEqual && standChecked>0;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","physics-view-native-result.json"),JsonSerializer.Serialize(new {
            status=pass?"passed":"failed",mismatches,nativeShapeMismatches,standChecked,nativeStandMismatch=standMismatch,standDiagnostics,
            displayFootprintOutside,displayOutsidePoints,displayGridSelfContained=displayFootprintOutside==0,scanBounds=new{scanLeft,scanRight,scanTop,scanBottom},halfCount,slopeCount,actuatedCount,
            snapshotEqual,sourceRequestEqual,rawChars=native.Length,rawBytes=Encoding.UTF8.GetByteCount(native),
            viewChars=projected.ToJsonString().Length,viewBytes=Encoding.UTF8.GetByteCount(projected.ToJsonString()),
            rawLegend=oldLegend.Count,viewLegend=newLegend.Count,shapeRows=viewGrid["shapeRows"]?.DeepClone(),native=raw,physics=projected,
            scope="Copied original house; native stand check uses sampled scan bounds, display footprint coverage is separate; same-query snapshot covers its recorded window; no route or LLM request."
        },new JsonSerializerOptions{WriteIndented=true}));
        houseFinished=true;Stop();TestLog($"physics view native {(pass?"passed":"failed")} gridMismatch={mismatches} shapeMismatch={nativeShapeMismatches} standMismatch={standMismatch}");
    }
}
