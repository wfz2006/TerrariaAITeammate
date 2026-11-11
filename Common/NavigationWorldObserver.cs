using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

/// <summary>On-demand game-thread reads. These tools neither move actors nor edit tiles.</summary>
public static class NavigationWorldObserver
{
    public static object Terrain(string query, NPC observer, int? x = null, int? y = null)
    {
        TerrainObservationRequest window;
        try { window = TerrainObservationRequest.Resolve(query, x, y, (int)(observer.Center.X / 16f),
            (int)(observer.Center.Y / 16f), Main.maxTilesX, Main.maxTilesY); }
        catch (ArgumentException exception) { return Error("terrain", exception.Message); }

        // Reuse the native bounded cell reader, not a cached structure or an invented map.
        JsonObject result = JsonSerializer.SerializeToNode(ConstructionSiteWorldObserver.QueryTerrain(
            $"{window.Left},{window.Top},{window.Width},{window.Height}", observer))!.AsObject();
        result["topic"] = "terrain";
        result["requestedWindow"] = JsonSerializer.SerializeToNode(window);
        result["worldModified"] = false;
        result.Remove("thisToolSupportsDemolition");
        result["movementLimits"] = JsonSerializer.SerializeToNode(NavigationRouteObservation.MovementLimits());
        result["note"] = "本轮真实地形格与支撑行；terrain可读取世界内指定远窗，每窗最多97×81（7857）格，另含实际下方支撑行，不要求先走近。tileRuns/wallRuns/liquidRuns逐行保留实际占用、坡度、半砖和液体。窗口外未知。墙不是实心阻挡，空格也不等于身体可通过；站位候选只做几何检查，未查到路径前不能声称可达。自然土草是否可挖仍须由定点mine验证保护、工具和实际掉落；此查询不批准拆房或施工。";
        if (result["available"]?.GetValue<bool>() != true) return result;

        var miningCells = new List<MiningTerrainCell>(window.Width * window.Height);
        for (int row = window.Top; row < window.Top + window.Height; row++)
        for (int column = window.Left; column < window.Left + window.Width; column++)
            miningCells.Add(ReadMiningCell(column, row));
        result["miningFacts"] = JsonSerializer.SerializeToNode(TerrainMiningObservation.Analyze(new TerrainMiningScan(
            window.Left, window.Top, window.Width, window.Height, miningCells.ToArray())));

        const int candidateLimit = 96;
        var candidates = new List<object>();
        int total = 0;
        for (int row = window.Top; row < window.Top + window.Height; row++)
        for (int column = window.Left; column < window.Left + window.Width; column++)
        {
            if (!TilePathfinder.TryGetStandPose(new Point(column, row), out TileStandPose pose)) continue;
            total++;
            if (candidates.Count >= candidateLimit) continue;
            ObservedRouteStand stand = Describe(pose);
            candidates.Add(new { x = stand.X, standRow = stand.Row, centerTileX = stand.CenterX,
                centerTileY = stand.CenterY, feetTileY = stand.Bottom, reachability = "not_tested" });
        }
        result["standingCandidates"] = JsonSerializer.SerializeToNode(new
        {
            count = total, listed = candidates, truncated = total > candidateLimit,
            scope = "geometric_body_clearance_and_support_only", pathSearchPerformed = false,
            note = "按行列列出的几何候选；列表裁切不影响上面的完整地形runs。需要inspect route确认指定点当前图路径。"
        });
        return result;
    }

    public static object Route(string query, NPC observer, Player player, int? x = null, int? y = null)
    {
        object token = new();
        RouteWorldInspection inspection = BeginRouteInspection(query, observer, player, token, x, y);
        inspection.Advance(observer, player, token);
        return inspection.DescribeCurrent(continuationRetained: false);
    }

    public static RouteWorldInspection BeginRouteInspection(string query, NPC observer, Player player,
        object actionToken, int? x = null, int? y = null)
    {
        NavigationRouteRequest request;
        try { request = NavigationRouteRequest.Resolve(query, x, y); }
        catch (ArgumentException exception) { return new RouteWorldInspection(Error("route", exception.Message)); }
        if (request.Kind == "player" && (!player.active || player.dead))
            return new RouteWorldInspection(Error("route", "所属玩家当前不在可查询的活动状态。"));
        Vector2 target = request.Kind == "player" ? player.Center : new Vector2(request.X!.Value * 16f + 8f, request.Y!.Value * 16f + 8f);
        if (!WorldGen.InWorld((int)(target.X / 16f), (int)(target.Y / 16f), 2))
            return new RouteWorldInspection(Error("route", "请求的真实目的地不在世界有效范围。"));
        var search = TilePathfinder.BeginSearch(observer.Center, target,
            radiusX: NavigationRouteObservation.PaddingX, radiusY: NavigationRouteObservation.PaddingY,
            limits: new TilePathSearchLimits(NavigationRouteObservation.MaxUniqueExpanded, NavigationRouteObservation.MaxDiscovered));
        var snapshot = search.Snapshot;
        var frame = new RouteObservationFrame(WorldKnowledgeObserver.LoadEpoch, Main.worldID, Main.GameUpdateCount, actionToken,
            observer.Center, observer.Bottom.Y, TilePathfinder.IsGroundedAt(observer.Center.X, observer.Bottom.Y, observer.velocity.Y, observer.width),
            observer.velocity.Y, target, player.active && !player.dead);
        return new RouteWorldInspection(request, new RouteObservationSession(search, frame, request.Kind == "player", request.CheckReturn),
            snapshot.StartPose is { } start ? Describe(start) : null, snapshot.GoalPose is { } destination ? Describe(destination) : null);
    }

    internal static ObservedRouteStand DescribeRouteStand(Point stand) => Describe(stand);
    internal static ObservedRouteStand DescribeRouteStand(TileStandPose stand) => Describe(stand);

    private static ObservedRouteStand Describe(TileStandPose stand)
        => new(stand.Node.X, stand.Node.Y, stand.Center.X / 16f, stand.Center.Y / 16f, stand.Bottom / 16f);

    private static ObservedRouteStand Describe(Point stand)
    {
        if (!TilePathfinder.TryGetStandPose(stand, out TileStandPose pose)) throw new ArgumentException("Stand has no current body/contact pose.");
        return Describe(pose);
    }

    public static MiningTerrainCell ReadMiningCell(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 2)) throw new ArgumentException("Mining cell must be inside the live world.");
        Tile tile = Main.tile[x, y];
        if (!tile.HasTile) return new MiningTerrainCell(false, -1, "", false, false, false);
        string resource = tile.TileType switch
        {
            TileID.Dirt => "dirt", TileID.Grass => "grass", TileID.Stone => "stone",
            TileID.Iron => "iron", TileID.Copper => "copper", TileID.Silver => "silver", TileID.Gold => "gold",
            TileID.Lead => "lead", TileID.Tin => "tin", TileID.Tungsten => "tungsten", TileID.Platinum => "platinum",
            TileID.Meteorite => "meteorite", TileID.Ruby => "ruby",
            TileID.Trees or TileID.PalmTree or TileID.VanityTreeSakura or TileID.VanityTreeYellowWillow or
                TileID.TreeAsh or TileID.LivingWood => "wood",
            _ => ""
        };
        return new MiningTerrainCell(true, tile.TileType, resource, tile.IsActuated,
            AIExecutor.IsPartOfHouseOrStructure(x, y), WorldGen.CanKillTile(x, y));
    }
    private static object Error(string topic, string error) => new
    {
        topic, source = "live_world", available = false, worldId = Main.worldID,
        observedAtTick = Main.GameUpdateCount, error, worldModified = false,
        note = "没有取得本次完整查询结果，不能把范围外或失败查询当作空气、通路或整片区域不可达。"
    };
}
