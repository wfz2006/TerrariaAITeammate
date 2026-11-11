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
    private bool shelterTransitStarted,shelterTransitFinished;private int shelterTransitPhase,shelterTransitFrames,shelterTransitRequests;
    private long shelterTransitSequence;private DateTime shelterTransitAt;
    private readonly List<object> shelterTransitEvents=new(),shelterTransitTrace=new();
    private object? shelterTransitBefore,shelterTransitAfter;
    private ShelterWorldAudit? shelterTransitAudit;
    private bool ShelterTransitEbonwood => Environment.GetEnvironmentVariable("AITEAMMATE_HOUSE_CASE")=="shelter_ebonwood_native";
    private object ShelterTransitPreservationObservation()
    {
        var conditions = new List<GoalCondition> { new() { Type = "structure_preserved" },
            new() { Type = "new_housing_valid", X=3100, Y=393, Width=10, Height=7 } };
        var native = GoalWorldObserver.Capture(this, conditions, goalBossDeathBoundary);
        return new { tick=Main.GameUpdateCount, goal=activeGoal, goalStartSequence, conditions,
            nativeCondition=native.NativeConditions, productionDebug=PreservationEvidence.DebugSnapshot(),
            scope="Production goal baseline captured by SetActiveGoal; native condition evaluated against that same immutable baseline." };
    }
    private static object ShelterTransitGeometry()
    {
        var cells=new List<object>();for(int x=3090;x<=3150;x++)for(int y=380;y<=410;y++)
        {Tile t=Main.tile[x,y];cells.Add(new{x,y,t.HasTile,t.TileType,t.WallType,t.Slope,t.IsHalfBlock,t.IsActuated,t.TileFrameX,t.TileFrameY,t.LiquidAmount,t.LiquidType,
            solid=t.HasTile&&Main.tileSolid[t.TileType],solidTop=t.HasTile&&Main.tileSolidTop[t.TileType],platform=t.HasTile&&TileID.Sets.Platforms[t.TileType]});}
        return new{bounds=new{x=3090,y=380,width=61,height=31},cells};
    }
    private void BeginShelterTransit()
    {
        Stop();Memory.Clear();LlmConversationStore.ResetAll();agentJournal.Clear();Executor.Clear();Executor.ResetNavigationTelemetry();
        NPC npc=CompanionNpc!;Player p=Main.LocalPlayer;npc.Center=new Vector2(3096.339f*16,398.6875f*16);npc.velocity=Vector2.Zero;p.Center=Stand(3173,395);p.velocity=Vector2.Zero;
        Loadout.Reset();Loadout.EnsureStarterTools();var material=ShelterTransitEbonwood?"ebonwood":"wood";var provided=CompanionConstruction.ShelterMaterials(material);
        foreach(var pair in provided)if(!Loadout.AddItem(pair.Key,pair.Value))throw new InvalidOperationException("Diagnostic starting materials did not fit.");
        shelterTransitAudit=ShelterWorldAudit.Capture(3220,390);
        shelterTransitEvents.Add(new{phase="fixture_audit_explanations",tick=Main.GameUpdateCount,
            scope="Separate fixture audit centered on the original house; not the production goal baseline.",
            at3109_399=shelterTransitAudit.ExplainProtection(3109,399),at3410_407=shelterTransitAudit.ExplainProtection(3410,407),at3474_402=shelterTransitAudit.ExplainProtection(3474,402)});
        SetActiveGoal("原屋保护通行诊断", "零 API 受控原屋保护通行诊断");
        shelterTransitEvents.Add(new{phase="production_preservation_before",observation=ShelterTransitPreservationObservation()});
        shelterTransitBefore=ShelterTransitGeometry();shelterTransitRequests=PrimaryDecisionRequestCount;shelterTransitSequence=agentJournal.LastSequence;shelterTransitAt=DateTime.UtcNow;shelterTransitStarted=true;
        var itemStyles=new List<object>();foreach(var pair in provided){var item=new Item();item.SetDefaults(pair.Key);itemStyles.Add(new{itemId=pair.Key,item.createTile,item.createWall,item.placeStyle});}
        shelterTransitEvents.Add(new{phase="setup",material,provided,itemStyles,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},player=new{x=p.Center.X/16,y=p.Center.Y/16,feet=p.Bottom.Y/16},scope="Controlled diagnostic materials, not an autonomous result."});
        DispatchShelterTransit();
    }
    private void DispatchShelterTransit()
    {
        AIPlan task=shelterTransitPhase switch{
            0 or 4=>new(){Action=AIActionType.Inspect,Target="route",Item="3104,397"},
            1 or 5=>new(){Action=AIActionType.Inspect,Target="route",Item="3114,398"},
            2 or 6=>new(){Action=AIActionType.Inspect,Target="route",Item="3140,395"},
            3=>new(){Action=AIActionType.Build,Blueprint="shelter",Target=ShelterTransitEbonwood?"ebonwood":"wood",X=3100,Y=393},
            7=>new(){Action=AIActionType.Explore,X=3104,Y=397},
            8=>new(){Action=AIActionType.Explore,X=3114,Y=398},
            _=>new(){Action=AIActionType.Explore,X=3140,Y=395}};
        shelterTransitEvents.Add(new{phase="dispatch",index=shelterTransitPhase,task});Executor.Apply(task);
    }
    private void TickShelterTransit()
    {
        if(Main.gameMenu || CompanionNpc==null)return;NPC npc=CompanionNpc;Player p=Main.LocalPlayer;shelterTransitFrames++;
        p.immune=true;p.immuneTime=600;p.statLife=p.statLifeMax2;foreach(NPC hostile in Main.npc)if(hostile.active&&!hostile.friendly)hostile.active=false;
        if(shelterTransitFrames%15==0)shelterTransitTrace.Add(new{frame=shelterTransitFrames,phase=shelterTransitPhase,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16,vx=npc.velocity.X,vy=npc.velocity.Y,npc.width,npc.height},
            grounded=TilePathfinder.IsGroundedAt(npc.Center.X,npc.Bottom.Y,npc.velocity.Y,npc.width),pathIndex=ContinuousField("navIndex"),action=Executor.CurrentAction.ToString(),progress=Executor.ConstructionProgress,warps=Executor.NavigationTeleportCount});
        if(Executor.NavigationTeleportCount>0 || DateTime.UtcNow-shelterTransitAt>TimeSpan.FromMinutes(5)){FinishShelterTransit("failed","teleport or deadline");return;}
        var outcome=agentJournal.Outcomes.LastOrDefault(o=>o.Sequence>shelterTransitSequence);
        if(outcome==null)return;shelterTransitSequence=outcome.Sequence;
        shelterTransitEvents.Add(new{phase="outcome",index=shelterTransitPhase,outcome,npc=new{x=npc.Center.X/16,y=npc.Center.Y/16,feet=npc.Bottom.Y/16},inventory=ShelterCounts(Loadout.Inventory),progress=Executor.ConstructionProgress});
        if(shelterTransitPhase==3)
        {
            shelterTransitAfter=ShelterTransitGeometry();
            shelterTransitEvents.Add(new{phase="production_preservation_after_build",outcomeStatus=outcome.Status,
                observation=ShelterTransitPreservationObservation(),fixtureAudit=shelterTransitAudit?.CheckPreservation(),
                scope="Production goal baseline and separate fixture audit are reported independently."});
            if(outcome.Status!="succeeded"){FinishShelterTransit("failed","native build did not complete");return;}
        }
        if(shelterTransitPhase>=7 && outcome.Status!="succeeded"){FinishShelterTransit("observed_blocked","actual movement blocked; graph result is separately recorded");return;}
        if(++shelterTransitPhase>9){FinishShelterTransit("observed","");return;}DispatchShelterTransit();
    }
    private void FinishShelterTransit(string status,string error)
    {
        shelterTransitFinished=true;
        File.WriteAllText(Path.Combine(Main.SavePath,"AITeammate","shelter-transit-native-result.json"),JsonSerializer.Serialize(new{status,error,frames=shelterTransitFrames,requests=PrimaryDecisionRequestCount-shelterTransitRequests,teleports=Executor.NavigationTeleportCount,
            geometryBefore=shelterTransitBefore,geometryAfter=shelterTransitAfter,events=shelterTransitEvents,trace=shelterTransitTrace,
            scope="Controlled original-house-copy diagnostic: supplied native template materials solely at setup, replayed actual Build site (3100,393); route Inspect pre/post and actual walk, no LLM, not autonomous construction evidence. Hostiles removed/player protected; no direct tile edits or mid-case reposition."
        },new JsonSerializerOptions{WriteIndented=false}));Stop();Executor.Clear(true);
    }
}
