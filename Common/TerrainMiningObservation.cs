using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

public sealed record MiningTerrainCell(bool HasTile, int ActualTileType, string ResourceTarget,
    bool Actuated, bool StructureProtected, bool NativeCanKill);
public sealed record TerrainMiningScan(int X, int Y, int Width, int Height, MiningTerrainCell[] Cells);

/// <summary>Compact independent facts, never a claim about tools, reachability or successful mining.</summary>
public static class TerrainMiningObservation
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    public static string AnalyzeJson(string json) => JsonSerializer.Serialize(Analyze(
        JsonSerializer.Deserialize<TerrainMiningScan>(json, ReadOptions) ?? throw new ArgumentException("Missing mining scan.")));

    public static object Analyze(TerrainMiningScan scan)
    {
        if (scan.Width < 1 || scan.Width > ConstructionSiteRequest.MaxWidth || scan.Height < 1 ||
            scan.Height > ConstructionSiteRequest.MaxHeight || scan.Cells == null || scan.Cells.Length != scan.Width * scan.Height)
            throw new ArgumentException("Mining facts require every cell of the bounded terrain rectangle.");
        string[] classes = new[] { "none" }.Concat(scan.Cells.Where(cell => cell.HasTile && !string.IsNullOrEmpty(cell.ResourceTarget))
            .Select(cell => cell.ResourceTarget).Distinct().OrderBy(target => target, StringComparer.Ordinal)).ToArray();
        var protection = new List<SiteRunRow>();
        var resources = new List<SiteRunRow>();
        int[][] Encode(int row, Func<MiningTerrainCell, int[]> values)
        {
            var runs = new List<int[]>();
            int column = 0;
            while (column < scan.Width)
            {
                MiningTerrainCell cell = scan.Cells[row * scan.Width + column];
                if (!cell.HasTile) { column++; continue; }
                int[] state = values(cell);
                int start = column++;
                while (column < scan.Width && scan.Cells[row * scan.Width + column].HasTile &&
                    state.SequenceEqual(values(scan.Cells[row * scan.Width + column]))) column++;
                runs.Add(new[] { scan.X + start, column - start }.Concat(state).ToArray());
            }
            return runs.ToArray();
        }
        for (int row = 0; row < scan.Height; row++)
        {
            int[][] flags = Encode(row, cell => new[] { cell.StructureProtected ? 1 : 0, cell.NativeCanKill ? 1 : 0, cell.Actuated ? 1 : 0 });
            int[][] targets = Encode(row, cell => new[] { string.IsNullOrEmpty(cell.ResourceTarget) ? 0 : Array.IndexOf(classes, cell.ResourceTarget) });
            if (flags.Length > 0) protection.Add(new SiteRunRow(scan.Y + row, flags));
            if (targets.Length > 0) resources.Add(new SiteRunRow(scan.Y + row, targets));
        }
        return new
        {
            bounds = new { x = scan.X, y = scan.Y, width = scan.Width, height = scan.Height },
            occupiedCellCount = scan.Cells.Count(cell => cell.HasTile),
            miningProtectionColumns = new[] { "x", "count", "structureProtected", "nativeCanKill", "actuated" },
            miningProtectionRuns = protection,
            resourceColumns = new[] { "x", "count", "resourceClassIndex" },
            resourceClasses = classes, resourceTargetRuns = resources,
            toolChecked = false, routeChecked = false,
            note = "两组逐行runs仅列有tile的格子，未列坐标为空气（实际type仍见measurement.tileRuns）。0/1为独立布尔事实，none表示无已支持mine资源分类。structureProtected与执行器同一房屋/箱子保护判定；nativeCanKill仅本轮原生CanKillTile谓词，不包含工具、路线、交互距离或实际掉落验证。任何一项都不是可挖保证。"
        };
    }
}
