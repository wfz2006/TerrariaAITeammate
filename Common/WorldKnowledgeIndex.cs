using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrariaAITeammate.Common;

public sealed record WorldTileSample(int X, int Y, int TileType);
public sealed record WorldTileGroup(int TileType, int CellCount, IReadOnlyList<WorldTileSample> Samples);
public sealed record WorldTileChunk(int ChunkX, int ChunkY, int Left, int Top, int Width, int Height,
    ulong ObservedAtTick, IReadOnlyList<WorldTileGroup> Groups)
{
    public ulong ScanStartedAtTick { get; init; }
}
public sealed record WorldTileCandidate(WorldTileChunk Chunk, WorldTileGroup Group, double DistanceTiles)
{
    public IReadOnlyList<WorldTileSample> MatchingSamples { get; init; } = Array.Empty<WorldTileSample>();
}
public sealed record WorldTileSearch(IReadOnlyList<WorldTileCandidate> Candidates, int TotalMatches, bool Truncated, int NextOffset);

/// <summary>
/// Main-thread-owned, pure index of observed native tile types. Stores counts and bounded
/// real samples per chunk, never a full cell map or claims about reachability or recoverable items.
/// </summary>
public sealed class WorldKnowledgeIndex
{
    public const int ChunkSize = 64, SampleLimit = 3;
    private readonly Dictionary<int, WorldTileChunk> chunks = new();
    private readonly HashSet<int> dirty = new();
    private readonly Dictionary<int, LinkedListNode<int>> queued = new();
    private readonly LinkedList<int> priority = new();
    private int columns, initialCursor, revisitCursor;

    public int WorldId { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string Epoch { get; private set; } = "";
    public long Version { get; private set; }
    public int TotalChunks { get; private set; }
    public int CoveredChunks => chunks.Count - dirty.Count;
    public int DirtyChunks => dirty.Count;
    public bool Ready => Epoch.Length > 0 && CoveredChunks == TotalChunks && dirty.Count == 0;

    public void Reset(int worldId, int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        long across = ((long)width + ChunkSize - 1) / ChunkSize;
        long down = ((long)height + ChunkSize - 1) / ChunkSize;
        if (across * down > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(height), "Too many chunks.");
        Clear();
        WorldId = worldId; Width = width; Height = height;
        columns = (int)across; TotalChunks = (int)(across * down);
        Epoch = Guid.NewGuid().ToString("N");
    }

    public void Clear()
    {
        chunks.Clear(); dirty.Clear(); queued.Clear(); priority.Clear();
        WorldId = Width = Height = TotalChunks = columns = initialCursor = revisitCursor = 0;
        Epoch = ""; Version = 0;
    }

    /// <summary>Commits a complete native chunk only while its captured load epoch is current.</summary>
    public bool TryReplaceChunk(WorldTileChunk chunk, string expectedEpoch)
    {
        if (Epoch.Length == 0 || !string.Equals(Epoch, expectedEpoch, StringComparison.Ordinal)) return false;
        ReplaceChunk(chunk);
        return true;
    }

    public void ReplaceChunk(WorldTileChunk chunk)
    {
        if (Epoch.Length == 0) throw new InvalidOperationException("No world is loaded.");
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.ScanStartedAtTick > chunk.ObservedAtTick)
            throw new ArgumentException("Scan start must not be later than its completion tick.", nameof(chunk));
        if (chunk.ChunkX < 0 || chunk.ChunkX >= columns || chunk.ChunkY < 0 ||
            chunk.ChunkY >= ((long)Height + ChunkSize - 1) / ChunkSize)
            throw new ArgumentException("Chunk is outside the loaded world.", nameof(chunk));
        int left = chunk.ChunkX * ChunkSize, top = chunk.ChunkY * ChunkSize;
        if (chunk.Left != left || chunk.Top != top || chunk.Width != Math.Min(ChunkSize, Width - left) ||
            chunk.Height != Math.Min(ChunkSize, Height - top) || chunk.Groups == null)
            throw new ArgumentException("Chunk bounds must be its complete world-clipped 64-tile region.", nameof(chunk));
        var types = new HashSet<int>();
        var occupied = new HashSet<(int, int)>();
        var groups = new List<WorldTileGroup>();
        int cells = 0;
        foreach (WorldTileGroup group in chunk.Groups)
        {
            if (group == null || group.TileType < 0 || !types.Add(group.TileType) || group.CellCount <= 0 ||
                group.CellCount > chunk.Width * chunk.Height || group.Samples == null ||
                group.Samples.Count > SampleLimit || group.Samples.Count > group.CellCount)
                throw new ArgumentException("Tile groups require distinct native IDs and valid cell/sample counts.", nameof(chunk));
            cells += group.CellCount;
            if (cells > chunk.Width * chunk.Height)
                throw new ArgumentException("Tile counts exceed the chunk area.", nameof(chunk));
            foreach (WorldTileSample sample in group.Samples)
                if (sample == null || sample.TileType != group.TileType || sample.X < left || sample.X >= left + chunk.Width ||
                    sample.Y < top || sample.Y >= top + chunk.Height || !occupied.Add((sample.X, sample.Y)))
                    throw new ArgumentException("Sample must be a distinct real cell of its native type inside the chunk.", nameof(chunk));
            groups.Add(new WorldTileGroup(group.TileType, group.CellCount,
                Array.AsReadOnly(group.Samples.OrderBy(sample => sample.Y).ThenBy(sample => sample.X).ToArray())));
        }
        int key = chunk.ChunkY * columns + chunk.ChunkX;
        chunks[key] = chunk with { Groups = Array.AsReadOnly(groups.OrderBy(group => group.TileType).ToArray()) };
        dirty.Remove(key);
        if (queued.Remove(key, out LinkedListNode<int>? waiting)) priority.Remove(waiting);
        Version++;
    }

    /// <summary>Invalidates cached facts. An unobserved chunk is only prioritized, not counted as previously observed dirty data.</summary>
    public void MarkDirty(int x, int y)
    {
        int key = CellKey(x, y);
        if (chunks.ContainsKey(key) && dirty.Add(key)) Version++;
        Enqueue(key, first: true);
    }

    public void Prioritize(int x, int y) => Enqueue(CellKey(x, y), first: false);

    /// <summary>
    /// Searches valid observed chunk/type groups, not individual tiles or recoverable item counts.
    /// Distance is to the nearest tile coordinate in the chunk rectangle. Uncovered and dirty
    /// regions are excluded, so an empty result never proves their absence. NextOffset is -1
    /// at the last page or the bounded offset ceiling, even if Truncated remains true there.
    /// Optional inclusive distance bounds test stored real samples before counting/paging;
    /// MatchingSamples contains only matching points. Unsampled cells remain unknown even
    /// with full chunk coverage. Region ordering/DistanceTiles still describe chunk edges.
    /// </summary>
    public WorldTileSearch FindTiles(IReadOnlySet<int> tileTypes, int originX, int originY, int offset = 0, int limit = 8,
        int? minDistance = null, int? maxDistance = null)
    {
        ArgumentNullException.ThrowIfNull(tileTypes);
        if (tileTypes.Any(type => type < 0)) throw new ArgumentOutOfRangeException(nameof(tileTypes));
        if (minDistance < 0 || minDistance > 100000 || maxDistance < 0 || maxDistance > 100000 || minDistance > maxDistance)
            throw new ArgumentOutOfRangeException(nameof(minDistance), "Distance bounds must be0..100000 and min<=max.");
        if (Epoch.Length == 0) return new WorldTileSearch(Array.Empty<WorldTileCandidate>(), 0, false, -1);
        _ = CellKey(originX, originY);
        offset = Math.Clamp(offset, 0, 50000); limit = Math.Clamp(limit, 1, 8);
        var candidates = new List<WorldTileCandidate>(limit);
        int total = 0;
        // Sort only chunks; groups are canonicalized at commit. Memory used by a
        // query depends on the chunk count and page size, not all matching cells.
        foreach (var entry in chunks.Where(pair => !dirty.Contains(pair.Key))
            .Select(pair => (Chunk: pair.Value, Distance: Distance(pair.Value, originX, originY)))
            .OrderBy(entry => entry.Distance).ThenBy(entry => entry.Chunk.ChunkY).ThenBy(entry => entry.Chunk.ChunkX))
            foreach (WorldTileGroup group in entry.Chunk.Groups)
                if (tileTypes.Contains(group.TileType))
                {
                    bool filtered = minDistance.HasValue || maxDistance.HasValue;
                    WorldTileSample[]? matching = filtered ? group.Samples.Where(sample =>
                    {
                        double dx = sample.X - (double)originX, dy = sample.Y - (double)originY;
                        double distance = Math.Sqrt(dx * dx + dy * dy);
                        return distance >= (minDistance ?? 0) && (!maxDistance.HasValue || distance <= maxDistance.Value);
                    }).ToArray() : null;
                    if (filtered && matching!.Length == 0) continue;
                    if (total >= offset && candidates.Count < limit)
                        candidates.Add(new WorldTileCandidate(entry.Chunk, group, entry.Distance)
                        { MatchingSamples = matching == null ? group.Samples : Array.AsReadOnly(matching) });
                    total = checked(total + 1);
                }
        bool truncated = total > (long)offset + candidates.Count;
        int next = truncated && candidates.Count > 0 && offset + candidates.Count <= 50000 ? offset + candidates.Count : -1;
        return new WorldTileSearch(Array.AsReadOnly(candidates.ToArray()), total, truncated, next);
    }

    public bool TryTakeNextChunk(out int chunkX, out int chunkY)
    {
        chunkX = chunkY = 0;
        if (Epoch.Length == 0) return false;
        int key;
        if (dirty.Count > 0)
        {
            key = dirty.Min();
            if (queued.Remove(key, out LinkedListNode<int>? waiting)) priority.Remove(waiting);
        }
        else if (priority.First is { } first)
        {
            key = first.Value; priority.RemoveFirst(); queued.Remove(key);
        }
        else if (chunks.Count < TotalChunks)
        {
            // At least one never-observed chunk exists. A chunk taken but not committed
            // remains unobserved and can be retried on the following bounded sweep.
            while (chunks.ContainsKey(initialCursor)) Advance(ref initialCursor);
            key = initialCursor; Advance(ref initialCursor);
        }
        else
        {
            key = revisitCursor; Advance(ref revisitCursor);
        }
        chunkX = key % columns; chunkY = key / columns;
        return true;
    }

    private int CellKey(int x, int y)
    {
        if (Epoch.Length == 0) throw new InvalidOperationException("No world is loaded.");
        if (x < 0 || x >= Width) throw new ArgumentOutOfRangeException(nameof(x));
        if (y < 0 || y >= Height) throw new ArgumentOutOfRangeException(nameof(y));
        return y / ChunkSize * columns + x / ChunkSize;
    }

    private void Enqueue(int key, bool first)
    {
        if (queued.TryGetValue(key, out LinkedListNode<int>? node))
        {
            if (!first) return;
            priority.Remove(node); priority.AddFirst(node);
        }
        else queued.Add(key, first ? priority.AddFirst(key) : priority.AddLast(key));
    }

    private static double Distance(WorldTileChunk chunk, int x, int y)
    {
        double dx = Math.Max(chunk.Left - (double)x, Math.Max(0d, x - (double)chunk.Left - chunk.Width + 1));
        double dy = Math.Max(chunk.Top - (double)y, Math.Max(0d, y - (double)chunk.Top - chunk.Height + 1));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void Advance(ref int cursor) { if (++cursor == TotalChunks) cursor = 0; }
}
