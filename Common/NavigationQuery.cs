using System;
using System.Text.RegularExpressions;

namespace TerrariaAITeammate.Common;

internal static class NavigationCoordinates
{
    internal static void ValidatePair(int? x, int? y)
    {
        if (x.HasValue != y.HasValue || x < 0 || y < 0 || x > 100000 || y > 100000)
            throw new ArgumentException("导航观察坐标必须是成对的非负x/y，不能只给一半或溢出范围。");
    }
    internal static bool ReadPair(string text, out int x, out int y)
    {
        Match pair = Regex.Match(text, @"^\s*(\d+)\s*[,，]\s*(\d+)\s*$");
        x = y = 0;
        return pair.Success && int.TryParse(pair.Groups[1].Value, out x) &&
            int.TryParse(pair.Groups[2].Value, out y) && x <= 100000 && y <= 100000;
    }
}

public sealed record TerrainObservationRequest(int Left, int Top, int Width, int Height, bool Clipped,
    string Selection, int? RequestedCenterX, int? RequestedCenterY, int RequestedWidth, int RequestedHeight)
{
    public const int DefaultSize = 33;
    public static TerrainObservationRequest Resolve(string query, int? x, int? y,
        int observerX, int observerY, int worldWidth, int worldHeight)
    {
        NavigationCoordinates.ValidatePair(x, y);
        query = (query ?? "").Trim();
        if (worldWidth < 6 || worldHeight < 6) throw new ArgumentException("世界尚未提供有效的地形观察范围。");
        if (query.Split(',', '，').Length == 4)
        {
            if (query.Contains(';')) throw new ArgumentException("terrain矩形只接受x,y,width,height；材料同异比较请使用site。");
            ConstructionSiteRequest rectangle = ConstructionSiteRequest.Parse(query, x, y);
            if (rectangle.X < 2 || rectangle.Y < 2 || (long)rectangle.X + rectangle.Width > worldWidth - 2 ||
                (long)rectangle.Y + rectangle.Height > worldHeight - 3)
                throw new ArgumentException("指定terrain矩形及支撑行必须完整位于世界中，不能把未读到的格子补成空气。");
            return new TerrainObservationRequest(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height,
                false, "explicit_rectangle", null, null, rectangle.Width, rectangle.Height);
        }
        if (query.Length > 0 && query is not ("here" or "nearby" or "附近"))
        {
            if (!NavigationCoordinates.ReadPair(query, out int centerX, out int centerY))
                throw new ArgumentException("terrain不填item时读周围33x33；可给x/y中心或item=\"x,y,width,height\"指定矩形。");
            if (x.HasValue && (x != centerX || y != centerY)) throw new ArgumentException("terrain中心与task.x/y不一致。");
            x = centerX; y = centerY;
        }
        int cx = x ?? observerX, cy = y ?? observerY;
        if (cx < 2 || cy < 2 || cx >= worldWidth - 2 || cy >= worldHeight - 2)
            throw new ArgumentException("terrain观察中心不在世界有效范围。");
        int requestedLeft = cx - DefaultSize / 2, requestedTop = cy - DefaultSize / 2;
        int left = Math.Max(2, requestedLeft), top = Math.Max(2, requestedTop);
        int right = Math.Min(worldWidth - 3, cx + DefaultSize / 2);
        int bottom = Math.Min(worldHeight - 4, cy + DefaultSize / 2);
        bool clipped = left != requestedLeft || top != requestedTop || right - left + 1 != DefaultSize || bottom - top + 1 != DefaultSize;
        return new TerrainObservationRequest(left, top, right - left + 1, bottom - top + 1,
            clipped, "centered_window", cx, cy, DefaultSize, DefaultSize);
    }
}

public sealed record NavigationRouteRequest(string Kind, int? X, int? Y)
{
    public bool CheckReturn { get; init; }

    public static NavigationRouteRequest Resolve(string query, int? x = null, int? y = null)
    {
        NavigationCoordinates.ValidatePair(x, y);
        query = (query ?? "").Trim();
        if (query.Equals("round_trip", StringComparison.OrdinalIgnoreCase))
        {
            if (!x.HasValue) throw new ArgumentException("round_trip需要明确的task.x/y目标坐标。");
            return new NavigationRouteRequest("coordinates", x, y) { CheckReturn = true };
        }
        if (query.Equals("player", StringComparison.OrdinalIgnoreCase) || query == "玩家")
        {
            if (x.HasValue) throw new ArgumentException("route的player目标和固定坐标不能同时指定。");
            return new NavigationRouteRequest("player", null, null);
        }
        if (query.Length > 0)
        {
            if (!NavigationCoordinates.ReadPair(query, out int qx, out int qy))
                throw new ArgumentException("route需要task.x/y、item=\"x,y\"或item=\"player\"。");
            if (x.HasValue && (x != qx || y != qy)) throw new ArgumentException("route的item坐标与task.x/y不一致。");
            x = qx; y = qy;
        }
        if (!x.HasValue) throw new ArgumentException("route没有目标；请明确坐标或player。");
        return new NavigationRouteRequest("coordinates", x, y);
    }
}
