using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public sealed class FloorChaseHitWitness : GlobalNPC
{
    public static int TrackedSlot = -1;
    public static readonly List<object> Hits = new();
    public override void OnHitByProjectile(NPC npc, Projectile projectile, NPC.HitInfo hit, int damageDone)
    {
        if (npc.whoAmI != TrackedSlot) return;
        Hits.Add(new { tick = Main.GameUpdateCount, projectileType = projectile.type, projectile.owner, fromCompanion = projectile.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion, damageDone, life = npc.life });
    }
}

public partial class AITeammateSystem
{
    private sealed class NativeBasicsCommandCaller(Player player) : CommandCaller
    {
        public Player Player => player;
        public CommandType CommandType => CommandType.Chat;
        public void Reply(string text, Color color = default) => Main.NewText(text, color);
    }

    private static void IssueNativeBasicsAttack(Player player) =>
        ModContent.GetInstance<AICommand>().Action(new NativeBasicsCommandCaller(player), "/ai attack 1", new[] { "attack", "1" });

    private ShelterWorldAudit? nativeBasicsHouseAudit;
    private bool continuousBasicsStarted, continuousBasicsFinished;
    private int continuousBasicsFrame, continuousBasicsLeg, continuousBasicsLegFrame, continuousBasicsStable, continuousBasicsPhase, continuousBasicsEnemy = -1;
    private int continuousBasicsPrimary, continuousBasicsCombat, continuousBasicsLife;
    private long continuousBasicsSequence;
    private DateTime continuousBasicsAt;
    private int continuousBasicsNoProgress;
    private Vector2 continuousBasicsLastObserved;
    private readonly HashSet<string> continuousBasicsVisited = new();
    private readonly List<object> continuousBasicsEvents = new(), continuousBasicsTrace = new();
    private static readonly (string name, int x, int floor)[] ContinuousBasicsTargets = {
        ("outside", 3173, 395), ("lower_room", 3220, 401), ("upper_room", 3220, 387),
        ("roof", 3219, 378), ("outside_return", 3173, 395), ("return", 3220, 401)
    };
    private void BeginContinuousBasics()
    {
        nativeBasicsHouseAudit = ShelterWorldAudit.Capture(3220, 390);
        Stop(); Memory.Clear(); LlmConversationStore.ResetAll(); agentJournal.Clear(); Executor.Clear(true); Executor.ResetNavigationTelemetry();
        NPC npc = CompanionNpc!; Player player = Main.LocalPlayer;
        npc.Center = Stand(3220, 401); npc.velocity = Vector2.Zero;
        player.Center = Stand(3173, 395); player.velocity = Vector2.Zero;
        Loadout.Reset(); Loadout.EnsureStarterTools(); Loadout.AddItem(ItemID.WoodenBow, 1); Loadout.AddItem(ItemID.WoodenArrow, 100);
        CombatPlannerEnabled = true; continuousBasicsPrimary = PrimaryDecisionRequestCount; continuousBasicsCombat = CombatDecisionRequestCount;
        continuousBasicsSequence = agentJournal.LastSequence; continuousBasicsAt = DateTime.UtcNow; continuousBasicsStarted = true;
        continuousBasicsLastObserved = npc.Center; HandlePlayerMessage(player, "跟着我");
        continuousBasicsEvents.Add(new { phase = "setup", scope = "single NPC placement; controlled player target updates; real Follow", manual = ManualControlActive });
    }
    private void SetContinuousBasicsTarget()
    {
        var target = ContinuousBasicsTargets[continuousBasicsLeg]; Player player = Main.LocalPlayer;
        player.Center = Stand(target.x, target.floor); player.velocity = Vector2.Zero;
        continuousBasicsLegFrame = continuousBasicsStable = continuousBasicsNoProgress = 0; continuousBasicsVisited.Clear();
        continuousBasicsEvents.Add(new { phase = "controlled_player_target", leg = continuousBasicsLeg, target.name, target.x, target.floor, frame = continuousBasicsFrame });
    }
    private void TickContinuousBasics()
    {
        if (Main.gameMenu) return;
        if (CompanionNpc == null) { FinishContinuousBasics("failed", "companion missing"); return; }
        NPC npc = CompanionNpc; continuousBasicsLastObserved = npc.Center; Player player = Main.LocalPlayer; continuousBasicsFrame++; continuousBasicsLegFrame++;
        player.immune = true; player.immuneTime = 600; player.statLife = player.statLifeMax2;
        bool grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
        if (continuousBasicsFrame % 5 == 0)
        {
            var path = ContinuousField("navPath") as List<Point>; int index = ContinuousField("navIndex") is int i ? i : -1;
            Vector2 goal = ContinuousField("navGoal") is Vector2 g ? g : Vector2.Zero;
            continuousBasicsTrace.Add(new { frame = continuousBasicsFrame, leg = continuousBasicsLeg, phase = continuousBasicsPhase,
                npc = new { x = npc.Center.X, y = npc.Center.Y, feet = npc.Bottom.Y, vx = npc.velocity.X, vy = npc.velocity.Y, grounded, npc.direction, npc.life, npc.active },
                player = new { x = player.Center.X, y = player.Center.Y, feet = player.Bottom.Y, player.direction }, action = Executor.CurrentAction.ToString(),
                navGoal = new { x = goal.X, y = goal.Y }, navIndex = index, pathCount = path?.Count,
                next = path != null && index >= 0 && index < path.Count ? new { x = path[index].X, y = path[index].Y } : null,
                nextFeet = path != null && index >= 0 && index < path.Count ? TilePathfinder.StandBottom(path[index]) : (float?)null,
                nativeNavigation = Executor.NavigationState, dropThrough = Executor.DropThroughPlatforms,
                supportSlope = Main.tile[(int)(npc.Center.X / 16), (int)(npc.Bottom.Y / 16)].Slope.ToString(),
                noProgress = ContinuousField("navigationNoProgressTicks"), status = Executor.StatusText(), jumps = Executor.NavigationJumpCount });
        }
        if (Executor.NavigationTeleportCount != 0 || DateTime.UtcNow - continuousBasicsAt > TimeSpan.FromMinutes(8)) { FinishContinuousBasics("failed", "warp or wall deadline"); return; }
        if (continuousBasicsPhase == 0)
        {
            bool resolved = TilePathfinder.TryResolveStand(npc.Center, out Point currentStand);
            var pathNow = ContinuousField("navPath") as List<Point>; int indexNow = ContinuousField("navIndex") is int n ? n : -1;
            string progressKey = grounded && resolved ? $"stand:{currentStand.X},{currentStand.Y}" : "";
            string nodeKey = pathNow != null && indexNow > 0 && indexNow <= pathNow.Count ? $"node:{pathNow[indexNow - 1].X},{pathNow[indexNow - 1].Y}" : "";
            bool advanced = progressKey.Length > 0 && continuousBasicsVisited.Add(progressKey);
            if (nodeKey.Length > 0) advanced |= continuousBasicsVisited.Add(nodeKey);
            if (advanced) continuousBasicsNoProgress = 0; else continuousBasicsNoProgress++;
            if (continuousBasicsNoProgress >= 2700) { FinishContinuousBasics("failed", "45 seconds without progress"); return; }
            player.direction = continuousBasicsLegFrame % 120 < 60 ? -1 : 1;
            bool visible = Collision.CanHitLine(npc.position, npc.width, npc.height, player.position, player.width, player.height);
            bool near = Math.Abs(npc.Center.X - player.Center.X) <= 80 && Math.Abs(npc.Bottom.Y - player.Bottom.Y) <= 24 && grounded && visible;
            continuousBasicsStable = near ? continuousBasicsStable + 1 : 0;
            if (continuousBasicsStable < 60) return;
            continuousBasicsEvents.Add(new { phase = "follow_arrival", leg = continuousBasicsLeg, frame = continuousBasicsFrame, npc = new { x = npc.Center.X, feet = npc.Bottom.Y }, player = new { x = player.Center.X, feet = player.Bottom.Y }, visible });
            continuousBasicsLeg++;
            if (continuousBasicsLeg == 3) { HandlePlayerMessage(player, "停下"); continuousBasicsPhase = 1; continuousBasicsLegFrame = 0; return; }
            if (continuousBasicsLeg >= ContinuousBasicsTargets.Length) { BeginContinuousBasicsCombat(false); return; }
            SetContinuousBasicsTarget(); return;
        }
        if (continuousBasicsPhase == 1)
        {
            if (continuousBasicsLegFrame < 120) return;
            continuousBasicsEvents.Add(new { phase = "wait_resume", frame = continuousBasicsFrame, action = Executor.CurrentAction.ToString(), held = Executor.HoldPosition });
            HandlePlayerMessage(player, "跟着我"); continuousBasicsPhase = 0; SetContinuousBasicsTarget(); return;
        }
        if (continuousBasicsPhase == 4)
        {
            bool near = Math.Abs(npc.Center.X - player.Center.X) <= 80 && Math.Abs(npc.Bottom.Y - player.Bottom.Y) <= 24 && grounded && Collision.CanHitLine(npc.position, npc.width, npc.height, player.position, player.width, player.height);
            if (near && Executor.CurrentAction == AIActionType.Follow) { continuousBasicsEvents.Add(new { phase = "follow_after_combat", frame = continuousBasicsFrame }); FinishContinuousBasics("passed", ""); }
            else if (continuousBasicsLegFrame >= 2700) FinishContinuousBasics("failed", "follow after combat deadline");
            return;
        }
        var outcome = agentJournal.Outcomes.LastOrDefault(o => o.Sequence > continuousBasicsSequence && o.Task.Action == AIActionType.Attack);
        if (outcome == null) { if (continuousBasicsLegFrame >= 2700) FinishContinuousBasics("failed", "combat deadline"); return; }
        continuousBasicsSequence = outcome.Sequence;
        NPC enemy = Main.npc[continuousBasicsEnemy];
        var companionHit = enemy.GetGlobalNPC<CompanionHitWitness>().Snapshot();
        continuousBasicsEvents.Add(new { phase = continuousBasicsPhase == 2 ? "melee_outcome" : "ranged_outcome", outcome, initialLife = continuousBasicsLife, life = enemy.life, enemy.active, companionHit, frame = continuousBasicsFrame });
        if (outcome.Status != "succeeded" || enemy.active || enemy.life > 0) { FinishContinuousBasics("failed", "actual enemy death not proven"); return; }
        if (continuousBasicsPhase == 2) { BeginContinuousBasicsCombat(true); return; }
        continuousBasicsPhase = 4; continuousBasicsLegFrame = 0;
    }
    private void BeginContinuousBasicsCombat(bool ranged)
    {
        NPC npc = CompanionNpc!; continuousBasicsPhase = ranged ? 3 : 2; continuousBasicsLegFrame = 0;
        Loadout.PreferredWeaponType = ranged ? ItemID.WoodenBow : ItemID.CopperShortsword;
        continuousBasicsEnemy = NPC.NewNPC(new EntitySource_Misc("ControlledContinuousBasics"), (int)npc.Center.X + (ranged ? 200 : 90), (int)npc.Bottom.Y - 32, NPCID.BlueSlime);
        if (continuousBasicsEnemy < 0 || continuousBasicsEnemy >= Main.maxNPCs) { FinishContinuousBasics("failed", "enemy spawn failed"); return; }
        continuousBasicsLife = Main.npc[continuousBasicsEnemy].life; continuousBasicsSequence = agentJournal.LastSequence;
        bool line = Collision.CanHitLine(npc.position, npc.width, npc.height, Main.npc[continuousBasicsEnemy].position, Main.npc[continuousBasicsEnemy].width, Main.npc[continuousBasicsEnemy].height);
        continuousBasicsEvents.Add(new { phase = ranged ? "ranged_spawn" : "melee_spawn", npc = new { x = npc.Center.X, y = npc.Center.Y }, enemy = new { x = Main.npc[continuousBasicsEnemy].Center.X, y = Main.npc[continuousBasicsEnemy].Center.Y }, lineOfSight = line, scope = "controlled enemy and weapon; door crossing / initial occlusion not guaranteed and must be reported uncovered if not observed" });
        IssueNativeBasicsAttack(Main.LocalPlayer);
    }
    private void FinishContinuousBasics(string status, string error)
    {
        continuousBasicsFinished = true; NPC? npc = CompanionNpc; Vector2 observed = npc?.Center ?? continuousBasicsLastObserved; int cx = (int)(observed.X / 16), cy = (int)(observed.Y / 16);
        var geometry = new List<object>();
        if (status != "passed") for (int y = Math.Max(1, cy - 40); y <= Math.Min(Main.maxTilesY - 2, cy + 40); y++) for (int x = Math.Max(1, cx - 48); x <= Math.Min(Main.maxTilesX - 2, cx + 48); x++)
        { Tile t = Main.tile[x, y]; geometry.Add(new { x, y, t.HasTile, t.TileType, t.WallType, t.TileFrameX, t.TileFrameY, t.Slope, t.IsHalfBlock, t.IsActuated, solid = t.HasTile && Main.tileSolid[t.TileType], solidTop = t.HasTile && Main.tileSolidTop[t.TileType] }); }
        File.WriteAllText(Path.Combine(Main.SavePath, "AITeammate", "continuous-basics-native-result.json"), JsonSerializer.Serialize(new { status, error, frames = continuousBasicsFrame, requests = PrimaryDecisionRequestCount - continuousBasicsPrimary, combatRequests = CombatDecisionRequestCount - continuousBasicsCombat, manual = ManualControlActive, warps = Executor.NavigationTeleportCount, protection = nativeBasicsHouseAudit?.CheckPreservation(), events = continuousBasicsEvents, trace = continuousBasicsTrace, failureGeometry = geometry, scope = "Original house copy, one NPC setup; player targets repositioned in controlled test. Enemy and ammunition supplied, attack via /ai command. No API or intermediate NPC placement." }));
        Stop(); Executor.Clear(true);
    }
    private bool nearDoorStarted, nearDoorFinished, nearDoorOpened, nearDoorVisible;
    private int nearDoorFrame, nearDoorPrimary, nearDoorCombat;
    private Vector2 nearDoorLastPosition;
    private readonly List<object> nearDoorTrace = new();
    private void BeginNearDoorFollow()
    {
        Stop(); Memory.Clear(); LlmConversationStore.ResetAll(); agentJournal.Clear(); Executor.Clear(true); Executor.ResetNavigationTelemetry();
        NPC npc = CompanionNpc!; Player player = Main.LocalPlayer;
        npc.Center = Stand(3205, 393); npc.velocity = Vector2.Zero; player.Center = Stand(3209, 393); player.velocity = Vector2.Zero;
        Loadout.Reset(); Loadout.EnsureStarterTools(); CombatPlannerEnabled = true;
        nearDoorPrimary = PrimaryDecisionRequestCount; nearDoorCombat = CombatDecisionRequestCount; nearDoorLastPosition = npc.Center; nearDoorStarted = true;
        bool closed = Enumerable.Range(390, 3).All(y => Main.tile[3207,y].HasTile && Main.tile[3207,y].TileType == TileID.ClosedDoor);
        if (!closed) { FinishNearDoor("uncovered", "original three-cell closed door not present"); return; }
        HandlePlayerMessage(player, "跟着我");
    }
    private void TickNearDoorFollow()
    {
        if (Main.gameMenu) return;
        if (CompanionNpc == null) { FinishNearDoor("failed", "companion missing"); return; }
        NPC npc = CompanionNpc; nearDoorLastPosition = npc.Center; Player player = Main.LocalPlayer;
        player.immune = true; player.immuneTime = 600; nearDoorFrame++;
        Tile door = Main.tile[3207,391];
        bool open = Enumerable.Range(3206, 3).Any(x => Enumerable.Range(390, 3).Any(y => Main.tile[x,y].HasTile && Main.tile[x,y].TileType == TileID.OpenDoor));
        bool visible = Collision.CanHitLine(npc.position, npc.width, npc.height, player.position, player.width, player.height);
        nearDoorOpened |= open; nearDoorVisible |= open && visible;
        var path = ContinuousField("navPath") as List<Point>; int index = ContinuousField("navIndex") is int i ? i : -1;
        nearDoorTrace.Add(new { frame = nearDoorFrame, npc = new { x=npc.Center.X, y=npc.Center.Y, feet=npc.Bottom.Y, vx=npc.velocity.X, vy=npc.velocity.Y, npc.life, npc.active },
            player = new { x=player.Center.X, y=player.Center.Y, feet=player.Bottom.Y }, door = new { door.HasTile, door.TileType, door.TileFrameX, door.TileFrameY }, open, visible,
            action=Executor.CurrentAction.ToString(), held=Executor.HoldPosition, navigation=Executor.NavigationState, navIndex=index, pathCount=path?.Count });
        if (Executor.NavigationTeleportCount != 0) { FinishNearDoor("failed", "warp"); return; }
        if (nearDoorOpened && nearDoorVisible && Executor.CurrentAction == AIActionType.Follow) { FinishNearDoor("passed", ""); return; }
        if (nearDoorFrame >= 180) FinishNearDoor("failed", "closed door still separates stationary near target after 180 frames");
    }
    private void FinishNearDoor(string status, string error)
    {
        nearDoorFinished = true; var geometry = new List<object>();
        for (int y=385; y<=398; y++) for (int x=3199; x<=3215; x++)
        { Tile t=Main.tile[x,y]; geometry.Add(new { x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsHalfBlock,t.Slope,t.IsActuated,solid=t.HasTile && Main.tileSolid[t.TileType],solidTop=t.HasTile && Main.tileSolidTop[t.TileType],t.LiquidAmount }); }
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","near-door-follow-native-result.json"),JsonSerializer.Serialize(new { status,error,frames=nearDoorFrame,nearDoorOpened,nearDoorVisible,lastPosition=new {x=nearDoorLastPosition.X,y=nearDoorLastPosition.Y},requests=PrimaryDecisionRequestCount-nearDoorPrimary,combatRequests=CombatDecisionRequestCount-nearDoorCombat,warps=Executor.NavigationTeleportCount,manual=ManualControlActive,trace=nearDoorTrace,geometry,scope="Controlled one-time actor placement on actual original-house closed-door stands; no tile or NPC relocation after setup. Visibility requires native body line of sight. Zero API." }));
        Stop(); Executor.Clear(true);
    }

    private bool combatFloorStarted, combatFloorFinished, combatFloorFirstHit, combatFloorCrossFloor, combatFloorWitnessed;
    private int combatFloorFrames, combatFloorEnemy, combatFloorInitialLife, combatFloorPrimary, combatFloorCombat;
    private long combatFloorSequence;
    private readonly List<object> combatFloorEvents = new(), combatFloorTrace = new();
    private object? combatFloorPreviousEnemyState;
    private void BeginCombatFloorChase()
    {
        nativeBasicsHouseAudit = ShelterWorldAudit.Capture(3220, 390);
        Stop(); Memory.Clear(); LlmConversationStore.ResetAll(); agentJournal.Clear(); Executor.Clear(true); Executor.ResetNavigationTelemetry();
        NPC npc = CompanionNpc!; Player player = Main.LocalPlayer;
        npc.Center = Stand(3220, 401); npc.velocity = Vector2.Zero; player.Center = Stand(3215, 401); player.velocity = Vector2.Zero;
        Loadout.Reset(); Loadout.EnsureStarterTools(); Loadout.PreferredWeaponType = ItemID.CopperShortsword; CombatPlannerEnabled = true;
        combatFloorPrimary = PrimaryDecisionRequestCount; combatFloorCombat = CombatDecisionRequestCount; combatFloorSequence = agentJournal.LastSequence; combatFloorStarted = true;
        var friendlyBefore = Main.npc.Where(n => n.active && n.friendly && n.whoAmI != npc.whoAmI).Select(n => new { n.whoAmI, n.type, x=n.Center.X, y=n.Center.Y }).ToArray();
        var projectilesBefore = Main.projectile.Where(pr => pr.active && pr.friendly).Select(pr => new { pr.whoAmI, pr.type, pr.owner, pr.damage, fromCompanion=pr.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion }).ToArray();
        foreach (NPC other in Main.npc) if (other.active && other.friendly && other.whoAmI != npc.whoAmI) other.active = false;
        foreach (Projectile projectile in Main.projectile) if (projectile.active && projectile.friendly && !projectile.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion) projectile.active = false;
        FloorChaseHitWitness.Hits.Clear(); FloorChaseHitWitness.TrackedSlot = -1;
        combatFloorEvents.Add(new { phase="controlled_collaborator_removal", friendlyBefore, projectilesBefore, scope="fixture-only active=false; other friendly NPCs and non-companion friendly projectiles removed from isolated copy" });
        combatFloorEnemy = NPC.NewNPC(new EntitySource_Misc("ControlledFloorChase"), 3220 * 16 + 8, 387 * 16 - 20, NPCID.BlueSlime);
        if (combatFloorEnemy < 0 || combatFloorEnemy >= Main.maxNPCs) { FinishCombatFloor("failed", "spawn failed"); return; }
        FloorChaseHitWitness.TrackedSlot = combatFloorEnemy;
        NPC enemy = Main.npc[combatFloorEnemy]; enemy.Bottom = new Vector2(3220 * 16 + 8, 387 * 16); enemy.velocity = Vector2.Zero;
        combatFloorInitialLife = enemy.life; combatFloorPreviousEnemyState = CombatFloorEnemyState(enemy);
        bool collision = Collision.SolidCollision(enemy.position, enemy.width, enemy.height);
        bool supported = Collision.SolidCollision(enemy.position + new Vector2(0, 2), enemy.width, enemy.height);
        bool visible = Collision.CanHitLine(npc.position, npc.width, npc.height, enemy.position, enemy.width, enemy.height);
        combatFloorEvents.Add(new { phase="setup", npc=new {x=npc.Center.X,feet=npc.Bottom.Y}, enemy=new {x=enemy.Center.X,feet=enemy.Bottom.Y,enemy.width,enemy.height,enemy.life}, collision,supported,visible, enemyState=CombatFloorEnemyState(enemy) });
        if (collision || !supported || visible) { FinishCombatFloor("uncovered", "enemy setup not valid supported and initially occluded"); return; }
        IssueNativeBasicsAttack(player);
    }
    private void TickCombatFloorChase()
    {
        if (Main.gameMenu) return;
        if (CompanionNpc == null) { FinishCombatFloor("failed", "companion missing"); return; }
        NPC npc=CompanionNpc; NPC enemy=Main.npc[combatFloorEnemy]; Player player=Main.LocalPlayer; player.immune=true;player.immuneTime=600;combatFloorFrames++;
        bool centerVisible=enemy.active && Collision.CanHitLine(npc.Center,1,1,enemy.Center,1,1);
        Vector2 aim = enemy.Center;
        bool visible=enemy.active && CompanionAim.TryFindVisiblePoint(npc.Center,enemy.Center,enemy.width,out aim) &&
            enemy.Hitbox.Contains(aim.ToPoint()) && Collision.CanHitLine(npc.Center,1,1,aim,1,1);
        var hitWitness=enemy.GetGlobalNPC<CompanionHitWitness>().Snapshot();
        if (!combatFloorWitnessed && hitWitness.total > 0)
        {
            combatFloorWitnessed=true; float distance=Vector2.Distance(npc.Center,enemy.Center);
            combatFloorCrossFloor = npc.Bottom.Y < 401 * 16 - 16 && visible;
            combatFloorEvents.Add(new {phase="companion_hit",frame=combatFloorFrames,npc=new {x=npc.Center.X,y=npc.Center.Y,feet=npc.Bottom.Y},enemy=new {x=enemy.Center.X,y=enemy.Center.Y,feet=enemy.Bottom.Y,enemy.life},distancePixels=distance,visible,centerVisible,aim=new {x=aim.X,y=aim.Y},netAscentPixels=401*16-npc.Bottom.Y,hitWitness,crossFloor=combatFloorCrossFloor});
        }
        if (!combatFloorFirstHit && enemy.life < combatFloorInitialLife)
        {
            combatFloorFirstHit=true;
            combatFloorEvents.Add(new {phase="first_hit",frame=combatFloorFrames,npc=new {x=npc.Center.X,feet=npc.Bottom.Y},enemy=new {x=enemy.Center.X,feet=enemy.Bottom.Y,enemy.life,enemy.active},previousEnemyState=combatFloorPreviousEnemyState,enemyState=CombatFloorEnemyState(enemy),projectileHits=FloorChaseHitWitness.Hits.ToArray(),visible,crossFloor=combatFloorCrossFloor});
        }
        if (combatFloorFrames % 5 == 0) combatFloorTrace.Add(new {frame=combatFloorFrames,npc=new {x=npc.Center.X,feet=npc.Bottom.Y,vx=npc.velocity.X,vy=npc.velocity.Y,npc.life,npc.active},enemy=new {x=enemy.Center.X,feet=enemy.Bottom.Y,vx=enemy.velocity.X,vy=enemy.velocity.Y,enemy.life,enemy.active},enemyState=CombatFloorEnemyState(enemy),visible,navigation=Executor.NavigationState,action=Executor.CurrentAction.ToString()});
        combatFloorPreviousEnemyState = CombatFloorEnemyState(enemy);
        if (Executor.NavigationTeleportCount != 0) { FinishCombatFloor("failed", "warp"); return; }
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>combatFloorSequence && o.Task.Action==AIActionType.Attack);
        if (outcome != null) { combatFloorSequence=outcome.Sequence; combatFloorEvents.Add(new {phase="outcome",outcome}); FinishCombatFloor(outcome.Status=="succeeded" && combatFloorCrossFloor ? "passed" : combatFloorFirstHit && !combatFloorCrossFloor ? "uncovered" : "failed", combatFloorCrossFloor ? "" : "cross-floor first hit not proven"); return; }
        if (combatFloorFrames >= 1800) FinishCombatFloor("failed", "1800 tick deadline");
    }
    private static object CombatFloorEnemyState(NPC enemy) => new { enemy.life, enemy.lifeRegen, enemy.wet, enemy.lavaWet, buffs=enemy.buffType.Select((type,i)=>new { type, time=enemy.buffTime[i] }).Where(b=>b.type!=0).ToArray(), friendlyProjectiles=Main.projectile.Where(pr=>pr.active && pr.friendly).Select(pr=>new { pr.whoAmI, pr.type, pr.owner, pr.damage, fromCompanion=pr.GetGlobalProjectile<CompanionProjectileTag>().FromCompanion }).ToArray() };
    private void FinishCombatFloor(string status,string error)
    {
        combatFloorFinished=true;
        NPC? current = CompanionNpc; NPC? enemy = combatFloorEnemy >= 0 && combatFloorEnemy < Main.maxNPCs ? Main.npc[combatFloorEnemy] : null;
        var geometry = new List<object>();
        var centers = new[] { current?.Center ?? new Vector2(3220*16, 401*16), enemy?.Center ?? new Vector2(3220*16, 387*16) };
        for (int region=0;region<centers.Length;region++) { int cx=(int)(centers[region].X/16),cy=(int)(centers[region].Y/16);
            for(int y=Math.Max(1,cy-40);y<=Math.Min(Main.maxTilesY-2,cy+40);y++) for(int x=Math.Max(1,cx-48);x<=Math.Min(Main.maxTilesX-2,cx+48);x++)
            { Tile t=Main.tile[x,y]; geometry.Add(new {region,x,y,t.HasTile,t.TileType,t.WallType,t.TileFrameX,t.TileFrameY,t.IsHalfBlock,t.Slope,t.IsActuated,t.LiquidAmount,t.LiquidType,solid=t.HasTile && Main.tileSolid[t.TileType],solidTop=t.HasTile && Main.tileSolidTop[t.TileType]}); } }
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","combat-floor-chase-native-result.json"),JsonSerializer.Serialize(new {status,error,frames=combatFloorFrames,firstHit=combatFloorFirstHit,companionHitWitnessed=combatFloorWitnessed,crossFloor=combatFloorCrossFloor,requests=PrimaryDecisionRequestCount-combatFloorPrimary,combatRequests=CombatDecisionRequestCount-combatFloorCombat,warps=Executor.NavigationTeleportCount,protection=nativeBasicsHouseAudit?.CheckPreservation(),events=combatFloorEvents,trace=combatFloorTrace,projectileHits=FloorChaseHitWitness.Hits.ToArray(),geometry,geometryScope="97x81 bounded regions centered on final companion and enemy; may overlap",scope="Controlled normal enemy and copper weapon, initially on separate floors; attack via /ai command. Enemy follows native AI, no NPC movement injection after setup. Companion damage witness plus actual line of sight and at least one tile net ascent required; actual native weapon collision is authoritative, no same-floor foot requirement."}));
        FloorChaseHitWitness.TrackedSlot=-1; Stop();Executor.Clear(true);
    }

}
