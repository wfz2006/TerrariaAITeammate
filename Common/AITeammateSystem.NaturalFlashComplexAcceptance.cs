using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private long naturalBossSpawnBoundary, naturalBossSpawnId, naturalBossPickupSequence;
    private int naturalBossLifeMax;
    private float? naturalSummonDistance;
    private ModNPC? naturalBossActor;
    private readonly HashSet<(int x, int y)> naturalHouseSites = new();
    private ShelterNewRoomReport? naturalNewRoom;

    private object? PrepareNaturalBoss(NPC npc, Player player)
    {
        if (!player.active || player.dead || Main.npc.Any(other => other.active && other.boss)) return null;
        npc.Center = player.Center - new Vector2(64f, 0f);
        npc.velocity = player.velocity = Vector2.Zero;
        if (!TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, 0, npc.width)) return null;
        naturalBossActor = npc.ModNPC;
        naturalBossSpawnBoundary = BossEvidence.Snapshot().Select(encounter => encounter.SpawnId).DefaultIfEmpty().Max();
        var supplies = new Dictionary<int, int>
        {
            [ItemID.SlimeCrown] = 1, [ItemID.PhoenixBlaster] = 1, [ItemID.MusketBall] = 400, [ItemID.HealingPotion] = 3,
            [ItemID.MoltenHelmet] = 1, [ItemID.MoltenBreastplate] = 1, [ItemID.MoltenGreaves] = 1
        };
        if (NaturalBossPreparation)
        {
            supplies.Remove(ItemID.SlimeCrown);
            if (!PrepareNaturalBossCraftStock(supplies)) return null;
        }
        foreach (var pair in supplies) if (!Loadout.AddItem(pair.Key, pair.Value)) return null;
        foreach (Item item in player.inventory.Concat(player.armor).Concat(player.miscEquips)) item.TurnToAir();
        for (int i = 0; i < player.buffType.Length; i++) { player.buffType[i] = 0; player.buffTime[i] = 0; }
        Main.mouseItem.TurnToAir();
        foreach (Projectile projectile in Main.projectile)
            if (projectile.active && projectile.friendly) projectile.active = false;
        return new { supplies, playerObservesOnly = true, playerDamageDisabled = NaturalFlashObserverNoPickup, ordinaryBossStats = true, companionLife = npc.life,
            companionInvulnerable = npc.dontTakeDamage, gameMode = Main.GameMode };
    }

    private void MaintainNaturalBossObserver(Player player)
    {
        player.controlUseItem = player.controlUseTile = false;
        player.itemAnimation = player.itemTime = 0;
        player.statLife = player.statLifeMax2;
        foreach (NPC npc in Main.npc)
            if (npc.active && npc.friendly && npc != CompanionNpc) npc.active = false;
    }

    private bool IsNaturalBossActor(NPC npc) => naturalFlashRunning && !naturalFlashFinished &&
        naturalBossActor != null && ReferenceEquals(naturalBossActor, npc.ModNPC);

    internal void RecordNaturalBossIncomingHit(NPC npc, int damage, NPC? sourceNpc, Projectile? projectile)
    {
        if (!IsNaturalBossActor(npc)) return;
        naturalEvents.Add(new { kind = "companion_incoming_hit", tick = Main.GameUpdateCount,
            sourceKind = sourceNpc != null ? "npc_contact" : "projectile", sourceType = sourceNpc?.type ?? projectile?.type,
            sourceSlot = sourceNpc?.whoAmI ?? projectile?.whoAmI, sourceBossSpawnId = sourceNpc?.GetGlobalNPC<BossWitness>().SpawnId,
            damage, lifeAfter = npc.life, x = npc.Center.X, feetY = npc.Bottom.Y, action = Executor.CurrentAction.ToString(),
            hold = Executor.HoldPosition, bossSpawnId = naturalBossSpawnId, replayStage = naturalReplayStage,
            requests = PrimaryDecisionRequestCount - naturalPrimaryStart });
    }

    internal void RecordNaturalBossActorDeath(NPC npc)
    {
        if (!IsNaturalBossActor(npc)) return;
        naturalEvents.Add(new { kind = "companion_death", tick = Main.GameUpdateCount, life = npc.life,
            x = npc.Center.X, feetY = npc.Bottom.Y, action = Executor.CurrentAction.ToString(), bossSpawnId = naturalBossSpawnId });
    }

    private void TrackNaturalBoss(NPC npc, Player player, IReadOnlyList<AgentOutcome> fresh)
    {
        var newSpawns = BossEvidence.Snapshot().Where(encounter => encounter.SpawnId > naturalBossSpawnBoundary).ToArray();
        if (newSpawns.Length == 0) return;
        if (newSpawns.Length != 1 || newSpawns[0].NpcType != NPCID.KingSlime)
        { naturalPhysicalViolation = true; return; }
        BossEncounter encounter = newSpawns[0];
        if (naturalBossSpawnId != 0)
        {
            if (NaturalBossReturnCase && naturalBossPickupSequence == 0 && encounter.DeathSequence > 0 && encounter.LootPickedUp > 0)
            {
                AgentOutcome? pickup = fresh.FirstOrDefault(outcome => outcome.Status == "succeeded" && outcome.Task.Action == AIActionType.Pickup);
                if (pickup != null)
                {
                    naturalBossPickupSequence = pickup.Sequence;
                    ObserveNaturalBossReturn(npc, player, "boss_pickup_position");
                }
            }
            if (NaturalBossReturnCase && naturalBossPickupSequence > 0)
                foreach (AgentOutcome outcome in fresh)
                    if (outcome.Status == "succeeded" && outcome.Task.Action == AIActionType.Come && outcome.Sequence > naturalBossPickupSequence)
                        ObserveNaturalBossReturn(npc, player, "boss_return_action", outcome.Sequence);
            bool blockedAttack = fresh.Any(outcome => outcome.Status == "blocked" && outcome.Task.Action == AIActionType.Attack);
            if (blockedAttack || player.dead || naturalFrames % 60 == 0)
            {
                NPC boss = Main.npc[encounter.Slot];
                naturalEvents.Add(new { kind = "boss_state", tick = Main.GameUpdateCount, encounter.SpawnId,
                    slotMatches = boss.GetGlobalNPC<BossWitness>().SpawnId == encounter.SpawnId, boss.active, boss.life,
                    boss.dontTakeDamage, chaseable = boss.CanBeChasedBy(), distance = Vector2.Distance(npc.Center, boss.Center) / 16f,
                    observerDead = player.dead, observerLife = player.statLife, companionLife = npc.life, blockedAttack });
            }
            return;
        }
        naturalBossSpawnId = encounter.SpawnId;
        naturalBossLifeMax = Main.npc[encounter.Slot].lifeMax;
        naturalSummonDistance = Vector2.Distance(npc.Center, player.Center) / 16f;
        naturalEvents.Add(new { kind = "actual_boss_spawn", spawn = encounter.SpawnId, npcType = encounter.NpcType,
            lifeMax = naturalBossLifeMax, distance = naturalSummonDistance, tick = Main.GameUpdateCount,
            crown = Loadout.CountItem(ItemID.SlimeCrown), useOutcome = fresh.LastOrDefault(outcome => outcome.Task.Action == AIActionType.Use) });
    }

    private bool VerifyNaturalBoss(NPC npc, Player player)
    {
        BossEncounter? encounter = BossEvidence.ReadEncounter(naturalBossSpawnId);
        bool usedSummoner = naturalOutcomes.Any(outcome => outcome.Status == "succeeded" && outcome.Task.Action == AIActionType.Use) &&
            Loadout.CountItem(ItemID.SlimeCrown) == 0 && naturalSummonDistance is <= 30f;
        bool checkedSummon = naturalOutcomes.Any(outcome => outcome.Status == "succeeded" && outcome.Task.Action == AIActionType.Inspect && outcome.Task.Target == "boss");
        bool returned = !NaturalBossReturnCase || ObserveNaturalBossReturn(npc, player, "boss_return_position");
        bool protectionBound = !NaturalBossReturnCase || NaturalFlashReplay || NaturalCompletionProof(NaturalRequest, "structure_preserved");
        bool prepared = !NaturalBossFromScratch || naturalRubySequence > 0 && naturalRubySequence < naturalMetalCrownSequence &&
            naturalMetalCrownSequence < naturalSlimeCrownSequence && naturalSlimeCrownSequence < naturalPreparedSummonSequence &&
            NaturalCompletionProof(NaturalRequest, "near_player");
        return prepared && usedSummoner && checkedSummon && returned && protectionBound && encounter is { DeathSequence: > 0, LootPickedUp: > 0 } &&
            naturalBossLifeMax > 0 && encounter.CompanionDamage >= naturalBossLifeMax &&
            NaturalCompletionProof(NaturalRequest, "boss_defeated") && NaturalCompletionProof(NaturalRequest, "boss_loot");
    }

    private bool ObserveNaturalBossReturn(NPC npc, Player player, string kind, long? returnSequence = null)
    {
        float horizontal = Math.Abs(npc.Center.X - player.Center.X);
        float vertical = Math.Abs(npc.Bottom.Y - player.Bottom.Y);
        bool grounded = TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);
        bool passage = OrderedGoalPassage(npc, player);
        bool verified = naturalBossPickupSequence > 0 && horizontal <= 48 && vertical <= 24 && grounded && passage;
        naturalEvents.Add(new { kind, tick = Main.GameUpdateCount, spawn = naturalBossSpawnId, pickupSequence = naturalBossPickupSequence, returnSequence,
            actorX = npc.Center.X, actorFeet = npc.Bottom.Y, playerX = player.Center.X, playerFeet = player.Bottom.Y,
            horizontal, vertical, grounded, passage, verified });
        return verified;
    }

    private object? PrepareNaturalHouse(NPC npc, Player player)
    {
        // An exterior starting stand, with no selected site passed to the planner.
        npc.Center = new Vector2(3096.339f * 16, 398.6875f * 16);
        player.Center = Stand(3173, 395);
        npc.velocity = player.velocity = Vector2.Zero;
        if (!TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, 0, npc.width)) return null;
        var supplies = new Dictionary<int, int>(CompanionConstruction.ShelterMaterials("wood"));
        supplies[ItemID.Wood] += 8;
        foreach (var pair in supplies) if (!Loadout.AddItem(pair.Key, pair.Value)) return null;
        return new { supplies, requestedWidth = 10, requestedHeight = 7, material = "wood" };
    }

    private bool AllowNaturalHousingAction(AIPlan plan, AIPlan task)
    {
        bool preserved = (plan.CompletionConditions ?? goalPlanningState.Completion.Conditions).Any(condition => condition.Type == "structure_preserved");
        return preserved && (task.Action == AIActionType.Place || task.Action == AIActionType.Build &&
            task.Target is "" or "wood" && task.Blueprint is "" or "shelter");
    }

    private void TrackNaturalHouse()
    {
        JsonElement progress = JsonSerializer.SerializeToElement(Executor.ConstructionProgress);
        if (progress.ValueKind == JsonValueKind.Object && progress.GetProperty("width").GetInt32() == 10 && progress.GetProperty("height").GetInt32() == 7 &&
            progress.GetProperty("material").GetString() == "wood")
            naturalHouseSites.Add((progress.GetProperty("left").GetInt32(), progress.GetProperty("top").GetInt32()));
    }

    private bool VerifyNaturalHouse()
    {
        long? built = naturalOutcomes.FirstOrDefault(outcome => outcome.Status == "succeeded" &&
            outcome.Task.Action is AIActionType.Build or AIActionType.Place)?.Sequence;
        bool preflight = built.HasValue && naturalOutcomes.Any(outcome => outcome.Sequence < built && outcome.Status == "succeeded" &&
            outcome.Task.Action == AIActionType.Inspect && outcome.Task.Target is "build" or "site" or "design");
        var candidates = naturalHouseSites.Select(site => naturalHouseAudit?.CheckNewRoom(site.x, site.y, 10, 7)).ToList();
        JsonElement proof = JsonSerializer.SerializeToElement(lastCompletedGoal);
        if (proof.ValueKind == JsonValueKind.Object)
            foreach (GoalCondition condition in proof.GetProperty("conditions").Deserialize<GoalCondition[]>() ?? Array.Empty<GoalCondition>())
                if (condition is { Type: "new_housing_valid", X: int x, Y: int y })
                    candidates.Add(naturalHouseAudit?.CheckNewRoom(x, y, condition.Width, condition.Height));
        foreach (ShelterNewRoomReport? room in candidates)
        {
            // RoomCheck bounds cover the interior; add the one-tile enclosing frame.
            if (room is not { validNew: true, nativeValid: true, protectedOverlapCells: 0, actualRoomBounds: { width: 8, height: 5 } interior } ||
                !NaturalWoodRoomMatches(new ShelterAuditBounds(interior.x - 1, interior.y - 1, 10, 7))) continue;
            naturalNewRoom = room;
            break;
        }
        return preflight && naturalNewRoom != null && NaturalCompletionProof(NaturalHouseRequest, "new_housing_valid") &&
            NaturalCompletionProof(NaturalHouseRequest, "structure_preserved");
    }

    private static bool NaturalWoodRoomMatches(ShelterAuditBounds bounds)
    {
        bool door = false, bench = false, chair = false, torch = false;
        for (int dy = 0; dy < 7; dy++)
        for (int dx = 0; dx < 10; dx++)
        {
            Tile tile = Main.tile[bounds.x + dx, bounds.y + dy];
            int type = tile.HasTile ? tile.TileType : -1;
            if (dy is 0 or 6)
            {
                if (type != TileID.WoodBlock) return false;
            }
            else if (dx is 0 or 9)
            {
                if (type is TileID.ClosedDoor or TileID.OpenDoor) door = true;
                else if (type != TileID.WoodBlock) return false;
            }
            else
            {
                if (tile.WallType != WallID.Wood) return false;
                bench |= type == TileID.WorkBenches;
                chair |= type == TileID.Chairs;
                torch |= type == TileID.Torches;
            }
        }
        return door && bench && chair && torch;
    }
}

public sealed class NaturalBossIncomingHitWitness : GlobalNPC
{
    public override void OnHitNPC(NPC npc, NPC target, NPC.HitInfo hit) =>
        AITeammateSystem.Instance?.RecordNaturalBossIncomingHit(target, hit.Damage, npc, null);

    public override void OnHitByProjectile(NPC npc, Projectile projectile, NPC.HitInfo hit, int damageDone) =>
        AITeammateSystem.Instance?.RecordNaturalBossIncomingHit(npc, damageDone, null, projectile);
}
