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
    private bool basicCombatVisualStarted,basicCombatVisualFinished,basicCombatAnimationSeen,basicCombatHealthDecreased,basicEquipWaiting,basicWaitProtected=true,basicPickupProtected,basicTargetIsolated=true,basicPickupExact;
    private int basicCombatPhase,basicCombatFrames,basicCombatPhaseFrames,basicCombatRequests,basicCombatCombatRequests,basicCombatEnemy=-1,basicCombatInitialLife,basicCombatDrop=-1,basicCombatOtherDrop=-1,basicCombatDecoy=-1,basicCombatDecoyLife;
    private long basicCombatSequence;private DateTime basicCombatAt;private ShelterWorldAudit? basicCombatAudit;
    private readonly List<object> basicCombatEvents=new(),basicCombatTrace=new();
    private static readonly int[] BasicCombatArmor={ItemID.IronHelmet,ItemID.IronChainmail,ItemID.IronGreaves,ItemID.AngelWings,ItemID.CobaltShield};
    private void BeginBasicCombatVisual()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear(true);Executor.ResetNavigationTelemetry();NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;
        npc.Center=Stand(3170,395);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;Loadout.Reset();Loadout.EnsureStarterTools();
        foreach(int id in BasicCombatArmor)Loadout.AddItem(id,1);Loadout.AddItem(ItemID.WoodenBow,1);Loadout.AddItem(ItemID.WoodenArrow,100);
        basicCombatAudit=ShelterWorldAudit.Capture(3220,390);basicCombatRequests=PrimaryDecisionRequestCount;basicCombatCombatRequests=CombatDecisionRequestCount;CombatPlannerEnabled=true;basicCombatSequence=agentJournal.LastSequence;basicCombatAt=DateTime.UtcNow;basicCombatVisualStarted=true;
        HandlePlayerMessage(p,"停下");basicCombatEvents.Add(new{phase="nude",frame=0,inventory=ShelterCounts(Loadout.Inventory),armor=ShelterCounts(Loadout.Armor),accessories=ShelterCounts(Loadout.Accessories)});
    }
    private void BasicCombatSpawn(NPC npc,bool ranged)
    {
        foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly && Vector2.Distance(hostile.Center,npc.Center)<800)hostile.active=false;
        Loadout.PreferredWeaponType=ranged?ItemID.WoodenBow:ItemID.CopperShortsword;
        basicCombatEnemy=NPC.NewNPC(new EntitySource_Misc("ControlledBasicCombatAcceptance"),(int)npc.Center.X+(ranged?150:55),(int)npc.Bottom.Y-32,NPCID.BlueSlime);
        basicCombatDecoy=NPC.NewNPC(new EntitySource_Misc("ControlledBasicCombatDecoy"),(int)npc.Center.X-100,(int)npc.Bottom.Y-32,NPCID.Zombie);
        if(basicCombatEnemy<0 || basicCombatEnemy>=Main.maxNPCs)throw new InvalidOperationException("Controlled enemy spawn failed.");
        if(basicCombatDecoy<0 || basicCombatDecoy>=Main.maxNPCs)throw new InvalidOperationException("Controlled decoy spawn failed.");basicCombatDecoyLife=Main.npc[basicCombatDecoy].life;
        basicCombatInitialLife=Main.npc[basicCombatEnemy].life;basicCombatAnimationSeen=false;basicCombatHealthDecreased=false;
        basicCombatEvents.Add(new{phase=ranged?"ranged_start":"melee_start",enemy=basicCombatEnemy,life=basicCombatInitialLife,weapon=Loadout.PreferredWeaponType,frame=basicCombatFrames});
        HandlePlayerMessage(Main.LocalPlayer,"攻击 1");
    }
    private void TickBasicCombatVisual()
    {
        if(Main.gameMenu || CompanionNpc==null)return;NPC npc=CompanionNpc;Player p=Main.LocalPlayer;basicCombatFrames++;basicCombatPhaseFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;
        if(basicCombatFrames%10==0)basicCombatTrace.Add(new{frame=basicCombatFrames,phase=basicCombatPhase,npc=new{x=npc.Center.X,y=npc.Center.Y,feet=npc.Bottom.Y},
            enemy=basicCombatEnemy>=0?new{active=Main.npc[basicCombatEnemy].active,life=Main.npc[basicCombatEnemy].life} : null,animation=Executor.Animation.Active,kind=Executor.Animation.Kind.ToString(),item=Executor.VisualItem?.type,inventory=ShelterCounts(Loadout.Inventory),warps=Executor.NavigationTeleportCount});
        if(DateTime.UtcNow-basicCombatAt>TimeSpan.FromMinutes(5) || Executor.NavigationTeleportCount>0){FinishBasicCombatVisual("failed","deadline or warp");return;}
        if(basicCombatPhase==0){if(basicCombatPhaseFrames==120){basicCombatEnemy=NPC.NewNPC(new EntitySource_Misc("ControlledWaitAggressor"),(int)npc.Center.X+180,(int)npc.Bottom.Y-32,NPCID.BlueSlime);Executor.NotifyPlayerAttacked(basicCombatEnemy);}
            if(basicCombatPhaseFrames>=121)basicWaitProtected &= Executor.CurrentAction==AIActionType.Wait && !Executor.IsAutoDefending;
            if(basicCombatPhaseFrames<600)return;basicCombatEvents.Add(new{phase="wait_protection",basicWaitProtected,action=Executor.CurrentAction.ToString(),autoDefending=Executor.IsAutoDefending});
            if(basicCombatEnemy>=0 && basicCombatEnemy<Main.maxNPCs)Main.npc[basicCombatEnemy].active=false;basicCombatPhase=1;basicCombatPhaseFrames=0;BasicCombatSpawn(npc,false);return;}
        if(basicCombatPhase is 1 or 2)
        {
            NPC enemy=Main.npc[basicCombatEnemy];basicCombatAnimationSeen |= Executor.Animation.Active;basicCombatHealthDecreased |= enemy.life<basicCombatInitialLife;
            if(!basicCombatHealthDecreased && basicCombatPhaseFrames<900)return;
            bool decoyUntouched=basicCombatDecoy>=0 && Main.npc[basicCombatDecoy].active && Main.npc[basicCombatDecoy].life==basicCombatDecoyLife;basicTargetIsolated &= decoyUntouched;
            basicCombatEvents.Add(new{phase=basicCombatPhase==1?"melee_observed":"ranged_observed",initialLife=basicCombatInitialLife,life=enemy.life,active=enemy.active,healthDecreased=basicCombatHealthDecreased,animationSeen=basicCombatAnimationSeen,visualItem=Executor.VisualItem?.type,decoyUntouched,frame=basicCombatFrames});
            if(!basicCombatHealthDecreased){FinishBasicCombatVisual("failed","weapon did not damage controlled enemy");return;}enemy.active=false;Main.npc[basicCombatDecoy].active=false;Executor.Clear(true);
            if(basicCombatPhase==1){basicCombatPhase=2;basicCombatPhaseFrames=0;BasicCombatSpawn(npc,true);return;}
            basicCombatPhase=3;basicCombatPhaseFrames=0;basicCombatDrop=Item.NewItem(new EntitySource_Misc("ControlledBasicPickupAcceptance"),npc.Center+new Vector2(-70,0),ItemID.Gel,5);
            basicCombatOtherDrop=Item.NewItem(new EntitySource_Misc("ControlledBasicPickupDecoy"),npc.Center+new Vector2(-78,0),ItemID.StoneBlock,4);
            basicCombatEnemy=NPC.NewNPC(new EntitySource_Misc("ControlledPickupAggressor"),(int)npc.Center.X+180,(int)npc.Bottom.Y-32,NPCID.BlueSlime);
            basicCombatEvents.Add(new{phase="pickup_start",drop=basicCombatDrop,type=ItemID.Gel,stack=5,requested=2,otherDrop=basicCombatOtherDrop});HandlePlayerMessage(p,"拾取 23 2");Executor.NotifyPlayerAttacked(basicCombatEnemy);basicPickupProtected=Executor.CurrentAction==AIActionType.Pickup && !Executor.IsAutoDefending;return;
        }
        if(basicCombatPhase==3 && Executor.CurrentAction==AIActionType.Pickup)basicPickupProtected &= !Executor.IsAutoDefending;
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>basicCombatSequence);if(outcome!=null){basicCombatSequence=outcome.Sequence;basicCombatEvents.Add(new{phase="outcome",step=basicCombatPhase,outcome,inventory=ShelterCounts(Loadout.Inventory),armor=ShelterCounts(Loadout.Armor),accessories=ShelterCounts(Loadout.Accessories)});
            if(basicCombatPhase==3){int remaining=basicCombatDrop>=0 && Main.item[basicCombatDrop].active?Main.item[basicCombatDrop].stack:0;bool otherPreserved=basicCombatOtherDrop>=0 && Main.item[basicCombatOtherDrop].active && Main.item[basicCombatOtherDrop].type==ItemID.StoneBlock && Main.item[basicCombatOtherDrop].stack==4;basicPickupExact=outcome.Status=="succeeded" && Loadout.CountItem(ItemID.Gel)==2 && remaining==3 && otherPreserved;basicCombatEvents.Add(new{phase="pickup_observed",gel=Loadout.CountItem(ItemID.Gel),remaining,requested=2,otherPreserved,basicPickupExact,basicPickupProtected});if(basicCombatEnemy>=0 && basicCombatEnemy<Main.maxNPCs)Main.npc[basicCombatEnemy].active=false;basicCombatPhase=4;basicCombatPhaseFrames=0;}
            else if(basicCombatPhase is >=4 and <=8){basicEquipWaiting=true;basicCombatPhaseFrames=0;}}
        if(basicEquipWaiting && basicCombatPhaseFrames>=600){basicEquipWaiting=false;basicCombatPhase++;basicCombatPhaseFrames=0;}
        if(basicCombatPhase is >=4 and <=8 && basicCombatPhaseFrames==1 && !basicEquipWaiting){Executor.Apply(new AIPlan{Action=AIActionType.Equip,Item=BasicCombatArmor[basicCombatPhase-4].ToString()});return;}
        if(basicCombatPhase==9 && basicCombatPhaseFrames>=600)FinishBasicCombatVisual("observed","");
        else if(basicCombatPhaseFrames>=900)FinishBasicCombatVisual("failed","action deadline");
    }
    private void FinishBasicCombatVisual(string status,string error)
    {
        basicCombatVisualFinished=true;File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","basic-combat-visual-native-result.json"),JsonSerializer.Serialize(new{
            status,error,frames=basicCombatFrames,requests=PrimaryDecisionRequestCount-basicCombatRequests,combatRequests=CombatDecisionRequestCount-basicCombatCombatRequests,manualControlActive=ManualControlActive,teleports=Executor.NavigationTeleportCount,basicWaitProtected,basicPickupProtected,basicTargetIsolated,basicPickupExact,preservation=basicCombatAudit?.CheckPreservation(),events=basicCombatEvents,trace=basicCombatTrace,
            scope="Controlled original-house-copy zero-API fixture: equipment/ammo supplied and small enemies/ground item spawned by fixture. Weapon damage, pickup and equipment are native execution observations; visual appearance requires separate screenshots. Not autonomous gathering. Hostile spawns and supplied gear are test-only."
        },new JsonSerializerOptions{WriteIndented=false}));Stop();Executor.Clear(true);
    }
    private bool visualSnapshotStarted, visualSnapshotFinished, visualSnapshotWaitingEquip, visualSnapshotReady;
    private int visualSnapshotStage, visualSnapshotGear, visualSnapshotRequests, visualSnapshotFrames, visualSnapshotCombatRequests;
    private bool visualMotionStarted;
    private readonly HashSet<int> visualMotionPoses = new();
    private long visualSnapshotSequence;
    private DateTime visualSnapshotAt;
    private readonly List<object> visualSnapshotEvents = new();
    private string VisualSnapshotPath(string name) => Path.Combine(Main.SavePath, "AITeammate", name);
    private void BeginVisualSnapshot()
    {
        Stop(); Memory.Clear(); LlmConversationStore.ResetAll(); agentJournal.Clear(); Executor.Clear(true); Executor.ResetNavigationTelemetry();
        NPC npc = CompanionNpc!; Player player = Main.LocalPlayer;
        npc.Center = Stand(3175, 395); npc.velocity = Vector2.Zero;
        player.Center = Stand(3185, 395); player.velocity = Vector2.Zero;
        Main.dayTime = true; Main.time = 18000; Main.raining = false;
        Loadout.Reset(); Loadout.EnsureStarterTools();
        foreach (int id in BasicCombatArmor) Loadout.AddItem(id, 1);
        visualSnapshotRequests = PrimaryDecisionRequestCount; visualSnapshotCombatRequests = CombatDecisionRequestCount; visualSnapshotSequence = agentJournal.LastSequence;
        CombatPlannerEnabled = true; CompanionPlayerRenderer.DiagnosticsEnabled = true;
        visualSnapshotAt = DateTime.UtcNow; visualSnapshotStarted = true;
        string ack = VisualSnapshotPath("visual-stage-ack.txt"); if (File.Exists(ack)) File.Delete(ack);
        Stop();
    }
    private void TickVisualSnapshot()
    {
        if (Main.gameMenu || CompanionNpc == null) return;
        visualSnapshotFrames++; Main.LocalPlayer.immune = true; Main.LocalPlayer.immuneTime = 600;
        if (visualSnapshotFrames % 60 == 0) File.WriteAllText(VisualSnapshotPath("visual-stage-progress.json"), JsonSerializer.Serialize(new { frame = visualSnapshotFrames, stage = visualSnapshotStage, gear = visualSnapshotGear, waitingEquip = visualSnapshotWaitingEquip, ready = visualSnapshotReady, action = Executor.CurrentAction.ToString(), hold = Executor.HoldPosition, sequence = agentJournal.LastSequence }));
        if (DateTime.UtcNow - visualSnapshotAt > TimeSpan.FromMinutes(4) || Executor.NavigationTeleportCount != 0)
        { FinishVisualSnapshot("failed", "deadline or warp"); return; }
        if (visualSnapshotWaitingEquip)
        {
            var outcome = agentJournal.Outcomes.LastOrDefault(o => o.Sequence > visualSnapshotSequence);
            if (outcome == null) return;
            visualSnapshotSequence = outcome.Sequence; visualSnapshotEvents.Add(new { phase = "equip", item = BasicCombatArmor[visualSnapshotGear], outcome });
            if (outcome.Status != "succeeded") { FinishVisualSnapshot("failed", "equip did not succeed"); return; }
            visualSnapshotWaitingEquip = false; visualSnapshotGear++; Stop();
        }
        if (visualSnapshotStage == 3)
        {
            if (!visualMotionStarted) { visualMotionStarted = true; var playerBefore = Main.LocalPlayer.Center; Main.LocalPlayer.Center = Stand(3173, 381); Main.LocalPlayer.velocity = Vector2.Zero; visualSnapshotEvents.Add(new { phase = "controlled_player_target", before = new { x = playerBefore.X, y = playerBefore.Y }, after = new { x = Main.LocalPlayer.Center.X, y = Main.LocalPlayer.Center.Y }, note = "Controlled player target from existing house route; NPC position and velocity unchanged." }); HandlePlayerMessage(Main.LocalPlayer, "跟着我"); File.WriteAllText(VisualSnapshotPath("visual-stage-ready.txt"), "motion"); }
            object? diagnostic = VisualSnapshotDiagnostic();
            if (diagnostic != null) { var element = JsonSerializer.SerializeToElement(diagnostic); if (element.TryGetProperty("poseIndex", out var pose)) visualMotionPoses.Add(pose.GetInt32()); }
            string motionAck = VisualSnapshotPath("visual-stage-ack.txt");
            if (File.Exists(motionAck) && File.ReadAllText(motionAck).Trim() == "motion") { File.Delete(motionAck); FinishVisualSnapshot("observed", ""); }
            return;
        }
        int needed = visualSnapshotStage == 0 ? 0 : visualSnapshotStage == 1 ? 1 : BasicCombatArmor.Length;
        if (visualSnapshotGear < needed)
        {
            visualSnapshotWaitingEquip = true;
            Executor.SetHold(false);
            Executor.Apply(new AIPlan { Action = AIActionType.Equip, Item = BasicCombatArmor[visualSnapshotGear].ToString() });
            return;
        }
        string stage = visualSnapshotStage == 0 ? "nude" : visualSnapshotStage == 1 ? "helmet" : "full_gear";
        if (!visualSnapshotReady)
        {
            Stop(); visualSnapshotReady = true;
            visualSnapshotEvents.Add(new { phase = stage, frame = visualSnapshotFrames, diagnostic = VisualSnapshotDiagnostic(), armor = ShelterCounts(Loadout.Armor), accessories = ShelterCounts(Loadout.Accessories), npc = new { x = CompanionNpc.Center.X, y = CompanionNpc.Center.Y }, player = new { x = Main.LocalPlayer.Center.X, y = Main.LocalPlayer.Center.Y } });
            File.WriteAllText(VisualSnapshotPath("visual-stage-ready.txt"), stage);
        }
        string ackPath = VisualSnapshotPath("visual-stage-ack.txt");
        if (!File.Exists(ackPath) || File.ReadAllText(ackPath).Trim() != stage) return;
        File.Delete(ackPath); visualSnapshotEvents.Add(new { phase = "ack", stage, frame = visualSnapshotFrames });
        visualSnapshotReady = false; visualSnapshotStage++;
        
    }
    private object? VisualSnapshotDiagnostic()
    {
        var modNpc = CompanionNpc?.ModNPC;
        var field = modNpc?.GetType().GetField("playerRenderer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (field?.GetValue(modNpc) as CompanionPlayerRenderer)?.LastDrawDiagnostics;
    }
    private void FinishVisualSnapshot(string status, string error)
    {
        visualSnapshotFinished = true;
        File.WriteAllText(VisualSnapshotPath("visual-snapshot-native-result.json"), JsonSerializer.Serialize(new { status, error, frames = visualSnapshotFrames, requests = PrimaryDecisionRequestCount - visualSnapshotRequests, teleports = Executor.NavigationTeleportCount, combatRequests = CombatDecisionRequestCount - visualSnapshotCombatRequests, manualControlActive = ManualControlActive, motionPoses = visualMotionPoses, lastDrawDiagnostic = VisualSnapshotDiagnostic(), events = visualSnapshotEvents, scope = "Controlled daylight and equipment provided; one initial placement; screenshot acknowledgement required for each stage. Zero API; no claim of autonomous equipment acquisition." }));
        Stop(); Executor.Clear(true);
    }

    private bool localBossStarted, localBossFinished, localBossDespawned;
    private int localBossPhase, localBossFrames, localBossPhaseFrames, localBossSlot, localBossRequests;
    private long localBossSequence, localBossDeathBoundary, localBossSecondBoundary;
    private DateTime localBossAt;
    private readonly List<object> localBossEvents = new();
    private void BeginLocalBossAttack()
    {
        Stop(); Memory.Clear(); LlmConversationStore.ResetAll(); agentJournal.Clear(); Executor.Clear(true); Executor.ResetNavigationTelemetry();
        NPC npc = CompanionNpc!; Player player = Main.LocalPlayer;
        npc.Center = Stand(3170, 395); npc.velocity = Vector2.Zero; player.Center = Stand(3180, 395); player.velocity = Vector2.Zero;
        Loadout.Reset(); Loadout.EnsureStarterTools(); Loadout.AddItem(ItemID.WoodenBow, 1); Loadout.AddItem(ItemID.WoodenArrow, 100);
        Loadout.PreferredWeaponType = ItemID.WoodenBow; localBossRequests = PrimaryDecisionRequestCount; localBossSequence = agentJournal.LastSequence;
        localBossDeathBoundary = BossEvidence.DeathSequence; localBossAt = DateTime.UtcNow; localBossStarted = true;
        SpawnLocalBoss(true);
    }
    private void SpawnLocalBoss(bool oneLife)
    {
        NPC npc = CompanionNpc!;
        localBossSlot = NPC.NewNPC(new EntitySource_Misc("ControlledLocalBossAcceptance"), (int)npc.Center.X + 150, (int)npc.Bottom.Y - 32, NPCID.KingSlime);
        if (localBossSlot < 0 || localBossSlot >= Main.maxNPCs) { FinishLocalBoss("failed", "boss spawn failed"); return; }
        if (oneLife) Main.npc[localBossSlot].life = 1;
        localBossPhaseFrames = 0; localBossSequence = agentJournal.LastSequence;
        localBossEvents.Add(new { phase = oneLife ? "spawn_one_life" : "spawn_disappearance", slot = localBossSlot, life = Main.npc[localBossSlot].life, bossEvidence = BossEvidence.Snapshot() });
        ApplyLocalTask(new AIPlan { Action = AIActionType.Attack, Target = NPCID.KingSlime.ToString() });
    }
    private void TickLocalBossAttack()
    {
        if (Main.gameMenu || CompanionNpc == null) return;
        localBossFrames++; localBossPhaseFrames++; Main.LocalPlayer.immune = true; Main.LocalPlayer.immuneTime = 600;
        if (DateTime.UtcNow - localBossAt > TimeSpan.FromMinutes(2) || Executor.NavigationTeleportCount != 0)
        { FinishLocalBoss("failed", "deadline or warp"); return; }
        if (localBossPhase == 1 && !localBossDespawned && localBossPhaseFrames >= 2 && Executor.CurrentAction == AIActionType.Attack && Main.npc[localBossSlot].active)
        {
            localBossSecondBoundary = BossEvidence.DeathSequence; Main.npc[localBossSlot].active = false; localBossDespawned = true;
            localBossEvents.Add(new { phase = "controlled_disappearance", frame = localBossFrames, action = Executor.CurrentAction.ToString(), bossEvidence = BossEvidence.Snapshot() });
        }
        var outcome = agentJournal.Outcomes.LastOrDefault(o => o.Sequence > localBossSequence && o.Task.Action == AIActionType.Attack);
        if (outcome == null) return;
        localBossSequence = outcome.Sequence;
        if (localBossPhase == 0)
        {
            bool realDeath = BossEvidence.Defeats(NPCID.KingSlime, localBossDeathBoundary) > 0;
            localBossEvents.Add(new { phase = "death_observed", outcome, realDeath, bossEvidence = BossEvidence.Snapshot() });
            if (outcome.Status != "succeeded" || !realDeath) { FinishLocalBoss("failed", "native death or companion damage not proven"); return; }
            localBossPhase = 1; SpawnLocalBoss(false); return;
        }
        bool noNewDeath = BossEvidence.DeathSequence == localBossSecondBoundary;
        localBossEvents.Add(new { phase = "disappearance_outcome", outcome, localBossDespawned, noNewDeath, bossEvidence = BossEvidence.Snapshot() });
        FinishLocalBoss(localBossDespawned && noNewDeath && outcome.Status == "blocked" ? "passed" : "failed", "");
    }
    private void FinishLocalBoss(string status, string error)
    {
        localBossFinished = true;
        File.WriteAllText(VisualSnapshotPath("local-boss-attack-native-result.json"), JsonSerializer.Serialize(new { status, error, frames = localBossFrames, requests = PrimaryDecisionRequestCount - localBossRequests, teleports = Executor.NavigationTeleportCount, events = localBossEvents, scope = "Controlled isolated test: first native King Slime spawned with one life; second spawned normally and explicitly deactivated after attack began. Not a boss combat strength assessment." }));
        Stop(); Executor.Clear(true);
    }

}
