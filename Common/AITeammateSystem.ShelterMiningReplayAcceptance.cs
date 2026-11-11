using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private bool shelterMiningReplayStarted,shelterMiningReplayFinished;
    private int shelterMiningReplayStep,shelterMiningReplayFrames,shelterMiningReplayRequestStart,shelterMiningReplayWoodBefore;
    private long shelterMiningReplaySequence;
    private DateTime shelterMiningReplayAt;
    private ShelterWorldAudit? shelterMiningReplayAudit;
    private bool ShelterEbonwoodMine => Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="shelter_ebonwood_mine_native";
    private readonly List<object> shelterMiningReplayEvents=new(),shelterMiningReplayTrace=new();
    private static object ShelterMiningReplayKeyCells()
    {
        var cells=new List<object>();
        foreach(var center in new[]{(x:3275,y:401),(x:3297,y:400),(x:3410,y:407),(x:3427,y:400)})
            for(int x=center.x-6;x<=center.x+6;x++)for(int y=center.y-6;y<=center.y+6;y++)
            {
                if(!WorldGen.InWorld(x,y,2))continue; Tile t=Main.tile[x,y]; int type=t.TileType,wall=t.WallType;
                bool safe=wall>0 && wall<Main.wallHouse.Length && Main.wallHouse[wall];
                if(!safe && wall!=WallID.LivingWoodUnsafe && !(t.HasTile && (Main.tileFrameImportant[type] || Main.tileSolidTop[type] || TileID.Sets.Platforms[type])))continue;
                cells.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsActuated,t.IsHalfBlock,t.Slope});
            }
        return cells;
    }
    private void BeginShelterMiningReplay()
    {
        Stop(); Memory.Clear(); LlmConversationStore.ResetAll(); agentJournal.Clear(); Executor.Clear(); Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!; Player p=Main.LocalPlayer; npc.Center=new Vector2(3217.7708f*16,399.6875f*16);npc.velocity=Vector2.Zero;
        p.Center=Stand(3173,395);p.velocity=Vector2.Zero;Loadout.Reset();Loadout.EnsureStarterTools();
        shelterMiningReplayAudit=ShelterWorldAudit.Capture(3220,390);shelterMiningReplayRequestStart=PrimaryDecisionRequestCount;
        shelterMiningReplaySequence=agentJournal.LastSequence;shelterMiningReplayAt=DateTime.UtcNow;shelterMiningReplayStarted=true;
        shelterMiningReplayEvents.Add(new{phase="baseline",tick=Main.GameUpdateCount,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},
            inventory=ShelterCounts(Loadout.Inventory),audit=shelterMiningReplayAudit.BaselineReport,preservation=shelterMiningReplayAudit.CheckPreservation(),keyCells=ShelterMiningReplayKeyCells()});
        if(ShelterEbonwoodMine){string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));ulong tick=Main.GameUpdateCount;
            object product=TreeProductWorldObserver.Observe(3427,400),preview=Executor.InspectMiningInteraction(npc,"wood",3427,400);
            string after=JsonSerializer.Serialize(CaptureMinePreviewState(npc,p));
            shelterMiningReplayEvents.Add(new{phase="ebonwood_pre_mine_prediction",tick,sameTick=tick==Main.GameUpdateCount,snapshotEqual=before==after,product,preview});}
        DispatchShelterMiningReplay();
    }
    private void DispatchShelterMiningReplay()
    {
        var task=ShelterEbonwoodMine ? new AIPlan{Action=AIActionType.Mine,Target="wood",Count=36,X=3427,Y=400} : shelterMiningReplayStep switch{
            0=>new AIPlan{Action=AIActionType.Mine,Target="wood",Count=70},
            1=>new AIPlan{Action=AIActionType.Mine,Target="wood",Count=20,X=3275,Y=401},
            _=>new AIPlan{Action=AIActionType.Mine,Target="wood",Count=20,X=3297,Y=400}};
        shelterMiningReplayWoodBefore=Loadout.CountItem(ItemID.Wood);
        shelterMiningReplayEvents.Add(new{phase="dispatch",step=shelterMiningReplayStep,frame=shelterMiningReplayFrames,tick=Main.GameUpdateCount,task,woodBefore=shelterMiningReplayWoodBefore});
        Executor.Apply(task);
    }
    private void TickShelterMiningReplay()
    {
        if(Main.gameMenu || CompanionNpc==null)return; NPC npc=CompanionNpc;Player p=Main.LocalPlayer;shelterMiningReplayFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(shelterMiningReplayFrames%30==0)shelterMiningReplayTrace.Add(new{frame=shelterMiningReplayFrames,step=shelterMiningReplayStep,tick=Main.GameUpdateCount,
            npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16,vx=npc.velocity.X,vy=npc.velocity.Y},inventory=ShelterCounts(Loadout.Inventory),action=Executor.CurrentAction.ToString(),warps=Executor.NavigationTeleportCount});
        if(Executor.NavigationTeleportCount>0 || DateTime.UtcNow-shelterMiningReplayAt>TimeSpan.FromMinutes(5)){FinishShelterMiningReplay("stopped","teleport or deadline");return;}
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>shelterMiningReplaySequence);
        if(outcome==null)return;shelterMiningReplaySequence=outcome.Sequence;
        shelterMiningReplayEvents.Add(new{phase="outcome",step=shelterMiningReplayStep,frame=shelterMiningReplayFrames,tick=Main.GameUpdateCount,outcome,
            npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},woodBefore=shelterMiningReplayWoodBefore,woodAfter=Loadout.CountItem(ItemID.Wood),
            inventory=ShelterCounts(Loadout.Inventory),ebonwood=Loadout.CountItem(ItemID.Ebonwood),preservation=shelterMiningReplayAudit!.CheckPreservation(),keyCells=ShelterMiningReplayKeyCells()});
        if(++shelterMiningReplayStep>=(ShelterEbonwoodMine?1:3)){FinishShelterMiningReplay("observed","");return;}DispatchShelterMiningReplay();
    }
    private void FinishShelterMiningReplay(string status,string error)
    {
        shelterMiningReplayFinished=true;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","shelter-mining-replay-result.json"),JsonSerializer.Serialize(new{
            status,error,frames=shelterMiningReplayFrames,requests=PrimaryDecisionRequestCount-shelterMiningReplayRequestStart,teleports=Executor.NavigationTeleportCount,
            inventory=ShelterCounts(Loadout.Inventory),finalPreservation=shelterMiningReplayAudit?.CheckPreservation(),events=shelterMiningReplayEvents,trace=shelterMiningReplayTrace,
            scope=ShelterEbonwoodMine?"Controlled zero-API explicit wood36 at model-observed seed 3427,400; starter tools only, no resource injection or reposition. Actual ebonwood count and outcome are separate; this is not autonomous source selection. Hostiles removed/player protected; audit is bounded.":"Controlled zero-API original-house-copy replay: automatic wood70 then explicit wood20 at two previously model-inspected seeds. Starter tools only; one initial placement, no resources injected or mid-case reposition. Hostiles removed/player protected; this does not prove autonomous source selection. Preservation audit is bounded."
        },new JsonSerializerOptions{WriteIndented=false}));Stop();Executor.Clear(true);
    }
}
