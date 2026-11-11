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
    private bool stage3MineStarted,stage3MineFinished;
    private int stage3MineFrames;
    private int stage3MinePhase;
    private long stage3LastSequence;
    private DateTime stage3MineAt;
    private string stage3PrimaryPreview="",stage3StoneOptions="",stage3WoodOptions="";
    private bool stage3ReadOnlySnapshotEqual;
    private readonly List<object> stage3Probes=new();
    private readonly List<object> stage3Chain=new();
    private void BeginStage3MineAcceptance()
    {
        NPC npc=CompanionNpc!;Player player=Main.LocalPlayer;
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear();Executor.ForgetBlockedGoal();
        npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;player.Center=Stand(3173,395);player.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();Executor.ResetNavigationTelemetry();
        string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));
        stage3PrimaryPreview=AgentKnowledge.Query("stone","mine",npc,player,Executor,3237,417);
        stage3StoneOptions=AgentKnowledge.Query("stone","mine",npc,player,Executor);
        stage3WoodOptions=AgentKnowledge.Query("wood","mine",npc,player,Executor);
        var selected=new Dictionary<string,List<(int x,int y)>>{{"stone",new()},{"wood",new()}};
        for(int y=380;y<=440;y++)for(int x=3180;x<=3280;x++)
        {
            var c=NavigationWorldObserver.ReadMiningCell(x,y);string kind=c.ResourceTarget;
            if(!c.HasTile || c.Actuated || c.StructureProtected || !c.NativeCanKill || !selected.ContainsKey(kind) || selected[kind].Count>=3 ||
                selected[kind].Any(p=>Math.Abs(p.x-x)+Math.Abs(p.y-y)<8))continue;
            selected[kind].Add((x,y));
        }
        foreach(var group in selected)foreach(var pos in group.Value)
        {
            string result=AgentKnowledge.Query(group.Key,"mine",npc,player,Executor,pos.x,pos.y);
            int actorX=(int)(npc.Center.X/16),actorY=(int)(npc.Center.Y/16);
            bool inOldStoneAutoRange=group.Key=="stone" && Math.Abs(pos.x-actorX)<=50 && Math.Abs(pos.y-actorY)<=32 &&
                Vector2.Distance(npc.Center,new Vector2(pos.x*16+8,pos.y*16+8))<=55*16;
            stage3Probes.Add(new{resource=group.Key,pos.x,pos.y,inOldStoneAutoRange,
                rangeNote=group.Key=="stone"?"Old FindOre range only, not proof this candidate was selected.":"Wood search uses a separate FindTree range; this sample is not classified as its coverage.",result=JsonSerializer.Deserialize<JsonElement>(result)});
        }
        stage3ReadOnlySnapshotEqual=before==JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));
        stage3MineAt=DateTime.UtcNow;stage3MineStarted=true;
        Executor.Apply(new AIPlan{Action=AIActionType.Mine,Target="stone",Count=10});
    }
    private void TickStage3MineAcceptance()
    {
        if(Main.gameMenu || CompanionNpc==null)return;
        stage3MineFrames++;Player p=Main.LocalPlayer;p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;
        foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>stage3LastSequence && o.Task.Action is AIActionType.Mine or AIActionType.Come);
        if(Executor.NavigationTeleportCount>0 || stage3MineFrames>7200 || DateTime.UtcNow-stage3MineAt>TimeSpan.FromMinutes(3))
        {FinishStage3Chain("failed","teleport or deadline");return;}
        if(outcome==null)return;
        stage3LastSequence=outcome.Sequence;
        stage3Chain.Add(new{phase=stage3MinePhase,outcome,wood=Loadout.CountItem(ItemID.Wood),stone=Loadout.CountItem(ItemID.StoneBlock),
            actor=new{x=CompanionNpc.Center.X/16,y=CompanionNpc.Center.Y/16,bottom=CompanionNpc.Bottom.Y/16}});
        if(stage3MinePhase==0)
        {
            File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","stage3-mine-result.json"),JsonSerializer.Serialize(new{
                status="observed",frames=stage3MineFrames,teleports=Executor.NavigationTeleportCount,
                seedPreview=JsonSerializer.Deserialize<JsonElement>(stage3PrimaryPreview),stoneOptions=JsonSerializer.Deserialize<JsonElement>(stage3StoneOptions),
                woodOptions=JsonSerializer.Deserialize<JsonElement>(stage3WoodOptions),stage3ReadOnlySnapshotEqual,
                probes=stage3Probes,outcome,stone=Loadout.CountItem(ItemID.StoneBlock),
                actor=new{x=CompanionNpc.Center.X/16,y=CompanionNpc.Center.Y/16,bottom=CompanionNpc.Bottom.Y/16},
                probeBounds=new{left=3180,right=3280,top=380,bottom=440,maxPerResource=3,minManhattanSeparation=8},
                scope="Fresh original-house copy, one initial actor setup; no seeded resources; hostile NPCs removed/player protected; previews are bounded graph queries, not walked paths; no LLM."
            },new JsonSerializerOptions{WriteIndented=true}));
            stage3MinePhase=1;Executor.Apply(new AIPlan{Action=AIActionType.Mine,Target="wood",Count=20});return;
        }
        if(outcome.Status!="succeeded"){FinishStage3Chain("failed",$"phase {stage3MinePhase}: {outcome.Result}");return;}
        if(stage3MinePhase==1)
        {stage3MinePhase=2;if(Loadout.CountItem(ItemID.StoneBlock)<10){Executor.Apply(new AIPlan{Action=AIActionType.Mine,Target="stone",Count=10-Loadout.CountItem(ItemID.StoneBlock)});return;}}
        if(stage3MinePhase==2){stage3MinePhase=3;Executor.Apply(new AIPlan{Action=AIActionType.Come});return;}
        if(stage3MinePhase==3)FinishStage3Chain("observed","");
    }
    private void FinishStage3Chain(string status,string error)
    {
        stage3MineFinished=true;NPC? npc=CompanionNpc;Player p=Main.LocalPlayer;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","stage3-mine-chain-result.json"),JsonSerializer.Serialize(new{
            status,error,frames=stage3MineFrames,teleports=Executor.NavigationTeleportCount,wood=Loadout.CountItem(ItemID.Wood),stone=Loadout.CountItem(ItemID.StoneBlock),
            horizontalPixels=npc==null?(float?)null:Math.Abs(npc.Center.X-p.Center.X),verticalFeetPixels=npc==null?(float?)null:Math.Abs(npc.Bottom.Y-p.Bottom.Y),
            chain=stage3Chain,scope="One initial setup in fresh original-house copy, no seeded resources; sequential direct native tasks, not model autonomy; hostiles removed/player protected; no LLM."
        },new JsonSerializerOptions{WriteIndented=true}));
        Stop();TestLog($"stage3 native chain {status}: {error}");
    }
}
