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
    private void RunShelterProtectionOriginNative()
    {
        Stop();Executor.Clear(true);Executor.ResetNavigationTelemetry();NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;Loadout.Reset();Loadout.EnsureStarterTools();
        var audit=ShelterWorldAudit.Capture(3220,390);ulong tick=Main.GameUpdateCount;int requests=PrimaryDecisionRequestCount;
        string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));var preserveBefore=audit.CheckPreservation();
        var cells=new List<object>();foreach(var point in new[]{(x:3098,y:399),(x:3416,y:406)})
            for(int x=point.x-3;x<=point.x+3;x++)for(int y=point.y-3;y<=point.y+3;y++)
            {if(!WorldGen.InWorld(x,y,2))continue;Tile t=Main.tile[x,y];int type=t.TileType;
                cells.Add(new{region=point,x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsActuated,t.IsHalfBlock,t.Slope,
                    solid=t.HasTile && Main.tileSolid[type],solidTop=t.HasTile && Main.tileSolidTop[type],frameImportant=t.HasTile && Main.tileFrameImportant[type],
                    isCorruptPlants=t.HasTile && type==TileID.CorruptPlants});}
        object origin3098=audit.ExplainProtection(3098,399),origin3416=audit.ExplainProtection(3416,406);
        object preview3098=Executor.InspectShelterBuild(npc,"ebonwood",3098,393),preview3100=Executor.InspectShelterBuild(npc,"ebonwood",3100,393);
        string after=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));var preserveAfter=audit.CheckPreservation();
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","shelter-protection-origin-native-result.json"),JsonSerializer.Serialize(new{
            tick,finishedTick=Main.GameUpdateCount,sameTick=tick==Main.GameUpdateCount,snapshotEqual=before==after,requests=PrimaryDecisionRequestCount-requests,teleports=Executor.NavigationTeleportCount,
            baseline=audit.BaselineReport,preserveBefore,preserveAfter,origin3098,origin3416,preview3098,preview3100,cells,
            corruptPlantsNativeFlags=new{tileType=TileID.CorruptPlants,solid=Main.tileSolid[TileID.CorruptPlants],solidTop=Main.tileSolidTop[TileID.CorruptPlants],frameImportant=Main.tileFrameImportant[TileID.CorruptPlants]},
            scope="Fresh original-house copy; same-tick bounded read-only diagnosis of immutable audit mask and native build previews. Nearby cells are +/-3 tiles; actor/world/inventory snapshot is bounded. No placement, clearing, mining, movement, or LLM call; RNG is not directly captured."
        },new JsonSerializerOptions{WriteIndented=false}));houseFinished=true;Stop();Executor.Clear(true);
    }
}
