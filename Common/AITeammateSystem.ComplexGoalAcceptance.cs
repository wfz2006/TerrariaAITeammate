using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private bool complexCombatStarted, complexCombatFinished, complexCombatAttacking, complexCombatPriorHideUI;
    private int complexCombatEnemy=-1, complexCombatFrames, complexCombatFirstHitFrame=-1, complexCombatPrimary, complexCombatCombat, complexCombatWeapon;
    private long complexCombatSequence;
    private float complexCombatPriorZoom;
    private string complexCombatCase="";
    private object? complexCombatInitialGeometry, complexCombatInitialCandidates;
    private readonly List<object> complexCombatTrace=new(), complexCombatEvents=new();
    private string ComplexCombatPath(string suffix)=>Path.Combine(Main.SavePath,"AITeammate",$"complex-combat-{complexCombatCase}-{suffix}");

    private void BeginComplexCombat()
    {
        Stop(); Memory.Clear(); LlmConversationStore.ResetAll(); agentJournal.Clear(); Executor.Clear(true); Executor.ResetNavigationTelemetry();
        complexCombatCase=Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="complex_combat_yoyo" ? "yoyo" : "staff";
        complexCombatWeapon=complexCombatCase=="yoyo" ? ItemID.WoodYoyo : ItemID.AmethystStaff;
        NPC npc=CompanionNpc!; Player player=Main.LocalPlayer;
        Main.dayTime=true;Main.time=18000;Main.raining=false;
        Loadout.Reset();Loadout.EnsureStarterTools();Loadout.AddItem(complexCombatWeapon,1);Loadout.PreferredWeaponType=complexCombatWeapon;Loadout.Mana=Loadout.ManaMax;
        complexCombatPrimary=PrimaryDecisionRequestCount;complexCombatCombat=CombatDecisionRequestCount;complexCombatSequence=agentJournal.LastSequence;
        complexCombatPriorHideUI=Main.hideUI;complexCombatPriorZoom=Main.GameZoomTarget;Main.hideUI=true;Main.GameZoomTarget=1.6f;
        complexCombatStarted=true;CombatPlannerEnabled=true;
        string ack=ComplexCombatPath("ack.txt");if(File.Exists(ack))File.Delete(ack);
        File.WriteAllText(ComplexCombatPath("ready.txt"),complexCombatCase);
    }

    private void TickComplexCombat()
    {
        if(Main.gameMenu)return;
        if(CompanionNpc==null){FinishComplexCombat("failed","companion missing");return;}
        NPC npc=CompanionNpc;Player player=Main.LocalPlayer;
        player.immune=true;player.immuneTime=600;
        if(!complexCombatAttacking)
        {
            string ack=ComplexCombatPath("ack.txt");if(!File.Exists(ack)||File.ReadAllText(ack).Trim()!=complexCombatCase)return;
            File.Delete(ack);
            npc.Center=new Vector2(51071.574f,6395f);npc.velocity=Vector2.Zero;
            player.Center=new Vector2(50968f,6299f);player.velocity=Vector2.Zero;
            var removed=Main.npc.Where(n=>n.active&&!n.friendly).Select(n=>new{n.whoAmI,n.type,x=n.Center.X,y=n.Center.Y}).ToArray();
            foreach(NPC other in Main.npc)if(other.active&&!other.friendly)other.active=false;
            var unrelated=Main.projectile.Where(pr=>pr.active&&!pr.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion).Select(pr=>new{pr.whoAmI,pr.type,pr.owner,pr.damage,x=pr.Center.X,y=pr.Center.Y}).ToArray();
            complexCombatEnemy=NPC.NewNPC(new EntitySource_Misc("ComplexCombatAcceptance"),51166,6375,NPCID.BlueSlime);
            if(complexCombatEnemy<0||complexCombatEnemy>=Main.maxNPCs){FinishComplexCombat("failed_setup","enemy spawn failed");return;}
            NPC spawned=Main.npc[complexCombatEnemy];spawned.Center=new Vector2(51166f,6375f);spawned.velocity=Vector2.Zero;
            complexCombatInitialGeometry=ComplexCombatGeometry(npc.Center);complexCombatInitialCandidates=ComplexCombatCandidates(npc,spawned);
            complexCombatEvents.Add(new{phase="setup",weapon=complexCombatWeapon,npc=ComplexCombatEntity(npc),enemy=ComplexCombatEntity(spawned),player=new{x=player.Center.X,y=player.Center.Y},removedHostiles=removed,unrelatedProjectiles=unrelated,
                enemyCollision=Collision.SolidCollision(spawned.position,spawned.width,spawned.height),scope="One controlled NPC/player/enemy placement; initial unrelated hostiles removed, player protected; normal enemy life and natural AI thereafter."});
            complexCombatAttacking=true;HandlePlayerMessage(player,"攻击 1");return;
        }
        NPC enemy=Main.npc[complexCombatEnemy];
        complexCombatFrames++;
        var witness=enemy.GetGlobalNPC<CompanionHitWitness>().Snapshot();
        bool los=enemy.active&&Collision.CanHitLine(npc.Center,1,1,enemy.Center,1,1);
        complexCombatTrace.Add(new{frame=complexCombatFrames,gameTick=Main.GameUpdateCount,npc=ComplexCombatEntity(npc),enemy=ComplexCombatEntity(enemy),
            feetDifference=enemy.Bottom.Y-npc.Bottom.Y,los,distance=Vector2.Distance(npc.Center,enemy.Center),action=Executor.CurrentAction.ToString(),navigation=Executor.NavigationState,stepResult=Executor.LastStepResult,hit=witness});
        if(complexCombatFirstHitFrame<0&&witness.count>0){complexCombatFirstHitFrame=complexCombatFrames;complexCombatEvents.Add(new{phase="own_first_hit",frame=complexCombatFrames,hit=witness});}
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>complexCombatSequence&&o.Task.Action==AIActionType.Attack);
        if(Executor.NavigationTeleportCount!=0){FinishComplexCombat("failed","warp");return;}
        if(PrimaryDecisionRequestCount!=complexCombatPrimary||CombatDecisionRequestCount!=complexCombatCombat){FinishComplexCombat("failed","unexpected API request");return;}
        if(outcome!=null&&outcome.Status!="succeeded"){complexCombatEvents.Add(new{phase="outcome",outcome});FinishComplexCombat("blocked",outcome.Result);return;}
        if(complexCombatFirstHitFrame>=0&&complexCombatFrames>=complexCombatFirstHitFrame+60){FinishComplexCombat("observed_hit","");return;}
        if(!enemy.active)
        {
            if(complexCombatFirstHitFrame>=0)FinishComplexCombat("observed_own_hit_then_inactive","enemy became inactive after an observed companion hit; no solo-kill claim");
            else FinishComplexCombat("uncovered","enemy became inactive without an observed companion hit");
            return;
        }
        if(complexCombatFrames>=900)FinishComplexCombat("deadline","900 frame deadline");
    }

    private static object ComplexCombatEntity(NPC n)=>new{x=n.Center.X,y=n.Center.Y,bottom=n.Bottom.Y,vx=n.velocity.X,vy=n.velocity.Y,n.width,n.height,n.direction,n.life,n.lifeMax,n.active,n.type,n.whoAmI};
    private static object ComplexCombatGeometry(Vector2 center)
    {
        var cells=new List<object>();int cx=(int)(center.X/16),cy=(int)(center.Y/16);
        var types=new HashSet<int>();
        for(int y=Math.Max(1,cy-40);y<=Math.Min(Main.maxTilesY-2,cy+40);y++)for(int x=Math.Max(1,cx-48);x<=Math.Min(Main.maxTilesX-2,cx+48);x++)
        {Tile t=Main.tile[x,y];if(t.HasTile)types.Add(t.TileType);cells.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsHalfBlock,t.Slope,t.IsActuated,t.LiquidAmount,t.LiquidType,solid=t.HasTile&&Main.tileSolid[t.TileType],solidTop=t.HasTile&&Main.tileSolidTop[t.TileType],platform=t.HasTile&&TileID.Sets.Platforms[t.TileType]});}
        return new{center=new{x=cx,y=cy},bounds="97x81 clipped at world edge",cells,tileTypes=types.OrderBy(t=>t).Select(t=>new{type=t,solid=Main.tileSolid[t],solidTop=Main.tileSolidTop[t],platform=TileID.Sets.Platforms[t]}).ToArray()};
    }
    private static object ComplexCombatCandidates(NPC npc,NPC enemy)
    {
        var projected=new Vector2(enemy.Center.X,enemy.Bottom.Y-npc.height/2f);
        bool resolved=TilePathfinder.TryResolveStand(projected,out Point stand);
        int bx=(int)(enemy.Center.X/16),by=(int)(enemy.Bottom.Y/16)-1;
        var candidates=new List<object>();
        for(int x=bx-3;x<=bx+3;x++)for(int y=by-8;y<=by+12;y++)
        {
            if(!TilePathfinder.IsSafeStand(x,y))continue;
            Point point=new(x,y);Vector2 center=TilePathfinder.StandCenter(point,npc.height);
            candidates.Add(new{x,standRow=y,center=new{x=center.X,y=center.Y},bottom=TilePathfinder.StandBottom(point),distance=Vector2.Distance(center,enemy.Center),
                los=Collision.CanHitLine(center,1,1,enemy.Center,1,1),bodyLos=Collision.CanHitLine(center-new Vector2(npc.width/2f,npc.height/2f),npc.width,npc.height,enemy.position,enemy.width,enemy.height)});
        }
        return new{projected=new{x=projected.X,y=projected.Y},resolved,resolvedStand=resolved?new{x=stand.X,standRow=stand.Y}:null,enemy=new{x=enemy.Center.X,y=enemy.Center.Y,enemy.width,enemy.height},candidates};
    }
    private void FinishComplexCombat(string status,string error)
    {
        complexCombatFinished=true;Main.hideUI=complexCombatPriorHideUI;Main.GameZoomTarget=complexCombatPriorZoom;
        NPC? npc=CompanionNpc;NPC? enemy=complexCombatEnemy>=0&&complexCombatEnemy<Main.maxNPCs?Main.npc[complexCombatEnemy]:null;
        object? finalGeometry=npc!=null?ComplexCombatGeometry(npc.Center):null;
        object? finalCandidates=npc!=null&&enemy!=null?ComplexCombatCandidates(npc,enemy):null;
        File.WriteAllText(ComplexCombatPath("result.json"),JsonSerializer.Serialize(new{status,error,caseName=complexCombatCase,weapon=complexCombatWeapon,frames=complexCombatFrames,firstOwnHitFrame=complexCombatFirstHitFrame,
            requests=PrimaryDecisionRequestCount-complexCombatPrimary,combatRequests=CombatDecisionRequestCount-complexCombatCombat,warps=Executor.NavigationTeleportCount,events=complexCombatEvents,trace=complexCombatTrace,
            initialGeometry=complexCombatInitialGeometry,initialCandidates=complexCombatInitialCandidates,finalGeometry,finalCandidates,
            scope="Isolated original house copy; initial positions and equipment controlled once, initial unrelated hostiles removed and player protected. Enemy normal life and native AI; no motion, animation, damage or terrain injection. Actual own-hit witness is separate from enemy HP. Candidate stand/LOS diagnostics only at setup/end; no synchronous route queries."}));
        Stop();Executor.Clear(true);
    }
}
