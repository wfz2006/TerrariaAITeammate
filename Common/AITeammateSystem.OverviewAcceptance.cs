using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Terraria;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private void RunOverviewNativeAcceptance()
    {
        NPC npc=CompanionNpc!;Player player=Main.LocalPlayer;
        string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));
        JsonNode noSelection=JsonNode.Parse(AgentKnowledge.Query("","structure_detail",npc,player,Executor))!;
        string nativeText=AgentKnowledge.Query("3200,390","structure",npc,player,Executor);
        JsonNode native=JsonNode.Parse(nativeText)!;
        string state=JsonSerializer.Serialize(new {knowledge=new {selectedStructure=JsonSerializer.Deserialize<JsonElement>(nativeText)},planning=new {sourcePlayerRequest="原始请求"}});
        JsonObject wire=PlanningRequest.CompactState(state,useStructureOverview:true);
        JsonNode overview=wire["knowledge"]!["selectedStructure"]!;
        string detailText=AgentKnowledge.Query("","structure_detail",npc,player,Executor);
        JsonNode detail=JsonNode.Parse(detailText)!;
        string after=JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));
        JsonNode rawObs=native["observation"]!,rawGrid=rawObs["grid"]!,viewObs=overview["observation"]!,viewGrid=viewObs["grid"]!;
        var legend=((JsonArray)rawGrid["legend"]!).OfType<JsonObject>().ToDictionary(e=>e["key"]!.GetValue<int>());
        int width=rawGrid["width"]!.GetValue<int>(),height=rawGrid["height"]!.GetValue<int>(),left=rawGrid["x"]!.GetValue<int>(),top=rawGrid["y"]!.GetValue<int>();
        var cells=new List<(int x,int y,JsonObject entry)>();
        int rowIndex=0;
        foreach(JsonNode? row in (JsonArray)rawGrid["rows"]!)
        {
            int x=left;foreach(string run in row!.GetValue<string>().Split(','))
            {string[] p=run.Split(':');int key=int.Parse(p[0]),count=int.Parse(p[1]);for(int i=0;i<count;i++)cells.Add((x++,top+rowIndex,legend[key]));}
            rowIndex++;
        }
        object CheckCounts(JsonNode report,int x,int y,int w,int h)
        {
            var selected=cells.Where(c=>c.x>=x&&c.x<x+w&&c.y>=y&&c.y<y+h).ToArray();
            bool ok=report["observedCells"]!.GetValue<int>()==selected.Length && report["unobservedCells"]!.GetValue<long>()==(long)w*h-selected.Length;
            ok &= report["emptyTileCells"]!.GetValue<int>()==selected.Count(c=>c.entry["hasTile"]?.GetValue<bool>()==false);
            ok &= report["noWallCells"]!.GetValue<int>()==selected.Count(c=>c.entry["wallType"]?.GetValue<int>()==0);
            foreach(var kind in new[]{(name:"tileCells",type:"tileType",has:"hasTile"),(name:"wallCells",type:"wallType",has:"")})
            {
                var expected=selected.Where(c=>kind.has==""?c.entry[kind.type]?.GetValue<int>()>0:c.entry[kind.has]?.GetValue<bool>()==true)
                    .GroupBy(c=>c.entry[kind.type]!.GetValue<int>()).ToDictionary(g=>g.Key,g=>g.Count());
                var actual=((JsonArray)report[kind.name]!).OfType<JsonObject>().ToDictionary(e=>e[kind.type]!.GetValue<int>(),e=>e["observedCells"]!.GetValue<int>());
                ok &= expected.Count==actual.Count && expected.All(e=>actual.TryGetValue(e.Key,out int v)&&v==e.Value);
                string prefix=kind.name=="tileCells"?"tile":"wall";
                foreach(JsonObject entry in ((JsonArray)report[kind.name]!).OfType<JsonObject>())
                {
                    int type=entry[kind.type]!.GetValue<int>();var group=selected.Where(c=>c.entry[kind.type]?.GetValue<int>()==type && (kind.has=="" || c.entry[kind.has]?.GetValue<bool>()==true)).ToArray();
                    var names=group.Select(c=>c.entry[prefix+"Name"]?.ToJsonString()??"null").Distinct().Order().ToArray();
                    var ids=group.Select(c=>c.entry[prefix+"ItemId"]?.GetValue<int>()??0).Where(v=>v>0).Distinct().Order().ToArray();
                    ok &= names.SequenceEqual(((JsonArray)entry["names"]!).Select(n=>n?.ToJsonString()??"null").Order()) && ids.SequenceEqual(((JsonArray)entry["placementItemIds"]!).Select(n=>n!.GetValue<int>()).Order());
                    ok &= entry["unmappedCells"]!.GetValue<int>()==group.Count(c=>(c.entry[prefix+"ItemId"]?.GetValue<int>()??0)<=0);
                }
            }
            return new {ok,expectedObserved=selected.Length,reportedObserved=report["observedCells"]!.GetValue<int>()};
        }
        JsonNode counts=viewObs["materialCellCounts"]!,sample=counts["sampleGrid"]!,bounds=counts["reportedBounds"]!;
        JsonNode cb=sample["countBounds"]!,rb=bounds["countBounds"]!;
        object sampleCheck=CheckCounts(sample,cb["x"]!.GetValue<int>(),cb["y"]!.GetValue<int>(),cb["width"]!.GetValue<int>(),cb["height"]!.GetValue<int>());
        object boundsCheck=CheckCounts(bounds,rb["x"]!.GetValue<int>(),rb["y"]!.GetValue<int>(),rb["width"]!.GetValue<int>(),rb["height"]!.GetValue<int>());
        bool sampleOk=(bool)sampleCheck.GetType().GetProperty("ok")!.GetValue(sampleCheck)!,boundsOk=(bool)boundsCheck.GetType().GetProperty("ok")!.GetValue(boundsCheck)!;
        bool shapeDimensions=((JsonArray)viewGrid["shapeRows"]!).Count==height && ((JsonArray)viewGrid["shapeRows"]!).All(r=>r!.GetValue<string>().Length==width);
        char Glyph(JsonObject e)
        {
            bool? B(string k)=>e[k] is JsonValue v&&v.TryGetValue(out bool b)?b:null;
            int? I(string k)=>e[k] is JsonValue v&&v.TryGetValue(out int n)?n:null;
            if(B("hasTile")==null)return '+';if(B("hasTile")==false)return I("wallType")==0?'.':I("wallType")>0?':':'+';
            if(B("actuated")==true)return 'a';if(B("halfBlock")==true)return 'h';int? slope=I("slope");if(slope is >=1 and <=4)return (char)('0'+slope.Value);
            if(B("door")==true)return 'D';if(B("platform")==true)return '=';if(B("solid")==true)return '#';if(B("furniture")==true&&B("solid")==false)return 'f';return '+';
        }
        int shapeMismatches=0;var shapeRows=(JsonArray)viewGrid["shapeRows"]!;
        for(int i=0;i<cells.Count && shapeDimensions;i++)if(shapeRows[i/width]!.GetValue<string>()[i%width]!=Glyph(cells[i].entry))shapeMismatches++;
        JsonNode envelope=new JsonObject{["state"]=wire.DeepClone()};
        JsonNode? Decode(JsonNode? value)
        {
            if(value is JsonObject o && o["$ref"] is JsonValue r && r.TryGetValue(out string? path) && path!=null && path.StartsWith("#/state/",StringComparison.Ordinal))
            {
                JsonNode? current=envelope;foreach(string part in path.Substring(2).Split('/'))current=current?[part.Replace("~1","/").Replace("~0","~")];
                if(current==null)throw new InvalidOperationException("Unresolved wire reference: "+path);return Decode(current);
            }
            if(value is JsonObject table && table["encoding"]?.GetValue<string>()=="columns_and_rows_v1" && table["columns"] is JsonArray columns && table["rows"] is JsonArray rows)
            {
                var expanded=new JsonArray();foreach(JsonArray row in rows.OfType<JsonArray>())
                {var entry=new JsonObject();for(int i=0;i<columns.Count;i++)entry[columns[i]!.GetValue<string>()]=Decode(row[i]);expanded.Add(entry);}return expanded;
            }
            if(value is JsonObject obj){var copy=new JsonObject();foreach(var p in obj)copy[p.Key]=Decode(p.Value);return copy;}
            if(value is JsonArray arr){var copy=new JsonArray();foreach(JsonNode? n in arr)copy.Add(Decode(n));return copy;}
            return value?.DeepClone();
        }
        bool metadataEqual=true;
        foreach(string field in new[]{"topic","available","error","worldId","observedAtTick","sampling","referenceAtSelection","placementCatalog"})metadataEqual &= JsonNode.DeepEquals(native[field],Decode(overview[field]));
        foreach(string field in new[]{"bounds","surfaces","standingPositions","objects","rooms","signature","anchorX","anchorY","truncated"})metadataEqual &= JsonNode.DeepEquals(rawObs[field],Decode(viewObs[field]));
        JsonNode direct=StructureOverviewView.Project(native.AsObject());bool directMetadataEqual=true;
        foreach(string field in new[]{"topic","available","error","worldId","observedAtTick","sampling","referenceAtSelection","placementCatalog"})directMetadataEqual &= JsonNode.DeepEquals(native[field],direct[field]);
        foreach(string field in new[]{"bounds","surfaces","standingPositions","objects","rooms","signature","anchorX","anchorY","truncated"})directMetadataEqual &= JsonNode.DeepEquals(rawObs[field],direct["observation"]?[field]);
        bool detailGrid=JsonNode.DeepEquals(rawGrid,detail["observation"]?["grid"]),detailFrames=detail["observation"]?["grid"]?["legend"] is JsonArray detailLegend && detailLegend.OfType<JsonObject>().All(e=>e.ContainsKey("frameX")&&e.ContainsKey("frameY"));
        var detailWire=PlanningRequest.CompactState(JsonSerializer.Serialize(new {knowledge=new{selectedStructure=JsonSerializer.Deserialize<JsonElement>(detailText)}}),usePhysicsStructureView:true,useStructureOverview:true);
        bool detailNotProjected=detailWire["knowledge"]?["selectedStructure"]?["observation"]?["grid"]?["view"]==null;
        JsonNode? latest=JsonSerializer.SerializeToNode(StructureWorldObserver.RefreshLatest(npc));
        bool latestNotSticky=latest?["topic"]?.GetValue<string>()=="structure" && latest?["observation"]?["grid"]?["rows"]!=null;
        bool noSelectionUnavailable=noSelection["available"]?.GetValue<bool>()==false && noSelection["topic"]?.GetValue<string>()=="structure_detail";
        bool snapshotEqual=before==after;
        bool pass=sampleOk&&boundsOk&&shapeDimensions&&shapeMismatches==0&&metadataEqual&&directMetadataEqual&&detailGrid&&detailFrames&&detailNotProjected&&latestNotSticky&&noSelectionUnavailable&&snapshotEqual&&cells.Count==width*height;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","overview-native-result.json"),JsonSerializer.Serialize(new{status=pass?"passed":"failed",sampleCheck,boundsCheck,shapeDimensions,shapeMismatches,metadataEqual,directMetadataEqual,detailGrid,detailFrames,detailNotProjected,latestNotSticky,noSelectionUnavailable,snapshotEqual,cells=cells.Count,width,height,native,overview,detail,detailWire,noSelection,
            scope="Copied real house; same-query snapshot limited to recorded actor/inventory/world window; no route or LLM request."},new JsonSerializerOptions{WriteIndented=true}));
        houseFinished=true;Stop();TestLog($"overview native {(pass?"passed":"failed")} sample={sampleOk} bounds={boundsOk} detail={detailGrid} snapshot={snapshotEqual}");
    }
}
