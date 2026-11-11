using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace TerrariaAITeammate.Common;

public partial class AIExecutor
{
    private AIActionType action = AIActionType.Follow;
    private uint pausedAtTick;
    private string target = "";
    private string itemQuery = "";
    private string slotQuery = "";
    private int count = 10;
    private string blueprint = "shelter";

    private int jumpCooldown;
    private int stuckTicks;
    private int attackCooldown;
    private int mineCooldown;
    private int mineAttempts;
    private int mineHitsDone;
    private int mineHitsNeeded;
    private int mineRemaining;
    private int mineAtTaskStart;
    private int woodDropWaitTicks;
    private int placeRemaining;
    private int mineTileType;
    private int mineX = -1;
    private int mineY = -1;
    private int mineStuckTicks;
    private readonly HashSet<Point> unreachableMineTiles = new();
    private readonly MiningProgressBudget miningBudget = new();
    private readonly MiningApproachBudget miningApproach = new();
    private int miningObservedQuantity;
    private int miningObservedHits;
    private DirectedMiningSource? miningSource;
    private DirectedMiningSource? miningObservedSource;
    private int miningSourceType = -1;
    private readonly List<Point> miningActualCandidates = new();
    private MiningCandidateSelectionResult? miningAutoSelection;
    private object? miningAutoSelectionReport;
    private double miningAutoSelectionElapsedMs;
    private readonly Dictionary<Point, int> miningChangedTiles = new();
    private readonly HashSet<Point> miningRemovedTiles = new();
    private Vector2? miningStand;
    private object? miningRouteFailure;
    private double miningRouteProbeElapsedMs;
    private readonly MiningWorkStandSelection miningWorkStands = new();
    private readonly List<object> miningStandProbeFailures = new();
    private int buildCooldown;
    private int buildIndex;
    private List<BuildStep>? buildSteps;
    private List<BuildStep>? shelterDraft;
    private string shelterDraftMaterial = "";
    private int buildBlockType = TileID.WoodBlock;
    private int buildWallType = WallID.Wood;
    private int buildMaterialType = ItemID.Wood;
    private AIPlan? blockedPlan;
    private string blockedReason = "";
    private Blueprint? blueprintBuild;
    private int blueprintWallIndex;
    private int blueprintTileIndex;
    private int blueprintPlaced;
    private int blueprintOriginX;
    private int blueprintOriginY;
    private int chestIndex = -1;
    private int lastInspectedChestIndex = -1;
    private int chestTicks;
    private bool waitRequested;
    private int areaIndex;
    private int areaPlaced;
    private int areaSkipped;
    private int areaRetries;
    private readonly Dictionary<Point, uint> blockedWaypoints = new();
    private Recipe? craftRecipe;
    private int craftStationX = -1;
    private int craftStationY = -1;
    private bool weaponWarned;
    private int combatPotionCooldown;
    private int retreatTicks;
    private int weaponSwitchCooldown;
    private int retreatRecoveryTicks;
    private bool autoDefend;
    private int autoDefendQuietTicks;
    private int autoDefendTarget = -1;
    private uint lastPlayerAttackedAt;

    private int detourTicks;
    private float detourDirection;
    private readonly List<(int X, int Y, uint OpenedAt)> openedDoors = new();
    private readonly HashSet<string> discoveries = new();
    private int sessionObservedMinX = int.MaxValue;
    private int sessionObservedMaxX = int.MinValue;
    private int exploreDiscoveries;
    private uint exploreEndTick;
    private uint exploreScanAt;
    private int exploreTargetX;
    private int exploreTargetY;
    private bool exploreHasCoordinates;
    private int exploreStartX;
    private int exploreStartY;
    private bool exploreDestinationChecked;
    private Point? exploreStandDestination;
    private TileStandPose? exploreStandPose;
    private readonly List<Point> exploreScanCenters = new();

    private List<Point>? navPath;
    private int navIndex;
    private Vector2 navGoal;
    private uint navBuiltAt;
    private int pathBlockedTicks;
    private float lastPathPosX;
    private int moveBlockedTicks;
    private float lastMovePosX;
    private int pickupItemIndex = -1;
    private int pickupTicks;
    private int pickupItemType;
    private PickupProgress pickupProgress = new(0);
    private string? attackResolvedQuery;
    private AttackCommandTarget? attackCommandTarget;
    private NPC? attackLastTarget;
    private int bossUntargetableTicks;
    private BossAttackEvidence? localBossAttackEvidence;

    private readonly Queue<AIPlan> pendingPlans = new();
    private AIPlan? currentPlan;
    private AIPlan? suspendedAttack;
    private bool sequenceActive;
    private int sequenceStep;
    private int stepTicks;
    private int followDiagTicks;
    private bool completeStepRequested;
    private string lastStepResult = "";
    private bool blockedThisStep;

    private bool followPaused;
    private int platformDropTicks;
    private float platformDropSourceBottom;
    private float platformDropClearPlane;
    private bool platformDropOwned;
    private Vector2 lastPathPosition;
    private int navigationNoProgressTicks;
    private float navigationBestDistance = float.MaxValue;
    private uint navigationRetryAt;
    private int navigationFailures;
    private readonly HashSet<Point> navigationVisitedNodes = new();
    public bool DropThroughPlatforms => platformDropTicks > 0;
    public int NavigationTeleportCount { get; private set; }
    public int NavigationJumpCount { get; private set; }
    public int NavigationFacingChanges { get; private set; }
    public string NavigationFailureReason { get; private set; } = "";
    public void ResetNavigationTelemetry()
    {
        NavigationTeleportCount = NavigationJumpCount = NavigationFacingChanges = 0;
        NavigationFailureReason = "";
    }

    public CompanionUseAnimation Animation { get; } = new();
    public AIActionType CurrentAction => action;
    public bool IsInCombat => action is AIActionType.Attack or AIActionType.Retreat || autoDefend;
    public int CombatPotionCooldown => combatPotionCooldown;
    public int WeaponSwitchCooldown => weaponSwitchCooldown;
    public bool RecentlyRetreated => retreatRecoveryTicks > 0;
    public bool IsAutoDefending => autoDefend;
    public bool HoldPosition { get; private set; }
    public bool HasCurrentPlan => currentPlan != null;
    public ExecutionPolicy Policy => new(ModContent.GetInstance<AIConfig>().AllowTeleportRecovery,
        ModContent.GetInstance<AIConfig>().AllowBlueprintWorldEditing);

    /// <summary>Bounded physical dependencies, not a claim that the whole world is unchanged.</summary>
    public string CaptureRetryConditions(AIPlan task)
    {
        var signature = new StringBuilder();
        NPC? actor = AITeammateSystem.Instance.CompanionNpc;
        signature.Append(Main.worldID).Append('|').Append(Policy).Append('|');
        if (actor == null) return signature.Append("no_actor").ToString();
        int actorX = (int)MathF.Floor(actor.Center.X / 16f), actorY = (int)MathF.Floor(actor.Bottom.Y / 16f);
        signature.Append(actorX).Append(',').Append(actorY).Append('|');
        signature.Append("weapon=").Append(Loadout.PreferredWeaponType).Append('|');
        foreach (Item item in Loadout.Inventory.Concat(Loadout.Armor).Concat(Loadout.Accessories))
            signature.Append(item.type).Append(':').Append(item.stack).Append(':').Append(item.prefix)
                .Append(':').Append(item.pick).Append(':').Append(item.axe).Append(';');
        if (task.Action is AIActionType.Come or AIActionType.Follow)
            signature.Append("player=").Append((int)(Main.LocalPlayer.Center.X / 16f)).Append(',')
                .Append((int)(Main.LocalPlayer.Bottom.Y / 16f));
        HashSet<int> stations = task.Action == AIActionType.Craft
            ? CompanionCrafting.FindRecipes(task.Item).SelectMany(r => r.requiredTile).Where(t => t >= 0).ToHashSet()
            : new();
        if (task.Action == AIActionType.Craft)
        {
            Player p = Main.LocalPlayer;
            signature.Append("environment=").Append(Main.dayTime).Append('/').Append(Main.moonPhase)
                .Append('/').Append(Main.raining).Append('/').Append(Main.bloodMoon).Append('/').Append(Main.hardMode)
                .Append('/').Append(p.ZoneSnow).Append('/').Append(p.ZoneDesert).Append('/').Append(p.ZoneJungle)
                .Append('/').Append(p.ZoneCorrupt).Append('/').Append(p.ZoneCrimson).Append('/').Append(p.ZoneHallow)
                .Append('/').Append(p.ZoneDungeon).Append('/').Append(p.ZoneRockLayerHeight).Append('|');
        }
        void Region(int cx, int cy, int radius)
        {
            for (int y = Math.Max(1, cy - radius); y <= Math.Min(Main.maxTilesY - 2, cy + radius); y++)
                for (int x = Math.Max(1, cx - radius); x <= Math.Min(Main.maxTilesX - 2, cx + radius); x++)
                {
                    Tile t = Main.tile[x, y];
                    bool physical = t.HasTile && (Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType] ||
                        TileID.Sets.Platforms[t.TileType] || t.TileType is TileID.ClosedDoor or TileID.OpenDoor ||
                        stations.Contains(t.TileType) || task.Action == AIActionType.Mine &&
                        (t.TileType is TileID.Trees or TileID.PalmTree || Main.tileFrameImportant[t.TileType]));
                    if (!physical && t.LiquidAmount == 0 && !(task.Action == AIActionType.Mine && t.WallType > WallID.None)) continue;
                    signature.Append(x).Append(',').Append(y).Append('=');
                    if (physical) signature.Append(t.TileType).Append('/').Append(t.IsActuated ? 1 : 0)
                        .Append('/').Append(t.IsHalfBlock ? 1 : 0).Append('/').Append((int)t.Slope);
                    if (t.LiquidAmount > 0) signature.Append('L').Append(t.LiquidType).Append(':').Append(t.LiquidAmount);
                    if (task.Action == AIActionType.Mine) signature.Append('W').Append(t.WallType);
                    signature.Append(';');
                }
        }
        Region(actorX, actorY, 20);
        if (task.X.HasValue && task.Y.HasValue) Region(task.X.Value, task.Y.Value, 8);
        if (task.Action is AIActionType.Withdraw or AIActionType.Deposit ||
            task.Action == AIActionType.Inspect && task.Target.Equals("chest", StringComparison.OrdinalIgnoreCase))
            foreach (Chest? chest in Main.chest)
                if (chest != null && Vector2.Distance(CompanionChests.Center(chest), actor.Center) <= 60 * 16)
                {
                    signature.Append("chest=").Append(chest.x).Append(',').Append(chest.y).Append(':');
                    foreach (Item item in chest.item) signature.Append(item.type).Append('/').Append(item.stack).Append(';');
                }
        if (task.Action == AIActionType.Pickup)
            foreach (Item item in Main.item)
                if (item.active && !item.IsAir && Vector2.Distance(item.Center, actor.Center) <= 60 * 16)
                    signature.Append("drop=").Append(item.type).Append('/').Append(item.stack).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }
    public string LastStepResult => lastStepResult;
    public bool PlayerRecentlyAttacked =>
        lastPlayerAttackedAt != 0 && Main.GameUpdateCount - lastPlayerAttackedAt < 300;
    public AIPlan? BlockedPlan => blockedPlan;
    public string BlockedReason => blockedReason;
    public object? ConstructionProgress => shelterDraft == null ? null : new
    {
        material = shelterDraftMaterial,
        left = shelterDraft.Min(step => step.X), top = shelterDraft.Min(step => step.Y),
        width = CompanionConstruction.Width, height = CompanionConstruction.Height,
        placed = shelterDraft.Count(ConstructionStepPresent), total = shelterDraft.Count,
        remainingMaterials = shelterDraft.Where(step => !ConstructionStepPresent(step))
            .GroupBy(step => step.ItemType).Select(group => new { id = group.Key,
                name = Lang.GetItemNameValue(group.Key), required = group.Count(),
                owned = Loadout.CountItem(group.Key) }).ToArray()
    };
    public int LastBlueprintOriginX { get; private set; }
    public int LastBlueprintOriginY { get; private set; }

    public Item? VisualItem => Animation.Active && Animation.Item is { IsAir: false }
        ? Animation.Item
        : action == AIActionType.Mine
            ? target == "wood"
                ? Loadout.Inventory.FirstOrDefault(item => !item.IsAir && item.axe > 0)
                : Loadout.GetBestPickaxe()
            : Loadout.GetBestWeapon();

    public bool HasCompanion => AITeammateSystem.Instance.CompanionNpc is { active: true };
    public int CompanionHp => AITeammateSystem.Instance.CompanionNpc?.life ?? 0;
    public bool IsBusy => action is AIActionType.Attack or AIActionType.Mine or AIActionType.Build
                          or AIActionType.Come or AIActionType.Explore or AIActionType.Pickup
                          or AIActionType.Deposit or AIActionType.Withdraw or AIActionType.Craft
                          or AIActionType.Retreat or AIActionType.Inspect or AIActionType.Place
                          || sequenceActive || autoDefend;

    public void Apply(AIPlan plan) => ApplySequence(new[] { plan });

    public void ApplySequence(IReadOnlyList<AIPlan> plans)
    {
        if (plans.Count == 1 && plans[0].Action == AIActionType.Stop)
        {
            SetHold(true);
            return;
        }
        pendingPlans.Clear();
        sequenceActive = false;
        sequenceStep = 0;

        if (plans.Count == 0)
        {
            action = AIActionType.Wait;
            return;
        }

        for (int i = 1; i < plans.Count; i++)
            pendingPlans.Enqueue(plans[i]);

        sequenceActive = plans.Count > 1;
        sequenceStep = sequenceActive ? 1 : 0;
        ApplyOne(plans[0]);
    }

    private void ApplyOne(AIPlan plan, bool admissionChecked = false)
    {
        if (plan.Action == AIActionType.Stop)
        {
            SetHold(true);
            return;
        }

        if (!admissionChecked && !TryAdmitTaskStart(plan)) return;

        if (plan.Action != AIActionType.Inspect) ClearBossReview();
        suspendedAttack = null;
        HoldPosition = false;
        pausedAtTick = 0;
        autoDefend = false;
        autoDefendTarget = -1;
        autoDefendQuietTicks = 0;
        lastStepResult = "";
        blockedThisStep = false;
        currentPlan = plan;
        action = plan.Action;
        waitRequested = plan.Action == AIActionType.Wait;
        areaIndex = areaPlaced = areaSkipped = areaRetries = 0;
        target = (plan.Target ?? "").Trim().ToLowerInvariant();
        itemQuery = (plan.Item ?? "").Trim();
        slotQuery = (plan.Slot ?? "").Trim().ToLowerInvariant();
        count = plan.Count > 0
            ? Math.Clamp(plan.Count, 0, 999)
            : plan.Action is AIActionType.Mine or AIActionType.Drop ? 10
            : plan.Action is AIActionType.Deposit or AIActionType.Withdraw ? 0
            : 1;
        blueprint = string.IsNullOrWhiteSpace(plan.Blueprint) ? "shelter" : plan.Blueprint.Trim().ToLowerInvariant();

        jumpCooldown = 0;
        stuckTicks = 0;
        attackCooldown = 0;
        mineCooldown = 0;
        mineAttempts = 0;
        mineHitsDone = 0;
        mineHitsNeeded = 0;
        woodDropWaitTicks = 0;
        mineX = -1;
        mineY = -1;
        mineStuckTicks = 0;
        unreachableMineTiles.Clear();
        miningBudget.Reset();
        miningApproach.Reset();
        miningObservedQuantity = miningObservedHits = 0;
        miningWoodYield = null;
        miningWoodFamilyDefinition = "not_captured";
        miningSource = miningObservedSource = null;
        miningSourceType = -1;
        miningActualCandidates.Clear();
        miningAutoSelection = null;
        miningAutoSelectionReport = null;
        miningAutoSelectionElapsedMs = 0;
        miningChangedTiles.Clear();
        miningRemovedTiles.Clear();
        miningStand = null;
        miningRouteFailure = null;
        miningRouteProbeElapsedMs = 0;
        miningWorkStands.Reset();
        miningStandProbeFailures.Clear();
        ResetMiningReturn();
        blockedWaypoints.Clear();
        chestIndex = -1;
        chestTicks = 0;
        craftRecipe = null;
        craftStationX = -1;
        craftStationY = -1;
        weaponWarned = false;
        retreatTicks = 0;
        buildCooldown = 0;
        buildIndex = 0;
        buildSteps = null;
        blueprintBuild = null;
        blockedPlan = null;
        blockedReason = "";
        blueprintWallIndex = 0;
        blueprintTileIndex = 0;
        blueprintPlaced = 0;
        blueprintOriginX = 0;
        blueprintOriginY = 0;
        detourTicks = 0;
        detourDirection = 0;
        exploreTargetX = 0;
        exploreTargetY = 0;
        exploreHasCoordinates = false;
        exploreStartX = 0;
        exploreStartY = 0;
        exploreDestinationChecked = false;
        exploreStandDestination = null;
        exploreStandPose = null;
        exploreScanCenters.Clear();
        exploreEndTick = 0;
        exploreScanAt = 0;
        exploreDiscoveries = 0;
        InvalidateRoutePath();
        navIndex = 0;
        navGoal = default;
        navBuiltAt = 0;
        pathBlockedTicks = 0;
        lastPathPosX = 0f;
        moveBlockedTicks = 0;
        lastMovePosX = 0f;
        pickupItemIndex = -1;
        pickupTicks = 0;
        pickupItemType = 0;
        pickupProgress = new(plan.Count > 0 ? count : 0);
        attackResolvedQuery = null;
        attackCommandTarget = null;
        attackLastTarget = null;
        bossUntargetableTicks = 0;
        localBossAttackEvidence = action == AIActionType.Attack ? new(AITeammateSystem.Instance.BossEvidence.DeathSequence) : null;
        completeStepRequested = false;
        stepTicks = 0;
        placeRemaining = count;
        followDiagTicks = 0;
        followPaused = false;
        platformDropTicks = 0;
        navigationNoProgressTicks = navigationFailures = 0;
        navigationVisitedNodes.Clear();
        navigationBestDistance = float.MaxValue;
        navigationRetryAt = 0;
        NavigationFailureReason = "";
        ResetRouteNavigation();
        CancelRouteInspection();
        CancelMiningSourceInspection();
        travelTickBudget = 180 * 60;

        if (action == AIActionType.Come && AITeammateSystem.Instance.CompanionNpc is { } comingNpc)
            travelTickBudget = NavigationTravelBudget.TicksForDistance(Vector2.Distance(comingNpc.Center, Main.LocalPlayer.Center));

        if (action == AIActionType.Retreat)
        {
            retreatTicks = 0;
            retreatRecoveryTicks = 0;
        }
        else if (action == AIActionType.Attack)
        {
            retreatRecoveryTicks = 0;
        }
        else if (action == AIActionType.Mine)
        {
            mineTileType = TileFor(target);
            mineRemaining = count;
            mineAtTaskStart = Loadout.CountItem(target == "wood" ? ItemID.Wood : ItemForTile(mineTileType));
            if (target == "wood") BeginWoodYield();
            miningObservedQuantity = 0;
            if (!IsDirectedMining && AITeammateSystem.Instance.CompanionNpc is { active: true } miningNpc)
                EnsureMiningReturnAnchor(miningNpc);
        }
        else if (action == AIActionType.Explore)
        {
            Player player = Main.player[Main.myPlayer];
            Vector2 origin = AITeammateSystem.Instance.CompanionNpc?.Center ?? player.Center;
            int px = (int)(origin.X / 16f);
            int py = (int)(origin.Y / 16f);
            int playerX = (int)(player.Center.X / 16f);
            sessionObservedMinX = Math.Min(sessionObservedMinX, Math.Min(px, playerX) - 28);
            sessionObservedMaxX = Math.Max(sessionObservedMaxX, Math.Max(px, playerX) + 28);
            int dir = target is "left" or "左" ? -1 : target is "right" or "右" ? 1 : player.direction == 0 ? 1 : player.direction;
            int frontierCenterX = dir < 0
                ? Math.Min(px, sessionObservedMinX + 28) - 40
                : Math.Max(px, sessionObservedMaxX - 28) + 40;
            int defaultTargetX = dir < 0
                ? Math.Max(px - 130, frontierCenterX)
                : Math.Min(px + 130, frontierCenterX);
            exploreStartX = px;
            exploreStartY = py;
            exploreTargetX = plan.X ?? Math.Clamp(defaultTargetX, 20, Main.maxTilesX - 20);
            exploreTargetY = plan.Y ?? Math.Clamp(py, 20, Main.maxTilesY - 20);
            exploreHasCoordinates = plan.X.HasValue || plan.Y.HasValue;
            travelTickBudget = exploreHasCoordinates
                ? NavigationTravelBudget.TicksForDistance(Vector2.Distance(origin, new Vector2(exploreTargetX * 16f + 8, exploreTargetY * 16f + 8)))
                : 180 * 60;
            exploreEndTick = Main.GameUpdateCount + (uint)(exploreHasCoordinates ? travelTickBudget : 20 * 60);
            AITeammateSystem.Instance.Memory.Remember($"探索：我从 ({px},{py}) 开始往{(dir > 0 ? "右" : "左")}边探索");
            AITeammateSystem.Instance.TestLog($"explore start from {px},{py} toward {exploreTargetX},{exploreTargetY}");
        }
    }

    private bool TryAdmitTaskStart(AIPlan plan)
    {
        TaskRetryDecision admission = AITeammateSystem.Instance.CheckTaskStart(plan);
        if (admission.allowed) return true;
        RejectTaskStart(plan, admission.code, $"已有{admission.actualFailures}次同条件的真实失败，尚未再次执行。" + admission.note);
        return false;
    }

    private void RejectTaskStart(AIPlan plan, string code, string reason)
    {
        // The action never began. A dispatch rejection must not manufacture
        // a physical failure or charge the same failed attempt twice.
        Clear();
        action = AIActionType.Wait;
        waitRequested = true;
        blockedThisStep = false;
        lastStepResult = reason;
        AITeammateSystem.Instance.NotifyTaskStartRejected(plan, code, reason);
    }

    public void ResetExplorationMemory()
    {
        discoveries.Clear();
        exploreScanCenters.Clear();
        sessionObservedMinX = int.MaxValue;
        sessionObservedMaxX = int.MinValue;
    }

    public void Clear(bool hold = false, bool resetExploration = true)
    {
        ClearBossReview();
        pausedAtTick = 0;
        platformDropTicks = 0;
        if (hold && resetExploration)
            ResetExplorationMemory();
        action = hold ? AIActionType.Wait : AIActionType.Follow;
        waitRequested = false;
        mineRemaining = 0;
        mineX = -1;
        mineY = -1;
        ResetMiningReturn();
        miningWoodYield = null;
        miningWoodFamilyDefinition = "not_captured";
        mineStuckTicks = 0;
        unreachableMineTiles.Clear();
        blockedWaypoints.Clear();
        buildSteps = null;
        shelterDraft = null;
        shelterDraftMaterial = "";
        blueprintBuild = null;
        blockedPlan = null;
        blockedReason = "";
        InvalidateRoutePath();
        pendingPlans.Clear();
        sequenceActive = false;
        sequenceStep = 0;
        completeStepRequested = false;
        stepTicks = 0;
        currentPlan = null;
        suspendedAttack = null;
        pickupItemIndex = -1;
        pickupItemType = 0;
        pickupProgress = new(0);
        attackResolvedQuery = null;
        attackCommandTarget = null;
        attackLastTarget = null;
        bossUntargetableTicks = 0;
        localBossAttackEvidence = null;
        ResetRouteNavigation();
        CancelRouteInspection();
        CancelMiningSourceInspection();
        HoldPosition = hold;
        if (hold)
        {
            Animation.Release();
            if (AITeammateSystem.Instance.CompanionNpc is { active: true } heldNpc)
                heldNpc.velocity.X = 0f;
        }
        autoDefend = false;
        autoDefendTarget = -1;
        autoDefendQuietTicks = 0;
    }

    internal void ClearForGoalReview()
    {
        Clear(HoldPosition, resetExploration: false);
        // An unfinished model goal with no tasks is waiting for another decision,
        // not an instruction to start following the observer.
        action = AIActionType.Wait;
    }

    public void ForgetBlockedGoal()
    {
        blockedPlan = null;
        blockedReason = "";
        blockedThisStep = false;
    }

    public void SetHold(bool hold)
    {
        if (hold)
        {
            ClearBossReview();
            Animation.Release();
            if (!HoldPosition) pausedAtTick = Main.GameUpdateCount;
            if (currentPlan == null) action = AIActionType.Wait;
            HoldPosition = true;
            autoDefend = false;
            autoDefendTarget = -1;
            autoDefendQuietTicks = 0;
            InvalidateRoutePath();
            CancelRouteSearch();
            CancelRouteInspection();
            CancelMiningSourceInspection();
            if (AITeammateSystem.Instance.CompanionNpc is { active: true } heldNpc)
                heldNpc.velocity.X = 0;
            AITeammateSystem.Instance.TestLog("hold position: auto defend suppressed");
            return;
        }

        if (HoldPosition)
        {
            if (exploreEndTick != 0) exploreEndTick += Main.GameUpdateCount - pausedAtTick;
            pausedAtTick = 0;
            InvalidateRoutePath();
            navBuiltAt = 0;
            navigationRetryAt = 0;
            navigationNoProgressTicks = 0;
        }
        HoldPosition = false;
        autoDefend = false;
        autoDefendTarget = -1;
        autoDefendQuietTicks = 0;
    }

    public void NotifyPlayerAttacked(int attackerIndex)
    {
        if (HoldPosition)
            return;

        if (AITeammateSystem.Instance.CompanionNpc is not { active: true })
            return;

        Player player = Main.player[Main.myPlayer];
        if (!player.active || player.dead)
            return;

        lastPlayerAttackedAt = Main.GameUpdateCount;

        if (!MayAutoDefendCurrentAction())
            return;

        int target = attackerIndex;
        if (target < 0 || target >= Main.maxNPCs)
        {
            NPC? nearest = FindNearestEnemyNear(player.Center, 55f * 16f);
            target = nearest?.whoAmI ?? -1;
        }

        if (target < 0)
            return;

        if (!autoDefend)
        {
            InvalidateRoutePath(); navHasTarget = false; routeSnapshot = null;
            navigationRetryAt = 0; navigationNoProgressTicks = 0;
            navigationBestDistance = float.MaxValue;
        }
        autoDefend = true;
        autoDefendQuietTicks = 0;
        autoDefendTarget = target;
        AITeammateSystem.Instance.TestLog(
            $"auto defend start attacker={target} action={action} playerHp={player.statLife}");
    }

    private void EndAutoDefend(string reason)
    {
        if (!autoDefend)
            return;

        autoDefend = false;
        autoDefendTarget = -1;
        autoDefendQuietTicks = 0;
        InvalidateRoutePath(); navHasTarget = false; routeSnapshot = null;
        navigationRetryAt = 0; navigationNoProgressTicks = 0;
        navigationBestDistance = float.MaxValue;
        AITeammateSystem.Instance.TestLog($"auto defend end: {reason}");
    }

    private bool MayAutoDefendCurrentAction() => BasicCommandRules.MayAutoDefend(HoldPosition, action,
        currentPlan != null, pendingPlans.Count > 0, !string.IsNullOrWhiteSpace(AITeammateSystem.Instance.ActiveGoal));

    public string StatusText()
    {
        string status = action switch
        {
            AIActionType.Follow => "跟随",
            AIActionType.Come => "赶来",
            AIActionType.Attack => "战斗",
            AIActionType.Mine => $"挖矿 {mineRemaining}/{count}",
            AIActionType.Build => blueprintBuild != null
                ? $"复刻 {blueprintBuild.Name} {blueprintTileIndex + blueprintWallIndex}/{blueprintBuild.Tiles.Count + blueprintBuild.Walls.Count}"
                : $"建造 {buildIndex}/{buildSteps?.Count ?? 0}",
            AIActionType.Explore => "探索",
            AIActionType.Pickup => "捡东西",
            AIActionType.Drop => "丢东西",
            AIActionType.Use => "用道具",
            AIActionType.Equip => "换装备",
            AIActionType.Deposit => "存箱子",
            AIActionType.Withdraw => "从箱子取",
            AIActionType.Craft => $"合成 {itemQuery}",
            AIActionType.Inspect => $"查询 {target} {itemQuery}",
            AIActionType.Place => $"放置 {itemQuery}",
            AIActionType.Retreat => "撤退",
            AIActionType.Wait => "待命",
            _ => "待命"
        };

        if (sequenceActive)
            status += $"（第 {sequenceStep} 步，剩 {pendingPlans.Count} 步）";
        if (HoldPosition)
            status += "（按住，不自动护主）";
        else if (autoDefend)
            status += "（护主中）";
        else if (action == AIActionType.Wait && bossReview != null)
            status += "（Boss战复盘中，近身避险）";

        return status;
    }

    public string InventorySummary()
    {
        return AITeammateSystem.Instance.Loadout.Summary();
    }

    public void GiveInventory(Player player)
    {
        if (AITeammateSystem.Instance.Loadout.Inventory.All(item => item.IsAir))
        {
            Main.NewText("小星：背包是空的。", Color.LightBlue);
            return;
        }

        AITeammateSystem.Instance.Loadout.GiveAll(player);
        Main.NewText("小星：都给你了。", Color.LightGreen);
    }

    public void Tick(NPC npc)
    {
        if (!npc.active)
            return;

        Player player = Main.player[Main.myPlayer];
        if (!player.active || player.dead)
            return;

        if (HoldPosition)
        {
            Animation.Tick();
            UpdatePlatformDrop(npc);
            npc.velocity.X = 0;
            npc.velocity.Y = Math.Min(CompanionMovement.MaximumFallVelocity, npc.velocity.Y + CompanionMovement.Gravity);
            return;
        }

        if (completeStepRequested)
        {
            completeStepRequested = false;
            bool blocked = blockedThisStep;
            blockedThisStep = false;
            CompleteStep(blocked);
            return;
        }

        UpdatePlatformDrop(npc);
        stepTicks++;
        if (currentPlan != null && !autoDefend && stepTicks > travelTickBudget &&
            action is not (AIActionType.Follow or AIActionType.Wait or AIActionType.Attack or AIActionType.Retreat))
        {
            FinishCurrentStep("任务执行超时，当前路径或条件无法完成，需要重新规划。", false);
            npc.velocity.X *= 0.7f;
            return;
        }
        Animation.Tick();
        Loadout.Mana = Math.Min(Loadout.ManaMax, Loadout.Mana + 1);
        if (combatPotionCooldown > 0)
            combatPotionCooldown--;
        if (weaponSwitchCooldown > 0)
            weaponSwitchCooldown--;
        if (retreatRecoveryTicks > 0)
            retreatRecoveryTicks--;

        npc.velocity.Y = Math.Min(CompanionMovement.MaximumFallVelocity, npc.velocity.Y + CompanionMovement.Gravity);

        Vector2? navTarget = null;

        if (autoDefend && !MayAutoDefendCurrentAction())
            EndAutoDefend("explicit command or goal takes priority");

        NPC? autoThreat = autoDefend
            ? FindAutoDefendEnemy(npc, player)
            : null;

        if (autoThreat != null)
        {
            navTarget = AttackEnemy(npc, player, autoThreat, true);
        }
        else
        {
            if (autoDefend)
            {
                autoDefendQuietTicks++;
                if (autoDefendQuietTicks > 120)
                    EndAutoDefend("no threat");
            }

            switch (action)
            {
            case AIActionType.Follow:
                navTarget = Follow(npc, player);
                break;
            case AIActionType.Come:
            {
                var system = AITeammateSystem.Instance;
                navTarget = player.Center;
                if (Math.Abs(npc.Center.X - player.Center.X) <= 48f && Math.Abs(npc.Center.Y - player.Center.Y) <= 24f &&
                    IsGrounded(npc) && Math.Abs(npc.velocity.Y) <= 0.6f && system.CanFinishCome(npc, player))
                {
                    system.RecordOrderedReturn();
                    FinishCurrentStep("已经步行到你身边了。");
                }
                else
                    Navigate(npc, player.Center, 4.4f);
                break;
            }
            case AIActionType.Attack:
                navTarget = Attack(npc, player);
                break;
            case AIActionType.Mine:
                navTarget = Mine(npc, player);
                break;
            case AIActionType.Build:
                navTarget = Build(npc, player);
                break;
            case AIActionType.Explore:
                navTarget = Explore(npc, player);
                break;
            case AIActionType.Pickup:
                navTarget = Pickup(npc, player);
                break;
            case AIActionType.Drop:
                navTarget = DropItem(npc, player);
                break;
            case AIActionType.Use:
                navTarget = UseItem(npc, player);
                break;
            case AIActionType.Equip:
                navTarget = EquipItem(npc, player);
                break;
            case AIActionType.Deposit:
                navTarget = Deposit(npc, player);
                break;
            case AIActionType.Withdraw:
                navTarget = Withdraw(npc, player);
                break;
            case AIActionType.Craft:
                navTarget = Craft(npc, player);
                break;
            case AIActionType.Inspect:
                if (target == "sources")
                {
                    TickMiningSources(npc);
                    break;
                }
                if (target == "route")
                {
                    TickRouteInspection(npc, player);
                    break;
                }
                if (target == "chest")
                {
                    navTarget = InspectChest(npc, player);
                    break;
                }
                string query = target == "recipe" && count > 1 && !itemQuery.Contains('×') &&
                    !System.Text.RegularExpressions.Regex.IsMatch(itemQuery, @"\s+x\s*\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    ? $"{itemQuery} x{count}" : itemQuery;
                FinishCurrentStep(AgentKnowledge.Query(query, target, npc, player, this, currentPlan?.X, currentPlan?.Y, currentPlan?.Design));
                break;
            case AIActionType.Place:
                navTarget = PlaceItem(npc, player);
                break;
            case AIActionType.Retreat:
                navTarget = Retreat(npc, player);
                break;
            case AIActionType.Wait:
                if (currentPlan == null && !waitRequested && (TickBossReview(npc) || TickPlanningDefense(npc))) break;
                if (CompanionMovement.ShouldRunIdleFollow(HoldPosition, currentPlan != null, waitRequested,
                    !string.IsNullOrWhiteSpace(AITeammateSystem.Instance.ActiveGoal), pendingPlans.Count > 0))
                {
                    navTarget = Follow(npc, player);
                    break;
                }
                npc.velocity.X = 0f;
                if ((sequenceActive && pendingPlans.Count > 0 || currentPlan != null &&
                     !string.IsNullOrWhiteSpace(AITeammateSystem.Instance.ActiveGoal)) && stepTicks > 120)
                    FinishCurrentStep("等了一会。");
                break;
            }
        }

        if (action == AIActionType.Follow &&
            Environment.GetEnvironmentVariable("AITEAMMATE_AUTOTEST") is { Length: > 0 } &&
            ++followDiagTicks >= 30)
        {
            followDiagTicks = 0;
            LogFollowState(npc, player, navTarget);
        }

        CloseOpenedDoors(npc);

        if (completeStepRequested)
        {
            completeStepRequested = false;
            bool blocked = blockedThisStep;
            blockedThisStep = false;
            CompleteStep(blocked);
            return;
        }

        if (jumpCooldown > 0)
            jumpCooldown--;

        CheckNavigationProgress(npc, navTarget);
    }

    public void ApplyTasks(IReadOnlyList<AIPlan> tasks)
    {
        if (tasks.Count == 0)
        {
            Clear();
            return;
        }

        if (currentPlan != null && action != AIActionType.Wait && SameTask(currentPlan, tasks[0]))
        {
            // Continue this dispatch with its original purpose binding and progress.
            // Revised metadata applies to newly dispatched actions in the replacement tail.
            pendingPlans.Clear();
            for (int i = 1; i < tasks.Count; i++)
                pendingPlans.Enqueue(tasks[i]);

            sequenceActive = tasks.Count > 1;
            sequenceStep = sequenceActive ? 1 : 0;
            return;
        }

        ApplySequence(tasks);
    }

    public List<AIPlan> SnapshotTasks()
    {
        var tasks = new List<AIPlan>();
        if (currentPlan != null)
            tasks.Add(currentPlan);
        tasks.AddRange(pendingPlans);
        return tasks;
    }

    public string TaskSummary()
    {
        List<AIPlan> tasks = SnapshotTasks();
        if (tasks.Count == 0)
            return "无任务";

        return string.Join(" | ", tasks.Select((task, index) =>
            (index == 0 ? "▶ " : "· ") + DescribeTask(task)));
    }

    private static string DescribeTask(AIPlan task)
    {
        return task.Action switch
        {
            AIActionType.Follow => "跟随",
            AIActionType.Come => "过来",
            AIActionType.Wait => "等待",
            AIActionType.Stop => "停止",
            AIActionType.Attack => "战斗",
            AIActionType.Mine => $"挖矿 {task.Target} x{NormalizeCount(task)}",
            AIActionType.Build => string.IsNullOrWhiteSpace(task.Blueprint) || task.Blueprint == "shelter"
                ? "建造小屋"
                : $"复刻蓝图 {task.Blueprint}",
            AIActionType.Explore => "探索",
            AIActionType.Pickup => $"捡 {task.Item}",
            AIActionType.Drop => $"丢 {task.Item} x{NormalizeCount(task)}",
            AIActionType.Use => $"使用 {task.Item}",
            AIActionType.Equip => $"装备 {task.Item}",
            AIActionType.Deposit => $"存 {task.Item}",
            AIActionType.Withdraw => $"取 {task.Item}",
            AIActionType.Craft => $"合成 {task.Item} x{NormalizeCount(task)}",
            AIActionType.Inspect => $"查询 {task.Target} {task.Item}",
            AIActionType.Place => $"放置 {task.Item} x{NormalizeCount(task)}",
            AIActionType.Retreat => "撤退",
            _ => "无"
        };
    }

    private static bool SameTask(AIPlan a, AIPlan b)
    {
        return PlanTaskIdentity.AreEquivalent(a, b);
    }

    private static int NormalizeCount(AIPlan task)
    {
        if (task.Count > 0)
            return task.Count;

        return task.Action is AIActionType.Mine or AIActionType.Drop ? 10
            : task.Action is AIActionType.Deposit or AIActionType.Withdraw ? 0
            : 1;
    }

    private void CompleteStep(bool blocked)
    {
        string completed = lastStepResult;
        AIPlan? completedPlan = currentPlan;
        bool finished = blocked || pendingPlans.Count == 0;
        bool review = AITeammateSystem.Instance.NotifyTaskResult(completedPlan, completed, blocked, finished);
        currentPlan = null;
        InvalidateRoutePath();
        CancelRouteSearch();
        CancelRouteInspection();
        CancelMiningSourceInspection();
        if (blocked)
            pendingPlans.Clear();
        if (blocked || review)
        {
            sequenceActive = false;
            sequenceStep = 0;
            action = AIActionType.Wait;
            return;
        }
        if (pendingPlans.Count > 0)
        {
            AIPlan next = pendingPlans.Dequeue();
            sequenceStep++;
            ApplyOne(next);
            return;
        }
        sequenceActive = false;
        sequenceStep = 0;
        action = AIActionType.Follow;
    }

    private void FinishCurrentStep(string text, bool success = true)
    {
        if (!success)
            BlockGoal(text);
        lastStepResult = text;
        FinishTask(text);
        completeStepRequested = true;
    }

    private Vector2 Follow(NPC npc, Player player)
    {
        float dx = player.Center.X - npc.Center.X;
        float dy = player.Center.Y - npc.Center.Y;
        // Nearness alone does not mean the owner is accessible through a wall or
        // closed door. Keep native body line of sight separate from route arrival.
        bool directPassage = Collision.CanHitLine(npc.position, npc.width, npc.height,
            player.position, player.width, player.height);
        followPaused = CompanionMovement.ShouldPauseFollow(followPaused, dx, dy, directPassage);
        if (followPaused)
        {
            npc.velocity.X = 0f;
            InvalidateRoutePath();
            navigationNoProgressTicks = 0;
            if ((sequenceActive && pendingPlans.Count > 0 || currentPlan != null &&
                 !string.IsNullOrWhiteSpace(AITeammateSystem.Instance.ActiveGoal)))
                FinishCurrentStep("已经走到你附近，停下等你。 ");
            return npc.Center;
        }
        // Facing is a visual property of the player, not a new movement destination.
        float speed = Math.Abs(dx) > 15f * 16f ? 5.2f : 4.4f;
        Navigate(npc, player.Center, speed);
        return player.Center;
    }

    private Vector2 Attack(NPC npc, Player player)
    {
        if (attackResolvedQuery != target || attackCommandTarget == null)
        {
            attackResolvedQuery = target;
            attackLastTarget = null;
            bossUntargetableTicks = 0;
            attackCommandTarget = BasicCommandRules.ResolveAttackTarget(target, NPCLoader.NPCCount, ResolveAttackTargetName);
        }
        if (!attackCommandTarget.Valid)
        {
            FinishCurrentStep($"无法识别攻击目标 {target}；请指定 nearest、真实NPC类型ID或完整名称，没有改打其它敌人。", false);
            return npc.Center;
        }
        int? requestedType = attackCommandTarget.NpcType;
        bool specificBoss = requestedType.HasValue && ContentSamples.NpcsByNetId.TryGetValue(requestedType.Value, out NPC? sample) && sample != null && sample.boss;
        bool goalScoped = !string.IsNullOrWhiteSpace(AITeammateSystem.Instance.ActiveGoal);
        long? fixedLocalBoss = specificBoss && !goalScoped ? localBossAttackEvidence?.BoundSpawnId : null;
        float pursuitRange = (specificBoss ? 160f : 45f) * 16f;
        bool WithinPursuitRange(NPC n) => Vector2.DistanceSquared(n.Center, npc.Center) <= pursuitRange * pursuitRange;
        bool MatchesRequestedEnemy(NPC n) => n.active && n.type == requestedType && !n.friendly && n.life > 0 &&
            (!fixedLocalBoss.HasValue || n.GetGlobalNPC<BossWitness>().SpawnId == fixedLocalBoss.Value);
        NPC? enemy = requestedType.HasValue ? Main.npc.Where(n => MatchesRequestedEnemy(n) && WithinPursuitRange(n) && !n.dontTakeDamage && n.CanBeChasedBy())
            .OrderBy(n => Vector2.DistanceSquared(n.Center, npc.Center)).FirstOrDefault() : FindNearestEnemy(npc, 45f * 16f);
        if (enemy == null)
        {
            if (specificBoss)
            {
                int bossType = requestedType!.Value;
                bool killed = goalScoped ? AITeammateSystem.Instance.BossDefeatedForCurrentGoal(bossType)
                    : localBossAttackEvidence?.IsProven(AITeammateSystem.Instance.BossEvidence, bossType) == true;
                NPC? activeBoss = killed ? null : Main.npc.Where(MatchesRequestedEnemy)
                    .MinBy(n => Vector2.DistanceSquared(n.Center, npc.Center));
                if (activeBoss != null)
                {
                    bool inRange = WithinPursuitRange(activeBoss);
                    if (++bossUntargetableTicks <= 180)
                    {
                        if (!goalScoped && inRange)
                            localBossAttackEvidence?.TryBind(AITeammateSystem.Instance.BossEvidence, activeBoss.GetGlobalNPC<BossWitness>().SpawnId, bossType);
                        if (bossUntargetableTicks == 1)
                        {
                            InvalidateRoutePath();
                            navHasTarget = false;
                            attackApproachStartedAt = null;
                            AITeammateSystem.Instance.TestLog($"boss still active, awaiting attack opportunity: type={bossType} life={activeBoss.life} inPursuitRange={inRange}");
                        }
                        npc.velocity.X *= 0.75f;
                        return inRange ? activeBoss.Center : npc.Center;
                    }
                    FinishCurrentStep(JsonSerializer.Serialize(new
                    {
                        kind = "attack_blocked",
                        code = inRange ? "boss_temporarily_untargetable" : "boss_outside_pursuit",
                        npcType = bossType, spawnId = activeBoss.GetGlobalNPC<BossWitness>().SpawnId,
                        x = (int)(activeBoss.Center.X / 16f), y = (int)(activeBoss.Center.Y / 16f),
                        distanceTiles = Vector2.Distance(npc.Center, activeBoss.Center) / 16f,
                        hp = activeBoss.life, observedAtTick = Main.GameUpdateCount, inPursuitRange = inRange,
                        pursuitRangeTiles = pursuitRange / 16f, waitedTicks = bossUntargetableTicks - 1,
                        reason = inRange ? "所选Boss仍存活，等待后仍不可攻击。重新核对战斗条件。" :
                            "所选Boss仍存活，等待后仍在接敌范围外。坐标是最后观测的Boss中心，尚未核对通路或可站位置。"
                    }), false);
                    return npc.Center;
                }
                FinishCurrentStep(killed ? $"取得本次{Lang.GetNPCNameValue(bossType)}真实死亡和同伴伤害证据。" :
                    $"当前未观察到{Lang.GetNPCNameValue(bossType)}；没找到/消失/脱战不能当击杀。" +
                    (goalScoped ? "本目标的真实死亡和同伴伤害证据尚未满足。" : "本次攻击固定Boss的新增同伴伤害和真实死亡证据尚未满足，没有改用旧击杀或另一Boss。"), killed);
                return player.Center;
            }
            if (sequenceActive || currentPlan != null)
            {
                bool observedDeath = attackLastTarget != null && attackLastTarget.life <= 0 &&
                    (!requestedType.HasValue || attackLastTarget.type == requestedType.Value);
                FinishCurrentStep(observedDeath ? "本次追踪的敌人生命值已归零，当前范围内没有其它符合本指令的可攻击目标。" :
                    "当前范围内没有符合本指令的可攻击目标；未观察到死亡不能声称击杀，也没有改打其它种类。", observedDeath);
                return player.Center;
            }

            return Follow(npc, player);
        }

        bossUntargetableTicks = 0;
        if (specificBoss && !goalScoped)
            localBossAttackEvidence?.TryBind(AITeammateSystem.Instance.BossEvidence, enemy.GetGlobalNPC<BossWitness>().SpawnId, requestedType!.Value);
        attackLastTarget = enemy;
        return AttackEnemy(npc, player, enemy, false);
    }

    private static int? ResolveAttackTargetName(string query)
    {
        int boss = BossRules.ResolveBoss(query);
        if (boss != 0) return boss;
        for (int type = 1; type < NPCLoader.NPCCount; type++)
            if (query.Equals(NPCID.Search.GetName(type), StringComparison.OrdinalIgnoreCase) ||
                query.Equals(Lang.GetNPCNameValue(type), StringComparison.OrdinalIgnoreCase)) return type;
        return null;
    }

    private Vector2 AttackEnemy(NPC npc, Player player, NPC enemy, bool temporary)
    {
        Vector2 targetPosition = enemy.Center;
        float distance = Vector2.Distance(npc.Center, enemy.Center);
        float attackRange = CompanionWeapons.AttackRange(Loadout);
        float safeMinDistance = attackRange >= 12f * 16f
            ? Math.Min(attackRange - 5f * 16f, 11f * 16f + enemy.width * 0.5f)
            : 0f;

        int aimWidth = attackRange == CompanionWeapons.MeleeRange ? enemy.width : 0;
        bool lineOfSight = CompanionAim.TryFindVisiblePoint(npc.Center, targetPosition, aimWidth, out Vector2 aimPoint);
        if (distance <= attackRange && lineOfSight) attackApproachStartedAt = null;

        if (distance > attackRange || !lineOfSight)
        {
            NavigateAttackApproach(npc, targetPosition, attackRange, aimWidth, 5.2f);
        }
        else if (safeMinDistance > 0f && distance < safeMinDistance)
        {
            float dirFromEnemy = npc.Center.X >= enemy.Center.X ? 1f : -1f;
            if (Math.Abs(npc.Center.X - player.Center.X) > 75f * 16f)
                dirFromEnemy = player.Center.X >= npc.Center.X ? 1f : -1f;
            Vector2 kiteTarget = new(enemy.Center.X + dirFromEnemy * (safeMinDistance + 3f * 16f), npc.Center.Y);
            MoveTowards(npc, kiteTarget, 5f, 8f);
            npc.direction = npc.spriteDirection = enemy.Center.X > npc.Center.X ? 1 : -1;
        }
        else
        {
            npc.velocity.X *= 0.8f;
            npc.direction = npc.spriteDirection = enemy.Center.X > npc.Center.X ? 1 : -1;
        }

        attackCooldown--;
        if (attackCooldown <= 0 && distance <= attackRange + 16f && lineOfSight)
        {
            WeaponUseResult result = CompanionWeapons.Use(npc, enemy, Loadout, Animation, aimPoint);
            attackCooldown = result.Cooldown;

            if (!string.IsNullOrWhiteSpace(result.Message) && !weaponWarned)
            {
                weaponWarned = true;
                if (temporary)
                {
                    Main.NewText($"小星：{result.Message}", Color.LightGreen);
                    AITeammateSystem.Instance.TestLog("auto defend weapon: " + result.Message);
                }
                else
                {
                    FinishTask(result.Message);
                }
            }
        }

        return targetPosition;
    }

    private NPC? FindAutoDefendEnemy(NPC npc, Player player)
    {
        if (autoDefendTarget >= 0 && autoDefendTarget < Main.maxNPCs)
        {
            NPC target = Main.npc[autoDefendTarget];
            if (target.active && !target.friendly && target.life > 0 && !target.dontTakeDamage &&
                Vector2.Distance(target.Center, player.Center) < 70f * 16f)
                return target;

            autoDefendTarget = -1;
        }

        NPC? nearest = FindNearestEnemyNear(player.Center, 55f * 16f);
        if (nearest != null)
            autoDefendTarget = nearest.whoAmI;

        return nearest;
    }

    private static CompanionLoadout Loadout => AITeammateSystem.Instance.Loadout;

    private Vector2 Pickup(NPC npc, Player player)
    {
        if (pickupProgress.QuotaReached)
        {
            FinishPickup(true, "本次新增拾取数量已达到指令上限，其余地面物品保持原样。");
            return npc.Center;
        }
        if (pickupItemIndex >= 0 && (pickupItemIndex >= Main.maxItems ||
            !BasicCommandRules.PickupTargetStillMatches(Main.item[pickupItemIndex].active, Main.item[pickupItemIndex].IsAir,
                MatchesItem(Main.item[pickupItemIndex], itemQuery), Main.item[pickupItemIndex].type, pickupItemType, Main.item[pickupItemIndex].stack)))
            pickupItemIndex = -1;
        if (pickupItemIndex < 0)
        {
            pickupItemIndex = FindGroundItem(itemQuery, npc.Center);
            if (pickupItemIndex < 0)
            {
                bool targetLooksLikeChest = target is "withdraw" or "chest" ||
                    CompanionChests.FindNearestChest(npc, 12f * 16f) >= 0;
                string hint = targetLooksLikeChest
                    ? "（pickup只捡地面掉落物；从箱子取物请用 type=\"withdraw\"）"
                    : "";
                FinishPickup(false, $"当前选择范围内没有更多匹配的地面{(string.IsNullOrWhiteSpace(itemQuery) ? "物品" : itemQuery)}；只报告本次实际转入数量，未补足的数量没有算成功。{hint}");
                return player.Center;
            }

            pickupTicks = 0;
            pickupItemType = Main.item[pickupItemIndex].type;
        }

        Item item = Main.item[pickupItemIndex];
        if (!item.active || item.IsAir)
        {
            pickupItemIndex = -1;
                        return player.Center;
        }

        float distance = Vector2.Distance(npc.Center, item.Center);
        if (distance > MiningApproachProbe.ToolReachPixels)
        {
            Navigate(npc, item.Center, 4.5f);
            pickupTicks++;
            if (pickupTicks > 150)
            {
                TeleportNear(npc, item.Center);
                pickupTicks = 0;
            }

            return item.Center;
        }

        int requested = pickupProgress.RequestFromStack(item.stack);
        int added = TransferPickupStack(item);
        if (added > 0)
        {
            bool capacityLimited = added < requested;
            for (int i = 0; i < Main.maxItems; i++)
            {
                if (i == pickupItemIndex) continue;
                Item extra = Main.item[i];
                if (!extra.active || extra.IsAir || extra.stack <= 0 || Vector2.Distance(npc.Center, extra.Center) > 5f * 16f)
                    continue;
                if (!MatchesItem(extra, itemQuery))
                    continue;
                int extraRequested = pickupProgress.RequestFromStack(extra.stack);
                int extraAdded = TransferPickupStack(extra);
                if (extraAdded < extraRequested) capacityLimited = true;
                if (pickupProgress.QuotaReached) break;
            }
            if (pickupProgress.QuotaReached || !pickupProgress.RequestedCount.HasValue && !capacityLimited)
                FinishPickup(true, "已按目标匹配拾取本次实际可装下的地面物品；没有拿取不匹配的其它掉落。");
            else if (capacityLimited)
                FinishPickup(false, "背包容量不足，只完成了部分实际拾取，未转入的物品仍在地面。");
        }
        else
        {
            FinishPickup(false, "背包容量不足，没有把未拾取的数量算入结果。");
        }

        pickupItemIndex = -1;
                return player.Center;
    }

    private int TransferPickupStack(Item item)
    {
        if (!item.active || item.IsAir || !MatchesItem(item, itemQuery)) return 0;
        int requested = pickupProgress.RequestFromStack(item.stack);
        if (requested <= 0) return 0;
        int type = item.type;
        int added = Loadout.AddItemStack(type, requested);
        if (added <= 0) return 0;
        pickupProgress.RecordTransfer(type, added);
        AITeammateSystem.Instance.BossEvidence.Loot(item.GetGlobalItem<BossLootTag>().BossSpawnId, added);
        item.stack -= added;
        if (item.stack <= 0) item.active = false;
        return added;
    }

    private void FinishPickup(bool success, string reason) => FinishCurrentStep(JsonSerializer.Serialize(new
    {
        kind = success ? "pickup_result" : "pickup_blocked", success, itemQuery,
        requestedCount = pickupProgress.RequestedCount, actualCount = pickupProgress.TotalAdded,
        remainingCount = pickupProgress.RemainingCount,
        actualItems = pickupProgress.Amounts.Select(row => new { itemType = row.ItemType, name = Lang.GetItemNameValue(row.ItemType), amount = row.Amount }).ToArray(),
        reason, inventorySource = "actual_ground_item_transfers_during_this_pickup_only",
        selectionRangeTiles = 80, interactionRangePixels = MiningApproachProbe.ToolReachPixels,
        note = "Prior inventory is excluded. Positive count is a total new-transfer upper bound across matching stacks; omitted/zero count keeps the nearby matching batch behavior. Remaining ground drops are not credited."
    }), success);

    private Vector2 DropItem(NPC npc, Player player)
    {
        int index = Loadout.FindInventoryIndex(itemQuery);
        if (index < 0)
        {
            FinishCurrentStep($"背包里没有{(string.IsNullOrWhiteSpace(itemQuery) ? "这件东西" : itemQuery)}。", false);
                        return player.Center;
        }

        Item item = Loadout.Inventory[index];
        int dropCount = Math.Min(count, item.stack);
        int itemType = item.type;
        int prefix = item.prefix;
        string name = Lang.GetItemNameValue(itemType);

        if (!Loadout.RemoveItem(itemType, dropCount))
        {
                        return player.Center;
        }

        Item.NewItem(new EntitySource_Misc("AITeammateDrop"), npc.Center, itemType, dropCount, prefix);
        FinishCurrentStep($"丢掉了 {name}x{dropCount}。");
                return player.Center;
    }

    private Vector2 InspectChest(NPC npc, Player player)
    {
        if (chestIndex < 0)
            chestIndex = CompanionChests.FindTargetChest(npc, false, itemQuery, currentPlan?.X, currentPlan?.Y, -1);
        Chest? chest = CompanionChests.GetChest(chestIndex);
        if (chest == null || !CompanionChests.MatchesExplicitTarget(chest, currentPlan?.X, currentPlan?.Y))
        {
            FinishCurrentStep("target_unavailable: 指定箱子不存在或已改变；没有改选其他箱子。", false);
            return npc.Center;
        }
        Vector2 position = CompanionChests.Center(chest);
        if (Vector2.Distance(npc.Center, position) > 5f * 16f)
        {
            Navigate(npc, position, 4.5f);
            chestTicks++;
            if (chestTicks > 150)
            {
                TeleportNear(npc, position);
                chestTicks = 0;
            }
            return position;
        }
        npc.velocity.X *= 0.7f;
        chestTicks = 0;
        lastInspectedChestIndex = chestIndex;
        chestIndex = -1;
        FinishCurrentStep(AgentKnowledge.Query(itemQuery, "chest", npc, player, this, currentPlan?.X, currentPlan?.Y));
        return npc.Center;
    }

    private Vector2 UseItem(NPC npc, Player player)
    {
        int index = Loadout.FindInventoryIndex(itemQuery);
        if (index < 0)
        {
            FinishCurrentStep($"背包里没有{(string.IsNullOrWhiteSpace(itemQuery) ? "这个道具" : itemQuery)}。", false);
                        return player.Center;
        }

        Item item = Loadout.Inventory[index];
        string name = Lang.GetItemNameValue(item.type);
        Vector2 aim = player.Center - npc.Center;
        bool summoned = BossKnowledge.TryUseSummoner(item, npc, player, out bool handled, out string summonResult);
        if (handled)
        {
            FinishCurrentStep(summonResult, summoned);
            return npc.Center;
        }
        Animation.Start(item, CompanionUseKind.Drink, item.useAnimation, aim);
        if (item.UseSound.HasValue)
            SoundEngine.PlaySound(item.UseSound, npc.Center);

        if (item.healLife > 0)
        {
            int heal = Math.Min(item.healLife, npc.lifeMax - npc.life);
            npc.life += heal;
            CombatText.NewText(npc.Hitbox, Color.LightGreen, $"+{heal}");
            Loadout.RemoveItem(item.type, 1);
            FinishCurrentStep($"喝了 {name}。");
        }
        else if (item.buffType > 0)
        {
            npc.AddBuff(item.buffType, item.buffTime);
            Loadout.RemoveItem(item.type, 1);
            FinishCurrentStep($"用了 {name}。");
        }
        else if (item.healMana > 0)
        {
            if (Loadout.Mana >= Loadout.ManaMax)
            {
                FinishCurrentStep("法力是满的，不用喝。");
            }
            else
            {
                int restore = Math.Min(item.healMana, Loadout.ManaMax - Loadout.Mana);
                Loadout.Mana += restore;
                CombatText.NewText(npc.Hitbox, Color.LightBlue, $"+{restore} 法力");
                Loadout.RemoveItem(item.type, 1);
                FinishCurrentStep($"喝了 {name}。");
            }
        }
        else
        {
            FinishCurrentStep($"{name}不是消耗品。", false);
        }

                return player.Center;
    }

    private Vector2 EquipItem(NPC npc, Player player)
    {
        int index = Loadout.FindInventoryIndex(itemQuery);
        if (index < 0)
        {
            FinishCurrentStep($"背包里没有{(string.IsNullOrWhiteSpace(itemQuery) ? "这件装备" : itemQuery)}。", false);
                        return player.Center;
        }

        Item item = Loadout.Inventory[index];
        string name = Lang.GetItemNameValue(item.type);
        int slot = ParseSlot(slotQuery);

        if (item.headSlot >= 0)
        {
            int armorSlot = slot is >= 0 and <= 2 ? slot : 0;
            if (Loadout.EquipArmorFromInventory(index, armorSlot))
                FinishCurrentStep($"戴上了 {name}。");
            else
                FinishCurrentStep($"{name}不能放在这个装备位。", false);
        }
        else if (item.bodySlot >= 0)
        {
            int armorSlot = slot is >= 0 and <= 2 ? slot : 1;
            if (Loadout.EquipArmorFromInventory(index, armorSlot))
                FinishCurrentStep($"穿上了 {name}。");
            else
                FinishCurrentStep($"{name}不能放在这个装备位。", false);
        }
        else if (item.legSlot >= 0)
        {
            int armorSlot = slot is >= 0 and <= 2 ? slot : 2;
            if (Loadout.EquipArmorFromInventory(index, armorSlot))
                FinishCurrentStep($"换上了 {name}。");
            else
                FinishCurrentStep($"{name}不能放在这个装备位。", false);
        }
        else if (item.accessory)
        {
            int accessorySlot = slot is >= 3 and <= 5 ? slot - 3 : FindAccessorySlot();
            if (Loadout.EquipAccessoryFromInventory(index, accessorySlot))
                FinishCurrentStep($"戴上了 {name}。");
            else
                FinishCurrentStep($"{name}不能放在这个饰品位。", false);
        }
        else if (CompanionLoadout.IsCombatWeapon(item))
        {
            Loadout.PreferredWeaponType = item.type;
            FinishCurrentStep($"记住了，战斗时用 {name}。");
        }
        else
        {
            string feedback = BossRules.BossTypeForItem(item.type) == 0
                ? $"{name}不是可装备的物品。"
                : $"{name}是召唤物。满足召唤条件时用独立任务 {{\"type\":\"use\",\"item\":{item.type}}}。";
            FinishCurrentStep(feedback, false);
        }

                return player.Center;
    }

    public bool ResumeCombat()
    {
        if (action != AIActionType.Retreat)
            return false;

        bool queuedAttack = pendingPlans.Count > 0 && pendingPlans.Peek().Action == AIActionType.Attack;
        AIPlan nextAttack = queuedAttack ? pendingPlans.Peek() : new AIPlan
        {
            Action = AIActionType.Attack,
            Target = string.IsNullOrWhiteSpace(target) ? "nearest" : target
        };
        // Only the exact action suspended by BeginRetreat is a continuation.
        // An identical-looking replacement is still a newly dispatched task.
        if (!ReferenceEquals(nextAttack, suspendedAttack))
        {
            if (!TryAdmitTaskStart(nextAttack)) return false;
            if (queuedAttack) pendingPlans.Dequeue();
            ApplyOne(nextAttack, admissionChecked: true);
            sequenceActive = pendingPlans.Count > 0;
            return action == AIActionType.Attack && ReferenceEquals(currentPlan, nextAttack);
        }
        currentPlan = pendingPlans.Dequeue();
        suspendedAttack = null;
        target = (currentPlan.Target ?? "").Trim().ToLowerInvariant();
        action = AIActionType.Attack;
        sequenceActive = pendingPlans.Count > 0;
        retreatTicks = 0;
        retreatRecoveryTicks = 0;
        localBossAttackEvidence = new(AITeammateSystem.Instance.BossEvidence.DeathSequence);
        InvalidateRoutePath();
        return true;
    }

    public bool TrySwitchWeapon(string query, out string message)
    {
        int index = Loadout.FindInventoryIndex(query);
        if (index < 0)
        {
            message = $"背包里没有{(string.IsNullOrWhiteSpace(query) ? "这把武器" : query)}。";
            return false;
        }

        Item item = Loadout.Inventory[index];
        if (!CompanionLoadout.IsCombatWeapon(item))
        {
            message = $"{Lang.GetItemNameValue(item.type)}不是战斗武器。";
            return false;
        }

        if (Loadout.PreferredWeaponType == item.type)
        {
            message = $"已经在用 {Lang.GetItemNameValue(item.type)}。";
            return true;
        }

        if (item.useAmmo > 0 && Loadout.FindAmmo(item.useAmmo) == null)
        {
            message = $"{Lang.GetItemNameValue(item.type)}没有弹药。";
            return false;
        }

        Item? current = Loadout.GetBestWeapon();
        bool emergency = current == null ||
                         (current.useAmmo > 0 && Loadout.FindAmmo(current.useAmmo) == null);
        if (weaponSwitchCooldown > 0 && !emergency)
        {
            message = "刚换过武器，先打一会。";
            return false;
        }

        Loadout.PreferredWeaponType = item.type;
        weaponSwitchCooldown = 120;
        attackCooldown = 0;
        InvalidateRoutePath();
        message = $"换上 {Lang.GetItemNameValue(item.type)}。";
        AITeammateSystem.Instance.Mod.Logger.Info("[AITEST] combat switch: " + message);
        return true;
    }

    public bool TryUseCombatItem(string query, out string message)
    {
        if (combatPotionCooldown > 0)
        {
            message = "药水还在冷却。";
            return false;
        }

        int index = Loadout.FindInventoryIndex(query);
        if (index < 0)
        {
            message = $"背包里没有{(string.IsNullOrWhiteSpace(query) ? "药水" : query)}。";
            return false;
        }

        Item item = Loadout.Inventory[index];
        string name = Lang.GetItemNameValue(item.type);
        NPC? companion = AITeammateSystem.Instance.CompanionNpc;
        if (companion == null)
        {
            message = "小星不在。";
            return false;
        }

        if (item.healLife > 0)
        {
            if (companion.life >= companion.lifeMax)
            {
                message = "血是满的，不用喝。";
                return false;
            }

            int heal = Math.Min(item.healLife, companion.lifeMax - companion.life);
            companion.life += heal;
            CombatText.NewText(companion.Hitbox, Color.LightGreen, $"+{heal}");
        }
        else if (item.healMana > 0)
        {
            if (Loadout.Mana >= Loadout.ManaMax)
            {
                message = "法力是满的，不用喝。";
                return false;
            }

            int restore = Math.Min(item.healMana, Loadout.ManaMax - Loadout.Mana);
            Loadout.Mana += restore;
            CombatText.NewText(companion.Hitbox, Color.LightBlue, $"+{restore} 法力");
        }
        else if (item.buffType > 0)
        {
            companion.AddBuff(item.buffType, item.buffTime);
        }
        else
        {
            message = $"{name}不是战斗药水。";
            return false;
        }

        Loadout.RemoveItem(item.type, 1);
        combatPotionCooldown = 1800;
        Animation.Start(item, CompanionUseKind.Drink, item.useAnimation,
            Main.player[Main.myPlayer].Center - companion.Center);
        if (item.UseSound.HasValue)
            SoundEngine.PlaySound(item.UseSound, companion.Center);
        message = $"临场用了 {name}。";
        AITeammateSystem.Instance.Mod.Logger.Info("[AITEST] combat use: " + message);
        return true;
    }

    public bool BeginRetreat(out string message)
    {
        if (!IsInCombat)
        {
            message = "当前不在战斗。";
            return false;
        }

        if (action == AIActionType.Retreat)
        {
            message = "已经在撤退。";
            return false;
        }

        EndAutoDefend("retreat");
        HoldPosition = false;
        suspendedAttack = currentPlan?.Action == AIActionType.Attack ? currentPlan : null;
        if (currentPlan != null)
        {
            AIPlan[] remaining = pendingPlans.ToArray();
            pendingPlans.Clear();
            pendingPlans.Enqueue(currentPlan);
            foreach (AIPlan task in remaining)
                pendingPlans.Enqueue(task);
        }
        currentPlan = new AIPlan { Action = AIActionType.Retreat };
        action = AIActionType.Retreat;
        retreatTicks = 0;
        InvalidateRoutePath();
        sequenceActive = pendingPlans.Count > 0;
        message = "小星：先撤！";
        Main.NewText(message, Color.Orange);
        AITeammateSystem.Instance.Mod.Logger.Info("[AITEST] combat retreat");
        return true;
    }

    private Vector2 Retreat(NPC npc, Player player)
    {
        retreatTicks++;
        NPC? enemy = FindNearestEnemy(npc, 45f * 16f);
        bool safe = enemy == null || Vector2.Distance(npc.Center, enemy.Center) > 24f * 16f;
        if (safe || retreatTicks > 360)
        {
            retreatTicks = 0;
            retreatRecoveryTicks = 600;
            InvalidateRoutePath();
            if (pendingPlans.Count > 0 && pendingPlans.Peek().Action == AIActionType.Attack)
            {
                bool resumed = ResumeCombat();
                if (resumed) retreatRecoveryTicks = 600;
                // A rejected new attack has already cleared this dispatch and
                // scheduled review; do not report a second retreat outcome.
                if (resumed || action != AIActionType.Retreat) return npc.Center;
            }
            FinishCurrentStep(safe ? "撤退完成，已拉开距离；需要复盘原目标。" :
                "撤退超时，敌人仍然很近，需要调整避险方案。", safe);
            return npc.Center;
        }

        if (enemy == null)
            return npc.Center;
        Vector2 away = npc.Center - enemy.Center;
        if (away.LengthSquared() < 0.01f)
            away = new Vector2(npc.direction == 0 ? 1 : -npc.direction, 0f);
        away.Normalize();

        Vector2 goal = player.Center;
        if (Vector2.Distance(player.Center, enemy.Center) < 10f * 16f)
            goal = npc.Center + away * 14f * 16f;

        Navigate(npc, goal, 5.4f);
        return goal;
    }

    private Vector2 Deposit(NPC npc, Player player) => HandleChest(npc, player, true);

    private Vector2 Withdraw(NPC npc, Player player) => HandleChest(npc, player, false);

    private Vector2 HandleChest(NPC npc, Player player, bool deposit)
    {
        if (chestIndex < 0)
        {
            chestIndex = CompanionChests.FindTargetChest(npc, deposit, itemQuery, currentPlan?.X, currentPlan?.Y,
                lastInspectedChestIndex);
            if (chestIndex < 0)
            {
                FinishCurrentStep("附近没有箱子，先找到一个箱子。", false);
                return player.Center;
            }

            chestTicks = 0;
            Chest? found = CompanionChests.GetChest(chestIndex);
            if (found != null)
                AITeammateSystem.Instance.TestLog($"chest target {found.x},{found.y} deposit={deposit}");
        }

        Chest? chest = CompanionChests.GetChest(chestIndex);
        if (chest == null || !CompanionChests.MatchesExplicitTarget(chest, currentPlan?.X, currentPlan?.Y))
        {
            FinishCurrentStep("target_unavailable: 箱子不见了或不再匹配指定坐标。", false);
            chestIndex = -1;
            return player.Center;
        }

        Vector2 center = CompanionChests.Center(chest);
        if (Vector2.Distance(npc.Center, center) > 5f * 16f)
        {
            Navigate(npc, center, 4.5f);
            chestTicks++;
            if (chestTicks > 150)
            {
                TeleportNear(npc, center);
                chestTicks = 0;
            }

            return center;
        }

        npc.velocity.X *= 0.8f;
        string message;
        bool moved = deposit
            ? CompanionChests.Deposit(Loadout, chest, itemQuery, count, out message)
            : CompanionChests.Withdraw(Loadout, chest, itemQuery, count, out message);

        if (!moved)
            AITeammateSystem.Instance.TestLog("chest action failed: " + message);

        FinishCurrentStep(message, moved);
        chestIndex = -1;
        return center;
    }

    private Vector2 Craft(NPC npc, Player player)
    {
        var system = AITeammateSystem.Instance;
        Recipe? recipe = craftRecipe;
        if (recipe == null)
        {
            recipe = CompanionCrafting.FindRecipe(itemQuery, Loadout, npc, currentPlan?.RecipeId, out string error);
            if (recipe == null)
            {
                FinishCurrentStep(error, false);
                return player.Center;
            }
        }

        int outputItemId = recipe.createItem.type;
        if (currentPlan is { } plan && system.OrderedCraftBlockReason(outputItemId) is string orderError)
        {
            craftRecipe = null;
            RejectTaskStart(plan, "ordered_action_pending", orderError);
            return npc.Center;
        }

        if (craftRecipe == null)
        {
            craftRecipe = recipe;
            string name = Lang.GetItemNameValue(outputItemId);

            if (!CompanionCrafting.HasStationNear(npc, recipe))
            {
                if (!CompanionCrafting.FindNearestStation(npc, recipe, out int stationX, out int stationY))
                {
                    FinishCurrentStep($"做{name}需要工作站，附近没有。", false);
                    craftRecipe = null;
                    return player.Center;
                }

                craftStationX = stationX;
                craftStationY = stationY;
                system.TestLog($"craft station {stationX},{stationY} for {name}");
            }

            system.TestLog($"craft target {name} x{count} recipe={outputItemId}");
        }

        if (!CompanionCrafting.HasStationNear(npc, recipe))
        {
            Vector2 station = new Vector2(craftStationX * 16f + 8f, craftStationY * 16f + 8f);
            Navigate(npc, station, 4.5f);
            mineStuckTicks++;
            if (mineStuckTicks > 150)
            {
                TeleportNear(npc, station);
                mineStuckTicks = 0;
            }
            return station;
        }
        mineStuckTicks = 0;

        npc.velocity.X *= 0.8f;
        bool crafted = CompanionCrafting.TryCraft(Loadout, recipe, count, npc, out string message, out int produced);
        if (crafted) system.RecordOrderedCraft(outputItemId, produced);
        FinishCurrentStep(message, crafted);
        craftRecipe = null;
        return npc.Center;
    }

    private static int FindAccessorySlot()
    {
        for (int i = 0; i < CompanionLoadout.AccessorySize; i++)
        {
            if (Loadout.Accessories[i].IsAir)
                return i;
        }

        return 0;
    }

    private static int ParseSlot(string slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
            return -1;

        if (slot.Contains("头") || slot.Contains("head"))
            return 0;
        if (slot.Contains("身") || slot.Contains("body") || slot.Contains("胸"))
            return 1;
        if (slot.Contains("腿") || slot.Contains("leg"))
            return 2;
        if (slot.Contains("饰") || slot.Contains("access"))
            return 3;

        return -1;
    }

    private static int FindGroundItem(string query, Vector2 center)
    {
        int best = -1;
        bool bestBossLoot = false;
        float bestDistance = 80f * 16f;

        for (int i = 0; i < Main.maxItems; i++)
        {
            Item item = Main.item[i];
            if (!item.active || item.IsAir || !MatchesItem(item, query))
                continue;

            bool isBossLoot = item.GetGlobalItem<BossLootTag>().BossSpawnId > 0;
            float distance = Vector2.Distance(item.Center, center);
            if (distance >= 80f * 16f)
                continue;
            if ((isBossLoot && !bestBossLoot) || (isBossLoot == bestBossLoot && distance < bestDistance))
            {
                bestBossLoot = isBossLoot;
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    public static bool HasGroundItem(string query, Vector2 center) => FindGroundItem(query, center) >= 0;

    public static bool IsPartOfHouseOrStructure(int x, int y)
    {
        bool nonBearingTree = WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile &&
            IsNonBearingNativeTree(Main.tile[x, y].TileType);
        if (nonBearingTree ? HasDirectTreeObjectDependency(x, y) : IsPartOfPlayerOrChestStructure(x, y))
            return true;
        for (int cx = x - 2; cx <= x + 2; cx++)
            for (int cy = y - 2; cy <= y + 2; cy++)
            {
                if (!WorldGen.InWorld(cx, cy, 2))
                    continue;
                ushort wall = Main.tile[cx, cy].WallType;
                // Generated living-tree rooms use unsafe walls: safety for NPC
                // housing does not decide whether an existing structure may be mined.
                if (wall > 0 && (Main.wallHouse[wall] || wall == WallID.LivingWoodUnsafe))
                    return true;
            }
        return false;
    }

    private static bool MatchesItem(Item item, string query)
    {
        string normalized = (query ?? "").Trim().ToLowerInvariant();
        if (normalized is "boss" or "boss_loot")
        {
            long spawnId = item.GetGlobalItem<BossLootTag>().BossSpawnId;
            return spawnId > 0 && AITeammateSystem.Instance.BossEvidence.ReadEncounter(spawnId) is { DeathSequence: > 0 };
        }
        if (string.IsNullOrWhiteSpace(normalized) || normalized is "nearest" or "any" or "nearest_item" or
            "all" or "loot" or "战利品" or "掉落" or "掉落物" or "全部")
            return true;

        if (int.TryParse(normalized, out int itemType))
            return item.type == itemType;

        string localized = Lang.GetItemNameValue(item.type);
        string english = item.Name ?? "";
        return localized.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
               english.Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private Vector2 PlaceArea(NPC npc, Item item, bool isWall, int x0, int y0, int w, int h)
    {
        // Removing the final unit clears the inventory slot. Keep placement metadata stable
        // while this tick continues, including when another stack contains the same item.
        item = item.Clone();
        int total = w * h;
        Rectangle footprint = new(x0 * 16, y0 * 16, w * 16, h * 16);
        int placedThisTick = 0;
        Vector2 lastCell = new((x0 + w / 2f) * 16f, (y0 + h / 2f) * 16f);

        while (areaIndex < total && placedThisTick < 3)
        {
            int tx = x0 + areaIndex % w;
            int ty = y0 + h - 1 - areaIndex / w;
            Vector2 cell = new(tx * 16f + 8, ty * 16f + 8);
            lastCell = cell;
            if (Vector2.Distance(npc.Center, cell) > 8f * 16f)
            {
                if (placedThisTick > 0)
                    break;
                bool hasStand = TryFindStandNear(cell, out Vector2 stand);
                if (++mineStuckTicks > 150)
                {
                    mineStuckTicks = 0;
                    if (hasStand)
                        TeleportNear(npc, stand);
                    else
                    {
                        areaSkipped++;
                        areaIndex++;
                    }
                    InvalidateRoutePath();
                    return cell;
                }
                Navigate(npc, hasStand ? stand : cell, 4.5f);
                return cell;
            }
            mineStuckTicks = 0;
            areaIndex++;

            if (!WorldGen.InWorld(tx, ty, 3))
            {
                areaSkipped++;
                continue;
            }
            Tile t = Main.tile[tx, ty];
            if (isWall)
            {
                if (t.WallType == item.createWall)
                    continue;
                if (t.WallType != WallID.None)
                {
                    areaSkipped++;
                    continue;
                }
            }
            else
            {
                if (t.HasTile)
                {
                    if (t.TileType != item.createTile)
                        areaSkipped++;
                    continue;
                }
                Rectangle cellRect = new(tx * 16, ty * 16, 16, 16);
                if (npc.Hitbox.Intersects(cellRect) || Main.player.Any(p => p.active && !p.dead && p.Hitbox.Intersects(cellRect)) ||
                    !HasAdjacentSupport(tx, ty) || t.LiquidAmount > 0 && t.LiquidType == LiquidID.Lava)
                {
                    areaSkipped++;
                    continue;
                }
            }

            if (Loadout.CountItem(item.type) <= 0)
            {
                FinishCurrentStep($"区域放置中途材料用完：已放 {areaPlaced}，跳过 {areaSkipped}，剩余未处理 {total - areaIndex + 1} 格。需要补充 {item.Name}。", false);
                return cell;
            }

            bool ok;
            if (isWall)
            {
                WorldGen.PlaceWall(tx, ty, item.createWall, true);
                ok = Main.tile[tx, ty].WallType == item.createWall;
            }
            else
            {
                WorldGen.PlaceTile(tx, ty, item.createTile, true, false, Main.myPlayer, item.placeStyle);
                WorldKnowledgeObserver.Invalidate(tx, ty);
                ok = IsTile(tx, ty, item.createTile);
            }
            if (ok)
            {
                Loadout.RemoveItem(item.type, 1);
                areaPlaced++;
                placedThisTick++;
            }
            else
            {
                areaSkipped++;
            }
        }

        npc.velocity.X *= 0.8f;
        if (areaIndex < total)
            return lastCell;

        if (areaSkipped > 0 && areaRetries < 1 && npc.Hitbox.Intersects(footprint))
        {
            areaRetries++;
            areaIndex = 0;
            areaSkipped = 0;
            TeleportNear(npc, new Vector2((x0 - 2) * 16f + 8f, (y0 + h) * 16f - 8f));
            InvalidateRoutePath();
            return lastCell;
        }

        string result = $"区域放置完成 {item.Name}：区域 ({x0},{y0}) {w}x{h}，新放 {areaPlaced} 格，" +
                        (areaSkipped > 0 ? $"{areaSkipped} 格被占用/悬空/有人站着而跳过。" : "全部到位。");
        areaIndex = 0;
        FinishCurrentStep(result, areaSkipped == 0);
        return lastCell;
    }

    private bool AllowPlaceFootprint(int width, int height)
    {
        var system = AITeammateSystem.Instance;
        if (!system.RequiresStructurePreservation) return true;
        if (currentPlan is not { X: int x, Y: int y })
        {
            FinishCurrentStep("原屋保护：放置需要明确的左上角 x/y。先查询场地，再选原屋保护区外的位置。", false);
            return false;
        }

        // Validate the entire footprint before walking, cutting vegetation, placing
        // any member of a rectangle/object, or consuming materials.
        GoalConstructionProtection protection = system.InspectConstructionProtection(x, y, width, height);
        if (ShelterProtectionAllows(protection)) return true;
        string reason = protection.Available && protection.Covered
            ? $"与原屋或预留通路的 {protection.ProtectedCellCount} 格重叠"
            : "未核实该范围的原屋保护覆盖";
        FinishCurrentStep($"原屋保护：拒绝放置 ({x},{y}) {width}×{height}，{reason}。请重新勘查并选择完整的空地区域。", false);
        return false;
    }

    private Vector2 PlaceItem(NPC npc, Player player)
    {
        int index = Loadout.FindInventoryIndex(itemQuery);
        if (index < 0)
        {
            if (currentPlan is { X: not null, Y: not null } area && (area.Width > 1 || area.Height > 1) && areaIndex > 0)
            {
                FinishCurrentStep($"区域放置中途材料用完：已放 {areaPlaced}，跳过 {areaSkipped}，剩余未处理 {area.Width * area.Height - areaIndex} 格。需要补充 {itemQuery}。", false);
                return npc.Center;
            }
            FinishCurrentStep($"背包里没有要放置的物品 {itemQuery}。", false);
            return npc.Center;
        }
        Item item = Loadout.Inventory[index];
        bool isWall = item.createWall > WallID.None && item.createTile < TileID.Dirt;
        if (currentPlan is { X: int ax, Y: int ay } areaPlan && (areaPlan.Width > 1 || areaPlan.Height > 1))
        {
            bool plainBlock = !isWall && item.createTile >= TileID.Dirt && Main.tileSolid[item.createTile] &&
                              !Main.tileSolidTop[item.createTile] && TileObjectData.GetTileData(item.createTile, item.placeStyle) == null;
            if (!isWall && !plainBlock)
            {
                FinishCurrentStep($"{item.Name}不是普通方块或背景墙，不能按区域批量放置。", false);
                return npc.Center;
            }
            if (!AllowPlaceFootprint(areaPlan.Width, areaPlan.Height)) return npc.Center;
            return PlaceArea(npc, item, isWall, ax, ay, areaPlan.Width, areaPlan.Height);
        }
        if (item.createTile < TileID.Dirt && !isWall)
        {
            FinishCurrentStep($"{item.Name}不是可放置的家具、方块或墙。", false);
            return npc.Center;
        }

        TileObjectData? data = isWall ? null : TileObjectData.GetTileData(item.createTile, item.placeStyle);
        if (!AllowPlaceFootprint(data?.Width ?? 1, data?.Height ?? 1)) return npc.Center;

        if (isWall)
        {
            if (currentPlan?.X is not int wx || currentPlan.Y is not int wy)
            {
                FinishCurrentStep("放置背景墙必须提供 x,y 坐标。", false);
                return npc.Center;
            }
            Vector2 wallPos = new(wx * 16f + 8, wy * 16f + 8);
            if (Vector2.Distance(npc.Center, wallPos) > 7f * 16f)
            {
                if (++mineStuckTicks > 150)
                {
                    mineStuckTicks = 0;
                    if (TryFindStandNear(wallPos, out Vector2 wallStand)) TeleportNear(npc, wallStand);
                    else { FinishCurrentStep($"够不到 ({wx},{wy})，无法放墙。", false); return npc.Center; }
                }
                Navigate(npc, wallPos, 4.5f);
                return wallPos;
            }
            mineStuckTicks = 0;
            if (WorldGen.InWorld(wx, wy, 3) && Main.tile[wx, wy].WallType == WallID.None)
                WorldGen.PlaceWall(wx, wy, item.createWall, true);
            if (!WorldGen.InWorld(wx, wy, 3) || Main.tile[wx, wy].WallType != item.createWall)
            {
                FinishCurrentStep($"指定位置 ({wx},{wy}) 无法放置 {item.Name}。", false);
                return npc.Center;
            }
            string wallName = item.Name;
            Loadout.RemoveItem(item.type, 1);
            if (--placeRemaining <= 0)
                FinishCurrentStep($"已实际放置 {wallName} x{count}。 ");
            return npc.Center;
        }

        int height = data?.Height ?? 1;
        int centerX = (int)(npc.Center.X / 16f);
        int feetY = (int)(npc.Bottom.Y / 16f);
        if (currentPlan?.X is int explicitX && currentPlan.Y is int explicitY)
        {
            Vector2 destination = new(explicitX * 16f + 8, explicitY * 16f + 8);
            if (Vector2.Distance(npc.Center, destination) > 7f * 16f)
            {
                if (++mineStuckTicks > 150)
                {
                    mineStuckTicks = 0;
                    if (TryFindStandNear(destination, out Vector2 placeStand))
                    {
                        TeleportNear(npc, placeStand);
                        InvalidateRoutePath();
                        return destination;
                    }
                    FinishCurrentStep($"够不到 ({explicitX},{explicitY})，附近没有可站立位置，无法放置 {item.Name}。", false);
                    return npc.Center;
                }
                Navigate(npc, destination, 4.5f);
                return destination;
            }
            mineStuckTicks = 0;
            if (Main.tileSolid[item.createTile] && !Main.tileSolidTop[item.createTile] &&
                !HasAdjacentSupport(explicitX, explicitY))
            {
                FinishCurrentStep($"({explicitX},{explicitY}) 四周没有任何方块或背景墙，放下去会悬空。先用 inspect nearby 或已知房顶坐标确认真实的屋顶行，再紧贴它放置。", false);
                return npc.Center;
            }
            if (!TryPlaceFurniture(explicitX, explicitY, item.createTile, item.placeStyle))
            {
                FinishCurrentStep($"指定位置 ({explicitX},{explicitY}) 无法放置 {item.Name}；坐标须为物体左上角且需要支撑。", false);
                return npc.Center;
            }
        }
        else
        {
            bool placed = false;
            foreach (int dx in new[] { 3, -3, 5, -5, 7, -7, 1, -1 })
            {
                for (int floorY = feetY - 4; floorY <= feetY + 7 && !placed; floorY++)
                {
                    if (!WorldGen.InWorld(centerX + dx, floorY, 3))
                        continue;
                    Tile ground = Main.tile[centerX + dx, floorY];
                    if (!ground.HasTile || !Main.tileSolid[ground.TileType])
                        continue;
                    placed = TryPlaceFurniture(centerX + dx, floorY - height, item.createTile, item.placeStyle);
                }
                if (placed)
                    break;
            }
            if (!placed)
            {
                FinishCurrentStep($"附近没有足够空地或支撑放置 {item.Name}。", false);
                return npc.Center;
            }
        }

        string name = item.Name;
        Loadout.RemoveItem(item.type, 1);
        if (--placeRemaining <= 0)
            FinishCurrentStep($"已实际放置 {name} x{count}。可查询附近工作站确认。 ");
        return npc.Center;
    }

    private static bool HasAdjacentSupport(int x, int y)
    {
        foreach ((int dx, int dy) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0) })
        {
            int nx = x + dx, ny = y + dy;
            if (!WorldGen.InWorld(nx, ny, 3))
                continue;
            Tile t = Main.tile[nx, ny];
            if (t.HasTile && Main.tileSolid[t.TileType])
                return true;
        }
        return WorldGen.InWorld(x, y, 3) && Main.tile[x, y].WallType != WallID.None;
    }

    // WorldGen remains responsible for anchors, object hooks and legal styles.
    private static bool TryPlaceFurniture(int left, int top, int tileType, int style = 0)
    {
        TileObjectData? data = TileObjectData.GetTileData(tileType, style);
        int width = data?.Width ?? 1;
        int height = data?.Height ?? 1;
        bool ordinaryBlock = data == null && Main.tileSolid[tileType] && !Main.tileSolidTop[tileType];
        for (int x = left; x < left + width; x++)
            for (int y = top; y < top + height; y++)
                if (!WorldGen.InWorld(x, y, 3) ||
                    Main.tile[x, y].HasTile && !(ordinaryBlock && IsReplaceableGroundPlant(Main.tile[x, y])) ||
                    Main.tile[x, y].LiquidAmount > 0 && Main.tile[x, y].LiquidType == LiquidID.Lava)
                    return false;

        // Ordinary grass is cut by a real block placement, not treated as an
        // existing building. Never clear trees, furniture, walls or whole sites.
        if (ordinaryBlock && IsReplaceableGroundPlant(Main.tile[left, top]))
        {
            WorldGen.KillTile(left, top, fail: false, effectOnly: false, noItem: false);
            if (Main.tile[left, top].HasTile) return false;
        }
        if (data == null)
            WorldGen.PlaceTile(left, top, tileType, true, false, Main.myPlayer, style);
        else
            WorldGen.PlaceObject(left + data.Origin.X, top + data.Origin.Y, tileType, true, style);

        // WorldGen placement does not invoke the player-only PlaceInWorld hook.
        // Invalidate even a partially rejected native object placement.
        WorldKnowledgeObserver.InvalidateArea(left, top, width, height);

        for (int x = left; x < left + width; x++)
            for (int y = top; y < top + height; y++)
                if (!IsTile(x, y, tileType))
                    return false;
        return true;
    }

    private static bool IsReplaceableGroundPlant(Tile tile) => tile.HasTile && !tile.IsActuated &&
        tile.WallType == WallID.None && tile.TileType is TileID.Plants or TileID.Plants2 && Main.tileCut[tile.TileType];

    private static bool IsChoppableWoodTile(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 2) || !Main.tile[x, y].HasTile)
            return false;
        return IsChoppableWoodType(Main.tile[x, y].TileType);
    }

    private static bool IsChoppableWoodType(int type) => type is TileID.Trees or TileID.PalmTree or TileID.VanityTreeSakura or
            TileID.VanityTreeYellowWillow or TileID.TreeAsh or TileID.LivingWood;

    public static bool IsValidTreeTrunkOrWood(int x, int y, bool allowTrees = true, bool allowLivingWood = true)
    {
        if (!WorldGen.InWorld(x, y + 1, 3) || !Main.tile[x, y].HasTile)
            return false;
        int type = Main.tile[x, y].TileType;
        if (allowLivingWood && type == TileID.LivingWood)
        {
            if (IsPartOfPlayerOrChestStructure(x, y))
                return false;
            return IsOreExposed(x, y);
        }
        if (!allowTrees || type is not (TileID.Trees or TileID.PalmTree or TileID.VanityTreeSakura or
            TileID.VanityTreeYellowWillow or TileID.TreeAsh))
            return false;

        for (int by = y; by <= Math.Min(Main.maxTilesY - 5, y + 16); by++)
        {
            Tile t = Main.tile[x, by];
            if (!t.HasTile)
                return false;
            if (t.TileType == type)
                continue;
            return Main.tileSolid[t.TileType];
        }
        return false;
    }

    private static bool IsPartOfPlayerOrChestStructure(int x, int y)
    {
        for (int cx = x - 6; cx <= x + 6; cx++)
        {
            for (int cy = y - 6; cy <= y + 6; cy++)
            {
                if (!WorldGen.InWorld(cx, cy, 2) || !Main.tile[cx, cy].HasTile)
                    continue;
                int t = Main.tile[cx, cy].TileType;
                // Include native anchored objects such as GardenGnome and platforms,
                // whose support can be destroyed even when CanKillTile accepts wood.
                // Natural flora are not buildings merely because they have important frames.
                if (Main.tileSolidTop[t] || TileID.Sets.Platforms[t] ||
                    Main.tileFrameImportant[t] && !IsNaturalFloraForMiningProtection(t))
                    return true;
            }
        }
        return false;
    }

    private static bool IsNaturalFloraForMiningProtection(int type) => TileID.Sets.IsATreeTrunk[type] ||
        TileID.Sets.TreeSapling[type] || TileID.Sets.IsVine[type] || type is
        TileID.Plants or TileID.Plants2 or TileID.CorruptPlants or TileID.JunglePlants or TileID.JunglePlants2 or
        TileID.MushroomPlants or TileID.HallowedPlants or TileID.HallowedPlants2 or TileID.CrimsonPlants or
        TileID.DyePlants or TileID.PlantDetritus or TileID.Pumpkins or TileID.Bamboo or TileID.OasisPlants or
        TileID.AshPlants or TileID.Sunflower or TileID.Cactus or TileID.ImmatureHerbs or TileID.MatureHerbs or TileID.BloomingHerbs;

    private static bool IsNonBearingNativeTree(int type) => type != TileID.LivingWood && TileID.Sets.IsATreeTrunk[type] &&
        !Main.tileSolid[type] && !Main.tileSolidTop[type];

    private static bool HasDirectTreeObjectDependency(int treeX, int treeY)
    {
        int treeType = Main.tile[treeX, treeY].TileType;
        // A native anchor is adjacent to its occupied object footprint, not an
        // unrelated six-tile decoration neighborhood.
        for (int x = treeX - 1; x <= treeX + 1; x++) for (int y = treeY - 1; y <= treeY + 1; y++)
        {
            if (!WorldGen.InWorld(x, y, 2)) continue;
            Tile part = Main.tile[x, y]; int type = part.TileType;
            if (!part.HasTile || IsNaturalFloraForMiningProtection(type) ||
                !(Main.tileFrameImportant[type] || Main.tileSolidTop[type] || TileID.Sets.Platforms[type])) continue;
            TileObjectData? data;
            try { data = TileObjectData.GetTileData(part); }
            catch { return true; }
            // Unknown immediately adjacent objects remain conservative; distant
            // unrelated objects no longer classify a nonbearing trunk as a house.
            if (data == null || data.CoordinateFullWidth <= 0 || data.CoordinateFullHeight <= 0) return true;
            int step = data.CoordinateWidth + data.CoordinatePadding;
            int fx = part.TileFrameX % data.CoordinateFullWidth, fy = part.TileFrameY % data.CoordinateFullHeight;
            if (step <= 0 || fx < 0 || fy < 0 || fx % step != 0 || data.Width < 1 || data.Width > 64 ||
                data.Height < 1 || data.Height > 64 || data.CoordinateHeights == null || data.CoordinateHeights.Length < data.Height) return true;
            int column = fx / step, row = 0, rowFrame = 0;
            while (row < data.Height && rowFrame != fy) { rowFrame += data.CoordinateHeights[row] + data.CoordinatePadding; row++; }
            if (column >= data.Width || row >= data.Height) return true;
            int left = x - column, top = y - row;
            bool TreeAnchor(AnchorData anchor) => anchor.tileCount > 0 &&
                ((anchor.type & Terraria.Enums.AnchorType.Tree) != 0 ||
                 (anchor.type & Terraria.Enums.AnchorType.AlternateTile) != 0 && data.AnchorAlternateTiles?.Contains(treeType) == true);
            bool Span(int coordinate, int first, AnchorData anchor) => coordinate >= first + anchor.checkStart && coordinate < first + anchor.checkStart + anchor.tileCount;
            if (treeY == top - 1 && TreeAnchor(data.AnchorTop) && Span(treeX, left, data.AnchorTop) ||
                treeY == top + data.Height && TreeAnchor(data.AnchorBottom) && Span(treeX, left, data.AnchorBottom) ||
                treeX == left - 1 && TreeAnchor(data.AnchorLeft) && Span(treeY, top, data.AnchorLeft) ||
                treeX == left + data.Width && TreeAnchor(data.AnchorRight) && Span(treeY, top, data.AnchorRight)) return true;
        }
        return false;
    }

    private static MiningTreeSourceCheck InspectTreeSource(int x, int y, int type) => MiningTreeSourceSafety.Inspect((x, y),
        (cx, cy) => WorldGen.InWorld(cx, cy, 3) && Main.tile[cx, cy].HasTile && Main.tile[cx, cy].TileType == type,
        (cx, cy) => !Main.tile[cx, cy].IsActuated && !IsPartOfHouseOrStructure(cx, cy) && WorldGen.CanKillTile(cx, cy));

    private bool FindTree(NPC npc, bool hasAxe, bool hasPickaxe, out int treeX, out int treeY)
    {
        return ChooseAutomaticMiningSeed(npc, "wood", TileFor("wood"), out treeX, out treeY);
    }

    private bool ChooseAutomaticMiningSeed(NPC npc, string resource, int expectedType, out int x, out int y)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        MiningReturnAnchor anchor = EnsureMiningReturnAnchor(npc);
        miningAutoSelection = FindAutomaticMiningOptions(npc, resource, expectedType, unreachableMineTiles, anchor);
        miningLastReturn = miningAutoSelection.SelectedApproach?.ReturnCheck ?? miningAutoSelection.Observations.LastOrDefault(o => o.Approach.ReturnCheck != null)?.Approach.ReturnCheck;
        miningLastReturnPhase = "automatic_selection";
        miningAutoSelectionElapsedMs += timer.Elapsed.TotalMilliseconds;
        miningAutoSelectionReport = DescribeAutomaticMiningSelection(npc, resource, miningAutoSelection, unreachableMineTiles.Count, anchor);
        x = miningAutoSelection.Selected?.Seed.X ?? -1;
        y = miningAutoSelection.Selected?.Seed.Y ?? -1;
        return miningAutoSelection.Selected != null;
    }

    private static MiningCandidateSelectionResult FindAutomaticMiningOptions(NPC observer, string resource, int expectedType,
        IReadOnlySet<Point>? excluded = null, MiningReturnAnchor? returnAnchor = null)
    {
        returnAnchor ??= CaptureMiningReturnAnchor(observer, preview: true);
        bool wood = resource == "wood";
        int radiusX = wood ? 52 : 50, radiusY = wood ? 30 : 32;
        float radiusPixels = (wood ? 58 : 55) * 16f;
        int cx = (int)(observer.Center.X / 16f), cy = (int)(observer.Center.Y / 16f);
        bool hasAxe = Loadout.Inventory.Any(item => !item.IsAir && item.axe > 0), hasPick = Loadout.GetBestPickaxe() != null;
        bool Allowed(int x, int y) => WorldGen.InWorld(x, y, 3) &&
            MatchesMiningTarget(resource, expectedType, Main.tile[x, y]) &&
            !IsPartOfHouseOrStructure(x, y) && WorldGen.CanKillTile(x, y);
        var observed = new Dictionary<Point, MiningResourceCandidate>();
        for (int x = cx - radiusX; x <= cx + radiusX; x++)
            for (int y = cy - radiusY; y <= cy + radiusY; y++)
            {
                if (!Allowed(x, y) || excluded?.Contains(new Point(x, y)) == true ||
                    wood && !IsValidTreeTrunkOrWood(x, y, hasAxe, hasPick)) continue;
                int type = Main.tile[x, y].TileType, seedY = y;
                if (wood && type != TileID.LivingWood)
                    while (seedY + 1 < Main.maxTilesY - 5 && Main.tile[x, seedY + 1].HasTile && Main.tile[x, seedY + 1].TileType == type)
                        seedY++;
                var seed = new Point(x, seedY);
                if (!Allowed(x, seedY) || excluded?.Contains(seed) == true) continue;
                float distance = Vector2.Distance(observer.Center, new Vector2(x * 16f + 8, seedY * 16f + 8));
                if (distance > radiusPixels) continue;
                float score = distance + (wood ? type == TileID.LivingWood ? 10 * 16f : 0 : IsOreExposed(x, seedY) ? 0 : 32 * 16f);
                observed[seed] = new(seed, type, score);
            }
        var shared = new MiningRouteProbeCache();
        var treeSafety = new Dictionary<Point, MiningTreeSourceCheck>();
        // This synchronous, read-only selection shares geometry across adjacent seeds.
        // Never retain it across an excavation or a later selection.
        var standGeometry = new Dictionary<Point, bool>();
        bool IsSafeSelectionStand(int x, int y)
        {
            var node = new Point(x, y);
            if (!standGeometry.TryGetValue(node, out bool safe))
                standGeometry[node] = safe = TilePathfinder.IsStableStand(x, y);
            return safe;
        }
        int product = wood ? -1 : ItemForTile(expectedType);
        int capacity = wood ? AvailableWoodCapacity() : product > 0 ? Loadout.CountFreeCapacity(product) : 0;
        return MiningCandidateSelection.Select(observed.Values.ToArray(), (candidate, budget, limit) =>
        {
            Vector2 targetCenter = new(candidate.Seed.X * 16f + 8, candidate.Seed.Y * 16f + 8);
            bool needsAxe = wood && candidate.TileType != TileID.LivingWood;
            Item? tool = SelectMiningTool(needsAxe);
            string? failure = capacity <= 0 ? "no_current_inventory_capacity" : tool == null
                ? needsAxe ? "no_actual_axe" : "no_actual_pickaxe"
                : MiningHitsNeeded(candidate.Seed.X, candidate.Seed.Y, tool, needsAxe) <= 0 ? "actual_tool_cannot_break_seed" : null;
            if (failure == null && needsAxe)
            {
                if (!treeSafety.TryGetValue(candidate.Seed, out MiningTreeSourceCheck? sourceCheck))
                {
                    sourceCheck = InspectTreeSource(candidate.Seed.X, candidate.Seed.Y, candidate.TileType);
                    treeSafety[candidate.Seed] = sourceCheck;
                }
                failure = sourceCheck.Failure;
            }
            return failure == null ? ProbeMiningApproach(observer, targetCenter, budget, shared, limit, returnAnchor, IsSafeSelectionStand)
                : new MiningApproachProbeResult(failure, false, Vector2.Distance(observer.Center, targetCenter),
                    0, 0, budget.ProbeCount, null, null, Array.Empty<MiningApproachFailure>());
        }, () => shared.Count);
    }

    private bool IsDirectedMining => currentPlan?.X.HasValue == true || currentPlan?.Y.HasValue == true;
    private bool MiningTileMatches(int x, int y, int? exactType = null)
    {
        if (!WorldGen.InWorld(x, y, 3)) return false;
        Tile tile = Main.tile[x, y];
        bool strippedGrass = target == "grass" && tile.TileType == TileID.Dirt &&
            miningChangedTiles.TryGetValue(new Point(x, y), out int originalType) && originalType == TileID.Grass;
        if (exactType.HasValue && tile.TileType != exactType.Value && !strippedGrass) return false;
        return MatchesMiningTarget(target, mineTileType, tile, strippedGrass);
    }

    private static bool MatchesMiningTarget(string resource, int expectedType, Tile tile, bool strippedGrass = false)
        => tile.HasTile && !tile.IsActuated && (resource == "wood" ? IsChoppableWoodType(tile.TileType) :
            tile.TileType == expectedType || strippedGrass);

    private static Item? SelectMiningTool(bool needsAxe) => needsAxe
        ? Loadout.Inventory.Where(item => !item.IsAir && item.axe > 0).OrderByDescending(item => item.axe).FirstOrDefault()
        : Loadout.GetBestPickaxe();

    private static int MiningHitsNeeded(int x, int y, Item tool, bool needsAxe) => needsAxe
        ? Math.Clamp((int)Math.Ceiling(120f / Math.Max(1, tool.axe * 5)), 1, 8)
        : GetHitsNeeded(x, y, tool.pick);

    private bool CanHarvestMiningTile(int x, int y, int? exactType = null)
        => MiningTileMatches(x, y, exactType) && !IsPartOfHouseOrStructure(x, y) && WorldGen.CanKillTile(x, y) &&
            AITeammateSystem.Instance?.AllowsNaturalBossExcavation(currentPlan, x, y) != false;

    private Vector2 Mine(NPC npc, Player player)
    {
        int itemType = target == "wood" ? ItemID.Wood : ItemForTile(mineTileType);
        CollectNearbyDrops(npc, 7f * 16f);
        int quantity = CurrentMiningGained();
        miningBudget.Tick(quantity > miningObservedQuantity ||
            mineHitsDone > miningObservedHits && mineHitsDone <= mineHitsNeeded);
        miningObservedQuantity = quantity;
        miningObservedHits = mineHitsDone;
        if (mineTileType < 0 || itemType <= 0)
        {
            FinishMiningResult(npc, false, "不支持这个采集目标；未选择或挖掘任何候选。");
            return npc.Center;
        }
        if (currentPlan?.X.HasValue != currentPlan?.Y.HasValue ||
            target is "dirt" or "grass" && !IsDirectedMining)
        {
            FinishMiningResult(npc, false, "定点采挖需要同时提供真实 x,y；dirt/grass 必须明确指定自然方块。");
            return npc.Center;
        }
        mineRemaining = count - quantity;
        bool directedSeedRemoved = currentPlan?.X is int seedX && currentPlan.Y is int seedY &&
            miningRemovedTiles.Contains(new Point(seedX, seedY));
        if (mineRemaining <= 0 && (!IsDirectedMining || directedSeedRemoved))
        {
            if (AwaitAutomaticMiningLanding(npc))
            {
                if (miningBudget.Exhausted)
                {
                    miningLastReturn = new("settle_budget_exhausted",
                        $"Existing mining idle budget exhausted while waiting for stable native contact; no return graph was checked from this unsettled pose. actualCenter={npc.Center.X / 16f},{npc.Center.Y / 16f} actualFeet={npc.Bottom.Y / 16f} vy={npc.velocity.Y}")
                        { ObservedAtTick = Main.GameUpdateCount };
                    miningLastReturnPhase = "completion_landing_budget_exhausted";
                    miningRouteFailure = new { reason = "waiting_for_stable_contact_budget_exhausted", phase = miningLastReturnPhase, returnSafety = DescribeMiningReturnSafety() };
                    FinishMiningResult(npc, false, "等待真实落地稳定的本次采集预算已耗尽；已保留实际产物，未从不稳定姿态验证返程，也未证明目标无路。");
                }
                return npc.Center;
            }
            if (!VerifyAutomaticMiningReturnFromActualPose(npc, "completion")) return npc.Center;
            FinishMiningResult(npc, true, "已收集到本任务要求的实际产物。");
            return npc.Center;
        }
        if (miningBudget.Exhausted)
        {
            FinishMiningResult(npc, false, "本次采集预算已耗尽；仅报告已尝试的点/初始源，未证明其它区域不可达。");
            return npc.Center;
        }
        if (CurrentMiningCapacity() <= 0)
        {
            FinishMiningResult(npc, false, "背包没有足够产物空间。");
            return npc.Center;
        }
        if (woodDropWaitTicks-- > 0) { npc.velocity.X *= 0.7f; return npc.Center; }

        if (mineX < 0 || !MiningTileMatches(mineX, mineY))
        {
            if (AwaitAutomaticMiningLanding(npc)) return npc.Center;
            if (!TryChooseMiningCandidate(npc)) return npc.Center;
        }
        if (!CanHarvestMiningTile(mineX, mineY))
        {
            RejectMiningCandidate(npc, "当前候选已不匹配、受保护或原生 CanKillTile 拒绝破坏。");
            return npc.Center;
        }
        bool tree = target == "wood" && !IsTile(mineX, mineY, TileID.LivingWood);
        Item? tool = SelectMiningTool(tree);
        if (tool == null)
        {
            FinishMiningResult(npc, false, tree ? "选中的树需要实际斧头。" : "选中的自然方块需要实际镐子。");
            return npc.Center;
        }
        if (mineHitsNeeded <= 0)
        {
            mineHitsNeeded = MiningHitsNeeded(mineX, mineY, tool, tree);
            if (mineHitsNeeded <= 0)
            {
                RejectMiningCandidate(npc, "当前实际工具无法破坏选中的候选。");
                return npc.Center;
            }
        }
        Vector2 tileCenter = new(mineX * 16f + 8, mineY * 16f + 8);
        float distance = Vector2.Distance(npc.Center, tileCenter);
        if (distance > 5.5f * 16f)
        {
            if (miningStand.HasValue && MiningWorkStandSelection.NeedsAlternative(false, navPath?.Count, navIndex))
            {
                miningStandProbeFailures.Add(new { x = miningStand.Value.X / 16f, y = miningStand.Value.Y / 16f,
                    reason = "route_consumed_but_actual_tool_reach_not_met", actualDistance = distance,
                    actualPosition = new { x = npc.Center.X / 16f, y = npc.Center.Y / 16f } });
                miningStand = null;
                InvalidateRoutePath();
            }
            if (!miningStand.HasValue && !TryFindMiningApproach(npc, tileCenter))
            {
                RejectMiningCandidate(npc, "本种子的近距工位预检未满足；具体区分无合法工位/有界图无路/预算未测见routeFailure；未挖无关通路。");
                return npc.Center;
            }
            Navigate(npc, miningStand!.Value, 4.5f);
            if (completeStepRequested) return tileCenter;
            if (miningApproach.Tick(distance) || navigationFailures >= 4 && navPath == null)
            {
                miningRouteFailure = new { reason = "approach_failed", path = TilePathfinder.LastDiagnostic,
                    stand = new { x = miningStand!.Value.X / 16f, y = miningStand.Value.Y / 16f } };
                RejectMiningCandidate(npc, "本候选的真实接近过程受阻；可查询地形/路线或明确选择其它自然方块开路。");
                return npc.Center;
            }
            return tileCenter;
        }
        miningApproach.Reset();
        if (AwaitAutomaticMiningLanding(npc)) return tileCenter;
        npc.velocity.X *= 0.7f;
        if (--mineCooldown > 0) return tileCenter;
        if (!VerifyAutomaticMiningReturnFromActualPose(npc, "before_tool_hit")) return tileCenter;
        if (tree && mineHitsDone + 1 >= mineHitsNeeded)
        {
            MiningTreeSourceCheck sourceCheck = InspectTreeSource(mineX, mineY, Main.tile[mineX, mineY].TileType);
            if (!sourceCheck.Safe)
            {
                FinishMiningResult(npc, false, sourceCheck.Failure + "：原生断树前复核发现未证明安全的源成员，没有忽略保护格继续砍树。");
                return tileCenter;
            }
            miningObservedSource = sourceCheck.Source;
        }
        mineCooldown = Math.Max(8, tool.useTime);
        Animation.Start(tool, CompanionUseKind.Mine, tool.useAnimation, tileCenter - npc.Center);
        if (tool.UseSound.HasValue) SoundEngine.PlaySound(tool.UseSound, npc.Center);
        else SoundEngine.PlaySound(SoundID.Dig, npc.Center);
        var before = miningObservedSource!.Cells.Where(c => MiningTileMatches(c.X, c.Y))
            .ToDictionary(c => new Point(c.X, c.Y), c => (int)Main.tile[c.X, c.Y].TileType);
        int activeTypeBefore = Main.tile[mineX, mineY].TileType;
        bool hitOnly = ++mineHitsDone < mineHitsNeeded;
        WorldGen.KillTile(mineX, mineY, hitOnly, false, false);
        foreach (var entry in before)
            if (!Main.tile[entry.Key.X, entry.Key.Y].HasTile || Main.tile[entry.Key.X, entry.Key.Y].TileType != entry.Value)
            {
                miningChangedTiles.TryAdd(entry.Key, entry.Value);
                if (!Main.tile[entry.Key.X, entry.Key.Y].HasTile) miningRemovedTiles.Add(entry.Key);
            }
        AITeammateSystem.Instance.TestLog($"mine {(hitOnly ? "hit" : "break")} {mineX},{mineY} target={target} selection={(IsDirectedMining ? "explicit_seed" : "auto")} hits={mineHitsDone}/{mineHitsNeeded}");
        if (!MiningTileMatches(mineX, mineY))
        {
            miningBudget.Tick(true);
            CollectNearbyDrops(npc, 7f * 16f);
            mineX = mineY = -1;
            mineHitsDone = mineHitsNeeded = 0;
            miningStand = null;
            InvalidateRoutePath();
            woodDropWaitTicks = tree ? 90 : 15;
        }
        else if (target == "grass" && activeTypeBefore == TileID.Grass && Main.tile[mineX, mineY].TileType == TileID.Dirt)
        {
            // Native grass stripping leaves a real solid dirt tile. Finish that same
            // selected cell before moving on; never flood into its newly connected soil.
            miningBudget.Tick(true);
            mineHitsDone = mineHitsNeeded = mineAttempts = 0;
        }
        else if (!hitOnly && ++mineAttempts > 4)
            RejectMiningCandidate(npc, "本候选经过真实工具操作仍未被破坏。");
        return tileCenter;
    }

    private bool TryChooseMiningCandidate(NPC npc)
    {
        (int X, int Y)? seed;
        if (IsDirectedMining && miningSource != null)
        {
            if (!miningChangedTiles.ContainsKey(new Point(currentPlan!.X!.Value, currentPlan.Y!.Value)) &&
                !MiningTileMatches(currentPlan.X.Value, currentPlan.Y.Value, miningSourceType))
            {
                FinishMiningResult(npc, false, "指定种子在本同伴成功采挖前已改变；不会跳到其它位置。");
                return false;
            }
            seed = miningSource.Next((x, y) => CanHarvestMiningTile(x, y, miningSourceType) && !unreachableMineTiles.Contains(new Point(x, y)));
            if (!seed.HasValue)
            {
                bool exhausted = !miningSource.Cells.Any(c => MiningTileMatches(c.X, c.Y, miningSourceType));
                FinishMiningResult(npc, false, exhausted ? miningSource.Capped
                    ? "本次有界初始集合已用尽，当前只有部分实际产物；完整资源源范围仍未知，不会自动扩张或换远源。"
                    : "初始指定源已耗尽，当前只有部分实际产物；不会换到远处资源。"
                    : "初始指定源剩余格受保护或已尝试失败；不代表其它资源点不可达。", exhausted);
                return false;
            }
        }
        else
        {
            seed = DirectedMiningSource.SelectSeed(currentPlan?.X, currentPlan?.Y, () =>
            {
                int x, y;
                bool found = target == "wood"
                    ? FindTree(npc, Loadout.Inventory.Any(i => !i.IsAir && i.axe > 0), Loadout.GetBestPickaxe() != null, out x, out y)
                    : FindOre(npc, out x, out y);
                return found ? (x, y) : null;
            });
            if (!seed.HasValue || !CanHarvestMiningTile(seed.Value.X, seed.Value.Y))
            {
                if (seed.HasValue) miningActualCandidates.Add(new Point(seed.Value.X, seed.Value.Y));
                int gainedSoFar = CurrentMiningGained();
                string progress = gainedSoFar > 0 || miningChangedTiles.Count > 0
                    ? $"本次采集任务已有部分进展：实际新增产物 {gainedSoFar}/{count}，已记录改变 {miningChangedTiles.Count} 个资源格；这些实际进展已保留。"
                    : $"本次采集任务当前实际新增产物为 0/{count}，尚未记录资源格改变。";
                FinishMiningResult(npc, false, IsDirectedMining ? "指定格不是可采挖的匹配自然资源，或受保护/原生拒绝破坏；没有换点。"
                    : progress + "本次自动有界选源预检未找到已证明可交互的后续来源；该次只读预检没有新增挖掘，已考虑/未考虑范围见autoSelection。未检查范围仍未知，不能据此断定整个区域资源耗尽。");
                return false;
            }
        }
        mineX = seed.Value.X; mineY = seed.Value.Y;
        miningActualCandidates.Add(new Point(mineX, mineY));
        int tileType = Main.tile[mineX, mineY].TileType;
        MiningTreeSourceCheck? treeCheck = target == "wood" && tileType != TileID.LivingWood
            ? InspectTreeSource(mineX, mineY, tileType) : null;
        miningObservedSource = treeCheck?.Source ?? (IsDirectedMining && miningSource != null ? miningSource :
            DirectedMiningSource.Capture(seed.Value, (x, y) => CanHarvestMiningTile(x, y, tileType)));
        if (treeCheck is { Safe: false })
        {
            FinishMiningResult(npc, false, treeCheck.Failure + "：有界原始树源含保护/原生拒绝成员或超限，不能把过滤成员当作不会连带破坏。");
            return false;
        }
        if (IsDirectedMining && miningSource == null) { miningSource = miningObservedSource; miningSourceType = tileType; }
        if (target == "wood" && tileType != TileID.LivingWood && miningObservedSource.Capped)
        {
            FinishMiningResult(npc, false, "木源超过256格安全观察上限，拒绝触发不可界定的整树连锁破坏。");
            return false;
        }
        mineHitsDone = mineHitsNeeded = mineAttempts = 0;
        miningApproach.Reset();
        miningStand = !IsDirectedMining ? miningAutoSelection?.SelectedApproach?.SelectedStand?.Center : null;
        miningRouteFailure = null;
        miningRouteProbeElapsedMs = !IsDirectedMining ? miningAutoSelectionElapsedMs : 0;
        miningWorkStands.Reset();
        miningStandProbeFailures.Clear();
        InvalidateRoutePath();
        navigationFailures = 0;
        navigationRetryAt = navBuiltAt = 0;
        AITeammateSystem.Instance.TestLog($"mine target {mineX},{mineY} tile={tileType} selection={(IsDirectedMining ? "explicit_seed" : "auto")}");
        return true;
    }

    private bool TryFindMiningApproach(NPC npc, Vector2 tileCenter)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        MiningApproachProbeResult probe = ProbeMiningApproach(npc, tileCenter, miningWorkStands,
            returnAnchor: IsDirectedMining ? null : EnsureMiningReturnAnchor(npc));
        if (!IsDirectedMining) { miningLastReturn = probe.ReturnCheck; miningLastReturnPhase = "work_stand_reprobe"; }
        foreach (MiningApproachFailure failed in probe.Failures)
            miningStandProbeFailures.Add(new { x = failed.Stand.Center.X / 16f, y = failed.Stand.Center.Y / 16f,
                reason = failed.Reason, diagnostic = failed.Diagnostic });
        if (probe.CanApproach)
        {
            miningStand = probe.SelectedStand?.Center ?? npc.Center;
            miningRouteProbeElapsedMs += timer.Elapsed.TotalMilliseconds;
            return true;
        }
        miningRouteFailure = new { reason = probe.Status, testedStandLimit = MiningWorkStandSelection.MaximumProbes,
            testedStandCount = miningWorkStands.ProbeCount, testedStands = miningStandProbeFailures,
            note = "仅已测试的采挖距离内站位；没有把可站立性等同于可达性。" };
        miningRouteProbeElapsedMs += timer.Elapsed.TotalMilliseconds;
        return false;
    }

    private static MiningApproachProbeResult ProbeMiningApproach(NPC observer, Vector2 tileCenter, MiningWorkStandSelection budget,
        MiningRouteProbeCache? shared = null, int maximumProbes = MiningWorkStandSelection.MaximumProbes, MiningReturnAnchor? returnAnchor = null,
        Func<int, int, bool>? isSafeStandOverride = null)
    {
        shared ??= returnAnchor == null ? null : new MiningRouteProbeCache();
        return MiningApproachProbe.Find(observer.Center, observer.height, tileCenter, isSafeStandOverride ?? TilePathfinder.IsStableStand,
            (node, height) => new MiningApproachStand(node, TilePathfinder.StandCenter(node, height), TilePathfinder.StandBottom(node)),
            (start, stand) =>
            {
                MiningApproachRoute Find(Vector2 actualStart, MiningApproachStand destination)
                {
                    List<Point>? route = TilePathfinder.FindPath(actualStart, destination.Center);
                    MiningApproachStand? endpoint = route is { Count: > 0 } ?
                        new(route[^1], TilePathfinder.StandCenter(route[^1], observer.height), TilePathfinder.StandBottom(route[^1])) : null;
                    return new MiningApproachRoute(route?.Count, TilePathfinder.LastDiagnostic, endpoint);
                }
                return shared == null ? Find(start, stand) : shared.GetOrFind(start, stand, Find);
            }, (x, y) => budget.ProbeCount < maximumProbes && (shared == null || shared.CanProbe(new Point(x, y))) && budget.TryProbe(x, y),
            () => budget.ProbeCount, maximumProbes, () => shared?.Exhausted == true,
            returnAnchor == null ? null : from => ProbeAutomaticMiningReturn(observer, from, returnAnchor, shared!));
    }

    /// <summary>Optional on-demand auto-source preview; shares selection with execution and owns only local budgets.</summary>
    public object InspectMiningOptions(NPC observer, string resource)
    {
        resource = (resource ?? "").Trim().ToLowerInvariant();
        int expectedType = TileFor(resource);
        if (expectedType < 0 || resource is "dirt" or "grass")
        {
            string code = expectedType < 0 ? "unsupported_resource" : "requires_seed";
            return new { topic = "mine", source = "live_world", available = false, callSucceeded = true, inputValid = false,
                worldId = Main.worldID, observedAtTick = Main.GameUpdateCount, resource, selectionMode = "auto", error = code,
                canAttempt = false, requiresSeed = expectedType >= 0,
                note = expectedType < 0 ? "不支持的资源分类；没有替换资源或搜索。" : "自然dirt/grass必须成对x/y明确起始格，自动模式不选择开挖地形。",
                worldModified = false, inventoryModified = false, actorsMoved = false, miningExecuted = false };
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        MiningReturnAnchor previewAnchor = CaptureMiningReturnAnchor(observer, preview: true);
        MiningCandidateSelectionResult selection = FindAutomaticMiningOptions(observer, resource, expectedType, returnAnchor: previewAnchor);
        return new
        {
            topic = "mine", source = "live_world", available = true, callSucceeded = true, inputValid = true,
            worldId = Main.worldID, observedAtTick = Main.GameUpdateCount, resource, selectionMode = "auto",
            canAttempt = selection.Selected != null, search = DescribeAutomaticMiningSelection(observer, resource, selection, returnAnchor: previewAnchor),
            probeElapsedMs = timer.Elapsed.TotalMilliseconds,
            worldModified = false, inventoryModified = false, actorsMoved = false, miningExecuted = false,
            note = "只读同一有界auto选择合同；不移动/采掘/换任务。候选或图路未测不等于不可达，工位条件成立不等于实际产出；地形/工具/位置变化后须按真实执行复核。"
        };
    }

    private static object DescribeAutomaticMiningSelection(NPC observer, string resource, MiningCandidateSelectionResult selection,
        int previousFailedSeedsExcluded = 0, MiningReturnAnchor? returnAnchor = null)
    {
        bool wood = resource == "wood";
        int cx = (int)(observer.Center.X / 16f), cy = (int)(observer.Center.Y / 16f);
        int rx = wood ? 52 : 50, ry = wood ? 30 : 32;
        object? chosen = null;
        if (selection.Selected is { } candidate)
        {
            bool axe = wood && candidate.TileType != TileID.LivingWood;
            Item? tool = SelectMiningTool(axe);
            MiningApproachStand? stand = selection.SelectedApproach?.SelectedStand;
            chosen = new { candidateId = $"{resource}:{candidate.Seed.X}:{candidate.Seed.Y}:{candidate.TileType}",
                x = candidate.Seed.X, y = candidate.Seed.Y, actualTileType = candidate.TileType,
                product = DescribeMiningProduct(resource, candidate.TileType),
                tool = new { kind = axe ? "axe" : "pickaxe", available = tool != null, itemId = tool?.type, pickPower = tool?.pick,
                    axePower = tool == null ? (int?)null : tool.axe * 5 },
                approachStatus = selection.SelectedApproach?.Status,
                actualWithinToolRange = selection.SelectedApproach?.ActualInToolReach,
                selectedStand = stand == null ? null : new { x = stand.Node.X, standRow = stand.Node.Y,
                    centerTileX = stand.Center.X / 16f, centerTileY = stand.Center.Y / 16f, feetTileY = stand.FeetY / 16f },
                pathWaypointCount = selection.SelectedApproach?.SelectedRoute?.WaypointCount,
                endpointDistanceToTargetPixels = stand == null ? (float?)null : Vector2.Distance(stand.Center,
                    new Vector2(candidate.Seed.X * 16f + 8, candidate.Seed.Y * 16f + 8)) };
        }
        return new
        {
            status = selection.Status, worldId = Main.worldID, observedAtTick = Main.GameUpdateCount,
            returnSafety = DescribeMiningReturnSafety(returnAnchor,
                selection.SelectedApproach?.ReturnCheck ?? selection.Observations.LastOrDefault(o => o.Approach.ReturnCheck != null)?.Approach.ReturnCheck, "automatic_selection"),
            origin = new { x = observer.Center.X / 16f, y = observer.Center.Y / 16f },
            scanBounds = new { left = Math.Max(3, cx - rx), right = Math.Min(Main.maxTilesX - 4, cx + rx),
                top = Math.Max(3, cy - ry), bottom = Math.Min(Main.maxTilesY - 4, cy + ry) },
            scanBoundsMeaning = "initial_cell_enumeration_window; native_protection_and_tool_checks_read_neighbouring_cells",
            maximumSeedDistanceTiles = wood ? 58 : 55, treeBaseNormalization = wood,
            treeBaseReadScope = wood ? "legacy_single_trunk_downward_normalization_may_read_below_enumeration_window_to_world_interior; resulting_seed_must_still_be_within58_tiles" : null,
            previousFailedSeedsExcluded,
            observedAllowedCandidateCount = selection.ObservedCandidateCount, consideredCandidateCount = selection.ConsideredCandidateCount,
            maximumGeometryCheckedCandidates = MiningCandidateSelection.MaximumGeometryChecks,
            maximumGeometricCandidates = MiningCandidateSelection.MaximumCandidates,
            geometryRejectedCandidateCount = selection.GeometryRejectedCandidateCount,
            geometricCandidateCount = selection.GeometricCandidateCount,
            cheapCheckLimitReached = selection.CheapCheckLimitReached,
            observationSamplesTruncated = selection.ObservationSamplesTruncated,
            unconsideredCandidateCount = Math.Max(0, selection.ObservedCandidateCount - selection.ConsideredCandidateCount),
            candidatesTruncated = selection.CandidatesTruncated, graphRouteProbes = selection.RouteProbeCount,
            maximumGraphRouteProbes = MiningRouteProbeCache.MaximumRouteProbes,
            maximumStandProbesPerSeed = MiningWorkStandSelection.MaximumProbes, chosen,
            candidateSummaries = selection.Observations.Select(observation => new
            {
                candidateId = $"{resource}:{observation.Candidate.Seed.X}:{observation.Candidate.Seed.Y}:{observation.Candidate.TileType}",
                x = observation.Candidate.Seed.X, y = observation.Candidate.Seed.Y, actualTileType = observation.Candidate.TileType,
                status = observation.Approach.Status, geometricWorkStands = observation.Approach.CandidateCount,
                standProbes = observation.Approach.TotalProbeCount, canApproach = observation.Approach.CanApproach,
                returnStatus = observation.Approach.ReturnCheck?.Status ?? "not_checked",
                failureReasons = observation.Approach.Failures.Select(failure => failure.Reason).Distinct().Take(3).ToArray()
            }).ToArray(),
            retry = selection.Selected != null ? "execute_then_verify_actual_yield" : "new_observation_or_explicit_seed_or_changed_conditions_required",
            scope = "same_legacy_enumeration_and_seed_distance; tree_base_normalization_and_native_validation_have_disclosed_extra_reads; up_to512_cheap_geometry_checks_then12_geometrically_eligible_seeds_and32_combined_forward_return_routes_per_selection; fixed_task_origin; untested_is_unknown",
            worldModified = false, actorsMoved = false, miningExecuted = false
        };
    }

    /// <summary>Read-only preflight for one explicitly selected resource seed; never touches the live task or movement state.</summary>
    public object InspectMiningInteraction(NPC observer, string resource, int x, int y)
    {
        resource = (resource ?? "").Trim().ToLowerInvariant();
        int expectedType = TileFor(resource);
        if (expectedType < 0)
            return MiningInspectionInputIssue(resource, x, y, "unsupported_resource", "不支持的资源名；没有自动替换、搜索或选择其它资源源。");
        if (!WorldGen.InWorld(x, y, 3))
            return MiningInspectionInputIssue(resource, x, y, "seed_out_of_world", "指定种子不在实际采矿允许的世界范围内。");

        MiningTerrainCell cell = NavigationWorldObserver.ReadMiningCell(x, y);
        Tile tile = Main.tile[x, y];
        object? treeProduct = resource == "wood" ? TreeProductWorldObserver.Observe(x, y) : null;
        bool targetMatches = MatchesMiningTarget(resource, expectedType, tile);
        bool needsAxe = resource == "wood" && tile.TileType != TileID.LivingWood;
        Item? tool = SelectMiningTool(needsAxe);
        int hitsNeeded = targetMatches && tool != null ? MiningHitsNeeded(x, y, tool, needsAxe) : 0;
        bool canBreakWithTool = tool != null && hitsNeeded > 0;
        bool harvestAllowed = targetMatches && !cell.StructureProtected && cell.NativeCanKill;
        int productItem = resource == "wood" ? -1 : ItemForTile(expectedType);
        int capacity = resource == "wood" ? AvailableWoodCapacity() : Loadout.CountFreeCapacity(productItem);

        // Actual wood mining refuses an unsafe uncapped native tree cascade. This is
        // another local snapshot, never the active task's selected/changed source.
        DirectedMiningSource? observedTreeSource = null;
        MiningTreeSourceCheck? treeSourceCheck = null;
        if (harvestAllowed && canBreakWithTool && needsAxe && capacity > 0)
        {
            int originalType = tile.TileType;
            treeSourceCheck = InspectTreeSource(x, y, originalType);
            observedTreeSource = treeSourceCheck.Source;
        }
        bool treeCascadeBlocked = treeSourceCheck is { Safe: false };
        bool prerequisitesMet = harvestAllowed && canBreakWithTool && capacity > 0 && !treeCascadeBlocked;
        Vector2 tileCenter = new(x * 16f + 8f, y * 16f + 8f);
        float actualDistance = Vector2.Distance(observer.Center, tileCenter);
        var localBudget = new MiningWorkStandSelection();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        MiningApproachProbeResult probe = prerequisitesMet ? ProbeMiningApproach(observer, tileCenter, localBudget) :
            new("not_probed_preconditions", actualDistance <= MiningApproachProbe.ToolReachPixels, actualDistance,
                0, 0, 0, null, null, Array.Empty<MiningApproachFailure>());
        double elapsedMs = timer.Elapsed.TotalMilliseconds;
        MiningApproachStand? selected = probe.SelectedStand;
        var reasons = new List<string>();
        if (!targetMatches) reasons.Add("seed_does_not_match_requested_resource_or_is_actuated");
        if (cell.StructureProtected) reasons.Add("seed_is_structure_protected");
        if (!cell.NativeCanKill) reasons.Add("native_can_kill_tile_refused");
        if (tool == null) reasons.Add(needsAxe ? "no_actual_axe" : "no_actual_pickaxe");
        else if (targetMatches && !canBreakWithTool) reasons.Add("actual_tool_cannot_break_seed");
        if (capacity <= 0) reasons.Add("no_current_inventory_capacity");
        if (treeCascadeBlocked) reasons.Add(treeSourceCheck!.Failure!);
        if (prerequisitesMet && !probe.CanApproach) reasons.Add(probe.Status);

        return new
        {
            topic = "mine", source = "live_world", available = true, callSucceeded = true, inputValid = true,
            worldId = Main.worldID, observedAtTick = Main.GameUpdateCount, resource,
            requestedSeed = new { x, y },
            returnSafety = DescribeMiningReturnSafety(null, null, "explicit_readonly_preview", explicitSelection: true),
            seed = new { x, y, hasTile = cell.HasTile, actualTileType = cell.ActualTileType, resourceTarget = cell.ResourceTarget,
                targetMatches, actuated = cell.Actuated, structureProtected = cell.StructureProtected, nativeCanKill = cell.NativeCanKill },
            tool = new { kind = needsAxe ? "axe" : "pickaxe", available = tool != null, itemId = tool?.type,
                name = tool?.Name, pickPower = tool?.pick, axePower = tool == null ? (int?)null : tool.axe * 5,
                hitsNeeded, canBreak = canBreakWithTool, scope = "same_current_inventory_tool_selection_and_native_pick_power_check_as_mine" },
            product = DescribeMiningProduct(resource, expectedType),
            treeProduct,
            sourceSafety = new { checkedTreeCascade = observedTreeSource != null, initialSourceCells = observedTreeSource?.Cells.Count,
                maximumInitialCells = DirectedMiningSource.MaximumCells, treeCascadeBlocked, treeCascadeIssue = treeSourceCheck?.Failure },
            actualDistancePixels = actualDistance, actualDistanceTiles = actualDistance / 16f,
            actualWithinToolRange = actualDistance <= MiningApproachProbe.ToolReachPixels, maximumToolRangePixels = MiningApproachProbe.ToolReachPixels,
            approach = new
            {
                status = probe.Status,
                selectedStand = selected == null ? null : new { x = selected.Node.X, standRow = selected.Node.Y,
                    centerTileX = selected.Center.X / 16f, centerTileY = selected.Center.Y / 16f, feetTileY = selected.FeetY / 16f },
                geometricCandidateCount = probe.CandidateCount, routeProbeCount = probe.TotalProbeCount, newRouteProbeCount = probe.NewProbeCount,
                maximumRouteProbes = MiningWorkStandSelection.MaximumProbes,
                selectedDistanceToTargetPixels = selected == null ? (float?)null : Vector2.Distance(selected.Center, tileCenter),
                pathWaypointCount = probe.SelectedRoute?.WaypointCount, diagnostic = probe.SelectedRoute?.Diagnostic,
                routeEndpoint = probe.SelectedRoute?.Endpoint is not { } endpoint ? null : new { x = endpoint.Node.X,
                    standRow = endpoint.Node.Y, centerTileX = endpoint.Center.X / 16f, centerTileY = endpoint.Center.Y / 16f,
                    distanceToTargetPixels = Vector2.Distance(endpoint.Center, tileCenter) },
                routeSearchPerformed = probe.NewProbeCount > 0, currentActualRangeNeedsNoRoute = probe.ActualInToolReach,
                failedStandProbes = probe.Failures.Select(failed => new { x = failed.Stand.Node.X, standRow = failed.Stand.Node.Y,
                    centerTileX = failed.Stand.Center.X / 16f, centerTileY = failed.Stand.Center.Y / 16f,
                    reason = failed.Reason, diagnostic = failed.Diagnostic }).ToArray(),
                probeElapsedMs = elapsedMs, travelExecuted = false,
                scope = "this_seed_work_stands_x_plus_minus_6_y_plus_minus_7_and_current_bounded_graph_only"
            },
            canAttempt = prerequisitesMet && probe.CanApproach, reasons,
            worldModified = false, inventoryModified = false, actorsMoved = false, miningExecuted = false,
            note = "这是单个明确资源格的只读采挖预检，复用实际Mine的资源匹配、保护、工具和近距工位探测。canAttempt只表示当前前置条件与距离/工位图条件具备，不表示已经走到、操作工具、获得掉落或完成数量；实际Mine结果才是产出证据。没有自动换源、采集地上物品、挖通道或改变正在执行的任务。负结论仅限本种子及本次有界候选/图探测，不能推广为整个方向或世界不能采。"
        };
    }

    private static object MiningInspectionInputIssue(string resource, int x, int y, string code, string message) => new
    {
        topic = "mine", source = "live_world", available = false, callSucceeded = true, inputValid = false,
        worldId = Main.worldID, observedAtTick = Main.GameUpdateCount, resource, requestedSeed = new { x, y },
        error = code, issues = new[] { new { code, severity = "error", message } }, canAttempt = false,
        worldModified = false, inventoryModified = false, actorsMoved = false, miningExecuted = false
    };

    private void RejectMiningCandidate(NPC npc, string reason)
    {
        var rejected = new Point(mineX, mineY);
        if (unreachableMineTiles.Add(rejected)) miningBudget.RejectCandidate();
        AITeammateSystem.Instance.TestLog($"mining candidate rejected {rejected.X},{rejected.Y}: {reason}");
        if (IsDirectedMining || miningBudget.Exhausted) FinishMiningResult(npc, false, reason);
        mineX = mineY = -1;
        mineHitsDone = mineHitsNeeded = mineAttempts = 0;
        miningApproach.Reset();
        miningStand = null;
        InvalidateRoutePath();
    }

    private void FinishMiningResult(NPC npc, bool success, string reason, bool sourceExhausted = false)
    {
        int itemType = target == "wood" ? ItemID.Wood : ItemForTile(mineTileType);
        int gained = CurrentMiningGained();
        IReadOnlyList<MiningYieldAmount> amounts = target == "wood"
            ? miningWoodYield?.Gains(MiningInventoryCounts()) ?? Array.Empty<MiningYieldAmount>()
            : new[] { new MiningYieldAmount(itemType, gained) };
        var actualYields = amounts.Select(row => new { itemType = row.ItemType, name = Lang.GetItemNameValue(row.ItemType), amount = row.Amount }).ToArray();
        DirectedMiningSource? evidenceSource = miningSource ?? miningObservedSource;
        object? seedObservation = null;
        if (currentPlan?.X is int requestedX && currentPlan.Y is int requestedY)
        {
            if (!WorldGen.InWorld(requestedX, requestedY, 2))
                seedObservation = new { x = requestedX, y = requestedY, available = false, error = "requested_seed_out_of_world" };
            else
            {
                var cell = NavigationWorldObserver.ReadMiningCell(requestedX, requestedY);
                seedObservation = new { x = requestedX, y = requestedY, available = true, hasTile = cell.HasTile, actualTile = cell.ActualTileType,
                    resourceTarget = cell.ResourceTarget, actuated = cell.Actuated,
                    targetMatches = MiningTileMatches(requestedX, requestedY), structureProtected = cell.StructureProtected,
                    nativeCanKill = cell.NativeCanKill };
            }
        }
        FinishCurrentStep(JsonSerializer.Serialize(new
        {
            kind = success ? "mining_result" : "mining_blocked", success, target, requestedCount = count, reason,
            selectionMode = IsDirectedMining ? "explicit_seed" : "auto",
            nextSourceSearch = !success && gained == 0 && mineTileType >= 0 && target is not ("dirt" or "grass")
                ? new { type = "inspect", target = "sources", item = $"{target};limit=8;maxDistance={MiningSourceRefinementRequest.NearbyDiscoveryDistance}" }
                : null,
            requestedSeed = IsDirectedMining ? new { x = currentPlan?.X, y = currentPlan?.Y } : null,
            seedObservation,
            actualCandidates = miningActualCandidates.Take(64).Select(p => new { x = p.X, y = p.Y }).ToArray(),
            actualCandidatesTotal = miningActualCandidates.Count, actualCandidatesTruncated = miningActualCandidates.Count > 64,
            changedTiles = miningChangedTiles.Take(64).Select(p => new { x = p.Key.X, y = p.Key.Y, beforeType = p.Value,
                removed = miningRemovedTiles.Contains(p.Key),
                afterType = Main.tile[p.Key.X, p.Key.Y].HasTile ? (int?)Main.tile[p.Key.X, p.Key.Y].TileType : null }).ToArray(),
            changedTilesTotal = miningChangedTiles.Count, changedTilesTruncated = miningChangedTiles.Count > 64,
            removedTiles = miningRemovedTiles.Take(64).Select(p => new { x = p.X, y = p.Y }).ToArray(),
            removedTilesTotal = miningRemovedTiles.Count, removedTilesTruncated = miningRemovedTiles.Count > 64,
            actualYield = actualYields.Length == 1 ? actualYields[0] : null, actualYields, actualGained = gained,
            yieldAccounting = new { family = target == "wood" ? "native_wood_family" : "single_native_product",
                woodFamilyDefinition = target == "wood" ? miningWoodFamilyDefinition : null,
                scope = target == "wood" ? "sum_of_positive_per_item_inventory_delta_capped_by_actual_ground_pickups_during_this_Mine; prior_inventory_and_direct_transfers_excluded" : "inventory_delta_since_this_Mine_started",
                nearbyPreexistingGroundDropsMayBeIncluded = true,
                everyDropIndependentlyProvenFromThisActionsCuts = false,
                note = "采集中会拾取7格内地上物，可能包括先前遗留掉落；没有逐件证明每份都由本次砍挖生成。木材族成员不保证可在某一具体配方中互换，请按实际itemType查询配方。" },
            sourceExhausted, sourceCapped = miningSource?.Capped ?? miningObservedSource?.Capped ?? false,
            returnSafety = DescribeMiningReturnSafety(),
            autoSelection = IsDirectedMining ? null : miningAutoSelectionReport,
            initialSource = evidenceSource == null ? null : new
            {
                count = evidenceSource.Cells.Count, capped = evidenceSource.Capped,
                bounds = evidenceSource.Cells.Count == 0 ? null : new { left = evidenceSource.Cells.Min(c => c.X),
                    right = evidenceSource.Cells.Max(c => c.X), top = evidenceSource.Cells.Min(c => c.Y), bottom = evidenceSource.Cells.Max(c => c.Y) },
                sample = evidenceSource.Cells.Take(32).Select(c => new { x = c.X, y = c.Y }).ToArray(),
                truncated = evidenceSource.Cells.Count > 32
            },
            routeFailure = miningRouteFailure, routeProbeElapsedMs = miningRouteProbeElapsedMs,
            routeStandProbes = miningWorkStands.ProbeCount, routeStandObservations = miningStandProbeFailures,
            actualPosition = new { x = npc.Center.X / 16f, y = npc.Center.Y / 16f },
            failedCandidates = miningBudget.FailedCandidates, idleTicks = miningBudget.IdleTicks,
            note = "只挖明确选中的资源格；没有自动挖路线、传送或测试全部区域。实际产物来自背包增量；变化格为工具操作前后在初始源内观察到的变化。"
        }), success);
    }

    private static int GetHitsNeeded(int x, int y, int pickPower)
    {
        if (!WorldGen.InWorld(x, y, 2))
            return 0;

        Tile tile = Main.tile[x, y];
        if (!tile.HasTile)
            return 0;

        int damage = Math.Max(1, pickPower);
        TileLoader.PickPowerCheck(tile, pickPower, ref damage);
        if (damage <= 0)
            return 0;

        int hardness = TileHardness(tile.TileType);
        return Math.Clamp((int)Math.Ceiling(hardness / (float)damage), 1, 8);
    }

    private static int TileHardness(int type)
    {
        if (Main.tileDungeon[type])
            return 220;

        return type switch
        {
            TileID.Dirt or TileID.Mud or TileID.ClayBlock or TileID.Sand or TileID.SnowBlock or
                TileID.Slush or TileID.Silt or TileID.Ash or TileID.Grass or TileID.JungleGrass or
                TileID.CorruptGrass or TileID.CrimsonGrass or TileID.HallowedGrass or
                TileID.MushroomGrass => 55,
            TileID.WoodBlock or TileID.BorealWood or TileID.RichMahogany or TileID.Ebonwood or
                TileID.Shadewood or TileID.Pearlwood or TileID.PalmWood => 80,
            TileID.Ebonstone or TileID.Crimstone or TileID.Pearlstone or TileID.Meteorite or
                TileID.Hellstone or TileID.Cobalt or TileID.Palladium or TileID.Mythril or
                TileID.Orichalcum or TileID.Adamantite or TileID.Titanium => 220,
            _ => 110
        };
    }

    private static void SpawnMiningDust(int x, int y)
    {
        for (int i = 0; i < 4; i++)
        {
            Dust dust = Dust.NewDustDirect(new Vector2(x * 16, y * 16), 16, 16, DustID.Stone,
                Main.rand.NextFloat(-1.2f, 1.2f), Main.rand.NextFloat(-1.2f, 0.2f), 100, default, 0.8f);
            dust.noGravity = Main.rand.NextBool(2);
        }
    }

    private int CollectNearbyDrops(NPC npc, float range)
    {
        int collected = 0;

        for (int i = 0; i < Main.maxItems; i++)
        {
            Item item = Main.item[i];
            if (!item.active || item.IsAir || item.stack <= 0)
                continue;

            if (Vector2.Distance(item.Center, npc.Center) > range)
                continue;

            int added = Loadout.AddItemStack(item.type, item.stack);
            if (added <= 0)
                continue;

            if (action == AIActionType.Mine && target == "wood") miningWoodYield?.RecordGroundPickup(item.type, added);

            collected += added;
            item.stack -= added;
            if (item.stack <= 0)
                item.active = false;
        }

        return collected;
    }

    private static bool TryFindStandNear(Vector2 center, out Vector2 stand)
    {
        int cx = (int)(center.X / 16f);
        int cy = (int)(center.Y / 16f);
        float best = float.MaxValue;
        stand = center;

        for (int x = cx - 5; x <= cx + 5; x++)
        {
            for (int y = cy - 4; y <= cy + 5; y++)
            {
                if (!IsSafeStanding(x, y))
                    continue;

                Vector2 candidate = TilePathfinder.StandCenter(new Point(x, y));
                float distance = Vector2.Distance(candidate, center);
                if (distance < best)
                {
                    best = distance;
                    stand = candidate;
                }
            }
        }

        return best < float.MaxValue;
    }

    private void BlockGoal(string reason)
    {
        if (currentPlan == null)
            return;

        blockedPlan = new AIPlan
        {
            Action = currentPlan.Action,
            Blueprint = blueprint,
            Target = target,
            Item = itemQuery,
            Count = count,
            X = currentPlan.X,
            Y = currentPlan.Y,
            Width = currentPlan.Width,
            Height = currentPlan.Height,
            Slot = currentPlan.Slot,
            RecipeId = currentPlan.RecipeId,
            Reason = currentPlan.Reason,
            SuccessCriteria = currentPlan.SuccessCriteria,
            PlanStep = currentPlan.PlanStep,
            Design = currentPlan.Design?.Clone()
        };
        blockedReason = reason;
        blockedThisStep = true;
        AITeammateSystem.Instance.TestLog("blocked goal: " + reason);
    }

    private static bool TakeMaterial(Player player, CompanionLoadout loadout, int itemType, int amount)
    {
        return loadout.RemoveItem(itemType, amount);
    }

    private static int CountMaterial(Player player, CompanionLoadout loadout, int itemType)
    {
        return loadout.CountItem(itemType);
    }

    private GoalConstructionProtection? shelterSelectionProtection;

    private static GoalConstructionProtection InspectShelterFootprint(int left, int top)
        => AITeammateSystem.Instance.InspectConstructionProtection(left, top, CompanionConstruction.Width, CompanionConstruction.Height);

    private static bool ShelterProtectionAllows(GoalConstructionProtection protection)
        => ShelterSiteProbe.AllowsConstruction(protection.Required, protection.Available, protection.Covered, protection.Allowed);

    private static object? ShelterProtectionRegion(GoalProtectionRegion? region)
        => region == null ? null : new { x = region.X, y = region.Y, width = region.Width, height = region.Height };

    private static object DescribeShelterProtection(GoalConstructionProtection protection, string? baselineReference = null)
    {
        var facts = new Dictionary<string, object?>
        {
            ["required"] = protection.Required, ["available"] = protection.Available, ["covered"] = protection.Covered,
            ["allowed"] = protection.Allowed, ["protectedCellCount"] = protection.ProtectedCellCount,
            ["protectedHits"] = protection.ProtectedHits.Select(hit => new { x = hit.X, y = hit.Y, reason = hit.Reason }).ToArray(),
            ["protectedHitsOmitted"] = protection.ProtectedHitsOmitted, ["reason"] = protection.Reason,
            ["region"] = ShelterProtectionRegion(protection.Region), ["observedAtTick"] = protection.ObservedAtTick,
            ["baseline_id"] = protection.Baseline?.baseline_id
        };
        if (baselineReference != null) facts["baseline_ref"] = baselineReference;
        else { facts["baseline"] = protection.Baseline; facts["sampleBounds"] = ShelterProtectionRegion(protection.SampleBounds); }
        return facts;
    }

    private ShelterSiteProbe CreateShelterSiteProbe()
        => new(CompanionConstruction.Width, CompanionConstruction.Height,
            (x, y) => WorldGen.InWorld(x, y, 3),
            (x, y) =>
            {
                Tile tile = Main.tile[x, y];
                return new SiteCell(tile.HasTile, tile.HasTile ? tile.TileType : 0,
                    Solid: tile.HasTile && Main.tileSolid[tile.TileType], LiquidAmount: tile.LiquidAmount);
            },
            (x, y) => IsReplaceableGroundPlant(Main.tile[x, y]),
            (left, top, width, height) =>
            {
                Rectangle footprint = new(left * 16, top * 16, width * 16, height * 16);
                return Main.player.Any(actor => actor.active && !actor.dead && actor.Hitbox.Intersects(footprint)) ||
                    Main.npc.Any(actor => actor.active && actor.Hitbox.Intersects(footprint));
            });

    private object[] DescribeShelterSelfCosts(IReadOnlyDictionary<int, int> costs)
        => costs.Select(pair => (object)new
        {
            id = pair.Key, name = Lang.GetItemNameValue(pair.Key), required = pair.Value,
            ownedSelf = Loadout.CountItem(pair.Key), missing = Math.Max(0, pair.Value - Loadout.CountItem(pair.Key))
        }).ToArray();

    /// <summary>Only observes bounded shelter predicates and self inventory. Never dispatches or edits a draft.</summary>
    public object InspectShelterBuild(NPC observer, string query = "shelter", int? x = null, int? y = null)
    {
        if (x.HasValue != y.HasValue)
            return new { topic = "build", available = false, error = "Shelter x/y must be paired.", worldModified = false, inventoryModified = false, actorsMoved = false };
        ShelterSiteScan scan = CreateShelterSiteProbe().Inspect((int)(observer.Center.X / 16f),
            (int)(observer.Bottom.Y / 16f), observer.direction, x, y);
        var checkedCandidates = scan.candidates.Select(geometry => new
        {
            geometry,
            protection = geometry.resolvedTopLeft == null ? null : InspectShelterFootprint(geometry.resolvedTopLeft.x, geometry.resolvedTopLeft.y)
        }).ToArray();
        var baselineContexts = checkedCandidates.Where(candidate => candidate.protection != null)
            .Select(candidate => (candidate.protection!.Baseline, candidate.protection!.SampleBounds)).Distinct().ToArray();
        int allowedCandidateCount = checkedCandidates.Count(candidate => candidate.geometry.status == "geometry_passed" &&
            candidate.protection != null && ShelterProtectionAllows(candidate.protection));
        var candidates = checkedCandidates.Select(candidate => new
        {
            candidate.geometry.centerX, candidate.geometry.feetReferenceY, candidate.geometry.status,
            geometryStatus = candidate.geometry.status, candidate.geometry.groundY, candidate.geometry.resolvedTopLeft,
            candidate.geometry.blockingCell, candidate.geometry.blockingTileType, candidate.geometry.actorIntersects,
            protection = candidate.protection == null ? null : DescribeShelterProtection(candidate.protection,
                "#/siteSearch/protectionBaselines/" + Array.IndexOf(baselineContexts, (candidate.protection.Baseline, candidate.protection.SampleBounds))),
            protectionStatus = candidate.protection == null ? "not_evaluated_no_resolved_footprint" : candidate.protection.Reason,
            allowedByCurrentChecks = candidate.geometry.status == "geometry_passed" && candidate.protection != null && ShelterProtectionAllows(candidate.protection)
        }).ToArray();
        bool upperMode = !x.HasValue && WantsUpperFloor();
        string requestedMaterial = (query ?? "").Trim().ToLowerInvariant();
        return new
        {
            topic = "build", name = "shelter", source = "predefined_template", observedFromWorld = false, available = true,
            worldId = Main.worldID, worldLoadEpoch = WorldKnowledgeObserver.LoadEpoch, observedAtTick = Main.GameUpdateCount,
            width = CompanionConstruction.Width, height = CompanionConstruction.Height,
            requestedMaterial = requestedMaterial is "wood" or "stone" or "ebonwood" ? requestedMaterial : null,
            materialCostScope = "full_predefined_template_self_inventory_only_not_remaining_work_or_player_inventory",
            options = new[] { "wood", "stone", "ebonwood" }.Select(material => new
            {
                material, items = DescribeShelterSelfCosts(CompanionConstruction.ShelterMaterials(material))
            }).ToArray(),
            actor = new { centerTileX = observer.Center.X / 16f, centerTileY = observer.Center.Y / 16f,
                feetTileY = observer.Bottom.Y / 16f, direction = observer.direction },
            siteSearch = new
            {
                source = "live_world", scan.mode, scan.requestedTopLeft, scan.origin, scan.groundScan,
                scan.requestedReadBounds, candidates, scan.candidateCount, scan.acceptedCandidateCount,
                acceptedCandidateCountMeaning = "geometry_only; allowedCandidateCount_also_checks_the_current_goal_original_mask",
                allowedCandidateCount,
                protectionBaselines = baselineContexts.Select(context => new { baseline = context.Baseline, sampleBounds = ShelterProtectionRegion(context.SampleBounds) }).ToArray(),
                protectionReferenceMeaning = "baseline_ref is local to this preflight result. Identity and sample bounds are shared only when the entire immutable identity and bounds are equal; per-region unknown/hits remain explicit. No result is a whole-goal preservation proof.",
                upperFloorModeCovered = !upperMode, candidatesAreFallbackOnly = upperMode,
                uncoveredMode = upperMode ? "existing_roof_preferred_branch_not_inspected" : null,
                boundedStatus = allowedCandidateCount > 0 ? "candidates_passed_geometry_and_current_protection_checks" :
                    scan.acceptedCandidateCount > 0 ? "geometry_exists_but_no_protection_allowed_candidate_in_this_scan" :
                    upperMode ? "ordinary_fallback_empty_upper_floor_unknown" : "no_geometric_candidate_in_this_bounded_scan",
                scan.scope,
                coordinateMeaning = "requestedTopLeft uses build x/y. The existing skill searches for ground and may resolve a different top, from requested y-3 through y+13. resolvedTopLeft is a measured candidate, not a promised exact placement. Auto scan uses this actor's current feet, including underground floors."
            },
            reachability = "not_tested", builderStand = "not_tested", nativePlacement = "not_tested", nativeHousing = "not_tested",
            structurePreserved = "not_tested", newHousing = "not_tested",
            worldModified = false, inventoryModified = false, actorsMoved = false, constructionExecuted = false,
            note = "模板尺寸与成本不是原屋测量。这里只读列出与现有build相同的有界候选，不选择或执行任务、不找路、不批准拆旧屋。geometryStatus独立保留几何事实；当前合同含structure_preserved时，整10×7区域必须有同一目标原mask完整覆盖且无保护命中，unknown不能当safe。原mask不会因本目标施工重拍，mask查询不证明全目标保留、原生施工/住房/新房或外部通路已通过。缺料只看小星背包；即使本次无候选也保留缺额。已有施工的实际剩余材料应看施工进度/失败回执。"
        };
    }

    private void FinishShelterBuildFailure(NPC observer, string material, string status, string reason, GoalConstructionProtection? protection = null)
    {
        var remaining = buildSteps?.Where(step => !ConstructionStepPresent(step)).GroupBy(step => step.ItemType)
            .ToDictionary(group => group.Key, group => group.Count());
        string report = JsonSerializer.Serialize(new
        {
            kind = "shelter_build_result", success = false, status, reason, material,
            protection = protection == null ? null : DescribeShelterProtection(protection),
            preflight = InspectShelterBuild(observer, material, currentPlan?.X, currentPlan?.Y),
            currentLayout = buildSteps == null ? null : new
            {
                left = buildSteps.Min(step => step.X), top = buildSteps.Min(step => step.Y),
                accountedPlacementSteps = buildIndex, plannedPlacementSteps = buildSteps.Count,
                note = "Step counts may include pre-existing matching tiles; they are not a new-world-mutation count. Current layout is separate from the fresh candidate scan."
            },
            remainingItems = remaining == null ? null : DescribeShelterSelfCosts(remaining)
        });
        FinishCurrentStep(report, false);
    }

    private Vector2 Build(NPC npc, Player player)
    {
        if (blueprint != "shelter")
        {
            if (!Policy.AllowBlueprintWorldEditing)
            {
                FinishCurrentStep("policy_denied: 编辑器式蓝图复刻未启用；请使用原生place技能施工。", false);
                return npc.Center;
            }
            Blueprint? saved = BlueprintManager.Load(blueprint);
            if (saved == null)
            {
                FinishCurrentStep($"没有叫“{blueprint}”的蓝图。", false);
                return player.Center;
            }
            return BuildBlueprint(npc, player, saved);
        }

        if (target is not ("" or "wood" or "stone" or "ebonwood"))
        {
            FinishShelterBuildFailure(npc, target, "unsupported_material", $"小屋材料支持 wood、stone 或 ebonwood，收到 {target}。");
            return npc.Center;
        }
        string material = string.IsNullOrEmpty(target) ? "wood" : target;
        if (buildSteps == null)
        {
            var palette = CompanionConstruction.ShelterLayout(0, 0, material);
            buildBlockType = palette[0].Tile;
            buildWallType = palette.First(step => step.Wall > 0).Wall;
            buildMaterialType = palette[0].ItemType;
            bool reuseDraft = shelterDraft != null && shelterDraftMaterial == material && currentPlan?.X == null;
            if (reuseDraft)
            {
                List<BuildStep> draft = shelterDraft!;
                GoalConstructionProtection draftProtection = InspectShelterFootprint(draft.Min(step => step.X), draft.Min(step => step.Y));
                if (!ShelterProtectionAllows(draftProtection))
                {
                    FinishShelterBuildFailure(npc, material, "construction_protection_blocked", "原施工草案整10×7范围未通过当前目标原mask保护检查；没有恢复或写入，请依据protection与预检重选。", draftProtection);
                    return npc.Center;
                }
            }
            buildSteps = reuseDraft ? shelterDraft : PlanBuild(npc, player);
            if (buildSteps == null)
            {
                GoalConstructionProtection? rejectedProtection = reuseDraft ? null : shelterSelectionProtection;
                FinishShelterBuildFailure(npc, material, rejectedProtection == null ? "no_site_in_bounded_search" : "construction_protection_blocked",
                    "本次有界候选未找到可采用的10×7场地；geometryStatus与原mask保护结果分别保留，请核对preflight.siteSearch范围及protection，未检查区域未知。", rejectedProtection);
                return player.Center;
            }
            shelterDraft = buildSteps;
            shelterDraftMaterial = material;
            var missing = buildSteps.Where(step => !ConstructionStepPresent(step))
                .GroupBy(step => step.ItemType).Select(group => new { Key = group.Key, Value = group.Count() })
                .Where(pair => CountMaterial(player, Loadout, pair.Key) < pair.Value)
                .Select(pair => $"{Lang.GetItemNameValue(pair.Key)}x{pair.Value - CountMaterial(player, Loadout, pair.Key)}")
                .ToArray();
            if (missing.Length > 0)
            {
                FinishShelterBuildFailure(npc, material, "missing_self_materials", "建屋缺少实际材料：" + string.Join("、", missing) + "。请查询建屋清单和配方后补齐。");
                return npc.Center;
            }
            buildIndex = 0;
        }

        int siteLeft = buildSteps.Min(step => step.X);
        int siteTop = buildSteps.Min(step => step.Y);
        (int standX, int standY) = CompanionConstruction.BuilderStand(siteLeft, siteTop);
        if (!(WorldGen.InWorld(standX, standY, 3) && Main.tile[standX, standY].HasTile && Main.tileSolid[Main.tile[standX, standY].TileType]) &&
            TryFindGround(standX, standY - 2, out int standGround) && Math.Abs(standGround - standY) <= 16)
            standY = standGround;
        Vector2 site = new(standX * 16f + 8, standY * 16f - npc.height / 2f);
        Rectangle footprint = new(siteLeft * 16, siteTop * 16, CompanionConstruction.Width * 16, CompanionConstruction.Height * 16);
        bool inReach = !npc.Hitbox.Intersects(footprint) &&
            (Math.Abs(npc.Center.X - site.X) <= 8f ||
             Vector2.Distance(npc.Center, footprint.Center.ToVector2()) <= 9f * 16f && IsGrounded(npc));
        if (!inReach)
        {
            if (Vector2.Distance(npc.Center, site) > 6f * 16f)
                Navigate(npc, site, 4.5f);
            else
                MoveTowards(npc, site, 2f, 2f);
            return site;
        }
        npc.velocity.X = 0f;
        if (--buildCooldown > 0)
            return site;

        BuildStep step = buildSteps[buildIndex];
        bool alreadyPlaced = ConstructionStepPresent(step);
        if (!alreadyPlaced)
        {
            // The bridge revalidates world/goal identity and the same immutable mask
            // immediately before any native write; multi-cell parts are inside this full envelope.
            GoalConstructionProtection writeProtection = InspectShelterFootprint(siteLeft, siteTop);
            if (!ShelterProtectionAllows(writeProtection))
            {
                FinishShelterBuildFailure(npc, material, "construction_protection_blocked", "待写入前整10×7范围未通过当前目标原mask保护检查；未执行本步写入或扣料。已做部分不豁免待写保护。", writeProtection);
                return site;
            }
        }
        if (!alreadyPlaced && CountMaterial(player, Loadout, step.ItemType) <= 0)
        {
            FinishShelterBuildFailure(npc, material, "missing_self_material_during_construction", $"施工中缺少 {Lang.GetItemNameValue(step.ItemType)}，已放置的部分保留，需要补料。");
            return site;
        }
        bool placed = alreadyPlaced;
        if (!placed && step.Wall > 0)
        {
            if (Main.tile[step.X, step.Y].WallType == WallID.None)
                WorldGen.PlaceWall(step.X, step.Y, step.Wall, true);
            placed = Main.tile[step.X, step.Y].WallType == step.Wall;
        }
        else if (!placed)
        {
            placed = TryPlaceFurniture(step.X, step.Y, step.Tile, step.Style);
        }
        if (!placed)
        {
            Tile failedTile = Main.tile[step.X, step.Y];
            AITeammateSystem.Instance.TestLog($"placement rejected x={step.X} y={step.Y} wanted={step.Tile} occupied={failedTile.HasTile}/{failedTile.TileType} " +
                $"liquid={failedTile.LiquidAmount} actor={npc.Hitbox.Intersects(new Rectangle(step.X * 16, step.Y * 16, 16, 16))} " +
                $"player={player.Hitbox.Intersects(new Rectangle(step.X * 16, step.Y * 16, 16, 16))} objectData={TileObjectData.GetTileData(step.Tile, step.Style) != null} " +
                $"center={npc.Center.X / 16f:0.00},{npc.Center.Y / 16f:0.00} stand={standX},{standY} site={siteLeft},{siteTop}");
            FinishShelterBuildFailure(npc, material, "native_placement_failed", $"施工位置 ({step.X},{step.Y}) 无法合法放置 {Lang.GetItemNameValue(step.ItemType)}。已完成 {buildIndex}/{buildSteps.Count}，材料只扣已放置部分。");
            return site;
        }
        if (!alreadyPlaced)
            TakeMaterial(player, Loadout, step.ItemType, 1);
        buildIndex++;
        buildCooldown = 4;
        if (buildIndex < buildSteps.Count)
            return site;

        bool verified = buildSteps.All(ConstructionStepPresent);
        if (!verified)
        {
            FinishShelterBuildFailure(npc, material, "construction_state_verification_failed", "施工结束复核失败，有方块或家具已被破坏；尚不能确认小屋完成。");
            return site;
        }
        int left = buildSteps.Min(s => s.X);
        int top = buildSteps.Min(s => s.Y);
        bool roomValid = WorldGen.StartRoomCheck(left + 5, top + 3) && WorldGen.RoomNeeds(-1);
        if (!roomValid)
        {
            FinishShelterBuildFailure(npc, material, "native_housing_failed", $"小屋部件实际存在，但游戏住房判定未通过，左上角 ({left},{top})；不能当作可住房屋完成。");
            return site;
        }
        FinishCurrentStep($"小屋施工并复核完成，左上角 ({left},{top})，10x7；方块、40格背景墙、门、工作台、椅子和火把全部实际存在，且游戏住房判定通过。 ");
        buildSteps = null;
        shelterDraft = null;
        return site;
    }

    private static bool ConstructionStepPresent(BuildStep step)
    {
        if (step.Wall > 0) return Main.tile[step.X, step.Y].WallType == step.Wall;
        if (step.Tile == TileID.ClosedDoor)
            return Enumerable.Range(0, 3).All(dy => Enumerable.Range(step.X - 1, 3).Any(x =>
                IsTile(x, step.Y + dy, TileID.ClosedDoor) || IsTile(x, step.Y + dy, TileID.OpenDoor)));
        TileObjectData? data = TileObjectData.GetTileData(step.Tile, step.Style);
        for (int x = step.X; x < step.X + (data?.Width ?? 1); x++)
            for (int y = step.Y; y < step.Y + (data?.Height ?? 1); y++)
                if (!IsTile(x, y, step.Tile)) return false;
        return true;
    }

    private Vector2 BuildBlueprint(NPC npc, Player player, Blueprint blueprint)
    {
        if (blueprintBuild == null)
        {
            if (!TryFindBlueprintSite(player, blueprint, out int left, out int top))
            {
                FinishCurrentStep("附近找不到能放下这栋建筑的空地。", false);
                return player.Center;
            }

            if (!CanClearBlueprintSite(left, top, blueprint, out string blocked))
            {
                FinishCurrentStep(blocked, false);
                return player.Center;
            }

            if (!BlueprintManager.ConsumeMaterials(player, Loadout, blueprint, out string materials))
            {
                BlockGoal(materials);
                FinishCurrentStep(materials);
                return player.Center;
            }

            ClearBlueprintSite(left, top, blueprint);

            blueprintBuild = blueprint;
            blueprintWallIndex = 0;
            blueprintTileIndex = 0;
            blueprintPlaced = 0;
            blueprintOriginX = left;
            blueprintOriginY = top;
            LastBlueprintOriginX = left;
            LastBlueprintOriginY = top;
            buildCooldown = 0;

            AITeammateSystem.Instance.TestLog(
                $"blueprint start {blueprint.Name} at {left},{top} size={blueprint.Width}x{blueprint.Height} {materials}");
        }

        Vector2 siteCenter = new(blueprintOriginX * 16f + blueprintBuild.Width * 8f,
            blueprintOriginY * 16f + blueprintBuild.Height * 8f);

        if (Vector2.Distance(npc.Center, new Vector2(
                (blueprintOriginX - 2) * 16f + 8f,
                (blueprintOriginY + blueprintBuild.Height) * 16f - 8f)) > 12f * 16f)
            MoveBuilderOutside(npc);

        if (completeStepRequested) return siteCenter;

        npc.velocity.X *= 0.7f;
        buildCooldown--;
        if (buildCooldown > 0)
            return siteCenter;

        if (blueprintWallIndex < blueprintBuild.Walls.Count)
        {
            BlueprintWall wall = blueprintBuild.Walls[blueprintWallIndex++];
            PlaceBlueprintWall(blueprintOriginX + wall.X, blueprintOriginY + wall.Y, wall);
            blueprintPlaced++;
            buildCooldown = 1;
            if (blueprintPlaced % 6 == 0)
                BlueprintDust(blueprintOriginX + wall.X, blueprintOriginY + wall.Y);
            return siteCenter;
        }

        if (blueprintTileIndex < blueprintBuild.Tiles.Count)
        {
            BlueprintTile tile = blueprintBuild.Tiles[blueprintTileIndex++];
            PlaceBlueprintTile(blueprintOriginX + tile.X, blueprintOriginY + tile.Y, tile);
            blueprintPlaced++;
            buildCooldown = 1;
            if (blueprintPlaced % 4 == 0)
                BlueprintDust(blueprintOriginX + tile.X, blueprintOriginY + tile.Y);
            return siteCenter;
        }

        int chests = CreateBlueprintChests();
        AITeammateSystem.Instance.TestLog(
            $"blueprint done {blueprintBuild.Name} placed={blueprintPlaced} chests={chests} inv={InventorySummary()}");
        string name = blueprintBuild.Name;
        FinishCurrentStep($"照蓝图“{name}”盖好了，共 {blueprintPlaced} 格。");
        blueprintBuild = null;
        return siteCenter;
    }

    private void MoveBuilderOutside(NPC npc)
    {
        if (blueprintBuild == null)
            return;

        int bottom = blueprintOriginY + blueprintBuild.Height - 1;
        int[] columns = { blueprintOriginX - 2, blueprintOriginX + blueprintBuild.Width + 1,
            blueprintOriginX - 3, blueprintOriginX + blueprintBuild.Width + 2 };

        foreach (int x in columns)
        {
            for (int y = bottom; y >= bottom - 4; y--)
            {
                if (!IsSafeStanding(x, y))
                    continue;

                Navigate(npc, TilePathfinder.StandCenter(new Point(x, y), npc.height), 4.4f);
                return;
            }
        }

        TeleportNear(npc, new Vector2((blueprintOriginX - 2) * 16f + 8f, (bottom + 1) * 16f - 8f));
    }

    private bool TryFindBlueprintSite(Player player, Blueprint blueprint, out int left, out int top)
    {
        left = 0;
        top = 0;
        int direction = player.direction == 0 ? 1 : player.direction;
        int px = (int)(player.Center.X / 16f);
        int py = (int)(player.Center.Y / 16f);

        foreach (int distance in new[] { 10, 12, 14, 16, 20, 24, 30 })
        {
            int startX = px + direction * (distance + blueprint.Width / 2) - blueprint.Width / 2;
            if (startX < 5 || startX + blueprint.Width >= Main.maxTilesX - 5)
                continue;

            int bottom = -1;
            bool found = true;

            for (int x = startX; x < startX + blueprint.Width; x++)
            {
                if (!TryFindGround(x, py - 3, out int groundY))
                {
                    found = false;
                    break;
                }

                bottom = Math.Max(bottom, groundY);
            }

            if (!found)
                continue;

            int candidateTop = bottom - blueprint.Height + 1;
            if (candidateTop < 5 || bottom >= Main.maxTilesY - 5)
                continue;

            left = startX;
            top = candidateTop;
            return true;
        }

        return false;
    }

    private static bool CanClearBlueprintSite(int left, int top, Blueprint blueprint, out string message)
    {
        for (int x = left; x < left + blueprint.Width; x++)
        {
            for (int y = top; y < top + blueprint.Height; y++)
            {
                if (!WorldGen.InWorld(x, y, 2))
                    continue;

                int chest = Chest.FindChest(x, y);
                if (chest < 0 || chest >= Main.chest.Length)
                    continue;

                Chest? found = Main.chest[chest];
                if (found == null)
                    continue;

                if (found.item.Any(item => !item.IsAir))
                {
                    message = "选的位置有装着东西的箱子，先清空或换个地方。";
                    return false;
                }
            }
        }

        message = "";
        return true;
    }

    private static void ClearBlueprintSite(int left, int top, Blueprint blueprint)
    {
        for (int x = left; x < left + blueprint.Width; x++)
        {
            for (int y = top; y < top + blueprint.Height; y++)
            {
                WorldGen.KillTile(x, y, false, false, true);
                WorldGen.KillWall(x, y, false);
            }
        }
    }

    private static void PlaceBlueprintWall(int x, int y, BlueprintWall wall)
    {
        if (!WorldGen.InWorld(x, y, 2))
            return;

        Tile tile = Main.tile[x, y];
        tile.WallType = (ushort)wall.Type;
        tile.WallFrameX = wall.FrameX;
        tile.WallFrameY = wall.FrameY;
        tile.WallFrameNumber = wall.FrameNumber;
        tile.WallColor = wall.Color;
    }

    private static void PlaceBlueprintTile(int x, int y, BlueprintTile data)
    {
        if (!WorldGen.InWorld(x, y, 2))
            return;

        Tile tile = Main.tile[x, y];
        tile.HasTile = true;
        tile.TileType = (ushort)data.Type;
        tile.TileFrameX = data.FrameX;
        tile.TileFrameY = data.FrameY;
        tile.Slope = (SlopeType)data.Slope;
        tile.IsHalfBlock = data.HalfBlock;
        tile.TileColor = data.Color;
        tile.TileFrameNumber = data.FrameNumber;
        WorldKnowledgeObserver.Invalidate(x, y);
    }

    private static void BlueprintDust(int x, int y)
    {
        Dust dust = Dust.NewDustDirect(new Vector2(x * 16, y * 16), 16, 16, DustID.Dirt,
            0f, -0.5f, 100, default, 0.7f);
        dust.noGravity = true;
    }

    private int CreateBlueprintChests()
    {
        int created = 0;
        if (blueprintBuild == null)
            return 0;

        foreach (BlueprintTile data in blueprintBuild.Tiles)
        {
            if (data.Type is not (TileID.Containers or TileID.Containers2))
                continue;
            if (data.FrameX != 0 || data.FrameY != 0)
                continue;

            int x = blueprintOriginX + data.X;
            int y = blueprintOriginY + data.Y;
            if (!WorldGen.InWorld(x, y, 2) || Chest.FindChest(x, y) >= 0)
                continue;

            if (Chest.CreateChest(x, y, -1) >= 0)
                created++;
        }

        return created;
    }

    private Vector2 Explore(NPC npc, Player player)
    {
        Vector2 destination = new(exploreTargetX * 16f + 8, exploreTargetY * 16f + 8);
        if (!exploreDestinationChecked)
        {
            exploreDestinationChecked = true;
            if (exploreHasCoordinates && (!currentPlan!.X.HasValue || !currentPlan.Y.HasValue ||
                !WorldGen.InWorld(exploreTargetX, exploreTargetY, 2)))
            {
                FinishExplore(npc, player, false, "明确目标必须成对且处于当前世界可检查范围；没有裁剪或替换原始坐标。");
                return npc.Center;
            }
            if (exploreHasCoordinates)
            {
                if (!TilePathfinder.TryResolveStableStand(destination, out Point resolved) ||
                    !TilePathfinder.TryGetStandPose(resolved, out TileStandPose captured))
                {
                    FinishExplore(npc, player, false,
                        "目标附近没有可站立位置；步行探索不会挖穿实心地形。请根据真实观察另选可达入口或继续探索未观察区域。");
                    return npc.Center;
                }
                exploreStandDestination = resolved;
                exploreStandPose = captured;
            }
        }
        if (exploreStandDestination.HasValue)
        {
            if (exploreStandPose is not { } captured || captured.Node != exploreStandDestination.Value ||
                !TilePathfinder.MatchesStandPose(captured) || !TilePathfinder.IsStableStand(captured.Node.X, captured.Node.Y))
            {
                ClearChangedPoseFailures(new[] { exploreStandDestination.Value });
                InvalidateRoutePath(); routeFailureCode = "destination_pose_changed";
                FinishExplore(npc, player, false, "原目标的冻结身体站姿已经失效；请求坐标与原站姿保留，没有换中心或宣称抵达。");
                return npc.Center;
            }
            destination = captured.Center;
        }
        if (Main.GameUpdateCount >= exploreEndTick)
        {
            FinishExplore(npc, player, !exploreHasCoordinates,
                exploreHasCoordinates ? "本段未在时限内抵达目标。" : "本段探索时间结束。");
            return npc.Center;
        }
        if (exploreScanAt == 0 || Main.GameUpdateCount - exploreScanAt > 30)
        {
            exploreScanAt = Main.GameUpdateCount;
            ScanDiscoveries(npc);
        }
        TileStandPose? arrivalStand = exploreStandPose;
        if (!exploreHasCoordinates && routeSnapshot is { Status: "found" } && navGoalPose is { } projected &&
            CompanionMovement.ValidDirectionalExploreEndpoint(exploreStartX + 0.5f, exploreTargetX + 0.5f,
                projected.Center.X / 16f) && TilePathfinder.MatchesStandPose(projected))
            arrivalStand = projected;
        bool reached = arrivalStand is { } arrival
            ? CompanionMovement.ReachedTerminalWaypoint(arrival.Center.X - npc.Center.X, arrival.Bottom - npc.Bottom.Y,
                npc.velocity.Y, IsGrounded(npc), HasSlopedSupport(npc))
            : !exploreHasCoordinates && Math.Abs(exploreTargetX - npc.Center.X / 16f) < 2f;
        if (reached)
        {
            if (arrivalStand is { } reachedStand)
            {
                exploreStandPose = reachedStand;
                npc.velocity.X = CompanionMovement.BrakeAtTerminalWaypoint(reachedStand.Center.X - npc.Center.X,
                    reachedStand.Bottom - npc.Bottom.Y, npc.velocity.X, HasSlopedSupport(npc));
            }
            FinishExplore(npc, player, true, "已抵达本段探索终点。");
            return npc.Center;
        }
        Navigate(npc, destination, 4.2f);
        return destination;
    }

    internal bool TryGetExploreVisitPose(AIPlan? task, out TileStandPose pose)
    {
        pose = default;
        if (!ReferenceEquals(task, currentPlan) || !exploreHasCoordinates || exploreStandPose is not { } captured ||
            !TilePathfinder.MatchesStandPose(captured) || !TilePathfinder.IsStableStand(captured.Node.X, captured.Node.Y)) return false;
        pose = captured;
        return true;
    }

    private void FinishExplore(NPC npc, Player player, bool success, string reason)
    {
        ScanDiscoveries(npc);
        // This is a real end-of-segment observation, not an inferred absence beyond sensor range.
        JsonElement observation = JsonSerializer.SerializeToElement(AIPerception.Build(player, npc))
            .GetProperty("self");
        int x = (int)(npc.Center.X / 16f);
        int y = (int)(npc.Center.Y / 16f);
        int radiusX = observation.GetProperty("radius").GetProperty("x").GetInt32();
        int radiusY = observation.GetProperty("radius").GetProperty("y").GetInt32();
        sessionObservedMinX = Math.Min(sessionObservedMinX, x - radiusX);
        sessionObservedMaxX = Math.Max(sessionObservedMaxX, x + radiusX);
        int direction = Math.Sign(exploreTargetX - exploreStartX);
        int sessionLeft = Math.Max(2, sessionObservedMinX);
        int sessionRight = Math.Min(Main.maxTilesX - 3, sessionObservedMaxX);
        var report = new
        {
            kind = "exploration_result", success, reason,
            origin = new { x = exploreStartX, y = exploreStartY },
            requestedDestination = new { x = exploreTargetX, y = exploreTargetY },
            resolvedDestination = exploreStandPose is { } resolved ? new { x = resolved.Node.X,
                standRow = resolved.Node.Y, centerTileX = resolved.Center.X / 16f, centerTileY = resolved.Center.Y / 16f,
                feet = resolved.Bottom / 16f, poseMatchesCurrentWorld = TilePathfinder.MatchesStandPose(resolved) } : null,
            navigation = NavigationState,
            platformRecovery = success ? null : PlatformRecoveryAfterRouteFailure(npc),
            actualEnd = new { x, y },
            horizontalTravelTiles = Math.Abs(x - exploreStartX),
            newDiscoveries = exploreDiscoveries,
            discoveryScanAreas = exploreScanCenters.Select(center => new
            {
                left = Math.Max(2, center.X - 28), right = Math.Min(Main.maxTilesX - 3, center.X + 28),
                top = Math.Max(2, center.Y - 18), bottom = Math.Min(Main.maxTilesY - 3, center.Y + 18)
            }).ToArray(),
            endpointObservationBounds = new
            {
                left = Math.Max(2, x - radiusX), right = Math.Min(Main.maxTilesX - 3, x + radiusX),
                top = Math.Max(2, y - radiusY), bottom = Math.Min(Main.maxTilesY - 3, y + radiusY)
            },
            sessionObservedBounds = new
            {
                left = sessionLeft,
                right = sessionRight
            },
            unexploredFrontier = new
            {
                direction = direction < 0 ? "left" : direction > 0 ? "right" : "vertical",
                beyondX = direction == 0 ? (int?)null : (direction < 0 ? sessionLeft : sessionRight),
                leftBeyondX = sessionLeft,
                rightBeyondX = sessionRight,
                note = "范围外仍未知；本段0个新发现不代表整个方向没有箱子或资源，可继续向 leftBeyondX 以左或 rightBeyondX 以右探索。"
            },
            observation
        };
        FinishCurrentStep(JsonSerializer.Serialize(report), success);
    }

    private void ScanDiscoveries(NPC npc)
    {
        int centerX = (int)(npc.Center.X / 16f);
        int centerY = (int)(npc.Center.Y / 16f);
        sessionObservedMinX = Math.Min(sessionObservedMinX, centerX - 28);
        sessionObservedMaxX = Math.Max(sessionObservedMaxX, centerX + 28);
        var center = new Point(centerX, centerY);
        if (!exploreScanCenters.Contains(center))
            exploreScanCenters.Add(center);
        for (int x = centerX - 28; x <= centerX + 28; x++)
        {
            for (int y = centerY - 18; y <= centerY + 18; y++)
            {
                if (!WorldGen.InWorld(x, y, 2) || !Main.tile[x, y].HasTile)
                    continue;
                string? name = DiscoveryName(Main.tile[x, y].TileType);
                if (name == null || (name is not ("宝箱" or "生命水晶") && exploreDiscoveries >= 6))
                    continue;
                int bucket = name is "铁矿" or "铜矿" or "金矿" or "银矿" or "铅矿" or "锡矿" or "钨矿" or "铂矿" ? 8 : 2;
                string key = $"{name}_{x / bucket}_{y / bucket}";
                if (!discoveries.Add(key))
                    continue;
                exploreDiscoveries++;
                string fact = $"探索：在 ({x},{y}) 发现{name}";
                AITeammateSystem.Instance.Memory.Remember(fact);
                AITeammateSystem.Instance.TestLog("discovered " + fact);
            }
        }
    }

    private static string? DiscoveryName(int tileType) => tileType switch
    {
        TileID.Iron => "铁矿",
        TileID.Copper => "铜矿",
        TileID.Gold => "金矿",
        TileID.Silver => "银矿",
        TileID.Lead => "铅矿",
        TileID.Tin => "锡矿",
        TileID.Tungsten => "钨矿",
        TileID.Platinum => "铂矿",
        TileID.Containers => "宝箱",
        TileID.Containers2 => "宝箱",
        TileID.Heart => "生命水晶",
        TileID.Pots => "罐子",
        _ => null
    };

    private List<BuildStep>? PlanBuild(NPC npc, Player player)
    {
        shelterSelectionProtection = null;
        if (currentPlan?.X is int left && currentPlan.Y is int top)
            return TryPlanBuildAt(left + CompanionConstruction.Width / 2, top + CompanionConstruction.Height);
        int px = (int)(npc.Center.X / 16f);
        int feetY = (int)(npc.Bottom.Y / 16f);

        if (WantsUpperFloor())
        {
            List<BuildStep>? upper = TryPlanOnExistingRoof(px, feetY);
            if (upper != null)
                return upper;
        }
        foreach (int center in ShelterSiteProbe.AutomaticCenters(px, npc.direction))
        {
            List<BuildStep>? steps = TryPlanBuildAt(center, feetY);
            if (steps != null)
                return steps;
        }

        return null;
    }

    private static bool WantsUpperFloor()
    {
        string goal = AITeammateSystem.Instance.ActiveGoal ?? "";
        return goal.Contains("二层") || goal.Contains("二楼") || goal.Contains("上方") ||
               goal.Contains("加盖") || goal.Contains("屋顶") || goal.Contains("楼上") ||
               goal.Contains("第二层") || goal.Contains("upper", StringComparison.OrdinalIgnoreCase);
    }

    // Finds an existing house roof (10 solid tiles over housing walls, free space above) and stacks a new floor on it.
    private List<BuildStep>? TryPlanOnExistingRoof(int px, int feetY)
    {
        int width = CompanionConstruction.Width;
        int bestLeft = 0, bestRoof = 0, bestScore = int.MaxValue;
        for (int left = px - 24; left <= px + 24; left++)
        {
            for (int roof = feetY - 16; roof <= feetY + 6; roof++)
            {
                bool ok = true;
                for (int x = left; x < left + width && ok; x++)
                {
                    if (!WorldGen.InWorld(x, roof, 4) || !WorldGen.InWorld(x, roof - 1, 4)) { ok = false; break; }
                    Tile t = Main.tile[x, roof];
                    ushort below = Main.tile[x, roof + 1].WallType;
                    ok = t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType] &&
                         below > 0 && Main.wallHouse[below] && !Main.tile[x, roof - 1].HasTile;
                }
                if (!ok)
                    continue;
                int score = Math.Abs(left + width / 2 - px) + Math.Abs(roof - feetY);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestLeft = left;
                    bestRoof = roof;
                }
            }
        }
        if (bestScore == int.MaxValue)
            return null;
        AITeammateSystem.Instance.TestLog($"upper floor on roof left={bestLeft} roof={bestRoof}");
        return TryPlanBuildAt(bestLeft + width / 2, bestRoof + 1);
    }

    private List<BuildStep>? TryPlanBuildAt(int centerX, int feetY)
    {
        ShelterSiteCandidate candidate = CreateShelterSiteProbe().ProbeAt(centerX, feetY);
        if (candidate.status != "geometry_passed" || candidate.resolvedTopLeft == null) return null;
        int left = candidate.resolvedTopLeft.x, top = candidate.resolvedTopLeft.y;
        GoalConstructionProtection protection = InspectShelterFootprint(left, top);
        if (!ShelterProtectionAllows(protection))
        {
            shelterSelectionProtection ??= protection;
            return null;
        }
        return CompanionConstruction.ShelterLayout(left, top, string.IsNullOrEmpty(target) ? "wood" : target)
            .Select(step =>
            {
                // Species share furniture tile IDs, but their real item defaults select
                // different native styles. Geometry/costs never invent those styles.
                var item = new Item();
                item.SetDefaults(step.ItemType);
                return step.Wall > 0
                    ? new BuildStep(step.X, step.Y, 0, item.createWall, step.ItemType)
                    : new BuildStep(step.X, step.Y, item.createTile, 0, step.ItemType, item.placeStyle);
            }).ToList();
    }

    private static bool TryFindGround(int x, int startY, out int groundY)
    {
        for (int y = startY; y <= startY + 16; y++)
        {
            if (!WorldGen.InWorld(x, y, 3))
                continue;

            Tile tile = Main.tile[x, y];
            if (tile.HasTile && Main.tileSolid[tile.TileType])
            {
                groundY = y;
                return true;
            }
        }

        groundY = -1;
        return false;
    }

    private static NPC? FindNearestEnemy(NPC npc, float range) =>
        FindNearestEnemyNear(npc.Center, range);

    private static NPC? FindNearestEnemyNear(Vector2 center, float range)
    {
        NPC? best = null;
        float bestDistance = range;

        foreach (NPC other in Main.npc)
        {
            if (!other.active || other.friendly || other.life <= 0 || other.dontTakeDamage)
                continue;

            float distance = Vector2.Distance(center, other.Center);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = other;
            }
        }

        return best;
    }

    private bool FindOre(NPC npc, out int x, out int y)
    {
        return ChooseAutomaticMiningSeed(npc, target, mineTileType, out x, out y);
    }

    private static bool IsOreExposed(int x, int y)
    {
        ReadOnlySpan<(int Dx, int Dy)> dirs = stackalloc (int, int)[] { (0, -1), (0, 1), (-1, 0), (1, 0) };
        foreach ((int dx, int dy) in dirs)
        {
            int nx = x + dx;
            int ny = y + dy;
            if (!WorldGen.InWorld(nx, ny, 2))
                continue;
            Tile tile = Main.tile[nx, ny];
            if (!tile.HasTile || !Main.tileSolid[tile.TileType] || TileID.Sets.Platforms[tile.TileType])
                return true;
        }
        return false;
    }

    private static bool TryFindSurfaceEscape(NPC npc, int direction, out Vector2 escapeWorld)
    {
        escapeWorld = npc.Center;
        int nx = (int)(npc.Center.X / 16f);
        int ny = (int)(npc.Center.Y / 16f);
        int dir = direction == 0 ? (npc.direction == 0 ? 1 : npc.direction) : Math.Sign(direction);

        for (int step = 3; step <= 24; step++)
        {
            int cx = nx + dir * step;
            for (int dy = -20; dy <= 6; dy++)
            {
                int cy = ny + dy;
                if (!IsSafeStanding(cx, cy))
                    continue;

                escapeWorld = TilePathfinder.StandCenter(new Point(cx, cy), npc.height);
                return true;
            }
        }

        return false;
    }

    private void CheckNavigationProgress(NPC npc, Vector2? targetPosition)
    {
        if (!targetPosition.HasValue || completeStepRequested ||
            action is AIActionType.Attack or AIActionType.Retreat or AIActionType.Mine || autoDefend || routeSearch != null)
            return;
        float distance = Vector2.Distance(npc.Center, targetPosition.Value);
        if (distance < navigationBestDistance - 8f)
        {
            navigationBestDistance = distance;
            navigationNoProgressTicks = 0;
            return;
        }
        if (++navigationNoProgressTicks <= 600) return;
        navigationNoProgressTicks = 0;
        NavigationFailureReason = "真实寻路十秒未接近目标；不会用传送代替到达。";
        InvalidateRoutePath();
        CancelRouteSearch();
        navigationRetryAt = Main.GameUpdateCount + 120;
        npc.velocity.X = 0f;
        if (action == AIActionType.Explore)
            FinishExplore(npc, Main.LocalPlayer, false, NavigationFailureReason);
        else if (currentPlan != null)
            FinishCurrentStep(NavigationFailureReason, false);
    }

    private void Navigate(NPC npc, Vector2 targetPosition, float speed)
        => NavigateRoute(npc, targetPosition, speed);

    private void FaceNavigation(NPC npc, int direction)
    {
        if (direction == 0) return;
        if (npc.direction != direction) NavigationFacingChanges++;
        npc.spriteDirection = npc.direction = direction;
    }

    private void FollowPath(NPC npc, float speed)
    {
        if (navPath == null || navIndex >= navPath.Count) return;
        bool grounded = IsGrounded(npc);
        bool onSlope = HasSlopedSupport(npc);
        while (navIndex < navPath.Count)
        {
            if (!RevalidateFrozenRoute(npc)) return;
            Point candidate = navPath[navIndex];
            TileStandPose candidatePose = navPathPoses![navIndex];
            float horizontalError = candidatePose.Center.X - npc.Center.X;
            float footError = candidatePose.Bottom - npc.Bottom.Y;
            bool terminal = navIndex == navPath.Count - 1;
            bool reached = terminal
                ? CompanionMovement.ReachedTerminalWaypoint(horizontalError, footError, npc.velocity.Y, grounded, onSlope)
                : CompanionMovement.ReachedWaypoint(horizontalError, footError, grounded, onSlope);
            if (!reached)
                break;
            if (terminal)
                npc.velocity.X = CompanionMovement.BrakeAtTerminalWaypoint(horizontalError, footError, npc.velocity.X, onSlope);
            if (navigationVisitedNodes.Add(candidate)) navigationNoProgressTicks = 0;
            Point from = navIndex > 0 ? navPath[navIndex - 1] : candidate;
            edgeProgress.Reached(from.X, from.Y, candidate.X, candidate.Y);
            if (action == AIActionType.Mine)
            {
                miningApproach.ReachedWaypoint(candidate.X, candidate.Y);
                miningBudget.NoteTravelProgress(candidate.X, candidate.Y);
            }
            navIndex++;
        }
        if (navIndex >= navPath.Count)
        {
            return;
        }
        Point waypoint = navPath[navIndex];
        TileStandPose waypointPose = navPathPoses![navIndex];
        float dx = waypointPose.Center.X - npc.Center.X;
        float footDelta = waypointPose.Bottom - npc.Bottom.Y;
        bool above = footDelta < -3f;
        bool below = footDelta > 3f;
        bool settlingTerminal = navIndex == navPath.Count - 1 &&
            CompanionMovement.InTerminalWaypointWindow(dx, footDelta, onSlope);
        Point standing = new((int)(npc.Center.X / 16f), CompanionMovement.StandRow(npc.Bottom.Y));
        if (grounded && TilePathfinder.TryResolveStand(npc.Center, out Point actualStand))
            standing = actualStand;
        bool walkingConnection = grounded && TilePathfinder.HasWalkingConnection(npc.Center.X, npc.Bottom.Y, waypoint);
        bool lowerGap = navIndex > 0 && CompanionMovement.IsLowerGapJump(waypoint.X - navPath[navIndex - 1].X,
            waypoint.Y - navPath[navIndex - 1].Y);
        bool gapJump = (Math.Abs(footDelta) <= 16f || lowerGap) && Math.Abs(dx) > 3f && !walkingConnection;
        int direction = CompanionMovement.WaypointDirection(dx, footDelta);
        FaceNavigation(npc, direction);
        bool doorOpened = TryOpenDoorAhead(npc, direction);
        if (!settlingTerminal && below && grounded && Math.Abs(dx) <= 18f)
            DropThroughPlatform(npc);
        bool walkUpStep = above && footDelta >= -16f && direction != 0 && walkingConnection;
        if (!settlingTerminal && grounded && ((above && !walkUpStep) || gapJump) && jumpCooldown <= 0 && !doorOpened)
            Jump(npc); // Includes same-column platform jumps.
        else if (!settlingTerminal && grounded && direction != 0)
            TryStepUpOneTile(npc, direction, true);

        float desiredSpeed = CompanionMovement.WaypointSpeed(dx, footDelta, speed);
        npc.velocity.X = MathHelper.Lerp(npc.velocity.X, desiredSpeed, grounded ? 0.25f : 0.18f);
        if (navIndex == navPath.Count - 1)
            npc.velocity.X = CompanionMovement.BrakeAtTerminalWaypoint(dx, footDelta, npc.velocity.X, onSlope);
        Point edgeFrom = navIndex > 0 ? navPath[navIndex - 1] : standing;
        bool progressing = edgeProgress.Observe(edgeFrom.X, edgeFrom.Y, waypoint.X, waypoint.Y,
            MathF.Sqrt(dx * dx + footDelta * footDelta));
        pathBlockedTicks = edgeProgress.NoProgressTicks;
        lastPathPosition = npc.Center;
        if (!progressing) RejectCurrentTransition(npc, waypoint, standing);
    }

    private void DropThroughPlatform(NPC npc)
    {
        if (platformDropTicks > 0) return;
        int row = (int)MathF.Floor((npc.Bottom.Y + 0.01f) / 16f);
        bool hasPlatform = false;
        float clearPlane = npc.Bottom.Y;
        for (int x = CompanionMovement.FootprintLeft(npc.Center.X, npc.width); x <= CompanionMovement.FootprintRight(npc.Center.X, npc.width); x++)
        {
            if (!WorldGen.InWorld(x, row, 2)) return;
            Tile tile = Main.tile[x, row];
            if (tile.HasTile && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType] && !TileID.Sets.Platforms[tile.TileType]) return;
            hasPlatform |= tile.HasTile && (Main.tileSolidTop[tile.TileType] || TileID.Sets.Platforms[tile.TileType]);
            if (tile.HasTile && !tile.IsActuated &&
                (Main.tileSolidTop[tile.TileType] || TileID.Sets.Platforms[tile.TileType]))
                clearPlane = Math.Max(clearPlane, CompanionMovement.DropClearPlane(npc.Bottom.Y, row,
                    (int)tile.Slope != 0 || tile.IsHalfBlock));
        }
        if (hasPlatform)
        {
            platformDropSourceBottom = npc.Bottom.Y;
            platformDropClearPlane = clearPlane;
            platformDropOwned = true;
            platformDropTicks = 10;
            npc.velocity.Y = Math.Max(1f, npc.velocity.Y);
        }
    }

    private void UpdatePlatformDrop(NPC npc)
    {
        if (!platformDropOwned) return;
        if (platformDropTicks > 0 &&
            !CompanionMovement.DropSourceCleared(platformDropClearPlane, npc.Bottom.Y))
            platformDropTicks--;
        else
            platformDropTicks = 0;
        if (platformDropTicks > 0) return;

        // Native SlopeCollision reads this per-NPC latch even after our hook has returned false.
        // End only the descent we initiated; Collision.stairFall is shared scratch and is untouched.
        npc.stairFall = false;
        platformDropOwned = false;
    }

    private static void TryStepUpOneTile(NPC npc, float direction, bool grounded)
    {
        if (!grounded || direction == 0)
            return;
        int dir = Math.Sign(direction);
        int frontX = (int)((npc.Center.X + dir * (npc.width / 2f + 4f)) / 16f);
        int feetY = (int)((npc.Bottom.Y - 1f) / 16f);
        if (!WorldGen.InWorld(frontX, feetY, 3))
            return;

        Tile stepTile = Main.tile[frontX, feetY];
        if (!stepTile.HasTile || !Main.tileSolid[stepTile.TileType] || Main.tileSolidTop[stepTile.TileType] ||
            stepTile.TileType == TileID.ClosedDoor)
            return;

        for (int h = 1; h <= 3; h++)
        {
            Tile clear = Main.tile[frontX, feetY - h];
            if (clear.HasTile && Main.tileSolid[clear.TileType] && !Main.tileSolidTop[clear.TileType] &&
                clear.TileType != TileID.OpenDoor)
                return;
        }

        float neededRise = CompanionMovement.StepRise(npc.Bottom.Y, feetY, stepTile.IsHalfBlock);
        if (neededRise <= 0f ||
            !TilePathfinder.IsBodyClear(npc.Center.X + dir * 2f, npc.Bottom.Y - neededRise, npc.width, npc.height))
            return;
        npc.position.Y -= neededRise;
        npc.position.X += dir * 2f;
    }

    private void Jump(NPC npc)
    {
        if (!IsGrounded(npc)) return;
        npc.velocity.Y = CompanionMovement.JumpVelocity;
        jumpCooldown = 20;
        NavigationJumpCount++;
    }

    private void MoveTowards(NPC npc, Vector2 targetPosition, float speed, float stopDistance)
    {
        float dx = targetPosition.X - npc.Center.X;
        float direction = Math.Sign(dx);
        if (direction == 0)
            direction = npc.direction == 0 ? 1 : npc.direction;

        if (detourTicks > 0)
        {
            detourTicks--;
            direction = detourDirection;
        }

        bool doorOpened = TryOpenDoorAhead(npc, direction);
        bool grounded = IsGrounded(npc);
        if (direction != 0)
            TryStepUpOneTile(npc, direction, grounded);

        if (detourTicks > 0)
        {
            npc.velocity.X = MathHelper.Lerp(npc.velocity.X, detourDirection * speed, 0.15f);
        }
        else if (Math.Abs(dx) > stopDistance || targetPosition.Y > npc.Bottom.Y + 24f)
        {
            npc.velocity.X = MathHelper.Lerp(npc.velocity.X, direction * speed, 0.12f);
        }
        else
        {
            npc.velocity.X *= 0.75f;
        }

        bool wallAhead = WallAhead(npc, direction);
        bool canJumpWall = CanJumpWall(npc, direction);
        bool targetAbove = targetPosition.Y < npc.Bottom.Y - 48f;
        bool wantMove = Math.Abs(dx) > stopDistance || detourTicks > 0 || targetPosition.Y > npc.Bottom.Y + 24f;
        bool progressed = Math.Abs(npc.position.X - lastMovePosX) > 0.15f;

        if (wantMove && grounded && !progressed)
            moveBlockedTicks++;
        else if (progressed)
            moveBlockedTicks = 0;
        lastMovePosX = npc.position.X;
        lastPathPosX = npc.position.X;

        if (grounded && jumpCooldown <= 0 &&
            (targetAbove || (wallAhead && canJumpWall) || (moveBlockedTicks >= 2 && moveBlockedTicks <= 24)))
            Jump(npc);

        if (moveBlockedTicks > 40 && !doorOpened && detourTicks <= 0 && grounded)
        {
            detourDirection = -direction;
            detourTicks = 28;
            AITeammateSystem.Instance.TestLog(
                $"blocked {moveBlockedTicks} ticks, detouring dir={detourDirection:0} " +
                $"npc={npc.Center.X / 16f:0},{npc.Center.Y / 16f:0} path={navPath?.Count ?? 0}/{navIndex}");
            moveBlockedTicks = 0;
        }

        if (direction != 0)
            npc.spriteDirection = npc.direction = (int)direction;
    }

    private bool TryOpenDoorAhead(NPC npc, float direction)
    {
        if (direction == 0)
            return false;

        int x = (int)((npc.Center.X + direction * (npc.width / 2f + 10f)) / 16f);
        int feetY = (int)(npc.Bottom.Y / 16f);

        for (int y = feetY; y >= feetY - 2; y--)
        {
            if (!WorldGen.InWorld(x, y, 2))
                continue;

            Tile tile = Main.tile[x, y];
            if (!tile.HasTile || tile.TileType != TileID.ClosedDoor)
                continue;

            WorldGen.OpenDoor(x, y, (int)direction);
            if (Main.tile[x, y].HasTile && Main.tile[x, y].TileType == TileID.OpenDoor)
            {
                openedDoors.Add((x, y, Main.GameUpdateCount));
                AITeammateSystem.Instance.TestLog($"opened door at {x},{y}");
                return true;
            }
        }

        return false;
    }

    private void CloseOpenedDoors(NPC npc)
    {
        for (int i = openedDoors.Count - 1; i >= 0; i--)
        {
            (int x, int y, uint openedAt) = openedDoors[i];
            if (Main.GameUpdateCount - openedAt < 120)
                continue;

            Vector2 doorCenter = new Vector2(x * 16 + 8, y * 16 + 8);
            if (Vector2.Distance(npc.Center, doorCenter) < 3f * 16f)
                continue;

            if (WorldGen.InWorld(x, y, 2))
            {
                Tile tile = Main.tile[x, y];
                if (tile.HasTile && tile.TileType == TileID.OpenDoor)
                {
                    WorldGen.CloseDoor(x, y, false);
                    AITeammateSystem.Instance.TestLog($"closed door at {x},{y}");
                }
            }

            openedDoors.RemoveAt(i);
        }
    }

    private static bool CanJumpWall(NPC npc, float direction)
    {
        if (direction == 0)
            return false;

        int x = (int)((npc.Center.X + direction * (npc.width / 2f + 8f)) / 16f);
        int feetY = (int)(npc.Bottom.Y / 16f);
        int height = 0;

        for (int h = 1; h <= 6; h++)
        {
            if (!WorldGen.InWorld(x, feetY - h, 2))
                break;

            Tile tile = Main.tile[x, feetY - h];
            if (!tile.HasTile || !Main.tileSolid[tile.TileType] || Main.tileSolidTop[tile.TileType])
                break;

            height = h;
        }

        return height is > 0 and <= 6;
    }

    private static bool WallAhead(NPC npc, float direction)
    {
        if (direction == 0)
            return false;

        int x = (int)((npc.Center.X + direction * (npc.width / 2f + 8f)) / 16f);
        int y1 = (int)(npc.Center.Y / 16f);
        int y2 = (int)((npc.Bottom.Y - 4f) / 16f);

        for (int y = y1; y <= y2; y++)
        {
            if (!WorldGen.InWorld(x, y, 2))
                continue;

            Tile tile = Main.tile[x, y];
            if (tile.HasTile && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType])
                return true;
        }

        return false;
    }

    private static bool IsGrounded(NPC npc)
        => TilePathfinder.IsGroundedAt(npc.Center.X, npc.Bottom.Y, npc.velocity.Y, npc.width);

    private static bool HasSlopedSupport(NPC npc)
    {
        int row = (int)MathF.Floor((npc.Bottom.Y + 1f) / 16f);
        int left = (int)MathF.Floor((npc.position.X + 0.01f) / 16f);
        int right = (int)MathF.Floor((npc.Right.X - 0.01f) / 16f);
        for (int x = left; x <= right; x++)
            if (WorldGen.InWorld(x, row, 2) && Main.tile[x, row].HasTile &&
                !Main.tile[x, row].IsActuated && (int)Main.tile[x, row].Slope != 0)
                return true;
        return false;
    }

    private static bool IsTile(int x, int y, int type)
    {
        return WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile && Main.tile[x, y].TileType == type;
    }

    private static int TileFor(string type) => type switch
    {
        "iron" => TileID.Iron,
        "wood" => TileID.Trees,
        "copper" => TileID.Copper,
        "silver" => TileID.Silver,
        "gold" => TileID.Gold,
        "lead" => TileID.Lead,
        "tin" => TileID.Tin,
        "tungsten" => TileID.Tungsten,
        "platinum" => TileID.Platinum,
        "meteorite" => TileID.Meteorite,
        "ruby" => TileID.Ruby,
        "stone" => TileID.Stone,
        "dirt" => TileID.Dirt,
        "grass" => TileID.Grass,
        _ => -1
    };

    private static string ItemNameForTile(int tile) => tile switch
    {
        TileID.Copper => "铜矿",
        TileID.Silver => "银矿",
        TileID.Gold => "金矿",
        TileID.Lead => "铅矿",
        TileID.Tin => "锡矿",
        TileID.Tungsten => "钨矿",
        TileID.Platinum => "铂矿",
        TileID.Meteorite => "陨石",
        TileID.Stone => "石头",
        _ => "铁矿"
    };

    private static int ItemForTile(int tile) => tile switch
    {
        TileID.Trees => ItemID.Wood,
        TileID.Copper => ItemID.CopperOre,
        TileID.Iron => ItemID.IronOre,
        TileID.Silver => ItemID.SilverOre,
        TileID.Gold => ItemID.GoldOre,
        TileID.Lead => ItemID.LeadOre,
        TileID.Tin => ItemID.TinOre,
        TileID.Tungsten => ItemID.TungstenOre,
        TileID.Platinum => ItemID.PlatinumOre,
        TileID.Meteorite => ItemID.Meteorite,
        TileID.Ruby => ItemID.Ruby,
        TileID.Stone => ItemID.StoneBlock,
        TileID.Dirt or TileID.Grass => ItemID.DirtBlock,
        _ => ItemID.None
    };

    private void LogFollowState(NPC npc, Player player, Vector2? navTarget)
    {
        int nx = (int)(npc.Center.X / 16f);
        int ny = (int)(npc.Center.Y / 16f);
        int px = (int)(player.Center.X / 16f);
        int py = (int)(player.Center.Y / 16f);
        int feetY = (int)(npc.Bottom.Y / 16f);
        Vector2 goal = navTarget ?? player.Center;
        int dir = Math.Sign(goal.X - npc.Center.X);
        int wall = dir == 0 ? 0 : WallHeight(npc, dir);
        string waypoint = navPath is { Count: > 0 } && navIndex < navPath.Count
            ? $"{navPath[navIndex].X},{navPath[navIndex].Y}"
            : "none";
        float dist = Vector2.Distance(npc.Center, player.Center) / 16f;

        AITeammateSystem.Instance.TestLog(
            $"follow diag npc={nx},{ny} bottom={feetY} player={px},{py} dist={dist:0.0} " +
            $"v={npc.velocity.X:0.0},{npc.velocity.Y:0.0} path={navPath?.Count ?? 0}/{navIndex} " +
            $"wp={waypoint} goal={goal.X / 16f:0},{goal.Y / 16f:0} grounded={IsGrounded(npc)} " +
            $"wallDir={dir} wallH={wall} detour={detourTicks} stuck={stuckTicks} jumpCd={jumpCooldown}");
    }

    private static int WallHeight(NPC npc, int direction)
    {
        int x = (int)((npc.Center.X + direction * (npc.width / 2f + 8f)) / 16f);
        int feetY = (int)(npc.Bottom.Y / 16f);
        int height = 0;

        for (int h = 1; h <= 12; h++)
        {
            int y = feetY - h;
            if (!WorldGen.InWorld(x, y, 2))
                break;

            Tile tile = Main.tile[x, y];
            if (!tile.HasTile || !Main.tileSolid[tile.TileType] || Main.tileSolidTop[tile.TileType])
                break;

            height = h;
        }

        return height;
    }

    private void TeleportNear(NPC npc, Vector2 center)
    {
        if (!Policy.AllowTeleportRecovery)
        {
            // Legacy callers fire this timer even during a progressing walk. Disabling assistance
            // must not introduce a shorter failure budget. Navigate/CheckNavigationProgress report stalls.
            return;
        }
        int baseX = (int)(center.X / 16f);
        int baseY = (int)(center.Y / 16f);
        int[] dyOffsets = { 0, -1, 1, -2, 2 };
        int[] dxOffsets = { 2, -2, 3, -3, 1, -1, 4, -4, 5, -5, 0, 6, -6 };
        Vector2? fallback = null;

        foreach (int dy in dyOffsets)
        {
            foreach (int dx in dxOffsets)
            {
                int x = baseX + dx;
                int y = baseY + dy;
                if (!IsSafeStanding(x, y))
                    continue;

                Vector2 candidate = TilePathfinder.StandCenter(new Point(x, y), npc.height);
                if (!Collision.CanHitLine(candidate, 1, 1, center, 1, 1))
                {
                    fallback ??= candidate;
                    continue;
                }

                npc.Center = candidate;
                npc.velocity = Vector2.Zero;
                NavigationTeleportCount++;
                SpawnTeleportDust(npc);
                return;
            }
        }

        if (fallback.HasValue)
        {
            npc.Center = fallback.Value;
            npc.velocity = Vector2.Zero;
            NavigationTeleportCount++;
            SpawnTeleportDust(npc);
        }
    }

    private static bool IsSafeStanding(int x, int y) => TilePathfinder.IsSafeStand(x, y);

    private static void SpawnTeleportDust(NPC npc)
    {
        for (int i = 0; i < 12; i++)
        {
            Dust dust = Dust.NewDustDirect(npc.position, npc.width, npc.height, DustID.MagicMirror, 0f, 0f, 100, Color.Cyan, 1.2f);
            dust.noGravity = true;
        }
    }

    private void FinishTask(string text)
    {
        string display = ActionFeedbackDisplay.Summarize(text, action == AIActionType.Inspect);
        Main.NewText($"小星：{display}", Color.LightGreen);
        AITeammateSystem.Instance.TestLog(text);
    }

    private readonly record struct BuildStep(int X, int Y, int Tile, int Wall, int ItemType = 0, int Style = 0);
}
