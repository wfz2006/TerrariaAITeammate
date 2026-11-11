using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

/// <summary>Reads actual current facts on the game thread. No model-supplied observation is accepted.</summary>
public static class GoalWorldObserver
{
    private static readonly FieldInfo[] HousingScratch = typeof(WorldGen)
        .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(f => !f.IsLiteral && !f.IsInitOnly && (f.Name.StartsWith("room", StringComparison.Ordinal) ||
            f.Name is "numRoomTiles" or "canSpawn" or "sharedRoomX")).ToArray();

    public static GoalEvidenceSnapshot Capture(AITeammateSystem system, IReadOnlyList<GoalCondition> conditions,
        long bossDeathBoundary)
    {
        NPC? actor = system.CompanionNpc;
        if (Main.gameMenu || actor is not { active: true }) return new();
        CompanionLoadout loadout = system.Loadout;
        Player player = Main.LocalPlayer;
        var equipped = loadout.Armor.Concat(loadout.Accessories).Where(i => !i.IsAir).Select(i => i.type).ToHashSet();
        if (loadout.GetBestWeapon() is { IsAir: false } weapon) equipped.Add(weapon.type);
        var bosses = conditions.Where(c => c.NpcType.HasValue).Select(c => c.NpcType!.Value).Distinct().ToArray();
        var native = new Dictionary<int, GoalNativeConditionResult>();
        for (int index = 0; index < conditions.Count; index++)
        {
            GoalCondition condition = conditions[index];
            if (condition.Type == "ordered_actions")
            {
                native[index] = system.ReadOrderedActions(condition);
                continue;
            }
            if (condition.Type == "visited_location")
            {
                native[index] = system.VisitEvidence.Read(condition, WorldKnowledgeObserver.LoadEpoch, system.GoalStartSequence);
                continue;
            }
            if (condition.Type == "structure_preserved")
            {
                native[index] = system.PreservationEvidence.Read(Main.worldID, WorldKnowledgeObserver.LoadEpoch,
                    system.GoalStartSequence, system.ActiveGoal, Main.GameUpdateCount);
                continue;
            }
            if (condition.Type == "new_housing_valid")
            {
                native[index] = system.PreservationEvidence.ReadNewHousing(condition, Main.worldID,
                    WorldKnowledgeObserver.LoadEpoch, system.GoalStartSequence, system.ActiveGoal, Main.GameUpdateCount);
                continue;
            }
            if (condition.Type is not ("area_matches" or "housing_valid")) continue;
            native[index] = ReadNative(condition, actor);
        }
        return new GoalEvidenceSnapshot
        {
            Inventory = loadout.Inventory.Where(i => !i.IsAir).GroupBy(i => i.type)
                .ToDictionary(g => g.Key, g => g.Sum(i => i.stack)),
            Equipped = equipped,
            HorizontalPixels = player.active && !player.dead ? Math.Abs(actor.Center.X - player.Center.X) : null,
            VerticalPixels = player.active && !player.dead ? Math.Abs(actor.Bottom.Y - player.Bottom.Y) : null,
            BossDefeats = bosses.ToDictionary(id => id, id => system.BossEvidence.Defeats(id, bossDeathBoundary)),
            BossLootPickups = bosses.ToDictionary(id => id, id => system.BossLootPickups(id, bossDeathBoundary)),
            NativeConditions = native
        };
    }

    internal static void BeginPreservation(AITeammateSystem system, string goal, string? sourcePlayerRequest)
    {
        NPC? actor = system.CompanionNpc;
        bool useCompanion = actor is { active: true };
        Vector2 center = useCompanion ? actor!.Center : Main.LocalPlayer.Center;
        int centerX = (int)(center.X / 16f), centerY = (int)(center.Y / 16f);
        system.PreservationEvidence.Begin(Main.worldID, WorldKnowledgeObserver.LoadEpoch, system.GoalStartSequence,
            goal, sourcePlayerRequest, Main.GameUpdateCount, centerX, centerY, useCompanion ? "companion_actual_position" : "player_actual_position",
            () => BindAudit(system, ShelterWorldAudit.Capture(centerX, centerY)));
    }

    internal static bool RestorePreservation(AITeammateSystem system, GoalPreservationIdentity identity, byte[] originalCells,
        string goal, string? sourcePlayerRequest)
    {
        if (identity.sourceGoal != goal || identity.sourcePlayerRequest != sourcePlayerRequest) return false;
        ShelterWorldAudit? audit = ShelterWorldAudit.FromOriginal(identity, originalCells);
        return audit != null && system.PreservationEvidence.RestoreOriginal(identity, Main.worldID,
            WorldKnowledgeObserver.LoadEpoch, system.GoalStartSequence, goal, sourcePlayerRequest, Main.GameUpdateCount, BindAudit(system, audit));
    }

    private static GoalPreservationCapture BindAudit(AITeammateSystem system, ShelterWorldAudit audit)
    {
        ShelterBaselineReport initial = audit.BaselineReport;
        object summary = new
        {
            initial.available, initial.sampleBounds, initial.protectedCells, initial.protectedAirCells,
            initial.protectedDoors, initial.primaryComponentBounds, initial.primaryProtectedCells,
            initial.primaryCoverageComplete, initial.primaryProtectedTouchesBoundary,
            initial.primarySelection, initial.protectedTouchesBoundary,
            edgeContactCount = initial.edgeContacts.Count,
            issues = initial.issues.Take(4).ToArray(), issuesOmitted = Math.Max(0, initial.issues.Count - 4), initial.scope
        };
        return new GoalPreservationCapture(summary, () =>
        {
            ShelterPreservationReport current = audit.CheckPreservation();
            object[] differences = current.differences.Take(8).Select(d => (object)new
            {
                d.x, d.y, d.category, before = PreservationCellSummary(d.before), after = PreservationCellSummary(d.after)
            }).ToArray();
            object observation = new
            {
                current.available, current.worldId, current.epoch, current.observedAtTick,
                current.sampleBounds, current.protectedCells, current.coverageComplete, current.preserved,
                current.diffCount, differences, differencesOmitted = Math.Max(0, current.diffCount - differences.Length),
                current.normalizedDoorCount, current.harmlessAutotileFrameChanges, current.allowedNaturalVegetationChanges,
                issues = current.issues.Take(4).ToArray(), issuesOmitted = Math.Max(0, current.issues.Count - 4), current.scope
            };
            return new GoalPreservationComparison(current.available, current.coverageComplete,
                current.preserved, current.diffCount, observation, current);
        }, initial, condition => ObserveNewHousing(audit, initial, condition, system.CompanionNpc), audit.ReadConstructionProtection)
        { SaveOriginal = audit.SaveOriginal };
    }

    private static GoalNewHousingComparison ObserveNewHousing(ShelterWorldAudit audit,
        ShelterBaselineReport baseline, GoalCondition condition, NPC? actor)
    {
        int x = condition.X!.Value, y = condition.Y!.Value;
        int width = condition.Width ?? 1, height = condition.Height ?? 1;
        if (width > 97 || height > 81 || !WorldGen.InWorld(x, y, 2) ||
            !WorldGen.InWorld(x + width - 1, y + height - 1, 2) || actor is not { active: true })
            return new(false, false, false, new { reason = "Candidate is outside bounded native housing observation or no actor is active." });
        if (Vector2.Distance(actor.Center, new Vector2((x + width / 2f) * 16, (y + height / 2f) * 16)) > 96 * 16)
            return new(false, false, false, new { reason = "New housing is outside the current 96-tile local observation range; approach to verify it." });
        var requested = new ShelterAuditBounds(x, y, width, height);
        bool requestedCovered = baseline.sampleBounds.Contains(requested);
        if (!requestedCovered)
            return new(false, false, false, new { requested, baseline.sampleBounds, reason = "Candidate lies outside the immutable goal baseline; outside cells are unknown." });
        ShelterNewRoomReport room = condition.Width.HasValue
            ? audit.CheckNewRoom(x, y, width, height) : audit.CheckNewRoom(x, y);
        // A measured empty/non-housing location is a negative, not missing evidence.
        // A room extending outside the baseline, however, is only partly observed.
        bool coverage = baseline.primaryCoverageComplete && (room.actualRoomBounds == null || room.roomWithinSample);
        object summary = new
        {
            room.available, coverageComplete = coverage, room.nativeValid, room.validNew,
            room.worldId, room.epoch, room.observedAtTick, room.sampleBounds,
            room.declaredBounds, room.actualRoomBounds, room.roomWithinSample, room.roomWithinDeclaredBounds,
            room.wasExistingValidRoom, room.protectedOverlapCells, room.baselineInteriorEmpty,
            room.baselineRoomCells, room.currentRoomCells, room.newStructuralCells, room.newWallCells, room.newFurnitureCells,
            issues = room.issues.Take(4).ToArray(), issuesOmitted = Math.Max(0, room.issues.Count - 4), room.scope
        };
        return new(room.available, coverage, room.validNew, summary, room);
    }

    private static object PreservationCellSummary(object value)
    {
        JsonElement cell = JsonSerializer.SerializeToElement(value);
        return new
        {
            hasTile = cell.GetProperty("hasTile").GetBoolean(), tileType = cell.GetProperty("tileType").GetInt32(),
            wallType = cell.GetProperty("wallType").GetInt32()
        };
    }

    internal static void RecordVisit(AITeammateSystem system, AIPlan? task, long sequence)
    {
        if (task?.Action != AIActionType.Explore || task.X is not int x || task.Y is not int y ||
            Main.gameMenu || system.CompanionNpc is not { active: true } actor || !WorldGen.InWorld(x, y, 2) ||
            !system.Executor.TryGetExploreVisitPose(task, out TileStandPose stand)) return;
        system.VisitEvidence.Record(WorldKnowledgeObserver.LoadEpoch, system.GoalStartSequence, sequence, task, true,
            new GoalVisitPose(stand.Node.X, stand.Node.Y, stand.Bottom, actor.Center.X, actor.Bottom.Y,
                actor.velocity.Y, TilePathfinder.IsGroundedAt(actor.Center.X, actor.Bottom.Y, actor.velocity.Y, actor.width), stand.Center.X));
    }

    private static GoalNativeConditionResult ReadNative(GoalCondition condition, NPC actor)
    {
        if (!condition.X.HasValue || !condition.Y.HasValue)
            return new(null, "World condition has no bound coordinates; submit a concrete observed site before claiming completion.");
        int x = condition.X.Value, y = condition.Y.Value;
        int width = condition.Width ?? 1, height = condition.Height ?? 1;
        if (width < 1 || height < 1 || width > 97 || height > 81 ||
            !WorldGen.InWorld(x, y, 2) || !WorldGen.InWorld(x + width - 1, y + height - 1, 2))
            return new(null, "Requested condition is outside the bounded world observation.");
        if (Vector2.Distance(actor.Center, new Vector2((x + width / 2f) * 16, (y + height / 2f) * 16)) > 96 * 16)
            return new(null, "Structure condition is outside the current local observation range; approach and verify it.");
        if (condition.Type == "area_matches")
        {
            if (!condition.TileType.HasValue && !condition.WallType.HasValue)
                return new(null, "Area condition has no tile or wall requirement.");
            int matched = 0;
            for (int ty = y; ty < y + height; ty++)
                for (int tx = x; tx < x + width; tx++)
                {
                    Tile tile = Main.tile[tx, ty];
                    bool tileMatch = !condition.TileType.HasValue || (condition.TileType == -1
                        ? !tile.HasTile : tile.HasTile && tile.TileType == condition.TileType.Value);
                    bool wallMatch = !condition.WallType.HasValue || tile.WallType == condition.WallType.Value;
                    if (tileMatch && wallMatch) matched++;
                }
            return new(matched == width * height,
                $"Read back {matched}/{width * height} matching world cells at ({x},{y}) {width}x{height}; tile/wall types are native IDs, not placement items. This proves these cells only, not a complete house or a new build.");
        }

        // Installed StartRoomCheck/CheckRoom/RoomNeeds write these scratch fields and arrays;
        // preserve them so observing a goal does not change another native housing query's state.
        var scratch = HousingScratch.Select(field => (field, value: field.GetValue(null)))
            .Select(pair => (pair.field, original: pair.value,
                saved: pair.value is Array array ? array.Clone() : pair.value)).ToArray();
        try
        {
            int checkX = condition.Width.HasValue ? x + width / 2 : x;
            int checkY = condition.Height.HasValue ? y + height / 2 : y;
            bool valid = WorldGen.StartRoomCheck(checkX, checkY) && WorldGen.RoomNeeds(-1);
            bool insideBounds = !condition.Width.HasValue || !condition.Height.HasValue ||
                WorldGen.roomX1 >= x && WorldGen.roomX2 < x + width &&
                WorldGen.roomY1 >= y && WorldGen.roomY2 < y + height;
            return new(valid && insideBounds,
                $"Native housing check at interior ({checkX},{checkY}): valid={valid}, withinDeclaredBounds={insideBounds}. Existing valid housing also satisfies this predicate; it does not by itself prove a requested new construction or whole-house scope.");
        }
        catch (Exception exception)
        {
            return new(null, "Native housing validation unavailable: " + exception.GetType().Name);
        }
        finally
        {
            foreach (var entry in scratch)
            {
                if (entry.original is Array original && entry.saved is Array saved)
                {
                    Array.Copy(saved, original, saved.Length);
                    if (!ReferenceEquals(entry.field.GetValue(null), original)) entry.field.SetValue(null, original);
                }
                else entry.field.SetValue(null, entry.saved);
            }
        }
    }
}
