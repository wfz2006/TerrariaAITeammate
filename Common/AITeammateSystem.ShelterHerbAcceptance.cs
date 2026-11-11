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
    private void RunShelterHerbNative()
    {
        Stop();Executor.Clear(true);Executor.ResetNavigationTelemetry();NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;Loadout.Reset();Loadout.EnsureStarterTools();
        SetActiveGoal("受控原生草药保护诊断","零 API 原生生长诊断");var audit=ShelterWorldAudit.Capture(3220,390);
        int requests=PrimaryDecisionRequestCount;ulong tick=Main.GameUpdateCount;const int x=3067,y=511;Tile tile=Main.tile[x,y];
        ushort originalType=tile.TileType;byte originalPaint=tile.TileColor;bool originalHas=tile.HasTile;short originalFrameX=tile.TileFrameX,originalFrameY=tile.TileFrameY;
        object Cell()=>new{tile.HasTile,tile.TileType,tile.TileFrameX,tile.TileFrameY,tile.TileColor,tile.WallType,tile.LiquidAmount,tile.IsHalfBlock,tile.Slope,tile.IsActuated,
            solid=tile.HasTile && Main.tileSolid[tile.TileType],solidTop=tile.HasTile && Main.tileSolidTop[tile.TileType],frameImportant=tile.HasTile && Main.tileFrameImportant[tile.TileType]};
        var calls=new List<object>();object initial=Cell();var initialAudit=audit.CheckPreservation();
        bool compatible=tile.HasTile && (tile.TileType is TileID.MatureHerbs or TileID.BloomingHerbs) && tile.TileFrameX==36 && tile.TileFrameY==0 && tile.LiquidAmount==0 &&
            !tile.IsHalfBlock && (int)tile.Slope==0 && !tile.IsActuated && !Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType];
        bool forward=false,reverse=false,forwardPreserved=false,reversePreserved=false,paintRejected=false,restored=false;object? afterForward=null,afterReverse=null,paintAudit=null,forwardProduction=null,reverseProduction=null,paintProduction=null;
        GoalNativeConditionResult Observe()=>PreservationEvidence.Read(Main.worldID,WorldKnowledgeObserver.LoadEpoch,goalStartSequence,activeGoal,Main.GameUpdateCount);
        try
        {
            if(compatible)
            {
                ushort target=originalType==TileID.MatureHerbs?TileID.BloomingHerbs:TileID.MatureHerbs;
                for(int i=0;i<64 && tile.TileType==originalType;i++){object prior=Cell();WorldGen.GrowAlch(x,y);calls.Add(new{direction="forward",attempt=i+1,before=prior,after=Cell()});}
                forward=tile.TileType==target;afterForward=Cell();var a=audit.CheckPreservation();forwardPreserved=a.available && a.preserved;forwardProduction=Observe();
                if(forward){for(int i=0;i<64 && tile.TileType==target;i++){object prior=Cell();WorldGen.GrowAlch(x,y);calls.Add(new{direction="reverse",attempt=i+1,before=prior,after=Cell()});}}
                reverse=tile.TileType==originalType;afterReverse=Cell();var b=audit.CheckPreservation();reversePreserved=b.available && b.preserved;reverseProduction=Observe();
                if(reverse){tile.TileColor=(byte)(originalPaint==0?1:0);paintAudit=audit.CheckPreservation();paintProduction=Observe();paintRejected=paintAudit is ShelterPreservationReport report && report.available && !report.preserved;}
            }
        }
        finally
        {
            tile.TileColor=originalPaint;tile.TileType=originalType;tile.HasTile=originalHas;tile.TileFrameX=originalFrameX;tile.TileFrameY=originalFrameY;
            restored=tile.TileType==originalType && tile.HasTile==originalHas && tile.TileColor==originalPaint && tile.TileFrameX==originalFrameX && tile.TileFrameY==originalFrameY;
        }
        var finalAudit=audit.CheckPreservation();var finalProduction=Observe();
        bool pass=compatible && forward && reverse && forwardPreserved && reversePreserved && paintRejected &&
            forwardProduction is GoalNativeConditionResult fp && fp.Met==true && reverseProduction is GoalNativeConditionResult rp && rp.Met==true &&
            paintProduction is GoalNativeConditionResult pp && pp.Met==false && restored && finalAudit.available && finalAudit.preserved && finalProduction.Met==true &&
            PrimaryDecisionRequestCount==requests && Executor.NavigationTeleportCount==0;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","shelter-herb-native-result.json"),JsonSerializer.Serialize(new{
            status=pass?"passed":"failed_or_uncovered",tick,finishedTick=Main.GameUpdateCount,requests=PrimaryDecisionRequestCount-requests,teleports=Executor.NavigationTeleportCount,
            checks=new{compatible,forward,reverse,forwardPreserved,reversePreserved,paintRejected,restored,finalPreserved=finalAudit.preserved},initial,initialAudit,afterForward,forwardProduction,afterReverse,reverseProduction,paintAudit,paintProduction,finalAudit,finalProduction,calls,
            scope="Fresh original-house copy; controlled native WorldGen.GrowAlch on the existing herb (3067,511), at most 64 calls per direction. This consumes native RNG and may invoke native frame/network operations; it is not a read-only or whole-world equivalence test. Paint is a deliberate same-tick negative mutation, then the test cell is restored. No LLM or teleport."
        },new JsonSerializerOptions{WriteIndented=false}));houseFinished=true;Stop();Executor.Clear(true);
    }
}
