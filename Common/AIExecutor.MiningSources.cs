using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

public partial class AIExecutor
{
    private MiningSourceInspection? miningSourcesInspection;
    // Shared by fresh compatibility calls and the retained action. Cancellation must
    // not reopen this tick's allowance and accidentally run another complete probe.
    private ulong? miningSourceWorkTick;
    public object? MiningSourcesInspectionProgress => miningSourcesInspection?.Progress;

    private bool TryBeginMiningSourceSample(ulong tick)
    {
        if (miningSourceWorkTick == tick) return false;
        miningSourceWorkTick = tick;
        return true;
    }

    private MiningSourceFrame MiningSourcesFrame(NPC observer, object owner)
        => new(Main.worldID, WorldKnowledgeObserver.LoadEpoch, Main.GameUpdateCount, owner,
            observer.Center.X, observer.Center.Y, observer.Bottom.Y, AITeammateSystem.Instance.GoalIdentity)
        { InventoryToolIdentity = MiningSourcesInventoryToolIdentity(observer) };

    private string MiningSourcesInventoryToolIdentity(NPC observer)
    {
        var signature = new StringBuilder().Append(observer.width).Append(':').Append(observer.height).Append('|');
        foreach (Item item in Loadout.Inventory.Concat(Loadout.Armor).Concat(Loadout.Accessories))
            signature.Append(item.type).Append(':').Append(item.stack).Append(':').Append(item.prefix).Append(':')
                .Append(item.pick).Append(':').Append(item.axe).Append(':').Append(item.hammer).Append(':')
                .Append(item.useTime).Append(':').Append(item.maxStack).Append(';');
        return signature.ToString();
    }

    private MiningSourceInspection CreateMiningSourcesInspection(NPC observer, string query, object owner, int? x, int? y)
    {
        MiningSourcePage page = WorldKnowledgeObserver.ReadMiningSourcesPage(query, observer, x, y);
        MiningReturnAnchor? observerStart = page.Header["available"]?.GetValue<bool>() == true &&
            page.Header["inputValid"]?.GetValue<bool>() == true ? CaptureMiningReturnAnchor(observer, preview: true) : null;
        string resource = page.Header["resource"]?.GetValue<string>() ?? "";
        int targetType = TileFor(resource);
        var survey = targetType >= 0 && resource is not ("wood" or "dirt" or "grass") &&
            page.Header["query"]?["refinementContinuation"]?.GetValue<bool>() != true && observerStart?.Stand is { } start
            ? TilePathfinder.BeginMiningStandSurvey(start.Node) : null;
        bool BeginSample(ulong tick)
        {
            if (!TryBeginMiningSourceSample(tick)) return false;
            if (survey?.IsSearching == true) survey.Advance(40, 2);
            return true;
        }

        bool NearSettledStand(int x, int y)
        {
            if (survey == null) return false;
            // The stand node's row and actor's body center differ. A generous
            // six-tile grid window is sufficient as an ordering hint only.
            for (int dx = -6; dx <= 6; dx++)
                for (int dy = -6; dy <= 6; dy++)
                    if (dx * dx + dy * dy <= 36 && survey.HasSettledStand(new Point(x + dx, y + dy))) return true;
            return false;
        }
        JsonObject Normalize(JsonObject sample)
        {
            int sx = sample["x"]!.GetValue<int>(), sy = sample["y"]!.GetValue<int>();
            if (resource != "wood") return new JsonObject { ["x"] = sx, ["y"] = sy, ["normalization"] = "sample_seed", ["treeProduct"] = null };
            JsonObject product = JsonSerializer.SerializeToNode(TreeProductWorldObserver.Observe(sx, sy))!.AsObject();
            return MiningSourceInspection.NormalizeWoodSeed(sx, sy, product, (bx, by) => WorldGen.InWorld(bx, by, 3) &&
                Main.tile[bx, by].HasTile && Main.tile[bx, by].TileType == TileID.Trees);
        }
        JsonObject ReadLiveCell(int sx, int sy)
        {
            if (sx < 0 || sy < 0 || sx >= Main.maxTilesX || sy >= Main.maxTilesY) return new JsonObject { ["available"] = false };
            Tile tile = Main.tile[sx, sy]; int reads = 0, exposed = 0, reachReads = 0; bool nearbyOpenSpace = false;
            if (tile.HasTile)
            {
                void Neighbour(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= Main.maxTilesX || ny >= Main.maxTilesY) return;
                    Tile neighbour = Main.tile[nx, ny]; reads++;
                    if (!neighbour.HasTile || neighbour.IsActuated || !Main.tileSolid[neighbour.TileType]) exposed++;
                }
                Neighbour(sx - 1, sy); Neighbour(sx + 1, sy); Neighbour(sx, sy - 1); Neighbour(sx, sy + 1);
                void ReachProbe(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= Main.maxTilesX || ny >= Main.maxTilesY) return;
                    Tile neighbour = Main.tile[nx, ny]; reads++; reachReads++;
                    if (!neighbour.HasTile || neighbour.IsActuated || !Main.tileSolid[neighbour.TileType]) nearbyOpenSpace = true;
                }
                int offset = MiningSourceRefinement.CoarseOpenSpaceProbeOffset;
                ReachProbe(sx - offset, sy); ReachProbe(sx + offset, sy);
                ReachProbe(sx, sy - offset); ReachProbe(sx, sy + offset);
            }
            return new JsonObject { ["available"] = true, ["hasTile"] = tile.HasTile, ["tileType"] = (int)tile.TileType,
                ["actuated"] = tile.IsActuated, ["exposedFaceCount"] = exposed, ["auxiliaryCellReads"] = reads,
                ["miningReachProbeReads"] = reachReads, ["nearbyOpenSpaceHint"] = nearbyOpenSpace,
                ["nearbySettledStandHint"] = tile.HasTile && !tile.IsActuated && tile.TileType == targetType && NearSettledStand(sx, sy) };
        }
        JsonObject ObserveGeometry(int sx, int sy)
        {
            MiningTerrainCell cell = NavigationWorldObserver.ReadMiningCell(sx, sy);
            if (!cell.HasTile || cell.Actuated || cell.StructureProtected || !cell.NativeCanKill)
            {
                var reasons = new JsonArray();
                if (!cell.HasTile) reasons.Add("seed_no_longer_present");
                if (cell.Actuated) reasons.Add("seed_is_actuated");
                if (cell.StructureProtected) reasons.Add("seed_is_structure_protected");
                if (!cell.NativeCanKill) reasons.Add("native_can_kill_tile_refused");
                return new JsonObject { ["available"] = true, ["sourceEligibilityBlocked"] = true,
                    ["hasTile"] = cell.HasTile, ["actuated"] = cell.Actuated,
                    ["structureProtected"] = cell.StructureProtected, ["nativeCanKill"] = cell.NativeCanKill,
                    ["actualTileType"] = cell.ActualTileType, ["reasons"] = reasons,
                    ["geometryStatus"] = "not_tested_native_seed_ineligible", ["geometricWorkStandCount"] = null,
                    ["gridChecks"] = 0, ["observedAtTick"] = Main.GameUpdateCount };
            }
            var target = new Microsoft.Xna.Framework.Vector2(sx * 16f + 8, sy * 16f + 8);
            MiningApproachGeometry geometry = MiningApproachProbe.EnumerateGeometry(observer.Center, observer.height, target,
                TilePathfinder.IsStableStand, (node, height) => new MiningApproachStand(node,
                    TilePathfinder.StandCenter(node, height), TilePathfinder.StandBottom(node)));
            var stands = new JsonArray(geometry.Candidates.OrderBy(stand => Microsoft.Xna.Framework.Vector2.DistanceSquared(observer.Center, stand.Center))
                .Take(3).Select(stand => (JsonNode?)new JsonObject { ["x"] = stand.Node.X, ["standRow"] = stand.Node.Y,
                    ["centerTileX"] = stand.Center.X / 16, ["centerTileY"] = stand.Center.Y / 16,
                    ["feetTileY"] = stand.FeetY / 16 }).ToArray());
            return new JsonObject { ["available"] = true, ["geometryStatus"] = "measured_current_window",
                ["geometricWorkStandCount"] = geometry.Candidates.Count, ["gridChecks"] = geometry.GridChecks,
                ["standStabilityHorizontalMarginPixels"] = TilePathfinder.StableStandHorizontalTolerance,
                ["standSamples"] = stands, ["standSamplesOmitted"] = System.Math.Max(0, geometry.Candidates.Count - stands.Count),
                ["structureProtected"] = cell.StructureProtected, ["nativeCanKill"] = cell.NativeCanKill,
                ["actualDistancePixels"] = geometry.ActualDistancePixels, ["actualWithinToolRangeByDistance"] = geometry.ActualInToolReach,
                ["maximumToolRangePixels"] = MiningApproachProbe.ToolReachPixels, ["bodyHeightPixels"] = observer.height,
                ["route"] = "not_tested", ["tool"] = "not_tested", ["returnToOrigin"] = "not_tested", ["observedAtTick"] = Main.GameUpdateCount };
        }
        return new MiningSourceInspection(page.Header, page.Samples, MiningSourcesFrame(observer, owner), Normalize,
            (sx, sy) => observerStart == null ? JsonSerializer.SerializeToNode(InspectMiningInteraction(observer, resource, sx, sy))!.AsObject() :
                InspectSourceWithReturnPreview(observer, resource, sx, sy, observerStart), BeginSample,
            ReadLiveCell, ObserveGeometry);
    }

    /// <summary>Compatibility query: a new local page, at most one sample; never continues the active executor session.</summary>
    public object InspectMiningSources(NPC observer, string query, int? x = null, int? y = null)
    {
        object owner = new();
        MiningSourceInspection inspection = CreateMiningSourcesInspection(observer, query, owner, x, y);
        inspection.Advance(MiningSourcesFrame(observer, owner));
        JsonObject result = inspection.Describe(continuationRetained: false);
        inspection.Cancel();
        return result;
    }

    private void CancelMiningSourceInspection()
    { miningSourcesInspection?.Cancel(); miningSourcesInspection = null; }

    private void TickMiningSources(NPC observer)
    {
        observer.velocity.X = 0; // Native gravity/collision remain active; no observer pose is assigned.
        // An Explore tail can reach this read-only action during a fall.
        // Let ordinary physics settle before freezing the observer's pose.
        if (miningSourcesInspection == null && stepTicks <= 60 && !IsGrounded(observer)) return;
        object owner = currentPlan ?? (object)this;
        miningSourcesInspection ??= CreateMiningSourcesInspection(observer, itemQuery, owner, currentPlan?.X, currentPlan?.Y);
        miningSourcesInspection.Advance(MiningSourcesFrame(observer, owner));
        if (!miningSourcesInspection.IsComplete) return;
        JsonObject result = miningSourcesInspection.Describe(continuationRetained: true);
        bool available = result["available"]?.GetValue<bool>() == true;
        bool inputValid = result["inputValid"]?.GetValue<bool>() == true;
        FinishCurrentStep(JsonSerializer.Serialize(result), available && inputValid);
        CancelMiningSourceInspection();
    }
}
