using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

public enum DesignItemKind { Unsupported, Block, Wall, Furniture, Platform }
public enum DesignAnchorRule { Unknown, Floor, FloorAndCeiling, Ceiling, SideOrWall, NativeObjectNoAnchors }
public readonly record struct DesignCell(bool HasTile = false, int TileType = -1, int WallType = 0,
    bool Solid = false, bool Platform = false, bool Actuated = false, bool HalfBlock = false, int Slope = 0,
    bool Furniture = false, int ObjectLeft = int.MinValue, int ObjectTop = int.MinValue, int Style = 0, bool Lava = false);
public sealed record DesignItemData(int ItemId, string Name, DesignItemKind Kind, int TileType = -1,
    int WallType = 0, int Width = 1, int Height = 1, int OriginX = 0, int OriginY = 0, int Style = 0,
    bool Solid = false, bool Platform = false, DesignAnchorRule Anchor = DesignAnchorRule.Unknown, string AnchorDetail = "");
public readonly record struct DesignActor(float CenterTileX, float FeetTileY, int Width = 20, int Height = 42);
public readonly record struct DesignWorkPose(int StandX, float FeetY);
public sealed record DesignRouteProof(bool HasRoute, bool ExactDestination, string Detail);
public sealed record DesignIssue(string Code, string Severity, int? Step, int? X, int? Y, string Message);
public sealed record DesignBillLine(int ItemId, string Name, int Required, int Owned, int Missing, int Reused);
public sealed record DesignMovementProof(int Step, string Status, string Detail);
public sealed record DesignIssueSummary(string Code, string Severity, int? Step, int Count);
public sealed class DesignReviewResult
{
    public bool InputValid { get; init; }
    public bool StaticConstraintsPassed { get; init; }
    public string Status { get; init; } = "rejected";
    public bool WorldModified => false;
    public bool ConstructionCompleted => false;
    public bool NativeFurniturePlacementValidated => false;
    public bool NativeHousingValidated => false;
    public bool NativeActualPlacementValidated => false;
    public bool NativeTravelValidated => false;
    public IReadOnlyList<DesignIssue> Issues { get; init; } = Array.Empty<DesignIssue>();
    public IReadOnlyList<DesignBillLine> Bill { get; init; } = Array.Empty<DesignBillLine>();
    public IReadOnlyList<DesignMovementProof> Movement { get; init; } = Array.Empty<DesignMovementProof>();
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();
    public int IssueCount { get; init; }
    public bool IssuesTruncated { get; init; }
    public int IssuesOmitted => IssueCount - Issues.Count;
    public IReadOnlyList<DesignIssueSummary> IssueSummary { get; init; } = Array.Empty<DesignIssueSummary>();
    public int OperationIndexBase => 1;
    public IReadOnlyList<int> MustOmitOperationIndices { get; init; } = Array.Empty<int>();
}

/// <summary>Read-only sparse-overlay preflight of a model-authored design. It never creates a layout.</summary>
public static class ConstructionDesignReview
{
    public static IReadOnlyList<int> ReadItemIds(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("placements", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return Array.Empty<int>();
        return rows.EnumerateArray().Take(96).Where(r => r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0 &&
                r[0].ValueKind == JsonValueKind.Number && r[0].TryGetInt32(out _))
            .Select(r => r[0].GetInt32()).Distinct().ToArray();
    }

    public static DesignReviewResult Review(JsonElement? raw, Func<int, int, DesignCell> readCell,
        IReadOnlyDictionary<int, DesignItemData> items, DesignActor initialActor,
        Func<DesignWorkPose, DesignRouteProof> firstRoute, IReadOnlyDictionary<int, int> inventory)
        => new ReviewSession(readCell, items, initialActor, firstRoute, inventory).Run(raw);

    private readonly record struct Rect(int X, int Y, int Width, int Height)
    {
        public long Right => (long)X + Width;
        public long Bottom => (long)Y + Height;
        public bool Contains(Rect r) => r.X >= X && r.Y >= Y && r.Right <= Right && r.Bottom <= Bottom;
        public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    }
    private readonly record struct Placement(int ItemId, Rect Area, DesignWorkPose Pose);

    private sealed class ReviewSession
    {
        private readonly Func<int, int, DesignCell> read;
        private readonly IReadOnlyDictionary<int, DesignItemData> items;
        private readonly DesignActor actor;
        private readonly Func<DesignWorkPose, DesignRouteProof> firstRoute;
        private readonly IReadOnlyDictionary<int, int> inventory;
        private readonly Dictionary<(int X, int Y), DesignCell> baseline = new(), overlay = new();
        private readonly List<DesignIssue> issues = new();
        private readonly Dictionary<(string Code, string Severity, int? Step), int> issueGroups = new();
        private readonly List<DesignMovementProof> movement = new();
        private readonly Dictionary<int, int> required = new(), reused = new();
        private readonly HashSet<(bool Wall, int X, int Y)> counted = new(), reusedCounted = new();
        private readonly List<Rect> spaces = new();
        private readonly HashSet<int> mustOmit = new();
        private readonly HashSet<int> platformNativeWarnings = new();
        private bool inputValid = true;
        private int issueCount, errorCount, warningCount;
        private Rect bounds;
        private DesignWorkPose? previous;
        private int actorWidth => Math.Max(20, actor.Width);
        private int actorHeight => Math.Max(42, actor.Height);

        public ReviewSession(Func<int, int, DesignCell> read, IReadOnlyDictionary<int, DesignItemData> items,
            DesignActor actor, Func<DesignWorkPose, DesignRouteProof> firstRoute, IReadOnlyDictionary<int, int> inventory)
        { this.read = read; this.items = items; this.actor = actor; this.firstRoute = firstRoute; this.inventory = inventory; }

        private void Issue(string code, string message, int? step = null, int? x = null, int? y = null, bool warning = false)
        {
            issueCount++;
            if (warning) warningCount++; else errorCount++;
            string severity = warning ? "warning" : "error";
            var key = (code, severity, step);
            issueGroups[key] = issueGroups.GetValueOrDefault(key) + 1;
            if (issues.Count < 48) issues.Add(new(code, severity, step, x, y, message));
        }
        private void InputError(string message)
        { inputValid = false; Issue("input_invalid", message); }
        private static bool Int(JsonElement e, out int value)
        { value = 0; return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value); }
        private static bool Float(JsonElement e, out float value)
        { value = 0; return e.ValueKind == JsonValueKind.Number && e.TryGetSingle(out value) && float.IsFinite(value); }
        private bool Rectangle(JsonElement e, out Rect rect)
        {
            rect = default;
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 4 ||
                !Int(e[0], out int x) || !Int(e[1], out int y) || !Int(e[2], out int w) || !Int(e[3], out int h) ||
                w < 1 || h < 1 || (long)x + w > int.MaxValue || (long)y + h > int.MaxValue)
            { InputError("矩形必须是[x,y,width,height]四个整数，宽高为正且坐标不能溢出。"); return false; }
            rect = new(x, y, w, h); return true;
        }

        public DesignReviewResult Run(JsonElement? raw)
        {
            if (raw is not { ValueKind: JsonValueKind.Object } value)
            { InputError("design必须是结构化对象，包含bounds、clearSpaces和placements。"); return Result(); }
            if (!value.TryGetProperty("bounds", out var b) || !Rectangle(b, out bounds))
            { if (inputValid) InputError("缺少有效bounds。"); return Result(); }
            if (bounds.Width > 97 || bounds.Height > 81)
            { Issue("bounds_limit", "设计包络最多97×81格。"); return Result(); }
            if (!float.IsFinite(actor.CenterTileX) || !float.IsFinite(actor.FeetTileY) || actorWidth > 128 || actorHeight > 128)
            { InputError("初始同伴位置或身体尺寸无效。"); return Result(); }
            if (value.TryGetProperty("clearSpaces", out var clear))
            {
                if (clear.ValueKind != JsonValueKind.Array || clear.GetArrayLength() > 96)
                    InputError("clearSpaces必须是最多96个矩形的数组。");
                else foreach (var e in clear.EnumerateArray())
                    if (Rectangle(e, out Rect room))
                    {
                        if (!bounds.Contains(room)) Issue("clear_space_out_of_bounds", "声明的室内净空超出设计包络。", x: room.X, y: room.Y);
                        else spaces.Add(room);
                        if (room.Width < (int)Math.Ceiling(actorWidth / 16f))
                            Issue("insufficient_clear_width", "20像素身体要求声明至少2格宽净空。", x: room.X, y: room.Y);
                        if (room.Height < 3) Issue("insufficient_headroom", "42像素身体要求声明至少3格高净空。", x: room.X, y: room.Y);
                    }
            }
            if (!spaces.Any(r => r.Width >= (int)Math.Ceiling(actorWidth / 16f) && r.Height >= 3))
                Issue("no_declared_clear_space", "必须显式声明至少一个在包络内、至少2格宽3格高的房间/通道净空；工具不会代填布局。");
            if (!value.TryGetProperty("placements", out var rows) || rows.ValueKind != JsonValueKind.Array)
            { InputError("placements必须是七列数组的数组。"); return Result(); }
            if (rows.GetArrayLength() == 0) InputError("placements不能为空。");
            if (rows.GetArrayLength() > 96)
            { inputValid = false; Issue("too_many_steps", "最多96次place操作，未审查超出部分。"); }
            int index = 0;
            foreach (var row in rows.EnumerateArray().Take(96))
            {
                int step = ++index;
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 7 ||
                    !Int(row[0], out int item) || !Int(row[1], out int x) || !Int(row[2], out int y) ||
                    !Int(row[3], out int w) || !Int(row[4], out int h) || !Int(row[5], out int sx) || !Float(row[6], out float fy) ||
                    item <= 0 || w < 1 || h < 1 || (long)x + w > int.MaxValue || (long)y + h > int.MaxValue ||
                    Math.Abs((double)fy) > 1e7 || Math.Abs((double)sx) > 1e7)
                { InputError($"第{step}步必须是[itemId,x,y,width,height,standX,feetY]；前六列整数，feetY有限数值。"); continue; }
                ReviewPlacement(new(item, new(x, y, w, h), new(sx, fy)), step);
            }
            foreach (Rect room in spaces)
            {
                bool found = false;
                for (int y = room.Y; y < room.Bottom && !found; y++)
                    for (int x = room.X; x < room.Right && !found; x++)
                    {
                        DesignCell c = Get(x, y);
                        if (c.HasTile && c.Solid && !c.Platform && !c.Actuated)
                        { Issue("clear_space_blocked", "最终声明净空被已有或计划实心方块占用。", x: x, y: y); found = true; }
                    }
            }
            return Result();
        }

        private DesignCell Get(int x, int y)
        {
            if (overlay.TryGetValue((x, y), out var c) || baseline.TryGetValue((x, y), out c)) return c;
            try { c = read(x, y); }
            catch (Exception e)
            { Issue("world_read_failed", "无法只读取得该格，不能将未知当空气：" + e.GetType().Name, x: x, y: y); c = new(true, -2, Solid: true); }
            baseline[(x, y)] = c; return c;
        }
        private bool FullSupport(int x, int y)
        {
            var c = Get(x, y);
            return c.HasTile && !c.Actuated && c.Solid && !c.Platform && !c.HalfBlock && c.Slope == 0;
        }
        private bool AdjacentSupport(int x, int y)
            => Get(x, y).WallType > 0 || new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) }
                .Any(p => { var c = Get(p.Item1, p.Item2); return c.HasTile && c.Solid; });
        private bool BodyClear(float center, float feet)
        {
            float left = center * 16 - actorWidth / 2f, right = center * 16 + actorWidth / 2f, bottom = feet * 16;
            int minX = CompanionMovement.FootprintLeft(center * 16, actorWidth), maxX = CompanionMovement.FootprintRight(center * 16, actorWidth);
            for (int x = minX; x <= maxX; x++)
                for (int y = (int)MathF.Floor((bottom - actorHeight + .01f) / 16); y <= (int)MathF.Floor((bottom - .01f) / 16); y++)
                {
                    var c = Get(x, y);
                    if (c.Lava) return false;
                    if (!c.HasTile || c.Actuated || !c.Solid || c.Platform) continue;
                    float top = y * 16f + (c.HalfBlock ? 8 : 0);
                    if (c.Slope == 1) top = y * 16f + Math.Clamp(left - x * 16f, 0, 16);
                    if (c.Slope == 2) top = y * 16f + Math.Clamp((x + 1) * 16f - right, 0, 16);
                    if (bottom > top + .01f) return false;
                }
            return true;
        }
        private bool Supported(float center, float feet)
        {
            int row = (int)MathF.Floor(feet + .01f);
            for (int x = CompanionMovement.FootprintLeft(center * 16, actorWidth); x <= CompanionMovement.FootprintRight(center * 16, actorWidth); x++)
            {
                var c = Get(x, row);
                if (!c.HasTile || c.Actuated || (!c.Solid && !c.Platform)) continue;
                float surface = row + (c.HalfBlock ? .5f : 0);
                if (c.Slope == 1) surface = row + Math.Clamp(center - actorWidth / 32f - x, 0, 1);
                if (c.Slope == 2) surface = row + Math.Clamp(x + 1 - center - actorWidth / 32f, 0, 1);
                if (Math.Abs(surface - feet) <= .0625f) return true;
            }
            return false;
        }
        private bool SelfOverlap(DesignWorkPose pose, int x, int y)
        {
            float left = pose.StandX + .5f - actorWidth / 32f, right = pose.StandX + .5f + actorWidth / 32f;
            return x < right && x + 1 > left && y < pose.FeetY && y + 1 > pose.FeetY - actorHeight / 16f;
        }
        private bool ReviewMovement(DesignWorkPose pose, int step)
        {
            float center = pose.StandX + .5f;
            bool valid = BodyClear(center, pose.FeetY) && Supported(center, pose.FeetY);
            if (!valid)
                Issue("invalid_work_pose", "当前施工前缀下，20×42身体没有完整净空或实际承重支撑。", step, pose.StandX, (int)MathF.Floor(pose.FeetY));
            if (previous == null)
            {
                try
                {
                    var proof = firstRoute(pose);
                    movement.Add(new(step, proof.HasRoute && proof.ExactDestination ? "native_graph_route_only" : "not_proven", proof.Detail));
                    if (!proof.HasRoute || !proof.ExactDestination)
                    {
                        valid = false;
                        Issue("first_work_pose_no_route", "首工位尚无到声明实际脚底面的原生图路线；投影到邻位不等于到位。", step);
                    }
                }
                catch (Exception e)
                { valid = false; movement.Add(new(step, "not_proven", e.GetType().Name)); Issue("first_work_pose_no_route", "首工位只读路线查询失败。", step); }
            }
            else if (previous.Value == pose)
                movement.Add(new(step, "same_pose_prefix_checked", "保持同一工位；净空与支撑按当前前缀重新检查。"));
            else
            {
                string status = LocalMovement(previous.Value, pose);
                movement.Add(new(step, status, "只检验虚拟前缀的局部几何，未实际移动同伴或调用修改后世界的原生寻路。"));
                if (status == "not_proven") Issue("future_movement_unproven", "没有证明从上一工位到此工位的施工前缀路线，需后续实机/原生复查。", step, warning: true);
            }
            previous = pose;
            return valid;
        }
        private string LocalMovement(DesignWorkPose from, DesignWorkPose to)
        {
            float dx = (to.StandX - (float)from.StandX) * 16, dy = (to.FeetY - from.FeetY) * 16;
            int samples = (int)Math.Ceiling(Math.Abs(dx) / 4) + 1;
            if (Math.Abs(dy) <= .01f && samples <= 1024)
            {
                bool walk = true;
                for (int i = 0; i <= samples; i++)
                {
                    float x = from.StandX + .5f + dx * i / samples / 16;
                    if (!BodyClear(x, from.FeetY) || !Supported(x, from.FeetY)) { walk = false; break; }
                }
                if (walk) return "local_walk_geometry_only";
            }
            float ticks = CompanionMovement.JumpFlightTicks(dy);
            if (!float.IsNaN(ticks) && ticks > 0 && Math.Abs(dx) <= 6 * 16 && dy >= -6 * 16 &&
                Math.Abs(dx) <= (ticks - 4) * 4.4f && ticks <= 120)
            {
                bool jump = true;
                for (int i = 0; i <= Math.Ceiling(ticks); i++)
                {
                    float t = Math.Min(i, ticks);
                    float x = from.StandX + .5f + dx * t / ticks / 16;
                    float feet = from.FeetY + (CompanionMovement.JumpVelocity * t + .5f * CompanionMovement.Gravity * t * t) / 16;
                    if (!BodyClear(x, feet)) { jump = false; break; }
                }
                if (jump && Supported(to.StandX + .5f, to.FeetY)) return "local_jump_geometry_only";
            }
            return "not_proven";
        }

        private void ReviewPlacement(Placement op, int step)
        {
            Rect area = op.Area;
            if (area.Width > 30 || area.Height > 30) { Issue("operation_dimension_limit", "单次place宽高分别最多30格。", step); return; }
            if ((long)area.Width * area.Height > 300) { Issue("operation_area_limit", "单次place最多300格。", step); return; }
            if (!items.TryGetValue(op.ItemId, out var item) || item.Kind == DesignItemKind.Unsupported)
            { Issue("unsupported_item", "物品不是已知支持审查的普通方块、背景墙、单格平台或基础家具；具体限制见itemMetadata。", step); return; }
            bool furniture = item.Kind == DesignItemKind.Furniture, wall = item.Kind == DesignItemKind.Wall;
            bool platform = item.Kind == DesignItemKind.Platform;
            if (platform)
            {
                if (area.Width != 1 || area.Height != 1)
                { Issue("platform_area_not_supported", "平台执行仅支持单格place，width/height必须为1。", step); return; }
                if (!item.Platform || item.Width != 1 || item.Height != 1 || item.OriginX != 0 || item.OriginY != 0 ||
                    item.Anchor != DesignAnchorRule.NativeObjectNoAnchors)
                { Issue("unsupported_platform_variant", "平台原生足迹/锚点尚未映射，不能批准此变体。", step); return; }
                if (platformNativeWarnings.Add(item.ItemId))
                    Issue("native_platform_not_validated", "仅按已核实的单格PlaceObject零anchor元数据检查；未实际执行原生放置、framing或潜在全局hooks，见itemMetadata。", step, warning: true);
            }
            if (furniture)
            {
                if (area.Width != 1 || area.Height != 1) { Issue("furniture_area_not_supported", "家具必须单次放置，width/height应为1；实际物体足迹来自原生元数据。", step); return; }
                if (item.Width < 1 || item.Height < 1 || item.Width > 30 || item.Height > 30 || item.Width * item.Height > 300)
                { Issue("unsupported_item", "家具原生足迹无效或超出有界审查。", step); return; }
                area = new(area.X, area.Y, item.Width, item.Height);
            }
            if (!bounds.Contains(area)) { Issue("placement_out_of_bounds", "物体真实足迹超出声明设计包络。", step, area.X, area.Y); return; }
            bool workValid = ReviewMovement(op.Pose, step);
            if (!wall && item.Solid && !item.Platform)
                foreach (Rect room in spaces)
                    if (area.X < room.Right && area.Right > room.X && area.Y < room.Bottom && area.Bottom > room.Y)
                        Issue("clear_space_blocked", "计划实心放置与声明房间/通道净空相交；矩形实心填充不是空心外框。", step, Math.Max(area.X, room.X), Math.Max(area.Y, room.Y));
            bool areaOperation = op.Area.Width > 1 || op.Area.Height > 1;
            float reach = areaOperation ? 8 : 7;
            float centerY = op.Pose.FeetY - actorHeight / 32f;
            if (furniture)
            {
                bool inReach = Distance(op.Pose.StandX + .5f, centerY, op.Area.X + .5f, op.Area.Y + .5f) <= reach;
                if (!inReach)
                    Issue("work_pose_out_of_reach", "家具原生放置坐标离声明工位超过7格。", step, op.Area.X, op.Area.Y);
                ReviewFurniture(item, area, op, step, workValid && inReach);
                return;
            }
            bool reachReported = false;
            for (int y = (int)area.Bottom - 1; y >= area.Y; y--)
                for (int x = area.X; x < area.Right; x++)
                {
                    bool inReach = Distance(op.Pose.StandX + .5f, centerY, x + .5f, y + .5f) <= reach;
                    if (!inReach && !reachReported)
                    { Issue("work_pose_out_of_reach", $"本步至少此目标离声明工位超过{reach}格。", step, x, y); reachReported = true; }
                    var c = Get(x, y);
                    bool match = wall ? c.WallType == item.WallType : c.HasTile && c.TileType == item.TileType && !c.Furniture &&
                        (!platform || c.Platform && c.Style == item.Style);
                    var key = (wall, x, y);
                    if (match)
                    {
                        CountReuse(item, key);
                        if (!areaOperation)
                        {
                            mustOmit.Add(step);
                            Issue("reused_single_placement_requires_noop", "必须省略此单格no-op操作。当前单格place不会自动复用且可能失败/扣料。", step, x, y);
                        }
                        continue;
                    }
                    if (wall ? c.WallType != 0 : c.HasTile)
                    { Issue("occupancy_conflict", "已有不同方块/物体或背景墙，不能批准拆除或覆盖。", step, x, y); continue; }
                    CountNew(item, key);
                    if (c.Lava) { Issue("lava_placement", "目标格有岩浆，不能批准此放置。", step, x, y); continue; }
                    if (!wall && SelfOverlap(op.Pose, x, y))
                    { Issue("work_pose_overlaps_placement", "新放置格占用了施工者自己的20×42身体。", step, x, y); continue; }
                    if (!wall && !platform && !AdjacentSupport(x, y))
                    { Issue("unsupported_cell", "此格在执行顺序前缀中没有四邻实心块或本格背景墙支持。", step, x, y); continue; }
                    if (inReach && workValid) overlay[(x, y)] = wall ? c with { WallType = item.WallType } : c with
                        { HasTile = true, TileType = item.TileType, Solid = item.Solid, Platform = item.Platform, Actuated = false, HalfBlock = false, Slope = 0, Furniture = false, Style = item.Style };
                }
        }
        private void ReviewFurniture(DesignItemData item, Rect area, Placement op, int step, bool workValid)
        {
            bool match = true, vacant = true, self = false;
            for (int x = area.X; x < area.Right; x++)
                for (int y = area.Y; y < area.Bottom; y++)
                {
                    var c = Get(x, y);
                    match &= c.HasTile && c.TileType == item.TileType && c.ObjectLeft == area.X && c.ObjectTop == area.Y && c.Style == item.Style;
                    vacant &= !c.HasTile && !c.Lava;
                    self |= SelfOverlap(op.Pose, x, y);
                }
            var key = (false, area.X, area.Y);
            if (match)
            {
                CountReuse(item, key);
                mustOmit.Add(step);
                Issue("reused_single_placement_requires_noop", "必须省略此重复家具place单指令；已有家具原地复用，不收取另一个家具。", step, area.X, area.Y);
                return;
            }
            if (!vacant) { Issue("occupancy_conflict", "家具真实足迹被不同物件或岩浆占用。", step, area.X, area.Y); return; }
            CountNew(item, key);
            if (self) Issue("work_pose_overlaps_placement", "家具足迹占用了施工者身体。", step, area.X, area.Y);
            bool floor = Enumerable.Range(area.X, area.Width).All(x => FullSupport(x, (int)area.Bottom));
            bool ceiling = Enumerable.Range(area.X, area.Width).All(x => FullSupport(x, area.Y - 1));
            bool sideOrWall = false;
            for (int x = area.X; x < area.Right; x++) for (int y = area.Y; y < area.Bottom; y++)
                sideOrWall |= AdjacentSupport(x, y);
            bool supported = item.Anchor switch
            {
                DesignAnchorRule.Floor => floor,
                DesignAnchorRule.FloorAndCeiling => floor && ceiling,
                DesignAnchorRule.Ceiling => ceiling,
                DesignAnchorRule.SideOrWall => sideOrWall,
                _ => false
            };
            if (item.Anchor == DesignAnchorRule.Unknown)
                Issue("unknown_furniture_anchor", "家具anchor规则未映射，不能批准；" + item.AnchorDetail, step, area.X, area.Y);
            else if (!supported) Issue("unsupported_cell", "当前前缀没有满足家具保守anchor几何。", step, area.X, area.Y);
            Issue("native_furniture_not_validated", "足迹/Origin/保守anchor审查不等于原生PlaceObject/hooks/style验证；" + item.AnchorDetail, step, warning: true);
            if (supported && !self && workValid)
                for (int x = area.X; x < area.Right; x++) for (int y = area.Y; y < area.Bottom; y++)
                {
                    var c = Get(x, y);
                    overlay[(x, y)] = c with { HasTile = true, TileType = item.TileType, Solid = item.Solid, Platform = item.Platform,
                        Actuated = false, HalfBlock = false, Slope = 0, Furniture = true, ObjectLeft = area.X, ObjectTop = area.Y, Style = item.Style };
                }
        }
        private static float Distance(float ax, float ay, float bx, float by)
            => MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        private void CountNew(DesignItemData item, (bool Wall, int X, int Y) key)
        { if (counted.Add(key)) required[item.ItemId] = required.GetValueOrDefault(item.ItemId) + 1; }
        private void CountReuse(DesignItemData item, (bool Wall, int X, int Y) key)
        {
            if (!counted.Contains(key) && reusedCounted.Add(key))
                reused[item.ItemId] = reused.GetValueOrDefault(item.ItemId) + 1;
        }
        private DesignReviewResult Result()
        {
            var bill = required.Keys.Union(reused.Keys).OrderBy(id => id).Select(id =>
            {
                int needed = required.GetValueOrDefault(id), owned = Math.Max(0, inventory.TryGetValue(id, out int n) ? n : 0);
                return new DesignBillLine(id, items[id].Name, needed, owned, Math.Max(0, needed - owned), reused.GetValueOrDefault(id));
            }).ToArray();
            return new DesignReviewResult
            {
                InputValid = inputValid, StaticConstraintsPassed = inputValid && errorCount == 0,
                Status = !inputValid || errorCount > 0 ? "rejected" : warningCount > 0 ? "conditional_static_preflight" : "static_preflight_pass",
                Issues = issues, IssueCount = issueCount, IssuesTruncated = issueCount > issues.Count, Bill = bill, Movement = movement,
                IssueSummary = issueGroups.Select(p => new DesignIssueSummary(p.Key.Code, p.Key.Severity, p.Key.Step, p.Value)).ToArray(),
                MustOmitOperationIndices = mustOmit.OrderBy(i => i).ToArray(),
                Limitations = new[] { "只读静态预检，不修改原世界、同伴位置或库存；不是建造完成、实际走过或原生住房验证。",
                    "每步沿用声明工位；区域按bottom-up/left-right前缀模拟，不自动安排布局/工位/路线。",
                    "材料表按不同意图格/家具去重；被拒绝的步骤仍须修正。已有单格/家具no-op应从执行列表省略。",
                    "平台仅限已映射的单格PlaceObject元数据；家具/平台原生hooks、framing、变体及最终住房有效性须另验；后续工位仅报告局部几何或明确缺失证明。" }
            };
        }
    }
}
