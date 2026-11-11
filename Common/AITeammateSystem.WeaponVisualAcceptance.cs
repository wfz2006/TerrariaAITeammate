using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private bool weaponVisualStarted, weaponVisualFinished, weaponVisualReady, weaponVisualAttacking, weaponVisualPriorHideUI;
    private int weaponVisualIndex, weaponVisualFrames, weaponVisualStageFrames, weaponVisualEnemy=-1, weaponVisualItem, weaponVisualPrimary, weaponVisualCombat;
    private long weaponVisualSequence;
    private float weaponVisualPriorZoom;
    private DateTime weaponVisualAt;
    private readonly List<object> weaponVisualEvents=new(), weaponVisualTrace=new();
    private static readonly (string name, string itemName, int direction)[] WeaponVisualCases={
        ("shortsword_right","CopperShortsword",1), ("shortsword_left","CopperShortsword",-1),
        ("broadsword_right","IronBroadsword",1), ("broadsword_left","IronBroadsword",-1),
        ("bow_right","WoodenBow",1), ("bow_left","WoodenBow",-1),
        ("magic_staff","AmethystStaff",1), ("yoyo","WoodYoyo",1), ("whip","BlandWhip",1)
    };
    private string WeaponVisualPath(string name)=>Path.Combine(Main.SavePath,"AITeammate",name);
    private void BeginWeaponVisual()
    {
        Stop(); Memory.Clear(); LlmConversationStore.ResetAll(); agentJournal.Clear(); Executor.Clear(true); Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!; Player player=Main.LocalPlayer; npc.Center=Stand(3175,395);npc.velocity=Vector2.Zero;player.Center=Stand(3185,395);player.velocity=Vector2.Zero;
        Main.dayTime=true;Main.time=18000;Main.raining=false;Loadout.Reset();Loadout.EnsureStarterTools();Loadout.AddItem(ItemID.WoodenArrow,999);
        foreach(var spec in WeaponVisualCases) { var f=typeof(ItemID).GetField(spec.itemName,BindingFlags.Public|BindingFlags.Static);if(f?.GetRawConstantValue() is object raw) { int id=Convert.ToInt32(raw); if(id>0)Loadout.AddItem(id,1); } }
        weaponVisualPriorZoom=Main.GameZoomTarget;Main.GameZoomTarget=1.6f;
        CombatPlannerEnabled=true;CompanionPlayerRenderer.DiagnosticsEnabled=true;weaponVisualPrimary=PrimaryDecisionRequestCount;weaponVisualCombat=CombatDecisionRequestCount;weaponVisualAt=DateTime.UtcNow;weaponVisualPriorHideUI=Main.hideUI;Main.hideUI=true;weaponVisualStarted=true;
        string ack=WeaponVisualPath("weapon-visual-ack.txt");if(File.Exists(ack))File.Delete(ack);Stop();
    }
    public override void ModifyScreenPosition()
    {
        if (((weaponVisualStarted && !weaponVisualFinished) || (complexCombatStarted && !complexCombatFinished && complexCombatAttacking) || (longGoalSubmitted && !longGoalFinished) || (closureSourcesStarted && !closureSourcesFinished)) && CompanionNpc is NPC npc && npc.active)
            Main.screenPosition=npc.Center-new Vector2(Main.screenWidth/2f,Main.screenHeight/2f);
    }
    private void TickWeaponVisual()
    {
        if(Main.gameMenu)return;if(CompanionNpc==null){FinishWeaponVisual("failed","companion missing");return;}
        NPC npc=CompanionNpc;Player player=Main.LocalPlayer;player.immune=true;player.immuneTime=600;weaponVisualFrames++;
        if(DateTime.UtcNow-weaponVisualAt>TimeSpan.FromMinutes(8)||Executor.NavigationTeleportCount!=0){FinishWeaponVisual("failed","deadline or warp");return;}
        if(weaponVisualIndex>=WeaponVisualCases.Length){FinishWeaponVisual("observed","");return;}
        var spec=WeaponVisualCases[weaponVisualIndex];
        if(!weaponVisualAttacking)
        {
            if(!weaponVisualReady){weaponVisualReady=true;File.WriteAllText(WeaponVisualPath("weapon-visual-ready.txt"),spec.name);}
            string ack=WeaponVisualPath("weapon-visual-ack.txt");if(!File.Exists(ack)||File.ReadAllText(ack).Trim()!=spec.name)return;File.Delete(ack);
            var f=typeof(ItemID).GetField(spec.itemName,BindingFlags.Public|BindingFlags.Static);
            if(f?.GetRawConstantValue() is not object raw){weaponVisualEvents.Add(new{phase="uncovered_item_id",spec.name,spec.itemName});weaponVisualIndex++;weaponVisualReady=false;return;}
            int id=Convert.ToInt32(raw);
            weaponVisualItem=id;Loadout.PreferredWeaponType=id;Loadout.Mana=Loadout.ManaMax;
            int removedPriorProjectiles=0;
            foreach(Projectile pr in Main.projectile)if(pr.active && pr.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion){pr.active=false;removedPriorProjectiles++;}
            var unrelatedProjectiles=Main.projectile.Where(pr=>pr.active && !pr.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion).Select(pr=>new{pr.type,pr.owner,pr.damage,x=pr.Center.X,y=pr.Center.Y}).ToArray();
            Item? weapon=Loadout.Inventory.FirstOrDefault(it=>!it.IsAir && it.type==id);
            Item? ammo=Loadout.Inventory.FirstOrDefault(it=>!it.IsAir && it.type==ItemID.WoodenArrow);
            foreach(NPC other in Main.npc)if(other.active&&!other.friendly)other.active=false;
            weaponVisualEnemy=NPC.NewNPC(new EntitySource_Misc("ControlledWeaponVisual"),(int)npc.Center.X+spec.direction*95,(int)npc.Bottom.Y-32,NPCID.BlueSlime);
            if(weaponVisualEnemy<0||weaponVisualEnemy>=Main.maxNPCs){FinishWeaponVisual("failed","enemy spawn failed");return;}
            NPC enemy=Main.npc[weaponVisualEnemy];enemy.lifeMax=5000;enemy.life=5000;
            weaponVisualSequence=agentJournal.LastSequence;weaponVisualStageFrames=0;weaponVisualAttacking=true;weaponVisualReady=false;
            weaponVisualEvents.Add(new{phase="attack_start",spec.name,item=id,spec.direction,enemy=weaponVisualEnemy,npc=new{x=npc.Center.X,y=npc.Center.Y},target=new{x=enemy.Center.X,y=enemy.Center.Y},player=new{x=player.Center.X,y=player.Center.Y},removedPriorProjectiles,unrelatedProjectiles,
                weapon=weapon==null?null:new{weapon.type,weapon.useStyle,weapon.noUseGraphic,weapon.noMelee,weapon.shoot,weapon.useAmmo,weapon.shootSpeed,weapon.damage},
                ammo=ammo==null?null:new{ammo.type,ammo.ammo,ammo.shoot,ammo.shootSpeed,ammo.damage},scope="fixture supplied weapon/ammo and high-health enemy"});
            HandlePlayerMessage(player,"攻击 1");File.WriteAllText(WeaponVisualPath("weapon-visual-running.txt"),spec.name);return;
        }
        weaponVisualStageFrames++;
        NPC target=Main.npc[weaponVisualEnemy];
        var renderer=(npc.ModNPC as TerrariaAITeammate.Content.NPCs.AICompanion)?.Renderer;
        weaponVisualTrace.Add(new {
            gameTick=Main.GameUpdateCount,frame=weaponVisualFrames,stageFrame=weaponVisualStageFrames,spec.name,item=weaponVisualItem,
            npc=new{x=npc.Center.X,y=npc.Center.Y,bottom=npc.Bottom.Y,npc.direction},
            target=new{x=target.Center.X,y=target.Center.Y,bottom=target.Bottom.Y,target.life,target.active},
            feetDifference=target.Bottom.Y-npc.Bottom.Y,actualLOS=Collision.CanHitLine(npc.Center,1,1,target.Center,1,1),
            action=Executor.CurrentAction.ToString(),stepResult=Executor.LastStepResult,
            animation=Executor.Animation.Active,animationKind=Executor.Animation.Kind.ToString(),
            animationDuration=Executor.Animation.Duration,animationTicks=Executor.Animation.Ticks,animationProgress=Executor.Animation.Progress,
            player=new{x=player.Center.X,y=player.Center.Y,player.heldProj,player.itemAnimation,player.itemRotation},
            visualItem=Executor.VisualItem?.type,draw=renderer?.LastDrawDiagnostics,hit=target.GetGlobalNPC<CompanionHitWitness>().Snapshot(),
            projectiles=Main.projectile.Where(pr=>pr.active&&pr.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion).Select(pr=>new {
                pr.whoAmI,pr.identity,pr.type,pr.owner,pr.damage,x=pr.Center.X,y=pr.Center.Y,
                distanceNpc=Vector2.Distance(pr.Center,npc.Center),distancePlayer=Vector2.Distance(pr.Center,player.Center),pr.timeLeft,pr.aiStyle,
                shortswordBound=pr.GetGlobalProjectile<CompanionBoundShortsword>().Bound,
                yoyo=new {
                    pr.GetGlobalProjectile<CompanionBoundYoyo>().Bound,pr.GetGlobalProjectile<CompanionBoundYoyo>().Returning,
                    lastDrawTick=pr.GetGlobalProjectile<CompanionBoundYoyo>().LastDrawTick,
                    stringAnchor=pr.GetGlobalProjectile<CompanionBoundYoyo>().LastStringAnchor is Vector2 anchor ? (object)new{x=anchor.X,y=anchor.Y}:null
                },
                ai0=pr.ai[0],ai1=pr.ai[1],ai2=pr.ai[2],velocity=new{x=pr.velocity.X,y=pr.velocity.Y}
            }).ToArray()
        });
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>weaponVisualSequence && o.Task.Action==AIActionType.Attack);
        string done=WeaponVisualPath("weapon-visual-done.txt"); bool acknowledged=File.Exists(done) && File.ReadAllText(done).Trim()==spec.name;
        if(acknowledged || weaponVisualStageFrames>=1200 || outcome!=null)
        {
            if(acknowledged)File.Delete(done);
            weaponVisualEvents.Add(new{phase="attack_end",spec.name,item=weaponVisualItem,frames=weaponVisualStageFrames,acknowledged,outcome,hit=target.GetGlobalNPC<CompanionHitWitness>().Snapshot(),alive=target.active,life=target.life});
            target.active=false;Stop();Executor.Clear(true);weaponVisualAttacking=false;weaponVisualIndex++;
            File.WriteAllText(WeaponVisualPath("weapon-visual-running-result.json"),JsonSerializer.Serialize(new{status="running",nextStage=weaponVisualIndex,frames=weaponVisualFrames,events=weaponVisualEvents,trace=weaponVisualTrace}));return;
        }
    }
    private void FinishWeaponVisual(string status,string error)
    {
        weaponVisualFinished=true;Main.hideUI=weaponVisualPriorHideUI;Main.GameZoomTarget=weaponVisualPriorZoom;File.WriteAllText(WeaponVisualPath("weapon-visual-native-result.json"),JsonSerializer.Serialize(new{status,error,frames=weaponVisualFrames,requests=PrimaryDecisionRequestCount-weaponVisualPrimary,combatRequests=CombatDecisionRequestCount-weaponVisualCombat,manual=ManualControlActive,warps=Executor.NavigationTeleportCount,events=weaponVisualEvents,trace=weaponVisualTrace,scope="Controlled isolated house copy; one initial placement, supplied weapons/ammo, high-health enemies. Test-only camera follows companion at native 1.6 zoom and UI is hidden during observation; zoom/UI restored at finish. Native Attack and actual Draw diagnostics; observation is not a visual pass without captured screenshots. No animation or damage injection."}));Stop();Executor.Clear(true);
    }
}
