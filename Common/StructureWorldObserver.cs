using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Terraria;
using Terraria.ID;
using Terraria.GameContent;
using Terraria.ObjectData;

namespace TerrariaAITeammate.Common;

public sealed record StructureReading(string topic, bool available, string error, int worldId,
    ulong observedAtTick, StructureSurvey? observation, object? referenceAtSelection,
    StructureSamplingInfo? sampling = null, PlacementCatalogResult? placementCatalog = null);

/// <summary>Samples the current world on the game thread, never a template or model memory.</summary>
public static class StructureWorldObserver
{
    public const int MaxAnchorDistance = 64;
    private const int RadiusX = 40;
    private const int RadiusY = 32;
    private static int selectedWorld = int.MinValue;
    private static int selectedX;
    private static int selectedY;
    private static int requestedX;
    private static int requestedY;
    private static StructureReading? latest;
    private static object? reference;

    internal static string ReadWithoutChangingSelection(Func<string> read)
    {
        ResetOnWorldChange();
        var selection = (selectedWorld, selectedX, selectedY, requestedX, requestedY, latest, reference);
        try { return read(); }
        finally
        {
            (selectedWorld, selectedX, selectedY, requestedX, requestedY, latest, reference) = selection;
        }
    }

    public static StructureReading Query(string query, NPC npc, int? x = null, int? y = null)
    {
        ResetOnWorldChange();
        if (x.HasValue != y.HasValue)
            return Unavailable("观察坐标必须同时包含x和y。");
        if (!x.HasValue && !string.IsNullOrWhiteSpace(query) &&
            query is not ("nearest" or "here" or "附近" or "当前" or "原屋" or "house" or "structure"))
        {
            Match coordinates = Regex.Match(query, @"^\s*\(?\s*(?:x\s*=\s*)?(\d+)\s*[,，\s]+\s*(?:y\s*=\s*)?(\d+)\s*\)?\s*$", RegexOptions.IgnoreCase);
            if (!coordinates.Success || !int.TryParse(coordinates.Groups[1].Value, out int parsedX) ||
                !int.TryParse(coordinates.Groups[2].Value, out int parsedY))
                return Unavailable("structure/house只接受附近实景或方块坐标，例如item=\"3244,379\"；shelter是模板名称，不是原屋实测。");
            x = parsedX; y = parsedY;
        }

        int anchorX = x ?? (int)(npc.Center.X / 16f), anchorY = y ?? (int)(npc.Center.Y / 16f);
        if (!CanObserve(npc, anchorX, anchorY, out string error)) return Unavailable(error);
        requestedX = anchorX; requestedY = anchorY;
        CalibratedStructure calibrated = Calibrate(observer: npc, anchorX, anchorY, anchorX, anchorY);
        StructureSurvey survey = calibrated.observation;
        selectedX = calibrated.sampling.sampledCenter.x; selectedY = calibrated.sampling.sampledCenter.y;
        reference = new
        {
            source = "historical_live_world_at_selection", observedAtTick = Main.GameUpdateCount,
            survey.signature, survey.bounds, survey.truncated, survey.surfaces, survey.objects,
            note = "这是选中原屋时的实测参考；当前世界的最新几何见observation，不能用这个旧参考覆盖新观察。"
        };
        latest = Reading(calibrated);
        Remember(survey);
        return latest;
    }

    public static StructureReading QueryDetail(string query, NPC npc, int? x = null, int? y = null)
    {
        ResetOnWorldChange();
        string selection = (query ?? "").Trim();
        bool selectedRequest = selection.Length == 0 || selection.ToLowerInvariant() is
            "selected" or "已选" or "nearest" or "here" or "附近" or "当前" or "原屋" or "house" or "structure" or "structure_detail";
        if (x.HasValue || y.HasValue || !selectedRequest)
            return Query(selection, npc, x, y) with { topic = "structure_detail" };
        if (latest?.observation?.found != true)
            return Unavailable("没有有效的已选结构。先用structure/house观察并选择，或给structure_detail明确x/y；无坐标的明细查询不会另选最近目标。")
                with { topic = "structure_detail" };

        // Detail is an explicit return value, never a sticky mode of the live selection.
        // Refresh keeps the original selection reference and validates current visibility.
        return (RefreshLatest(npc) ?? Unavailable("先前选择已失效，请重新明确选择结构。"))
            with { topic = "structure_detail" };
    }

    public static StructureReading? RefreshLatest(NPC? npc)
    {
        ResetOnWorldChange();
        if (latest == null) return null;
        if (npc == null || !CanObserve(npc, selectedX, selectedY, out _))
            return Unavailable("先前选中的结构目前不在可观察范围；旧记忆不是当前几何，需要靠近再观察。");
        CalibratedStructure calibrated = Calibrate(npc, requestedX, requestedY, selectedX, selectedY);
        StructureSurvey survey = calibrated.observation;
        selectedX = calibrated.sampling.sampledCenter.x; selectedY = calibrated.sampling.sampledCenter.y;
        latest = Reading(calibrated);
        Remember(survey);
        return latest;
    }

    private static void ResetOnWorldChange()
    {
        if (selectedWorld == Main.worldID) return;
        selectedWorld = Main.worldID;
        latest = null; reference = null;
    }

    private static StructureReading Unavailable(string error) => new("structure", false, error,
        Main.worldID, Main.GameUpdateCount, null, null);

    private static CalibratedStructure Calibrate(NPC observer, int anchorX, int anchorY, int centerX, int centerY) =>
        StructureWindowCalibration.Calibrate(anchorX, anchorY, centerX, centerY,
            (x, y) => SampleCentered(x, y, anchorX, anchorY),
            (x, y) => CanObserve(observer, x, y, out _), maxRecenters: 2);

    private static StructureReading Reading(CalibratedStructure calibrated) => new("structure",
        !calibrated.observation.truncated,
        calibrated.observation.truncated
            ? "仅取得局部结构，完整尺寸未知。observation保留本轮实测partial网格；bounds只能作为可见包络，不能当整屋尺寸或可施工矩形。" : "",
        Main.worldID, Main.GameUpdateCount, calibrated.observation, reference, calibrated.sampling,
        PlacementCatalogWorldObserver.Read(calibrated.observation.grid.x, calibrated.observation.grid.y,
            calibrated.observation.grid.width, calibrated.observation.grid.height));

    private static bool CanObserve(NPC npc, int x, int y, out string error)
    {
        if (!WorldGen.InWorld(x, y, 2))
        { error = "这个坐标不在世界有效范围内。"; return false; }
        double dx = npc.Center.X / 16f - x, dy = npc.Center.Y / 16f - y;
        if (dx * dx + dy * dy > MaxAnchorDistance * MaxAnchorDistance)
        { error = $"目标超出{MaxAnchorDistance}格观察范围；先移动到实际可站位置附近。come只会靠近玩家，不会读取原屋。"; return false; }
        error = ""; return true;
    }

    private static StructureSurvey Sample(int anchorX, int anchorY) => SampleCentered(anchorX, anchorY, anchorX, anchorY);

    private static StructureSurvey SampleCentered(int centerX, int centerY, int selectionX, int selectionY)
    {
        var tileItems = ContentSamples.ItemsByType.Values.Where(item => item.createTile >= TileID.Dirt)
            .GroupBy(item => (item.createTile, item.placeStyle)).ToDictionary(group => group.Key, group => group.OrderBy(item => item.type).First());
        var wallItems = ContentSamples.ItemsByType.Values.Where(item => item.createWall > 0)
            .GroupBy(item => item.createWall).ToDictionary(group => group.Key, group => group.OrderBy(item => item.type).First());
        int left = Math.Max(2, centerX - RadiusX), top = Math.Max(2, centerY - RadiusY);
        int right = Math.Min(Main.maxTilesX - 3, centerX + RadiusX);
        int bottom = Math.Min(Main.maxTilesY - 3, centerY + RadiusY);
        var cells = new List<StructureCell>((right - left + 1) * (bottom - top + 1));
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            Tile tile = Main.tile[x, y];
            int type = tile.HasTile ? tile.TileType : 0;
            bool platform = tile.HasTile && (Main.tileSolidTop[type] || TileID.Sets.Platforms[type]);
            bool door = tile.HasTile && type is TileID.ClosedDoor or TileID.OpenDoor;
            bool solid = tile.HasTile && Main.tileSolid[type] && !platform && !tile.IsActuated;
            bool furniture = tile.HasTile && Main.tileFrameImportant[type] && !solid && !platform &&
                type is not (TileID.Trees or TileID.PalmTree or TileID.Cactus or TileID.Vines or TileID.Plants or TileID.Plants2);
            string tileName = tile.HasTile ? TileID.Search.GetName(type) ?? type.ToString() : "air";
            bool structural = tile.HasTile && (door || platform || furniture || IsConstructionMaterial(tileName));
            bool safeWall = tile.WallType > WallID.None && tile.WallType < Main.wallHouse.Length && Main.wallHouse[tile.WallType];
            int style = 0, alternate = 0;
            if (tile.HasTile)
            {
                try { TileObjectData.GetTileInfo(tile, ref style, ref alternate); }
                catch { style = 0; }
            }
            tileItems.TryGetValue((type, style), out Item? tileItem);
            wallItems.TryGetValue(tile.WallType, out Item? wallItem);
            cells.Add(new StructureCell(tile.HasTile, type, tile.WallType, solid, platform, door, furniture,
                structural, safeWall, tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Lava,
                tile.HasTile ? tile.TileFrameX : 0, tile.HasTile ? tile.TileFrameY : 0, tileName,
                tile.WallType == WallID.None ? "none" : WallID.Search.GetName(tile.WallType) ?? tile.WallType.ToString(),
                tile.HasTile ? tileItem?.type ?? 0 : 0, tile.HasTile ? tileItem?.Name ?? "" : "",
                wallItem?.type ?? 0, wallItem?.Name ?? "", tile.IsActuated, tile.IsHalfBlock, (int)tile.Slope));
        }
        var scan = new StructureScan(left, top, right - left + 1, bottom - top + 1, cells.ToArray());
        return StructureObservation.Analyze(scan, selectionX, selectionY, (x, supportRow) =>
        {
            // The 20px body spans the center tile and both neighbours. Keep the entire
            // candidate inside the sampled grid; this is a geometry query, never a route search.
            if (!scan.Contains(x - 1, supportRow) || !scan.Contains(x + 1, supportRow) ||
                !TilePathfinder.IsSafeStand(x, supportRow - 1)) return null;
            return TilePathfinder.StandBottom(new Microsoft.Xna.Framework.Point(x, supportRow - 1)) / 16d;
        });
    }

    private static bool IsConstructionMaterial(string name) => name.Contains("Brick", StringComparison.Ordinal) ||
        name.Contains("Beam", StringComparison.Ordinal) || name.Contains("Plank", StringComparison.Ordinal) ||
        name.EndsWith("Wood", StringComparison.Ordinal) || name.EndsWith("Slab", StringComparison.Ordinal) || name == "WoodBlock";

    private static void Remember(StructureSurvey survey)
    {
        string fact = survey.found && survey.bounds != null
            ? survey.truncated
                ? $"只取得局部结构可见包络 ({survey.bounds.x},{survey.bounds.y}) {survey.bounds.width}x{survey.bounds.height}，完整尺寸未知，signature={survey.signature}；不得把partial尺寸或shelter模板当原屋整体尺寸。"
                : $"实测结构 ({survey.bounds.x},{survey.bounds.y}) {survey.bounds.width}x{survey.bounds.height}，signature={survey.signature}，truncated=false；原屋尺寸以当前observation为准，shelter模板不能作为实测。"
            : $"在({survey.anchorX},{survey.anchorY})附近本次未识别到房屋；不能宣称已读取原屋，signature={survey.signature}。";
        AITeammateSystem.Instance.Memory.Observe("structure", fact);
    }
}
