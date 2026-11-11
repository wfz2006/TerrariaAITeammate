using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private bool admissionTestStarted,admissionTestFinished,admissionOldReview;
    private int admissionFrames,admissionRequests,admissionCombatRequests;
    private long admissionQueueSequence;
    private readonly List<object> admissionChecks=new();
    private static FieldInfo AdmissionField(string name)=>typeof(AIExecutor).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)??throw new MissingFieldException(typeof(AIExecutor).FullName,name);
    private void AdmissionReset(string goal)
    {
        Executor.Clear(true);ClearActiveGoal();decisionReceipts.Clear();SetActiveGoal(goal);manualControlActive=false;autoMode=false;reviewAfterEachStep=false;dispatchRepairs.Reset();
    }
    private void AdmissionSeed(AIPlan task)
    {
        string key=TaskFingerprint(task),conditions=Executor.CaptureRetryConditions(task);
        taskRetries.RecordFailure(key,conditions,10001);taskRetries.RecordFailure(key,conditions,10002);
    }
    private void BeginTaskAdmissionAcceptance()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear(true);Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;npc.Center=Stand(3220,401);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();Loadout.PreferredWeaponType=ItemID.CopperShortsword;
        admissionOldReview=reviewAfterEachStep;admissionRequests=PrimaryDecisionRequestCount;admissionCombatRequests=CombatDecisionRequestCount;admissionTestStarted=true;
        try
        {
            // Ledger entries are deliberately synthetic prerequisites. No physical failure or outcome is fabricated.
            AdmissionReset("fixture explicit retreat queued attack");var queued=new AIPlan{Action=AIActionType.Attack,Target="nearest"};AdmissionSeed(queued);
            long seq=agentJournal.LastSequence;Executor.ApplySequence(new[]{new AIPlan{Action=AIActionType.Retreat},queued});bool resumed=Executor.ResumeCombat();
            admissionChecks.Add(new{name="explicit_retreat_queued_attack_rejected",passed=!resumed&&Executor.CurrentAction==AIActionType.Wait&&agentJournal.LastSequence==seq&&decisionReceipts.Last?.code=="unchanged_preconditions_retry_exhausted",journalBefore=seq,journalAfter=agentJournal.LastSequence,action=Executor.CurrentAction.ToString(),receipt=decisionReceipts.Last});

            AdmissionReset("fixture explicit retreat constructed attack");var constructed=new AIPlan{Action=AIActionType.Attack,Target="nearest"};AdmissionSeed(constructed);
            seq=agentJournal.LastSequence;Executor.Apply(new AIPlan{Action=AIActionType.Retreat});resumed=Executor.ResumeCombat();
            admissionChecks.Add(new{name="explicit_retreat_constructed_attack_rejected",passed=!resumed&&Executor.CurrentAction==AIActionType.Wait&&agentJournal.LastSequence==seq&&decisionReceipts.Last?.code=="unchanged_preconditions_retry_exhausted",journalBefore=seq,journalAfter=agentJournal.LastSequence,action=Executor.CurrentAction.ToString(),receipt=decisionReceipts.Last});

            AdmissionReset("fixture suspended original attack");var original=new AIPlan{Action=AIActionType.Attack,Target="nearest"};Executor.Apply(original);bool began=Executor.BeginRetreat(out string retreatMessage);AdmissionSeed(original);
            seq=agentJournal.LastSequence;resumed=Executor.ResumeCombat();bool same=ReferenceEquals(ContinuousField("currentPlan"),original);
            admissionChecks.Add(new{name="suspended_exact_reference_resumes",passed=began&&resumed&&same&&Executor.CurrentAction==AIActionType.Attack&&agentJournal.LastSequence==seq,began,resumed,same,journalBefore=seq,journalAfter=agentJournal.LastSequence,retreatMessage});

            AdmissionReset("fixture replacement attack");var first=new AIPlan{Action=AIActionType.Attack,Target="nearest"};Executor.Apply(first);began=Executor.BeginRetreat(out _);
            var queue=(Queue<AIPlan>)(ContinuousField("pendingPlans")??throw new InvalidOperationException("pending queue missing"));AIPlan removed=queue.Dequeue();var replacement=new AIPlan{Action=AIActionType.Attack,Target="nearest"};queue.Enqueue(replacement);AdmissionSeed(replacement);
            seq=agentJournal.LastSequence;resumed=Executor.ResumeCombat();
            admissionChecks.Add(new{name="same_parameters_different_reference_rejected",passed=began&&ReferenceEquals(first,removed)&&TaskFingerprint(first)==TaskFingerprint(replacement)&&!ReferenceEquals(first,replacement)&&!resumed&&Executor.CurrentAction==AIActionType.Wait&&agentJournal.LastSequence==seq&&decisionReceipts.Last?.code=="unchanged_preconditions_retry_exhausted",began,resumed,sameFingerprint=TaskFingerprint(first)==TaskFingerprint(replacement),journalBefore=seq,journalAfter=agentJournal.LastSequence,receipt=decisionReceipts.Last});

            AdmissionReset("fixture craft cache signature");var craft=new AIPlan{Action=AIActionType.Craft,Item=ItemID.WoodenChair.ToString(),Count=1};
            var fieldX=AdmissionField("craftStationX");var fieldY=AdmissionField("craftStationY");object? savedX=fieldX.GetValue(Executor),savedY=fieldY.GetValue(Executor);
            Point? station=null;for(int x=3160;x<=3270&&!station.HasValue;x++)for(int y=350;y<=420;y++)if(Math.Abs(x-(int)(npc.Center.X/16))>20&&Main.tile[x,y].HasTile&&Main.tile[x,y].TileType==TileID.WorkBenches){station=new Point(x,y);break;}
            string before="",after="";try{fieldX.SetValue(Executor,-1);fieldY.SetValue(Executor,-1);before=Executor.CaptureRetryConditions(craft);if(station.HasValue){fieldX.SetValue(Executor,station.Value.X);fieldY.SetValue(Executor,station.Value.Y);after=Executor.CaptureRetryConditions(craft);}}finally{fieldX.SetValue(Executor,savedX);fieldY.SetValue(Executor,savedY);}
            admissionChecks.Add(new{name="craft_stale_station_cache_ignored",passed=station.HasValue&&before==after,station=station.HasValue?new{x=station.Value.X,y=station.Value.Y}:null,before,after,cacheRestored=true});

            AdmissionReset("fixture queued tail admission");var mine=new AIPlan{Action=AIActionType.Mine,Target="wood",Count=1};AdmissionSeed(mine);
            admissionQueueSequence=agentJournal.LastSequence;Executor.ApplySequence(new[]{new AIPlan{Action=AIActionType.Equip,Item=ItemID.CopperShortsword.ToString()},mine});
            File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","task-admission-ready.txt"),"queue_started");
        }
        catch(Exception e){FinishTaskAdmissionAcceptance("failed_setup",e.GetType().Name+": "+e.Message);}
    }
    private void TickTaskAdmissionAcceptance()
    {
        if(Main.gameMenu||CompanionNpc==null)return;admissionFrames++;Main.LocalPlayer.immune=true;Main.LocalPlayer.immuneTime=600;
        var outcomes=agentJournal.Outcomes.Where(o=>o.Sequence>admissionQueueSequence).ToArray();
        if(outcomes.Any(o=>o.Task.Action==AIActionType.Equip))
        {
            bool equip=outcomes.Any(o=>o.Task.Action==AIActionType.Equip&&o.Status=="succeeded");bool mineOutcome=outcomes.Any(o=>o.Task.Action==AIActionType.Mine);
            bool rejected=decisionReceipts.Last?.code=="unchanged_preconditions_retry_exhausted"&&Executor.CurrentAction==AIActionType.Wait;
            admissionChecks.Add(new{name="queued_tail_mine_rejected_after_real_equip",passed=equip&&!mineOutcome&&rejected,equip,mineOutcome,action=Executor.CurrentAction.ToString(),outcomes,receipt=decisionReceipts.Last});
            bool passed=admissionChecks.All(c=>JsonSerializer.SerializeToElement(c).GetProperty("passed").GetBoolean())&&PrimaryDecisionRequestCount==admissionRequests&&CombatDecisionRequestCount==admissionCombatRequests&&Executor.NavigationTeleportCount==0;
            FinishTaskAdmissionAcceptance(passed?"passed":"failed",passed?"":"one or more runtime checks failed");return;
        }
        if(admissionFrames>=180)FinishTaskAdmissionAcceptance("failed","equip outcome deadline");
    }
    private void FinishTaskAdmissionAcceptance(string status,string error)
    {
        admissionTestFinished=true;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","task-admission-native-result.json"),JsonSerializer.Serialize(new{status,error,frames=admissionFrames,requests=PrimaryDecisionRequestCount-admissionRequests,combatRequests=CombatDecisionRequestCount-admissionCombatRequests,warps=Executor.NavigationTeleportCount,checks=admissionChecks,
            scope="Independent isolated zero-API admission test. Retry ledger is seeded synthetically as a prerequisite; no physical failure/outcome is forged. Queue prefix Equip executes through the real game tick. Replacement queue reference and stale craft cache are fixture-only mutations; no world terrain, inventory materials, actor motion or damage is injected after setup."}));
        Executor.Clear(true);ClearActiveGoal();reviewAfterEachStep=admissionOldReview;manualControlActive=true;autoMode=false;pendingCommand=null;dispatchRepairs.Reset();
    }
}
