using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.ID;
using Terraria.ObjectData;
using Cell = TerrariaAITeammate.Common.ShelterAuditCell;
using Door = TerrariaAITeammate.Common.ShelterAuditDoor;
using DoorKey = TerrariaAITeammate.Common.ShelterAuditDoorKey;

namespace TerrariaAITeammate.Common;

internal sealed record ShelterCellDifference(int x, int y, string category, object before, object after);
internal sealed record ShelterProtectionOrigin(int x, int y, string reason, int tileType, string tileName,
    int wallType, bool safeWall, bool structural, bool furniture, bool platform, bool door);
internal sealed record ShelterProtectionNeighbor(int x, int y, bool isProtected, bool primaryComponent, object baselineCell);
internal sealed record ShelterProtectionReport(bool available, int worldId, string epoch, ulong observedAtTick,
    ShelterAuditBounds sampleBounds, int x, int y, bool isProtected, IReadOnlyList<string> maskReasons,
    bool primaryComponent, string? componentId, ShelterAuditBounds? componentBounds, int componentProtectedCells,
    bool componentTouchesSampleBoundary, object? baselineCell, IReadOnlyList<ShelterProtectionOrigin> origins,
    IReadOnlyList<ShelterProtectionNeighbor> nearbyBaselineCells, string scope);
internal sealed record ShelterPreservationReport(bool available, bool preserved, int worldId, string epoch, ulong observedAtTick,
    ShelterAuditBounds sampleBounds, int protectedCells, bool coverageComplete, int diffCount,
    IReadOnlyList<ShelterCellDifference> differences, bool differencesTruncated, int normalizedDoorCount,
    int harmlessAutotileFrameChanges, IReadOnlyList<string> issues, string scope)
{
    public int allowedNaturalVegetationChanges { get; init; }
    public IReadOnlyList<ShelterCellDifference> naturalVegetationSamples { get; init; } = Array.Empty<ShelterCellDifference>();
}
internal sealed record ShelterNewRoomReport(bool available, bool validNew, bool nativeValid, int worldId, string epoch,
    ulong observedAtTick, ShelterAuditBounds sampleBounds, ShelterAuditBounds? declaredBounds, ShelterAuditBounds? actualRoomBounds,
    bool roomWithinSample, bool roomWithinDeclaredBounds, bool wasExistingValidRoom, int protectedOverlapCells,
    bool baselineInteriorEmpty, int baselineInteriorNaturalNonSolidTiles, int baselineBoundaryNaturalTiles,
    ShelterAuditCounts? baselineRoomCells, ShelterAuditCounts? currentRoomCells,
    int newStructuralCells, int newWallCells, int newFurnitureCells, IReadOnlyList<string> issues, string scope);

/// <summary>Read-only game-thread world audit shared by goal evidence and acceptance. Never edits terrain, inventory, actors or memory.</summary>
internal sealed partial class ShelterWorldAudit
{
    private const int Width = 512, Height = 320, MaximumRoomProbes = 64, MaximumDiffSamples = 64;
    private const byte Existing = 1, Clearance = 2;
    private const string Scope = "Bounded snapshot comparison only, not a whole-world guarantee. Mask covers existing safe housing walls (including empty interior), recognizable construction/furniture/doors/platforms, their boundary cells and originally empty adjacent/traversable clearance. A standalone torch retains exact protection of its own cell but does not establish a travel corridor in neighboring air; room walls, doors, other construction/furniture use space and platform headroom retain their clearance. Coverage completeness concerns the original center-selected connected building including connected basement/platform clearance; remote edge contacts and their unknown outside continuation are explicitly listed, not silently declared covered. All masked cells inside the wide sample remain compared. Uncontextualized natural soil/stone/grass/trees are not globally frozen. Known nonblocking grass/vine growth into empty clearance and native pumpkin maturation are counted separately; clay pots, planter boxes and other construction/furniture remain protected, as do wall/liquid/paint/mechanism changes. Native door hinge/style/row attributes are retained with only native legal open/close frame variants normalized; other object frames remain exact, and non-object autotile/wall frame changes are counted separately. No material ledger or construction permission is supplied.";
    private static readonly FieldInfo[] HousingScratch = typeof(WorldGen).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(f => !f.IsLiteral && (f.Name.StartsWith("room", StringComparison.Ordinal) || f.Name is "numRoomTiles" or "canSpawn" or "sharedRoomX")).ToArray();
    private readonly int worldId;
    private readonly string epoch;
    private readonly ShelterAuditBounds bounds;
    private readonly Cell[] baseline;
    private readonly byte[] mask;
    private readonly bool[] primaryMask;
    private readonly Dictionary<DoorKey, Door> baselineDoors;
    private readonly List<string> baselineIssues;
    private readonly ShelterBaselineReport originalReport;
    public ShelterBaselineReport BaselineReport { get; }

    private ShelterWorldAudit(int centerX, int centerY)
    {
        if (Main.gameMenu || !WorldGen.InWorld(centerX, centerY, 2)) throw new ArgumentException("Audit capture requires a loaded world and an in-world tile center.");
        worldId = Main.worldID; epoch = WorldKnowledgeObserver.LoadEpoch;
        bounds = BoundsAround(centerX, centerY);
        baseline = ReadCells(); mask = new byte[baseline.Length]; baselineIssues = new();
        baselineDoors = ReadDoors(baseline, baselineIssues);
        for (int i = 0; i < baseline.Length; i++)
        {
            Cell cell = baseline[i]; int x = X(i), y = Y(i);
            if (cell.safeWall || cell.structural) Protect(x, y, Existing);
            if (RequiresAdjacentClearance(cell))
                foreach (var d in Adjacent)
                {
                    int nx = x + d.X, ny = y + d.Y;
                    if (!bounds.Contains(nx, ny)) continue;
                    if (!At(baseline, nx, ny).hasTile) Protect(nx, ny, Clearance);
                    else if (cell.safeWall) Protect(nx, ny, Existing);
                }
            if (cell.platform)
                for (int dx = -1; dx <= 1; dx++) for (int dy = 1; dy <= 3; dy++)
                    if (bounds.Contains(x + dx, y - dy) && !At(baseline, x + dx, y - dy).hasTile) Protect(x + dx, y - dy, Clearance);
        }
        bool edge = ProtectedEdge();
        primaryMask = PrimaryComponent(centerX, centerY, out int primarySeed);
        bool[] primary = primaryMask;
        bool primaryEdge = Enumerable.Range(0, mask.Length).Any(i => primary[i] && OnEdge(i));
        var knownDoorCells = baselineDoors.Values.SelectMany(d => d.cells).ToHashSet();
        bool unknownPrimaryDoor = Enumerable.Range(0, mask.Length).Any(i => primary[i] && baseline[i].door && !knownDoorCells.Contains(i));
        bool primaryComplete = primarySeed >= 0 && !primaryEdge && !unknownPrimaryDoor && epoch.Length > 0;
        if (primarySeed < 0) baselineIssues.Add("No baseline safe housing-wall anchor was found; original-building coverage is unknown.");
        if (primaryEdge) baselineIssues.Add("The original center-selected connected building reaches the sample edge; its complete coverage is not established. See edgeContacts where primaryComponent=true.");
        else if (edge) baselineIssues.Add("Other protected components reach the sample edge; only their sampled cells are compared. The original center-selected component does not reach that edge; see exact edgeContacts.");
        if (epoch.Length == 0) baselineIssues.Add("World load epoch is unavailable.");
        var rooms = new List<ShelterNativeRoom>();
        var examinedRooms = new List<ShelterAuditBounds>(); int probes = 0; bool truncated = false;
        for (int i = 0; i < baseline.Length; i++)
        {
            Cell cell = baseline[i]; int x = X(i), y = Y(i);
            if (!cell.safeWall || cell.solid || cell.door || examinedRooms.Any(r => r.Contains(x, y))) continue;
            if (probes == MaximumRoomProbes) { truncated = true; break; }
            ShelterNativeRoom room = ReadRoom(x, y); probes++;
            if (room.available && room.bounds != null) examinedRooms.Add(room.bounds);
            if (room.nativeValid && room.bounds != null && bounds.Contains(room.bounds)) rooms.Add(room);
        }
        originalReport = BaselineReport = new(true, worldId, epoch, Main.GameUpdateCount, bounds, mask.Count(m => m != 0),
            Enumerable.Range(0, mask.Length).Count(i => mask[i] != 0 && !baseline[i].hasTile), baselineDoors.Count, edge,
            Counts(baseline), rooms.ToArray(), probes, truncated, baselineIssues.ToArray(), Scope)
        {
            primaryComponentBounds = ComponentBounds(primary), primaryProtectedCells = primary.Count(v => v),
            primaryProtectedTouchesBoundary = primaryEdge, primaryCoverageComplete = primaryComplete,
            primarySeedX = primarySeed < 0 ? null : X(primarySeed), primarySeedY = primarySeed < 0 ? null : Y(primarySeed),
            edgeContacts = Enumerable.Range(0, mask.Length).Where(i => mask[i] != 0 && OnEdge(i)).Select(i =>
                new ShelterAuditEdgeContact(X(i), Y(i), baseline[i].tileType, baseline[i].hasTile ? TileID.Search.GetName(baseline[i].tileType) ?? "unknown" : "air",
                    baseline[i].wallType, primary[i], baseline[i].safeWall, baseline[i].structural, baseline[i].furniture,
                    baseline[i].platform, baseline[i].door, (mask[i] & Clearance) != 0)).ToArray()
        };
    }

    public static ShelterWorldAudit Capture(int centerX, int centerY) => new(centerX, centerY);

    /// <summary>Reads the original mask in O(width*height); never rebuilds it from the current world.</summary>
    public GoalConstructionProtection ReadConstructionProtection(int x, int y, int width, int height)
    {
        GoalConstructionProtection Result(bool available, bool covered, bool? allowed, string reason,
            int count = 0, IReadOnlyList<GoalProtectedCell>? hits = null) =>
            new(false, available, covered, allowed, count, hits ?? Array.Empty<GoalProtectedCell>(),
                Math.Max(0, count - (hits?.Count ?? 0)), reason)
            {
                Region = new(x, y, width, height),
                SampleBounds = new(bounds.x, bounds.y, bounds.width, bounds.height),
                ObservedAtTick = Main.GameUpdateCount
            };
        if (!SameWorld() || Main.GameUpdateCount < BaselineReport.observedAtTick)
            return Result(false, false, null, "Original protected mask belongs to another world load or a later capture tick.");
        if (width is < 1 or > 97 || height is < 1 or > 81 || x < 0 || y < 0 ||
            (long)x + width - 1 > int.MaxValue || (long)y + height - 1 > int.MaxValue)
            return Result(false, false, null, "Rectangle must be positive, non-overflowing and at most 97x81 cells.");
        if (!bounds.Contains(new ShelterAuditBounds(x, y, width, height)))
            return Result(true, false, null, "The requested write region extends outside the immutable sample; protection is unknown.");
        if (!BaselineReport.primaryCoverageComplete)
            return Result(true, false, null, "Original building coverage is incomplete; this check cannot approve construction.");
        int count = 0;
        var hits = new List<GoalProtectedCell>();
        for (int ty = y; ty < y + height; ty++) for (int tx = x; tx < x + width; tx++)
        {
            byte protection = mask[Index(tx, ty)];
            if (protection == 0) continue;
            count++;
            if (hits.Count < 8) hits.Add(new(tx, ty, protection == Clearance ? "original_empty_clearance" :
                (protection & Clearance) != 0 ? "original_structure_and_clearance" : "original_structure_wall_or_boundary"));
        }
        return Result(true, true, count == 0, count == 0
            ? "No original protected cell intersects this write region; route, native placement and housing are not tested."
            : "Write region intersects the original goal protection mask; existing structure and empty clearance must remain unchanged.",
            count, hits.AsReadOnly());
    }

    /// <summary>Explains the unchanged mask from its immutable original cells; does not approve or relax construction.</summary>
    public ShelterProtectionReport ExplainProtection(int x, int y)
    {
        const string explanation = "Immutable baseline protection provenance only. Adjacency to a decoration is reported as such; this is not proof that a new obstacle blocked a native travel route. Compare actual passage before/after separately. No mask, world, actor, inventory or planning state was modified.";
        if (!SameWorld() || !bounds.Contains(x, y))
            return new(false, worldId, epoch, BaselineReport.observedAtTick, bounds, x, y, false, Array.Empty<string>(),
                false, null, null, 0, false, null, Array.Empty<ShelterProtectionOrigin>(), Array.Empty<ShelterProtectionNeighbor>(), explanation + " Coordinate/world is outside this audit; protection is unknown, not absent.");
        int index = Index(x, y); Cell cell = baseline[index];
        var origins = new List<ShelterProtectionOrigin>();
        void Origin(int sx, int sy, string reason)
        {
            Cell source = At(baseline, sx, sy);
            origins.Add(new(sx, sy, reason, source.tileType, source.hasTile ? TileID.Search.GetName(source.tileType) ?? "unknown" : "air",
                source.wallType, source.safeWall, source.structural, source.furniture, source.platform, source.door));
        }
        if (cell.safeWall) Origin(x, y, "self_existing_safe_housing_wall_including_interior");
        if (cell.structural) Origin(x, y, "self_existing_recognized_construction_or_object");
        foreach (var d in Adjacent)
        {
            int sx = x + d.X, sy = y + d.Y;
            if (!bounds.Contains(sx, sy)) continue;
            Cell source = At(baseline, sx, sy);
            if (!cell.hasTile && RequiresAdjacentClearance(source)) Origin(sx, sy, "original_empty_clearance_adjacent_to_room_door_construction_or_other_furniture");
            else if (cell.hasTile && source.safeWall) Origin(sx, sy, "existing_boundary_cell_adjacent_to_safe_housing_wall");
        }
        if (!cell.hasTile)
            for (int dx = -1; dx <= 1; dx++) for (int dy = 1; dy <= 3; dy++)
                if (bounds.Contains(x + dx, y + dy) && At(baseline, x + dx, y + dy).platform)
                    Origin(x + dx, y + dy, "original_platform_twenty_pixel_body_three_tile_headroom");
        var component = new HashSet<int>();
        if (mask[index] != 0)
        {
            var frontier = new Queue<int>(); component.Add(index); frontier.Enqueue(index);
            while (frontier.Count > 0)
            {
                int item = frontier.Dequeue(), sx = X(item), sy = Y(item);
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    if (!bounds.Contains(sx + dx, sy + dy)) continue;
                    int next = Index(sx + dx, sy + dy);
                    if (mask[next] != 0 && component.Add(next)) frontier.Enqueue(next);
                }
            }
        }
        ShelterAuditBounds? componentBounds = component.Count == 0 ? null : new(component.Min(X), component.Min(Y),
            component.Max(X) - component.Min(X) + 1, component.Max(Y) - component.Min(Y) + 1);
        int first = component.Count == 0 ? -1 : component.Min();
        var neighbors = new List<ShelterProtectionNeighbor>();
        for (int sy = y - 3; sy <= y + 3; sy++) for (int sx = x - 1; sx <= x + 1; sx++)
            if (bounds.Contains(sx, sy))
            { int next = Index(sx, sy); neighbors.Add(new(sx, sy, mask[next] != 0, primaryMask[next], baseline[next])); }
        var reasons = new List<string>();
        if ((mask[index] & Existing) != 0) reasons.Add("existing_structure_wall_or_boundary");
        if ((mask[index] & Clearance) != 0) reasons.Add("original_empty_clearance");
        return new(true, worldId, epoch, BaselineReport.observedAtTick, bounds, x, y, mask[index] != 0, reasons.ToArray(),
            primaryMask[index], first < 0 ? null : $"{X(first)},{Y(first)}", componentBounds, component.Count, component.Any(OnEdge),
            cell, origins.ToArray(), neighbors.ToArray(), explanation);
    }

    public ShelterPreservationReport CheckPreservation()
    {
        var issues = new List<string>(baselineIssues);
        if (!SameWorld())
            return new(false, false, Main.worldID, WorldKnowledgeObserver.LoadEpoch, Main.GameUpdateCount, bounds,
                mask.Count(m => m != 0), false, 0, Array.Empty<ShelterCellDifference>(), false, 0, 0,
                new[] { "Audit belongs to a different world load; no baseline was borrowed or recaptured." }, Scope);
        Cell[] current = ReadCells(); var currentDoorIssues = new List<string>();
        var doors = ReadDoors(current, currentDoorIssues);
        var normalized = new HashSet<int>(); int normalizedDoors = 0;
        foreach (var pair in baselineDoors)
        {
            Door before = pair.Value;
            if (!doors.TryGetValue(pair.Key, out Door? after) || !before.rowAttributes.SequenceEqual(after.rowAttributes)) continue;
            normalizedDoors++;
            foreach (int index in before.cells.Concat(after.cells).Distinct())
            {
                bool beforeFits = before.cells.Contains(index) || !baseline[index].hasTile;
                bool afterFits = after.cells.Contains(index) || !current[index].hasTile;
                if (beforeFits && afterFits) normalized.Add(index);
            }
        }
        int differences = 0, harmlessFrames = 0, vegetationChanges = 0;
        var samples = new List<ShelterCellDifference>();
        var vegetationSamples = new List<ShelterCellDifference>();
        for (int i = 0; i < baseline.Length; i++)
        {
            if (mask[i] == 0) continue;
            Cell before = baseline[i], after = current[i];
            bool wallChanged = before.wallType != after.wallType || before.wallColor != after.wallColor ||
                before.wallInvisible != after.wallInvisible || before.wallFullbright != after.wallFullbright;
            bool vegetationEquivalent = !SameTile(before, after) && AllowedVegetationChange(before, after);
            bool tileChanged = !normalized.Contains(i) && !vegetationEquivalent && !SameTile(before, after);
            bool liquidChanged = before.liquidAmount != after.liquidAmount || before.liquidType != after.liquidType;
            if (vegetationEquivalent)
            {
                vegetationChanges++;
                if (vegetationSamples.Count < 16) vegetationSamples.Add(new(X(i), Y(i),
                    before.hasTile ? "allowed_known_vegetation_maturation_or_frame" : "allowed_nonblocking_grass_or_vine_growth", before, after));
            }
            if (wallChanged || tileChanged || liquidChanged)
            {
                differences++;
                if (samples.Count < MaximumDiffSamples) samples.Add(new(X(i), Y(i),
                    string.Join("+", new[] { wallChanged ? "wall" : null, tileChanged ? "tile_or_clearance" : null, liquidChanged ? "liquid" : null }.Where(s => s != null)), before, after));
            }
            else if (!vegetationEquivalent && (before.frameX != after.frameX || before.frameY != after.frameY || before.wallFrameX != after.wallFrameX || before.wallFrameY != after.wallFrameY ||
                before.frameNumber != after.frameNumber || before.wallFrameNumber != after.wallFrameNumber)) harmlessFrames++;
        }
        // Unknown/malformed current doors are relevant only if they touch the protected mask.
        if (currentDoorIssues.Count > 0) issues.AddRange(currentDoorIssues.Take(16));
        bool coverage = BaselineReport.primaryCoverageComplete;
        return new(true, differences == 0 && coverage, worldId, epoch, Main.GameUpdateCount, bounds, BaselineReport.protectedCells,
            coverage, differences, samples.ToArray(), differences > samples.Count, normalizedDoors, harmlessFrames, issues.ToArray(), Scope)
        { allowedNaturalVegetationChanges = vegetationChanges, naturalVegetationSamples = vegetationSamples.ToArray() };
    }

    /// <summary>Without dimensions x/y is an interior point; with both dimensions it is a declared room envelope's top-left.</summary>
    public ShelterNewRoomReport CheckNewRoom(int x, int y, int? width = null, int? height = null)
    {
        var issues = new List<string>(); ShelterAuditBounds? declared = null;
        if (width.HasValue != height.HasValue || width is <= 0 || height is <= 0 || width > Width || height > Height)
            issues.Add("Supply either an interior x/y or a positive complete width/height envelope within the sampled bounds.");
        if (width.HasValue && height.HasValue && issues.Count == 0) declared = new(x, y, width.Value, height.Value);
        int queryX = declared == null ? x : x + declared.width / 2, queryY = declared == null ? y : y + declared.height / 2;
        if (!SameWorld() || !bounds.Contains(queryX, queryY) || declared != null && !bounds.Contains(declared))
            issues.Add("Candidate/world load lies outside this immutable audit baseline; outside cells are unknown.");
        ShelterNativeRoom room = issues.Count == 0 ? ReadRoom(queryX, queryY) : new(queryX, queryY, false, false, null, "Query not executed.");
        if (!room.available && issues.Count == 0) issues.Add(room.error);
        bool inSample = room.bounds != null && bounds.Contains(room.bounds);
        bool inDeclared = room.bounds != null && (declared == null || declared.Contains(room.bounds));
        if (room.bounds != null && !inSample) issues.Add("Actual native room extends beyond the captured audit rectangle.");
        if (room.bounds != null && !inDeclared) issues.Add("Actual native room extends beyond the declared candidate envelope.");
        int overlap = 0, structural = 0, walls = 0, furniture = 0, naturalInterior = 0, naturalBoundary = 0;
        bool interiorEmpty = false, existing = false;
        ShelterAuditCounts? beforeCounts = null, afterCounts = null;
        if (inSample && room.bounds is { } rb)
        {
            var before = new List<Cell>(); var after = new List<Cell>(); interiorEmpty = true;
            existing = BaselineReport.existingValidRooms.Any(r => r.bounds != null && r.bounds.Contains(queryX, queryY));
            for (int ty = rb.y; ty < rb.y + rb.height; ty++) for (int tx = rb.x; tx < rb.x + rb.width; tx++)
            {
                int index = Index(tx, ty); Cell old = baseline[index], now = ReadCell(tx, ty); before.Add(old); after.Add(now);
                if (mask[index] != 0) overlap++;
                bool interior = tx > rb.x && ty > rb.y && tx < rb.x + rb.width - 1 && ty < rb.y + rb.height - 1;
                if (interior && (old.solid || old.platform || old.structural || old.wallType != 0)) interiorEmpty = false;
                if (interior && old.hasTile && !old.solid && !old.structural && !old.platform) naturalInterior++;
                if (!interior && old.hasTile && !old.structural && old.wallType == 0) naturalBoundary++;
                if (!old.hasTile && now.hasTile && now.structural) structural++;
                if (old.wallType == 0 && now.safeWall) walls++;
                if (!old.hasTile && now.furniture) furniture++;
            }
            beforeCounts = Counts(before); afterCounts = Counts(after);
            // Even an originally valid room absent from the bounded discovery list remains ineligible.
            existing |= beforeCounts.safeWallCells > 0 || beforeCounts.structuralCells > 0;
        }
        bool validNew = room.available && room.nativeValid && inSample && inDeclared && !existing && overlap == 0 && interiorEmpty &&
            structural > 0 && walls > 0 && furniture > 0;
        return new(room.available, validNew, room.nativeValid, Main.worldID, WorldKnowledgeObserver.LoadEpoch, Main.GameUpdateCount,
            bounds, declared, room.bounds, inSample, inDeclared, existing, overlap, interiorEmpty, naturalInterior, naturalBoundary,
            beforeCounts, afterCounts, structural, walls, furniture, issues.ToArray(),
            "Native valid housing plus immutable baseline comparison; not a template match. Baseline interior must lack solid barriers, construction/platforms and walls; non-solid natural vegetation and natural perimeter foundation are separately counted. New structural/wall/furniture counts are cells, not recovered items. Existing housing/protected overlap/outside coverage cannot qualify. This does not assert material provenance or complete goal success.");
    }

    private bool SameWorld() => !Main.gameMenu && Main.worldID == worldId && epoch.Length > 0 && WorldKnowledgeObserver.LoadEpoch == epoch;
    private static readonly (int X, int Y)[] Adjacent = { (-1, 0), (1, 0), (0, -1), (0, 1) };
    private int X(int index) => bounds.x + index % bounds.width;
    private int Y(int index) => bounds.y + index / bounds.width;
    private int Index(int x, int y) => (y - bounds.y) * bounds.width + x - bounds.x;
    private Cell At(Cell[] cells, int x, int y) => cells[Index(x, y)];
    private static bool RequiresAdjacentClearance(Cell cell) => cell.safeWall ||
        cell.structural && cell.tileType != TileID.Torches;
    private void Protect(int x, int y, byte reason) { if (bounds.Contains(x, y)) mask[Index(x, y)] |= reason; }
    private bool OnEdge(int index) => X(index) == bounds.x || Y(index) == bounds.y || X(index) == bounds.x + bounds.width - 1 || Y(index) == bounds.y + bounds.height - 1;
    private bool ProtectedEdge() => Enumerable.Range(0, mask.Length).Any(i => mask[i] != 0 && OnEdge(i));
    private bool[] PrimaryComponent(int centerX, int centerY, out int seed)
    {
        seed = -1; long best = long.MaxValue;
        for (int i = 0; i < baseline.Length; i++)
        {
            if (!baseline[i].safeWall) continue;
            long dx = X(i) - (long)centerX, dy = Y(i) - (long)centerY, distance = dx * dx + dy * dy;
            if (distance < best) { seed = i; best = distance; }
        }
        var primary = new bool[mask.Length];
        if (seed < 0) return primary;
        var frontier = new Queue<int>(); primary[seed] = true; frontier.Enqueue(seed);
        while (frontier.Count > 0)
        {
            int index = frontier.Dequeue(), x = X(index), y = Y(index);
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                if (!bounds.Contains(x + dx, y + dy)) continue;
                int next = Index(x + dx, y + dy);
                if (mask[next] == 0 || primary[next]) continue;
                primary[next] = true; frontier.Enqueue(next);
            }
        }
        return primary;
    }
    private ShelterAuditBounds? ComponentBounds(bool[] component)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (int i = 0; i < component.Length; i++) if (component[i])
        { left = Math.Min(left, X(i)); right = Math.Max(right, X(i)); top = Math.Min(top, Y(i)); bottom = Math.Max(bottom, Y(i)); }
        return left == int.MaxValue ? null : new(left, top, right - left + 1, bottom - top + 1);
    }
    private Cell[] ReadCells()
    {
        var cells = new Cell[bounds.width * bounds.height];
        for (int i = 0; i < cells.Length; i++) cells[i] = ReadCell(X(i), Y(i));
        return cells;
    }
    private static bool ConstructionMaterial(int type)
    {
        string name = TileID.Search.GetName(type) ?? "";
        return name.Contains("Brick", StringComparison.Ordinal) || name.Contains("Beam", StringComparison.Ordinal) ||
            name.Contains("Plank", StringComparison.Ordinal) || name.EndsWith("Wood", StringComparison.Ordinal) ||
            name.EndsWith("Slab", StringComparison.Ordinal) || name == "WoodBlock";
    }
    private static bool DoorType(int type) => type is TileID.ClosedDoor or TileID.OpenDoor || type >= 0 && (type < TileID.Sets.OpenDoorID.Length && TileID.Sets.OpenDoorID[type] >= 0 ||
        type < TileID.Sets.CloseDoorID.Length && TileID.Sets.CloseDoorID[type] >= 0);
    private static Cell ReadCell(int x, int y)
    {
        Tile tile = Main.tile[x, y]; int type = tile.HasTile ? tile.TileType : 0;
        bool platform = tile.HasTile && (Main.tileSolidTop[type] || TileID.Sets.Platforms[type]);
        bool door = tile.HasTile && DoorType(type);
        bool solid = tile.HasTile && !tile.IsActuated && Main.tileSolid[type] && !platform;
        bool furniture = tile.HasTile && Main.tileFrameImportant[type] && !solid && !platform && !door && !VegetationType(type) &&
            !TileID.Sets.IsATreeTrunk[type] && !TileID.Sets.IsVine[type] &&
            type is not (TileID.Trees or TileID.PalmTree or TileID.Cactus or TileID.Vines or TileID.Plants or TileID.Plants2);
        bool structural = tile.HasTile && (door || platform || furniture || ConstructionMaterial(type));
        return new(tile.HasTile, type, tile.WallType, tile.TileColor, tile.WallColor, tile.TileFrameX, tile.TileFrameY, tile.WallFrameX, tile.WallFrameY,
            tile.TileFrameNumber, tile.WallFrameNumber, tile.IsHalfBlock, (int)tile.Slope, tile.IsActuated, tile.HasActuator,
            tile.IsTileInvisible, tile.IsTileFullbright, tile.IsWallInvisible, tile.IsWallFullbright,
            tile.RedWire, tile.BlueWire, tile.GreenWire, tile.YellowWire, tile.LiquidAmount, tile.LiquidType,
            solid, platform, door, furniture, structural,
            tile.WallType > WallID.None && tile.WallType < Main.wallHouse.Length && Main.wallHouse[tile.WallType], tile.HasTile && Main.tileFrameImportant[type]);
    }
    private static bool SameTile(Cell a, Cell b) => a.hasTile == b.hasTile && (!a.hasTile ||
        a.tileType == b.tileType && a.tileColor == b.tileColor && a.half == b.half && a.slope == b.slope && a.actuated == b.actuated &&
        a.actuator == b.actuator && a.tileInvisible == b.tileInvisible && a.tileFullbright == b.tileFullbright &&
        (!a.frameImportant || a.frameX == b.frameX && a.frameY == b.frameY)) &&
        a.redWire == b.redWire && a.blueWire == b.blueWire && a.greenWire == b.greenWire && a.yellowWire == b.yellowWire;
    // Corrupt grass plants have important sprite frames but are wild vegetation,
    // not furniture whose neighboring air defines an existing building passage.
    // CorruptThorns is deliberately excluded; real protected clearance stays strict.
    private static bool VegetationType(int type) => type is TileID.Plants or TileID.Plants2 or TileID.CorruptPlants or TileID.Pumpkins ||
        type >= 0 && type < TileID.Sets.IsVine.Length && TileID.Sets.IsVine[type];
    private static bool NonBlockingVegetation(Cell c) => c.hasTile && VegetationType(c.tileType) &&
        !c.solid && !c.platform && !c.door && !c.furniture && !c.structural;
    private static bool AllowedVegetationChange(Cell before, Cell after)
    {
        // This classifies explicitly known vegetation, never every frame-important
        // tile: native ClayPot/PlanterBox and their anchors remain ordinary mask cells.
        if (before.tileColor != after.tileColor || before.half != after.half || before.slope != after.slope ||
            before.actuated != after.actuated || before.actuator != after.actuator || before.tileInvisible != after.tileInvisible ||
            before.tileFullbright != after.tileFullbright || before.redWire != after.redWire || before.blueWire != after.blueWire ||
            before.greenWire != after.greenWire || before.yellowWire != after.yellowWire) return false;
        // Preserve the original mask/classification: only an already-present native
        // herb's verified same-species cycle is equivalent. Removal/replanting is not.
        if (ShelterVegetationRules.IsKnownHerbBloomTransition(
            new(before.hasTile, before.tileType, before.frameX, before.frameY, before.solid, before.platform, before.door, before.half, before.slope, before.actuated),
            new(after.hasTile, after.tileType, after.frameX, after.frameY, after.solid, after.platform, after.door, after.half, after.slope, after.actuated))) return true;
        if (!NonBlockingVegetation(after)) return false;
        bool grassOrVine = after.tileType is TileID.Plants or TileID.Plants2 or TileID.CorruptPlants || TileID.Sets.IsVine[after.tileType];
        if (!before.hasTile) return grassOrVine;
        if (!NonBlockingVegetation(before) || before.tileType != after.tileType) return false;
        if (after.tileType != TileID.Pumpkins) return grassOrVine;
        // Installed WorldGen.GrowPumpkin: a2x2 crop, +36px frameX per growth
        // step, only while stage<4; frameY (six natural variants/local row) stays.
        // Retain the local column and vertical variant; removal/restyling/regression
        // is not silently treated as native maturation.
        return before.frameX >= 0 && after.frameX >= before.frameX && after.frameX / 36 <= 4 &&
            before.frameX % 36 is 0 or 18 && after.frameX % 36 == before.frameX % 36 &&
            after.frameY == before.frameY && before.frameY >= 0 && before.frameY < 6 * 36 && before.frameY % 18 == 0;
    }
    private static ShelterAuditCounts Counts(IEnumerable<Cell> cells)
    {
        int tiles = 0, walls = 0, safe = 0, structural = 0, furniture = 0, doors = 0, platforms = 0;
        foreach (Cell c in cells) { if (c.hasTile) tiles++; if (c.wallType != 0) walls++; if (c.safeWall) safe++; if (c.structural) structural++; if (c.furniture) furniture++; if (c.door) doors++; if (c.platform) platforms++; }
        return new(tiles, walls, safe, structural, furniture, doors, platforms);
    }
    private static ShelterNativeRoom ReadRoom(int x, int y)
    {
        var scratch = HousingScratch.Select(f => (field: f, original: f.GetValue(null)))
            .Select(p => (p.field, p.original, saved: p.original is Array a ? a.Clone() : p.original)).ToArray();
        try
        {
            bool started = WorldGen.StartRoomCheck(x, y);
            ShelterAuditBounds? room = started ? new(WorldGen.roomX1, WorldGen.roomY1,
                WorldGen.roomX2 - WorldGen.roomX1 + 1, WorldGen.roomY2 - WorldGen.roomY1 + 1) : null;
            return new(x, y, true, started && WorldGen.RoomNeeds(-1), room, "");
        }
        catch (Exception exception) { return new(x, y, false, false, null, exception.GetType().Name); }
        finally
        {
            foreach (var s in scratch)
                if (s.original is Array original && s.saved is Array copy)
                { Array.Copy(copy, original, copy.Length); if (!s.field.IsInitOnly && !ReferenceEquals(s.field.GetValue(null), original)) s.field.SetValue(null, original); }
                else if (!s.field.IsInitOnly) s.field.SetValue(null, s.saved);
        }
    }

    private Dictionary<DoorKey, Door> ReadDoors(Cell[] cells, List<string> issues)
    {
        var result = new Dictionary<DoorKey, Door>(); var visited = new HashSet<int>();
        for (int i = 0; i < cells.Length; i++)
        {
            if (!cells[i].door || visited.Contains(i)) continue;
            if (!TryReadDoor(X(i), Y(i), cells, out Door? door))
            { if (issues.Count < 32) issues.Add($"Door metadata/footprint unresolved at {X(i)},{Y(i)}; exact cell/frame comparison retained, no guessed normalization."); continue; }
            foreach (int index in door!.cells) visited.Add(index);
            if (!result.TryAdd(door.key, door) && issues.Count < 32) issues.Add($"Ambiguous native door anchor at {door.key.anchorX},{door.key.anchorY}.");
        }
        return result;
    }
    private bool TryReadDoor(int x, int y, Cell[] cells, out Door? door)
    {
        door = null;
        try
        {
            if (At(cells, x, y).tileType is TileID.ClosedDoor or TileID.OpenDoor) return TryReadVanillaDoor(x, y, cells, out door);
            Tile tile = Main.tile[x, y]; TileObjectData? data = TileObjectData.GetTileData(tile);
            if (data == null || data.Width < 1 || data.Height < 1 || data.Width > 8 || data.Height > 8 ||
                data.CoordinateFullWidth <= 0 || data.CoordinateFullHeight <= 0) return false;
            int localX = tile.TileFrameX % data.CoordinateFullWidth, localY = tile.TileFrameY % data.CoordinateFullHeight;
            int step = data.CoordinateWidth + data.CoordinatePadding;
            if (localX < 0 || localY < 0 || step <= 0 || localX % step != 0) return false;
            int column = localX / step, row = 0, rowFrame = 0;
            while (row < data.Height && rowFrame != localY) { rowFrame += data.CoordinateHeights[row] + data.CoordinatePadding; row++; }
            if (column >= data.Width || row >= data.Height) return false;
            int left = x - column, top = y - row;
            if (!bounds.Contains(left, top) || !bounds.Contains(left + data.Width - 1, top + data.Height - 1)) return false;
            int style = 0, alternate = 0; TileObjectData.GetTileInfo(tile, ref style, ref alternate);
            int closedType = TileID.Sets.CloseDoorID[tile.TileType] >= 0 ? TileID.Sets.CloseDoorID[tile.TileType] : tile.TileType;
            var key = new DoorKey(left + data.Origin.X, top + data.Origin.Y, closedType, style, data.Height);
            var indices = new List<int>(); var rows = new List<string>(); int dyFrame = 0;
            for (int dy = 0; dy < data.Height; dy++)
            {
                string? attributes = null;
                for (int dx = 0; dx < data.Width; dx++)
                {
                    int index = Index(left + dx, top + dy); Cell cell = cells[index]; Tile part = Main.tile[left + dx, top + dy];
                    int partStyle = 0, partAlternate = 0; TileObjectData.GetTileInfo(part, ref partStyle, ref partAlternate);
                    if (!cell.hasTile || cell.tileType != tile.TileType || partStyle != style ||
                        cell.frameX % data.CoordinateFullWidth != dx * step || cell.frameY % data.CoordinateFullHeight != dyFrame) return false;
                    string current = DoorAttributes(cell);
                    if (attributes != null && current != attributes) return false;
                    attributes = current; indices.Add(index);
                }
                rows.Add(attributes!); dyFrame += data.CoordinateHeights[dy] + data.CoordinatePadding;
            }
            door = new(key, indices.ToArray(), rows.ToArray()); return true;
        }
        catch { return false; }
    }
    private static string DoorAttributes(Cell cell) => $"{cell.tileColor}/{cell.half}/{cell.slope}/{cell.actuated}/{cell.actuator}/{cell.tileInvisible}/{cell.tileFullbright}/{cell.redWire}/{cell.blueWire}/{cell.greenWire}/{cell.yellowWire}";
    private bool TryReadVanillaDoor(int x, int y, Cell[] cells, out Door? door)
    {
        // Installed WorldGen.OpenDoor/CloseDoor use built-in10/11 paths, not the mod Sets mappings.
        // CloseDoor IL019d..01b4 writes per-row Next(3)*18 +54*horizontalStyleGroup.
        // Both native methods retain frameY's54px style stride and18px local rows.
        // OpenDoor/CloseDoor use frameX%72 in{0,18,36,54} to identify leaf column and hinge.
        door = null; Cell tile = At(cells, x, y); bool closed = tile.tileType == TileID.ClosedDoor;
        if (tile.frameX < 0 || tile.frameY < 0 || tile.frameX % 18 != 0 || tile.frameY % 18 != 0) return false;
        int stride = closed ? 54 : 72, group = tile.frameX / stride, local = tile.frameX % stride;
        int row = tile.frameY % 54 / 18, styleY = tile.frameY / 54, top = y - row;
        int left = closed ? x : x - local % 36 / 18, width = closed ? 1 : 2;
        int hinge = closed ? x : local < 36 ? left : left + 1;
        int openDirectionBase = local < 36 ? 0 : 36;
        if (!bounds.Contains(left, top) || !bounds.Contains(left + width - 1, top + 2)) return false;
        var indices = new List<int>(); var attributes = new List<string>();
        for (int dy = 0; dy < 3; dy++)
        {
            string? rowAttributes = null;
            for (int dx = 0; dx < width; dx++)
            {
                int index = Index(left + dx, top + dy); Cell part = cells[index];
                if (!part.hasTile || part.tileType != tile.tileType || part.frameY != styleY * 54 + dy * 18 || part.frameX < 0 ||
                    (closed ? part.frameX / 54 != group || part.frameX % 54 is not (0 or 18 or 36)
                        : part.frameX != group * 72 + openDirectionBase + dx * 18)) return false;
                string value = DoorAttributes(part);
                if (rowAttributes != null && value != rowAttributes) return false;
                rowAttributes = value; indices.Add(index);
            }
            attributes.Add(rowAttributes!);
        }
        door = new(new DoorKey(hinge, top, TileID.ClosedDoor, styleY + 36 * group, 3), indices.ToArray(), attributes.ToArray());
        return true;
    }
}
