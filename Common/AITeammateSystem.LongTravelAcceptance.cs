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
    private bool longTravelStarted,longTravelFinished;
    private int longTravelLeg,longTravelFrames,longTravelLegFrames;
    private long longTravelSequence;
    private DateTime longTravelAt,longTravelLegAt;
    private readonly List<(string name,int x,int y)> longTravelTargets=new();
    private readonly List<object> longTravelEvents=new(),longTravelTrace=new(),longTravelGeometry=new();
    private bool longTravelModelTarget; private JsonElement longTravelTerrain; private bool longTravelTerrainUnchanged;

    private void BeginLongTravelModelTarget()
    {
        Stop(); Memory.Clear(); LlmConversationStore.ResetAll(); agentJournal.Clear(); Executor.Clear(); Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!; Player player=Main.LocalPlayer;
        npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;player.Center=Stand(3173,395);player.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();
        string before=JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));
        longTravelTerrain=JsonSerializer.SerializeToElement(NavigationWorldObserver.Terrain("2850,380,60,50",npc));
        longTravelTerrainUnchanged=before==JsonSerializer.Serialize(CaptureMinePreviewState(npc,player));
        longTravelModelTarget=true;longTravelTargets.Add(("previous_model_resource",2880,405));longTravelTargets.Add(("come_player",3173,394));
        for(int y=330;y<=460;y++)for(int x=2840;x<=3260;x++)
        {Tile t=Main.tile[x,y];longTravelGeometry.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.LiquidAmount,t.LiquidType,t.Slope,t.IsHalfBlock,t.IsActuated,t.TileFrameX,t.TileFrameY,
            solid=t.HasTile&&Main.tileSolid[t.TileType],solidTop=t.HasTile&&Main.tileSolidTop[t.TileType],platform=t.HasTile&&TileID.Sets.Platforms[t.TileType]});}
        longTravelStarted=true;longTravelAt=DateTime.UtcNow;
        if(!longTravelTerrain.GetProperty("available").GetBoolean() || !longTravelTerrainUnchanged){FinishLongTravelNative("failed","terrain query unavailable or bounded readonly snapshot changed");return;}
        StartLongTravelLeg();
    }

    private void BeginLongTravelNative()
    {
        Stop(); Memory.Clear(); LlmConversationStore.ResetAll(); agentJournal.Clear(); Executor.Clear(); Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!; Player player=Main.LocalPlayer;
        npc.Center=Stand(3220,401); npc.velocity=Vector2.Zero; player.Center=Stand(3173,395); player.velocity=Vector2.Zero;
        longTravelTargets.Add(("exit_house",3173,394)); longTravelTargets.Add(("far_outbound",3432,406));
        longTravelTargets.Add(("return_house_exterior",3173,394)); longTravelTargets.Add(("roof_enclosed",3219,377)); longTravelTargets.Add(("return_initial",3220,400));
        if(longTravelTargets.Any(t=>!TilePathfinder.IsSafeStand(t.x,t.y))) {longTravelStarted=true;longTravelAt=DateTime.UtcNow;FinishLongTravelNative("uncovered","Original baseline target no longer a native safe stand; no substitute selected.");return;}
        int right=Math.Min(Main.maxTilesX-2,3432+32);
        for(int y=330;y<=450;y++)for(int x=3140;x<=right;x++)
        { Tile t=Main.tile[x,y]; longTravelGeometry.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.LiquidAmount,t.LiquidType,t.Slope,t.IsHalfBlock,t.IsActuated,t.TileFrameX,t.TileFrameY,
            solid=t.HasTile&&Main.tileSolid[t.TileType],solidTop=t.HasTile&&Main.tileSolidTop[t.TileType],platform=t.HasTile&&TileID.Sets.Platforms[t.TileType]}); }
        longTravelStarted=true; longTravelAt=DateTime.UtcNow; StartLongTravelLeg();
    }
    private void StartLongTravelLeg()
    {
        var target=longTravelTargets[longTravelLeg]; NPC npc=CompanionNpc!;
        if(longTravelModelTarget && longTravelLeg==1)
        {longTravelLegAt=DateTime.UtcNow;longTravelLegFrames=0;longTravelEvents.Add(new{phase="dispatch",leg=longTravelLeg,action="Come",player=new{x=Main.LocalPlayer.Center.X/16,y=Main.LocalPlayer.Center.Y/16,feet=Main.LocalPlayer.Bottom.Y/16}});Executor.Apply(new AIPlan{Action=AIActionType.Come});return;}
        var position=new Vector2(target.x*16+8,target.y*16+8);
        bool resolved=TilePathfinder.TryResolveStand(position,out Point stand);
        longTravelLegAt=DateTime.UtcNow;longTravelLegFrames=0;
        longTravelEvents.Add(new{phase="dispatch",leg=longTravelLeg,target=new{target.name,target.x,target.y},requested=new{x=target.x,y=target.y},resolved,resolvedStand=resolved?new{x=stand.X,y=stand.Y,feet=TilePathfinder.StandBottom(stand)/16f}:null,
            actor=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},safeRequested=TilePathfinder.IsSafeStand(target.x,target.y)});
        Executor.Apply(new AIPlan{Action=AIActionType.Explore,X=target.x,Y=target.y});
    }
    private void TickLongTravelNative()
    {
        if(Main.gameMenu || CompanionNpc==null)return; NPC npc=CompanionNpc; Player p=Main.LocalPlayer;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;
        foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        longTravelFrames++;longTravelLegFrames++;
        bool detailed=npc.Center.X/16>=3374 && npc.Center.X/16<=3381;
        if(longTravelFrames%(detailed?5:30)==0)
        {
            var path=ContinuousField("navPath") as List<Point>; int index=ContinuousField("navIndex") is int i?i:-1;
            Point? next=path!=null && index>=0 && index<path.Count?path[index]:null;
            bool resolvedCurrent=TilePathfinder.TryResolveStand(npc.Center,out Point current);
            var goal=ContinuousField("navGoal") is Vector2 v?v:Vector2.Zero;
            longTravelTrace.Add(new{leg=longTravelLeg,frame=longTravelFrames,x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16,
                vx=npc.velocity.X,vy=npc.velocity.Y,pathIndex=index,path=path?.Select(v=>new{x=v.X,y=v.Y}).ToArray(),
                next=next.HasValue?new{x=next.Value.X,y=next.Value.Y,foot=TilePathfinder.StandBottom(next.Value)/16f,safe=TilePathfinder.IsSafeStand(next.Value.X,next.Value.Y),footDeltaPixels=TilePathfinder.StandBottom(next.Value)-npc.Bottom.Y}:null,
                current=resolvedCurrent?new{x=current.X,y=current.Y,foot=TilePathfinder.StandBottom(current)/16f,safe=TilePathfinder.IsSafeStand(current.X,current.Y)}:null,
                navGoal=new{x=goal.X/16,y=goal.Y/16},grounded=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width),
                collider=new{x=npc.position.X,y=npc.position.Y,npc.width,npc.height,solidCollision=Collision.SolidCollision(npc.position,npc.width,npc.height)},
                status=Executor.StatusText(),jumps=Executor.NavigationJumpCount,warps=Executor.NavigationTeleportCount});
        }
        if(Executor.NavigationTeleportCount>0 || DateTime.UtcNow-longTravelAt>TimeSpan.FromSeconds(longTravelModelTarget?180:360) || DateTime.UtcNow-longTravelLegAt>TimeSpan.FromSeconds(180))
        {FinishLongTravelNative("failed","teleport or deadline");return;}
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>longTravelSequence && (o.Task.Action==AIActionType.Explore || longTravelModelTarget && o.Task.Action==AIActionType.Come));
        if(outcome==null)return; longTravelSequence=outcome.Sequence;
        longTravelEvents.Add(new{phase="outcome",leg=longTravelLeg,frames=longTravelLegFrames,outcome,
            actor=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},jumps=Executor.NavigationJumpCount,warps=Executor.NavigationTeleportCount});
        if(outcome.Status!="succeeded"){FinishLongTravelNative("failed","native Explore blocked");return;}
        if(longTravelModelTarget && outcome.Task.Action==AIActionType.Come && (Math.Abs(npc.Center.X-p.Center.X)>48 || Math.Abs(npc.Bottom.Y-p.Bottom.Y)>24 || !TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width)))
        {FinishLongTravelNative("failed","Come did not meet grounded native proximity");return;}
        if(++longTravelLeg>=longTravelTargets.Count){FinishLongTravelNative("passed","");return;}
        StartLongTravelLeg();
    }
    private void FinishLongTravelNative(string status,string error)
    {
        longTravelFinished=true;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate",longTravelModelTarget?"long-travel-model-target-result.json":"long-travel-native-result.json"),JsonSerializer.Serialize(new{
            status,error,targets=longTravelTargets.Select(t=>new{t.name,t.x,t.y}).ToArray(),events=longTravelEvents,trace=longTravelTrace,geometry=longTravelGeometry,
            geometryBounds=longTravelModelTarget?"x2840..3260,y330..460":"x3140..farX+32,y330..450",frames=longTravelFrames,teleports=Executor.NavigationTeleportCount,requests=PrimaryDecisionRequestCount,
            terrainQuery=longTravelModelTarget?(JsonElement?)longTravelTerrain:null,terrainReadonlySnapshotEqual=longTravelModelTarget?(bool?)longTravelTerrainUnchanged:null,
            scope=longTravelModelTarget?"Controlled execution of previously model-chosen target, no new LLM planning; one initial actor setup; no seeded terrain/resources, no intermediate reset or teleport; hostiles removed/player protected; readonly snapshot bounded to house/actor/inventory/task.":"Original house copy; one initial actor setup; no added terrain/resources, no intermediate reset or teleport. Hostile NPCs removed and player protected; graph existence is not travel success. First blocked or deadline stops."
        },new JsonSerializerOptions{WriteIndented=false}));
        Executor.Clear(true);
    }
}
