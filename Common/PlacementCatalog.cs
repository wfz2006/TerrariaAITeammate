using System;
using System.Linq;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

public sealed record CatalogItem(int itemId, string name, int tileType = -1, int wallType = 0, int placeStyle = 0);
public sealed record CatalogStyle(bool styleKnown, int? observedStyle, string styleSource);
public sealed record CatalogTile(int tileType, string name, int? observedStyle, bool styleKnown, string styleSource);
public sealed record CatalogWall(int wallType, string name);
public sealed record PlacementCandidate(int itemId, string name, int placeStyle);
public sealed record TileCatalogEntry(int tileType, string name, int? observedStyle, bool styleKnown, string styleSource,
    PlacementCandidate[] placementCandidates, int candidateCount, bool candidatesTruncated);
public sealed record WallCatalogEntry(int wallType, string name, PlacementCandidate[] placementCandidates,
    int candidateCount, bool candidatesTruncated);
public sealed record PlacementCatalogResult(TileCatalogEntry[] tileTypes, WallCatalogEntry[] wallTypes, string source, string note);
public sealed record CatalogScan(CatalogTile[] tiles, CatalogWall[] walls, CatalogItem[] items);

/// <summary>Maps explicit observed type/style facts to candidates, never to a chosen design material.</summary>
public static class PlacementCatalog
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    public const int CandidateLimit = 3;

    public static CatalogStyle ResolveStyle(bool hasObjectData, int? nativeStyle, int[] availableStyles)
    {
        if (hasObjectData) return nativeStyle.HasValue ? new(true, nativeStyle, "native_object_data") : new(false, null, "native_style_read_failed");
        int[] unique = availableStyles.Distinct().ToArray();
        return unique.Length == 1 ? new(true, unique[0], "single_place_style_for_type") : new(false, null, "no_object_data_and_style_not_unique");
    }

    public static string BuildJson(string json) => JsonSerializer.Serialize(Build(
        JsonSerializer.Deserialize<CatalogScan>(json, ReadOptions) ?? throw new ArgumentException("Missing catalog facts.")));

    public static PlacementCatalogResult Build(CatalogScan scan)
    {
        var tiles = scan.tiles.DistinctBy(tile => (tile.tileType, tile.styleKnown, tile.observedStyle))
            .OrderBy(tile => tile.tileType).ThenBy(tile => tile.observedStyle ?? -1).Select(tile =>
            {
                CatalogItem[] matches = !tile.styleKnown || !tile.observedStyle.HasValue ? Array.Empty<CatalogItem>() :
                    scan.items.Where(item => item.tileType == tile.tileType && item.placeStyle == tile.observedStyle).OrderBy(item => item.itemId).ToArray();
                return new TileCatalogEntry(tile.tileType, tile.name, tile.styleKnown ? tile.observedStyle : null,
                    tile.styleKnown, tile.styleSource, matches.Take(CandidateLimit).Select(Candidate).ToArray(), matches.Length, matches.Length > CandidateLimit);
            }).ToArray();
        var walls = scan.walls.DistinctBy(wall => wall.wallType).OrderBy(wall => wall.wallType).Select(wall =>
        {
            CatalogItem[] matches = scan.items.Where(item => item.wallType == wall.wallType && item.wallType > 0).OrderBy(item => item.itemId).ToArray();
            return new WallCatalogEntry(wall.wallType, wall.name, matches.Take(CandidateLimit).Select(Candidate).ToArray(), matches.Length, matches.Length > CandidateLimit);
        }).ToArray();
        return new(tiles, walls, "ContentSamples.ItemsByType_and_live_observed_types",
            "tileType/wallType是格子类型，不能当itemId；place和design首列必须用placementCandidates.itemId。候选来自真实createTile/createWall/placeStyle；styleKnown=false时不猜对应样式，空候选不代表空气。候选不是选材建议，也不证明工具、配方、原生锚点、刷漆或住房合法性。");
    }
    private static PlacementCandidate Candidate(CatalogItem item) => new(item.itemId, item.name, item.placeStyle);
}
