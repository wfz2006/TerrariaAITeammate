using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerrariaAITeammate.Common;

public sealed record ConstructionSiteRequest(int X, int Y, int Width, int Height,
    int? ExpectedTileType = null, int? ExpectedWallType = null)
{
    public const int MaxWidth = 97;
    public const int MaxHeight = 81;
    public const int LocalObservationDistanceTiles = 64;

    public void ValidateObservationBounds(int worldWidth, int worldHeight, double observerTileX, double observerTileY,
        bool allowRemoteTerrain = false)
    {
        long right = (long)X + Width - 1, bottom = (long)Y + Height - 1;
        if (Width < 1 || Width > MaxWidth || Height < 1 || Height > MaxHeight ||
            X < 2 || Y < 2 || right >= (long)worldWidth - 2 || bottom + 1 >= (long)worldHeight - 2)
            throw new ArgumentException("site矩形及其下方支撑行必须完整位于世界有效范围，不能以未读到的格子代替空气。");
        if (!double.IsFinite(observerTileX) || !double.IsFinite(observerTileY))
            throw new ArgumentException("观察者没有有效的本轮坐标。");
        if (allowRemoteTerrain) return;
        double nx = Math.Clamp(observerTileX, X, right), ny = Math.Clamp(observerTileY, Y, bottom);
        double dx = observerTileX - nx, dy = observerTileY - ny;
        if (dx * dx + dy * dy > LocalObservationDistanceTiles * LocalObservationDistanceTiles)
            throw new ArgumentException($"site矩形距小星超过{LocalObservationDistanceTiles}格；请先靠近实际区域再勘察。");
    }

    public static ConstructionSiteRequest Parse(string query, int? x = null, int? y = null)
    {
        if (x.HasValue != y.HasValue) throw new ArgumentException("site坐标x/y必须成对提供。");
        string[] parts = (query ?? "").Split(';', StringSplitOptions.TrimEntries);
        Match rectangle = Regex.Match(parts[0], @"^\s*(\d+)\s*[,，]\s*(\d+)\s*[,，]\s*(\d+)\s*[,，]\s*(\d+)\s*$");
        if (!rectangle.Success) throw new ArgumentException("site需要item=\"x,y,width,height\"，可追加;tile=方块类型ID;wall=墙类型ID进行同/异比较。");
        int Read(int group) => int.TryParse(rectangle.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value : throw new ArgumentException("site坐标或尺寸超出整数范围。");
        int left = Read(1), top = Read(2), width = Read(3), height = Read(4);
        if (width < 1 || width > MaxWidth || height < 1 || height > MaxHeight ||
            (long)left + width > int.MaxValue || (long)top + height > int.MaxValue)
            throw new ArgumentException($"site尺寸必须为1..{MaxWidth} × 1..{MaxHeight}，坐标不得溢出。");
        if (x.HasValue && (x != left || y != top)) throw new ArgumentException("task.x/y与site矩形左上角不一致，不能猜测施工位置。");
        int? tile = null, wall = null;
        for (int i = 1; i < parts.Length; i++)
        {
            Match expected = Regex.Match(parts[i], @"^(tile|wall)\s*=\s*(\d+)$", RegexOptions.IgnoreCase);
            if (!expected.Success || !int.TryParse(expected.Groups[2].Value, out int type) || type > ushort.MaxValue)
                throw new ArgumentException("site可选比较仅接受tile=非负类型ID、wall=非负类型ID。");
            bool isTile = expected.Groups[1].Value.Equals("tile", StringComparison.OrdinalIgnoreCase);
            if ((isTile && tile.HasValue) || (!isTile && wall.HasValue)) throw new ArgumentException("site材料比较类型不能重复声明。");
            if (isTile) tile = type; else wall = type;
        }
        return new ConstructionSiteRequest(left, top, width, height, tile, wall);
    }
}

public sealed record SiteCell(bool HasTile = false, int TileType = 0, int WallType = 0,
    bool Solid = false, bool Platform = false, bool Door = false, bool Furniture = false,
    int LiquidAmount = 0, int LiquidType = 0, bool Lava = false, bool Actuated = false,
    int Slope = 0, bool HalfBlock = false);
public sealed record SiteActor(string Kind, int Id, string Name, int X, int Y, int Width, int Height);
public sealed record ConstructionSiteScan(int X, int Y, int Width, int Height, SiteCell[] Cells,
    SiteCell[] Below, SiteActor[] Actors, int? ExpectedTileType = null, int? ExpectedWallType = null);
public sealed record SiteRunRow(int y, int[][] runs);

/// <summary>Measures occupancy only. It neither chooses a layout nor approves world mutations.</summary>
public static class ConstructionSiteObservation
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    public static string AnalyzeJson(string scanJson) => JsonSerializer.Serialize(Analyze(
        JsonSerializer.Deserialize<ConstructionSiteScan>(scanJson, ReadOptions) ?? throw new ArgumentException("Missing site scan.")));

    public static object Analyze(ConstructionSiteScan scan)
    {
        if (scan.Width < 1 || scan.Width > ConstructionSiteRequest.MaxWidth || scan.Height < 1 ||
            scan.Height > ConstructionSiteRequest.MaxHeight || scan.Cells == null ||
            scan.Cells.Length != scan.Width * scan.Height || scan.Below == null || scan.Below.Length != scan.Width)
            throw new ArgumentException("Site scan must include every requested cell and the measured support row below.");

        int tileCount = scan.Cells.Count(cell => cell.HasTile), wallCount = scan.Cells.Count(cell => cell.WallType > 0);
        int? matchingTiles = scan.ExpectedTileType.HasValue ? scan.Cells.Count(cell => cell.HasTile && cell.TileType == scan.ExpectedTileType) : null;
        int? differentTiles = scan.ExpectedTileType.HasValue ? scan.Cells.Count(cell => cell.HasTile && cell.TileType != scan.ExpectedTileType) : null;
        int? matchingWalls = scan.ExpectedWallType.HasValue ? scan.Cells.Count(cell => cell.WallType == scan.ExpectedWallType) : null;
        int? differentWalls = scan.ExpectedWallType.HasValue ? scan.Cells.Count(cell => cell.WallType > 0 && cell.WallType != scan.ExpectedWallType) : null;

        var tiles = new List<SiteRunRow>(); var walls = new List<SiteRunRow>();
        var liquids = new List<SiteRunRow>(); var objects = new List<SiteRunRow>();
        for (int dy = 0; dy < scan.Height; dy++)
        {
            SiteCell[] row = scan.Cells.Skip(dy * scan.Width).Take(scan.Width).ToArray();
            tiles.Add(new SiteRunRow(scan.Y + dy, Encode(row, scan.X, cell => new[] {
                cell.HasTile ? cell.TileType : -1, Flag(cell.Solid), Flag(cell.Platform), Flag(cell.Actuated), cell.Slope, Flag(cell.HalfBlock) })));
            walls.Add(new SiteRunRow(scan.Y + dy, Encode(row, scan.X, cell => new[] { cell.WallType })));
            int[][] liquidRow = Encode(row, scan.X, cell => new[] { cell.LiquidType, cell.LiquidAmount, Flag(cell.Lava) }, cell => cell.LiquidAmount > 0);
            if (liquidRow.Length > 0) liquids.Add(new SiteRunRow(scan.Y + dy, liquidRow));
            int[][] objectRow = Encode(row, scan.X, cell => new[] { cell.TileType, Flag(cell.Door), Flag(cell.Furniture) }, cell => cell.HasTile && (cell.Door || cell.Furniture));
            if (objectRow.Length > 0) objects.Add(new SiteRunRow(scan.Y + dy, objectRow));
        }

        bool Support(SiteCell cell) => !cell.Actuated && (cell.Solid || cell.Platform);
        int touchingSupport = 0;
        for (int dy = 0; dy < scan.Height; dy++)
        for (int dx = 0; dx < scan.Width; dx++)
        {
            if (scan.Cells[dy * scan.Width + dx].HasTile) continue;
            bool Near(int x, int y) => x >= 0 && x < scan.Width && y >= 0 && y <= scan.Height &&
                Support(y == scan.Height ? scan.Below[x] : scan.Cells[y * scan.Width + x]);
            if (Near(dx - 1, dy) || Near(dx + 1, dy) || Near(dx, dy - 1) || Near(dx, dy + 1)) touchingSupport++;
        }

        SiteActor[] actors = (scan.Actors ?? Array.Empty<SiteActor>()).Where(actor => actor.Width > 0 && actor.Height > 0 &&
            (long)actor.X < (long)scan.X + scan.Width && (long)actor.X + actor.Width > scan.X &&
            (long)actor.Y < (long)scan.Y + scan.Height && (long)actor.Y + actor.Height > scan.Y).ToArray();
        return new
        {
            kind = "construction_site_observation",
            bounds = new { x = scan.X, y = scan.Y, width = scan.Width, height = scan.Height },
            totalCells = scan.Width * scan.Height, occupiedTileCells = tileCount, emptyTileCells = scan.Cells.Length - tileCount,
            occupiedWallCells = wallCount, emptyWallCells = scan.Cells.Length - wallCount,
            allTileCellsEmpty = tileCount == 0, allWallCellsEmpty = wallCount == 0,
            occupiedAboveBottomRow = scan.Cells.Take((scan.Height - 1) * scan.Width).Count(cell => cell.HasTile),
            solidCells = scan.Cells.Count(cell => cell.HasTile && cell.Solid && !cell.Actuated),
            platformCells = scan.Cells.Count(cell => cell.HasTile && cell.Platform),
            doorCells = scan.Cells.Count(cell => cell.HasTile && cell.Door),
            furnitureCells = scan.Cells.Count(cell => cell.HasTile && cell.Furniture),
            liquidCells = scan.Cells.Count(cell => cell.LiquidAmount > 0), lavaCells = scan.Cells.Count(cell => cell.Lava && cell.LiquidAmount > 0),
            tileComparison = new { expectedType = scan.ExpectedTileType, matching = matchingTiles, different = differentTiles, empty = scan.Cells.Length - tileCount },
            wallComparison = new { expectedType = scan.ExpectedWallType, matching = matchingWalls, different = differentWalls, empty = scan.Cells.Length - wallCount },
            tileRunColumns = new[] { "x", "count", "actualTileType(-1=air)", "solid", "platform", "actuated", "slope", "halfBlock" }, tileRuns = tiles,
            wallRunColumns = new[] { "x", "count", "actualWallType(0=none)" }, wallRuns = walls,
            liquidRunColumns = new[] { "x", "count", "liquidType", "amount", "lava" }, liquidRuns = liquids,
            objectRunColumns = new[] { "x", "count", "actualTileType", "door", "furniture" }, objectRuns = objects,
            supportBelow = new { y = scan.Y + scan.Height, columns = new[] { "x", "count", "tileType(-1=air)", "solid", "platform", "actuated", "slope", "halfBlock" },
                runs = Encode(scan.Below, scan.X, cell => new[] { cell.HasTile ? cell.TileType : -1, Flag(cell.Solid), Flag(cell.Platform), Flag(cell.Actuated), cell.Slope, Flag(cell.HalfBlock) }) },
            supportedBottomColumns = scan.Below.Count(Support), emptyCellsTouchingObservedSupport = touchingSupport,
            actorConflicts = actors.Select(actor => new { kind = actor.Kind, id = actor.Id, name = actor.Name,
                x = actor.X, y = actor.Y, width = actor.Width, height = actor.Height }).ToArray(),
            note = "All counts and runs are measured facts in this rectangle. Existing matching blocks/walls remain occupied, not empty; a supporting roof does not make the entire upper volume clear. Entity conflicts are current and may move. Support adjacency is limited to this rectangle and its row below; it does not approve any placement. No demolition, site choice or construction is performed."
        };
    }

    private static int Flag(bool value) => value ? 1 : 0;
    private static int[][] Encode(IReadOnlyList<SiteCell> row, int left, Func<SiteCell, int[]> properties,
        Func<SiteCell, bool>? include = null)
    {
        var runs = new List<int[]>();
        int start = 0;
        while (start < row.Count)
        {
            if (include != null && !include(row[start])) { start++; continue; }
            int[] values = properties(row[start]); int end = start + 1;
            while (end < row.Count && (include == null || include(row[end])) && values.SequenceEqual(properties(row[end]))) end++;
            runs.Add(new[] { left + start, end - start }.Concat(values).ToArray()); start = end;
        }
        return runs.ToArray();
    }
}
