using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

public sealed record StructureCell(bool HasTile = false, int TileType = 0, int WallType = 0,
    bool Solid = false, bool Platform = false, bool Door = false, bool Furniture = false,
    bool Structural = false, bool SafeWall = false, bool Hazard = false,
    int FrameX = 0, int FrameY = 0, string TileName = "", string WallName = "",
    int TileItemId = 0, string TileItemName = "", int WallItemId = 0, string WallItemName = "",
    bool? Actuated = null, bool? HalfBlock = null, int? Slope = null);

public sealed record StructureScan(int X, int Y, int Width, int Height, StructureCell[] Cells)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
    public StructureCell At(int x, int y) => Cells[(y - Y) * Width + x - X];
}

public sealed record StructureBounds(int x, int y, int width, int height);
public sealed record StructureSurface(double y, int left, int right, bool platform);
public sealed record StructureStandingPosition(int x, double feetY);
public sealed record StructureObject(int type, string name, int x, int y, int width, int height, bool door);
public sealed record StructureRoom(string kind, StructureBounds bounds, int openCellCount, double[] standingFeetRows);
public sealed record StructureLegend(int key, bool hasTile, int tileType, string tileName,
    int wallType, string wallName, bool solid, bool platform, bool door, bool furniture,
    bool safeWall, bool hazard, int frameX, int frameY,
    int tileItemId, string tileItemName, int wallItemId, string wallItemName,
    bool? actuated = null, bool? halfBlock = null, int? slope = null);
public sealed record StructureGrid(int x, int y, int width, int height,
    string encoding, string[] rows, StructureLegend[] legend);
public sealed record StructureSurvey(string kind, string source, bool found, int anchorX, int anchorY,
    StructureBounds? bounds, bool truncated, string signature, StructureGrid grid,
    StructureBounds[] candidates, StructureSurface[] surfaces,
    StructureStandingPosition[] standingPositions, StructureObject[] objects, StructureRoom[] rooms, string note);

/// <summary>Reports measured geometry. It does not choose or design any building.</summary>
public static class StructureObservation
{
    public const int MaxWidth = 97;
    public const int MaxHeight = 81;
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static string AnalyzeJson(string scanJson, int anchorX, int anchorY)
    {
        StructureScan scan = JsonSerializer.Deserialize<StructureScan>(scanJson, ReadOptions)
            ?? throw new ArgumentException("Missing sampled grid.");
        return JsonSerializer.Serialize(Analyze(scan, anchorX, anchorY));
    }

    public static StructureSurvey Analyze(StructureScan scan, int anchorX, int anchorY,
        Func<int, int, double?>? standingFeet = null)
    {
        if (scan.Width < 1 || scan.Height < 1 || scan.Width > MaxWidth || scan.Height > MaxHeight ||
            scan.Cells == null || scan.Cells.Length != scan.Width * scan.Height)
            throw new ArgumentException("Structure observation requires a bounded, complete sampled grid.");

        bool[] relevant = new bool[scan.Cells.Length];
        bool hasSafeWalls = scan.Cells.Any(cell => cell.SafeWall);
        for (int y = scan.Y; y < scan.Y + scan.Height; y++)
        for (int x = scan.X; x < scan.X + scan.Width; x++)
        {
            StructureCell cell = scan.At(x, y);
            bool touchingWall = false;
            if (cell.HasTile && (cell.Solid || cell.Platform || cell.Door || cell.Furniture))
                for (int dy = -1; dy <= 1 && !touchingWall; dy++)
                for (int dx = -1; dx <= 1 && !touchingWall; dx++)
                    touchingWall = scan.Contains(x + dx, y + dy) && scan.At(x + dx, y + dy).SafeWall;
            // Long outdoor platforms and loose furniture must not fuse every attached building.
            // Safe-wall regions identify a house; only their touching boundary tiles join it.
            relevant[(y - scan.Y) * scan.Width + x - scan.X] = cell.SafeWall || touchingWall ||
                (!hasSafeWalls && cell.HasTile && cell.Structural);
        }

        bool[] visited = new bool[relevant.Length];
        var candidates = new List<StructureBounds>();
        for (int index = 0; index < relevant.Length; index++)
        {
            if (!relevant[index] || visited[index]) continue;
            var queue = new Queue<int>();
            queue.Enqueue(index); visited[index] = true;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            int wallCells = 0, solidCells = 0;
            while (queue.TryDequeue(out int current))
            {
                int x = scan.X + current % scan.Width, y = scan.Y + current / scan.Width;
                StructureCell cell = scan.Cells[current];
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                if (cell.SafeWall) wallCells++;
                if (cell.HasTile && (cell.Solid || cell.Platform)) solidCells++;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx == 0 && dy == 0) || !scan.Contains(x + dx, y + dy)) continue;
                    int next = (y + dy - scan.Y) * scan.Width + x + dx - scan.X;
                    if (visited[next] || !relevant[next]) continue;
                    visited[next] = true; queue.Enqueue(next);
                }
            }
            // A loose chair, a single torch or natural ground is not proof of a building.
            if ((wallCells >= 4 || solidCells >= 6) && maxX - minX >= 2 && maxY - minY >= 2)
                candidates.Add(new StructureBounds(minX, minY, maxX - minX + 1, maxY - minY + 1));
        }

        StructureBounds[] ordered = candidates.OrderBy(bounds => Distance(bounds, anchorX, anchorY)).Take(8).ToArray();
        StructureBounds? selected = ordered.FirstOrDefault();
        StructureBounds region = selected == null
            ? new StructureBounds(scan.X, scan.Y, scan.Width, scan.Height)
            : Expand(selected, scan, 2, 4);
        bool clipped = selected != null && (selected.x == scan.X || selected.y == scan.Y ||
            selected.x + selected.width == scan.X + scan.Width || selected.y + selected.height == scan.Y + scan.Height);

        var surfaces = new List<StructureSurface>();
        var standing = new List<StructureStandingPosition>();
        if (selected != null)
            for (int y = region.y + 3; y < region.y + region.height; y++)
            {
                var row = new List<StructureStandingPosition>();
                for (int x = region.x; x < region.x + region.width - 1; x++)
                {
                    double? feetY = standingFeet == null ? SampledStandingFeet(scan, x, y) : standingFeet(x, y);
                    if (feetY.HasValue) row.Add(new StructureStandingPosition(x, feetY.Value));
                }
                if (row.Count == 0) continue;
                foreach (var level in row.GroupBy(point => point.feetY))
                foreach (var group in Runs(level.Select(point => point.x)))
                {
                    if (group.Right - group.Left < 1) continue;
                    bool platform = Enumerable.Range(group.Left, group.Right - group.Left + 1)
                        .Any(x => scan.At(x, y).Platform && scan.At(x, y).Actuated != true);
                    surfaces.Add(new StructureSurface(level.Key, group.Left, group.Right + 1, platform));
                }
                int stride = Math.Max(1, row.Count / 5);
                standing.AddRange(row.Where((point, i) => i % stride == 0).Take(6));
            }

        StructureGrid map = Encode(scan, region);
        string signature = Signature(scan, region);
        string standingNote = standingFeet == null
            ? " Standing candidates use approximate sampled-grid clearance; missing physical flags remain unknown, and known slopes are not treated as full-block support."
            : " Standing candidates use the companion's read-only native body/support geometry; x is its center tile and feetY is measured in tiles, including half tiles. They do not prove a travel route.";
        return new StructureSurvey("structure_observation", "live_world", selected != null,
            anchorX, anchorY, selected, clipped, signature, map, ordered, surfaces.Take(48).ToArray(),
            standing.Take(96).ToArray(), Objects(scan, region), Rooms(scan, region, standing),
            (selected == null
                ? "No building was identified in this measured area. A shelter template is not an observed house."
                : clipped
                    ? "Only the sampled part is known; the structure reaches the scan edge. Dimensions are observed extents, not a complete-house claim."
                    : "Bounds come from connected safe walls and touching structural tiles. Grid cells are measured facts; surfaces include roofs and platforms, not inferred room plans. No housing-validity claim is made.") + standingNote);
    }

    private static int Distance(StructureBounds bounds, int x, int y)
    {
        int dx = Math.Max(bounds.x - x, Math.Max(0, x - bounds.x - bounds.width + 1));
        int dy = Math.Max(bounds.y - y, Math.Max(0, y - bounds.y - bounds.height + 1));
        return (dx + dy) * 1000 + Math.Abs(x - bounds.x - bounds.width / 2) + Math.Abs(y - bounds.y - bounds.height / 2);
    }

    private static StructureBounds Expand(StructureBounds bounds, StructureScan scan, int marginX, int marginY)
    {
        int x = Math.Max(scan.X, bounds.x - marginX), y = Math.Max(scan.Y, bounds.y - marginY);
        int right = Math.Min(scan.X + scan.Width, bounds.x + bounds.width + marginX);
        int bottom = Math.Min(scan.Y + scan.Height, bounds.y + bounds.height + marginY);
        return new StructureBounds(x, y, right - x, bottom - y);
    }

    private static double? SampledStandingFeet(StructureScan scan, int x, int supportY)
    {
        if (!scan.Contains(x + 1, supportY) || !scan.Contains(x, supportY - 3)) return null;
        bool Support(StructureCell cell) => cell.HasTile && cell.Actuated != true &&
            (cell.Solid || cell.Platform) && (cell.Slope == null || cell.Slope == 0);
        double feetY = double.PositiveInfinity;
        for (int dx = 0; dx <= 1; dx++)
        {
            StructureCell support = scan.At(x + dx, supportY);
            if (support.Hazard) return null;
            if (Support(support)) feetY = Math.Min(feetY, supportY + (support.HalfBlock == true ? 0.5 : 0));
        }
        if (double.IsPositiveInfinity(feetY)) return null;
        for (int dx = 0; dx <= 1; dx++)
        for (int dy = 1; dy <= 3; dy++)
        {
            StructureCell cell = scan.At(x + dx, supportY - dy);
            if ((cell.Solid && !cell.Platform && cell.Actuated != true) || cell.Hazard) return null;
        }
        // A fractional stand must not overlap another active, non-half support in the same row.
        if (feetY > supportY && Enumerable.Range(x, 2).Select(column => scan.At(column, supportY))
            .Any(cell => cell.HasTile && cell.Actuated != true && cell.Solid && cell.HalfBlock != true)) return null;
        return feetY;
    }

    private static IEnumerable<(int Left, int Right)> Runs(IEnumerable<int> positions)
    {
        int start = -1, last = -1;
        foreach (int position in positions)
        {
            if (start < 0) { start = last = position; continue; }
            if (position != last + 1) { yield return (start, last); start = position; }
            last = position;
        }
        if (start >= 0) yield return (start, last);
    }

    private static StructureObject[] Objects(StructureScan scan, StructureBounds region)
    {
        var found = new List<StructureObject>();
        var visited = new HashSet<(int X, int Y)>();
        for (int y = region.y; y < region.y + region.height; y++)
        for (int x = region.x; x < region.x + region.width; x++)
        {
            StructureCell cell = scan.At(x, y);
            if (!cell.HasTile || !(cell.Door || cell.Furniture) || !visited.Add((x, y))) continue;
            int right = x, bottom = y;
            var queue = new Queue<(int X, int Y)>(); queue.Enqueue((x, y));
            int left = x, top = y;
            while (queue.TryDequeue(out var current))
            {
                left = Math.Min(left, current.X); top = Math.Min(top, current.Y);
                right = Math.Max(right, current.X); bottom = Math.Max(bottom, current.Y);
                foreach (var next in new[] { (current.X - 1, current.Y), (current.X + 1, current.Y), (current.X, current.Y - 1), (current.X, current.Y + 1) })
                {
                    if (!scan.Contains(next.Item1, next.Item2) || visited.Contains(next)) continue;
                    StructureCell other = scan.At(next.Item1, next.Item2);
                    if (other.HasTile && other.TileType == cell.TileType && (other.Door || other.Furniture))
                    { visited.Add(next); queue.Enqueue(next); }
                }
            }
            found.Add(new StructureObject(cell.TileType, cell.TileName, left, top, right - left + 1, bottom - top + 1, cell.Door));
            if (found.Count >= 80) return found.ToArray();
        }
        return found.ToArray();
    }

    private static StructureRoom[] Rooms(StructureScan scan, StructureBounds region,
        IReadOnlyList<StructureStandingPosition> standing)
    {
        var rooms = new List<StructureRoom>();
        var visited = new HashSet<(int X, int Y)>();
        bool Open(int x, int y) => x >= region.x && y >= region.y && x < region.x + region.width && y < region.y + region.height &&
            scan.At(x, y).SafeWall && !scan.At(x, y).Solid && !scan.At(x, y).Platform;
        for (int y = region.y; y < region.y + region.height; y++)
        for (int x = region.x; x < region.x + region.width; x++)
        {
            if (!Open(x, y) || !visited.Add((x, y))) continue;
            var queue = new Queue<(int X, int Y)>(); queue.Enqueue((x, y));
            int minX = x, maxX = x, minY = y, maxY = y, count = 0;
            while (queue.TryDequeue(out var current))
            {
                count++; minX = Math.Min(minX, current.X); maxX = Math.Max(maxX, current.X);
                minY = Math.Min(minY, current.Y); maxY = Math.Max(maxY, current.Y);
                foreach (var next in new[] { (current.X - 1, current.Y), (current.X + 1, current.Y), (current.X, current.Y - 1), (current.X, current.Y + 1) })
                    if (Open(next.Item1, next.Item2) && visited.Add(next)) queue.Enqueue(next);
            }
            if (count < 6) continue;
            rooms.Add(new StructureRoom("measured_wall_backed_open_space_not_housing_validation",
                new StructureBounds(minX, minY, maxX - minX + 1, maxY - minY + 1), count,
                standing.Where(point => point.x >= minX && point.x <= maxX && point.feetY > minY && Math.Floor(point.feetY) <= maxY + 1)
                    .Select(point => point.feetY).Distinct().OrderBy(row => row).ToArray()));
            if (rooms.Count >= 24) return rooms.ToArray();
        }
        return rooms.ToArray();
    }

    private static StructureGrid Encode(StructureScan scan, StructureBounds bounds)
    {
        var keys = new Dictionary<StructureCell, int>();
        var legend = new List<StructureLegend>();
        var rows = new List<string>();
        for (int y = bounds.y; y < bounds.y + bounds.height; y++)
        {
            var runs = new List<string>();
            int last = -1, count = 0;
            for (int x = bounds.x; x < bounds.x + bounds.width; x++)
            {
                StructureCell cell = scan.At(x, y);
                if (!keys.TryGetValue(cell, out int key))
                {
                    key = keys.Count; keys.Add(cell, key);
                    legend.Add(new StructureLegend(key, cell.HasTile, cell.TileType, cell.TileName,
                        cell.WallType, cell.WallName, cell.Solid, cell.Platform, cell.Door,
                        cell.Furniture, cell.SafeWall, cell.Hazard, cell.FrameX, cell.FrameY,
                        cell.TileItemId, cell.TileItemName, cell.WallItemId, cell.WallItemName,
                        cell.Actuated, cell.HalfBlock, cell.Slope));
                }
                if (last == key) count++;
                else { if (count > 0) runs.Add($"{last}:{count}"); last = key; count = 1; }
            }
            runs.Add($"{last}:{count}"); rows.Add(string.Join(",", runs));
        }
        return new StructureGrid(bounds.x, bounds.y, bounds.width, bounds.height,
            "Each rows[y-grid.y] covers every x left-to-right. Runs are legendKey:cellCount. Expand runs to recover the exact tile and wall at each coordinate.",
            rows.ToArray(), legend.ToArray());
    }

    private static string Signature(StructureScan scan, StructureBounds bounds)
    {
        ulong hash = 14695981039346656037UL;
        void Mix(int value) { hash ^= unchecked((uint)value); hash *= 1099511628211UL; }
        Mix(bounds.x); Mix(bounds.y); Mix(bounds.width); Mix(bounds.height);
        for (int y = bounds.y; y < bounds.y + bounds.height; y++)
        for (int x = bounds.x; x < bounds.x + bounds.width; x++)
        {
            StructureCell cell = scan.At(x, y);
            Mix(cell.HasTile ? cell.TileType + 1 : 0); Mix(cell.WallType);
            Mix(cell.FrameX); Mix(cell.FrameY); Mix(cell.Platform ? 1 : 0); Mix(cell.Hazard ? 1 : 0);
            Mix(cell.Actuated.HasValue ? cell.Actuated.Value ? 2 : 1 : 0);
            Mix(cell.HalfBlock.HasValue ? cell.HalfBlock.Value ? 2 : 1 : 0);
            Mix(cell.Slope.HasValue ? 1 : 0);
            if (cell.Slope.HasValue) Mix(cell.Slope.Value);
        }
        return hash.ToString("x16");
    }
}
