using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private void RunShelterMiningNative()
    {
        Stop(); Executor.Clear(true); NPC npc=CompanionNpc!; Player player=Main.LocalPlayer;
        npc.Center=new Vector2(3217.7708f*16,399.6875f*16); npc.velocity=Vector2.Zero;
        player.Center=Stand(3173,395); player.velocity=Vector2.Zero;
        Loadout.Reset(); Loadout.EnsureStarterTools();
        SetActiveGoal("原屋采矿和住房只读诊断", "零 API 受控保护和住房诊断");
        ulong tick=Main.GameUpdateCount; int requestStart=PrimaryDecisionRequestCount;
        string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));
        var housingConditions=new List<GoalCondition>{
            new(){Type="housing_valid",X=3216,Y=373},
            new(){Type="new_housing_valid",X=3216,Y=373},
            new(){Type="new_housing_valid",X=3220,Y=340}};
        var housing=GoalWorldObserver.Capture(this,housingConditions,goalBossDeathBoundary);
        object productionDebug=PreservationEvidence.DebugSnapshot();
        var treeProducts=new[]{new{x=3262,y=398,product=TreeProductWorldObserver.Observe(3262,398),preview=Executor.InspectMiningInteraction(npc,"wood",3262,398)},
            new{x=3427,y=400,product=TreeProductWorldObserver.Observe(3427,400),preview=Executor.InspectMiningInteraction(npc,"wood",3427,400)},
            new{x=3410,y=407,product=TreeProductWorldObserver.Observe(3410,407),preview=Executor.InspectMiningInteraction(npc,"wood",3410,407)}};
        object worldWood=WorldKnowledgeObserver.Query("wood;limit=8",npc);
        JsonElement worldStone=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Query("stone;minDistance=0;maxDistance=80;limit=8",npc));
        JsonElement worldStoneCustomOrigin=JsonSerializer.SerializeToElement(WorldKnowledgeObserver.Query("stone;minDistance=0;maxDistance=80;limit=8",npc,3100,390));
        var stoneSamples=new List<object>();
        if(worldStone.TryGetProperty("data",out var data) && data.TryGetProperty("entries",out var entries) && entries.ValueKind==JsonValueKind.Array)
            foreach(var entry in entries.EnumerateArray())if(entry.TryGetProperty("samples",out var samples) && samples.ValueKind==JsonValueKind.Array)
                foreach(var sample in samples.EnumerateArray())if(sample.TryGetProperty("x",out var sx) && sample.TryGetProperty("y",out var sy))
                {int x=sx.GetInt32(),y=sy.GetInt32();stoneSamples.Add(new{x,y,worldSample=sample.Clone(),fullInspect=Executor.InspectMiningInteraction(npc,"stone",x,y)});}
        var fixedAccessChecks=new[]{new{x=3364,y=408,resource="stone",geometry=MiningAccessWorldObserver.Observe(npc,3364,408),fullInspect=Executor.InspectMiningInteraction(npc,"stone",3364,408)},
            new{x=3427,y=400,resource="wood",geometry=MiningAccessWorldObserver.Observe(npc,3427,400),fullInspect=Executor.InspectMiningInteraction(npc,"wood",3427,400)},
            new{x=3262,y=398,resource="wood",geometry=MiningAccessWorldObserver.Observe(npc,3262,398),fullInspect=Executor.InspectMiningInteraction(npc,"wood",3262,398)}};
        var reports=new List<object>();
        foreach(var seed in new[]{(x:3262,y:398),(x:3275,y:401),(x:3297,y:400),(x:3324,y:399),(x:3410,y:407)})
        {
            var nearby=new List<object>();
            for(int x=seed.x-6;x<=seed.x+6;x++)for(int y=seed.y-6;y<=seed.y+6;y++)
            {
                if(!WorldGen.InWorld(x,y,2))continue;
                Tile t=Main.tile[x,y]; int type=t.TileType,wall=t.WallType;
                bool safeWall=wall>0 && wall<Main.wallHouse.Length && Main.wallHouse[wall];
                bool livingWall=wall==WallID.LivingWoodUnsafe;
                bool important=t.HasTile && Main.tileFrameImportant[type];
                bool solidTop=t.HasTile && Main.tileSolidTop[type];
                bool platform=t.HasTile && TileID.Sets.Platforms[type];
                if(!important && !solidTop && !platform && !safeWall && !livingWall && (x!=seed.x || y!=seed.y))continue;
                nearby.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsActuated,t.IsHalfBlock,t.Slope,
                    frameImportant=important,solid=t.HasTile && Main.tileSolid[type],solidTop,platform,safeWall,livingWoodUnsafeWall=livingWall,
                    treeTrunk=t.HasTile && TileID.Sets.IsATreeTrunk[type],vine=t.HasTile && TileID.Sets.IsVine[type],
                    flora=t.HasTile && (type is TileID.Plants or TileID.Plants2)});
            }
            reports.Add(new{seed.x,seed.y,productionProtected=AIExecutor.IsPartOfHouseOrStructure(seed.x,seed.y),
                inspect=Executor.InspectMiningInteraction(npc,"wood",seed.x,seed.y),nearby});
        }
        string after=JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","shelter-mining-native-result.json"),JsonSerializer.Serialize(new{
            tick,finishedTick=Main.GameUpdateCount,requests=PrimaryDecisionRequestCount-requestStart,teleports=Executor.NavigationTeleportCount,
            sameTick=tick==Main.GameUpdateCount,snapshotEqual=before==after,housingConditions,housing.NativeConditions,productionDebug,treeProducts,worldWood,worldStone,worldStoneCustomOrigin,stoneSamples,fixedAccessChecks,reports,
            scope="Fresh original-house copy; actor/player initialized once and starter tools only. Each seed records its actual cell and relevant frame-important/solid-top/platform objects and safe/living-wood walls within +/-6 tiles. Same-tick read-only native protection and mining preview; no mining, world edits, or LLM calls. Bounded snapshot equality is not a full-world proof."
        },new JsonSerializerOptions{WriteIndented=false}));
        houseFinished=true; Stop(); Executor.Clear(true);
    }
}
