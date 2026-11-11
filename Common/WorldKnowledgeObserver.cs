using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Terraria;
using Terraria.ID;

namespace TerrariaAITeammate.Common;

/// <summary>Game-thread index maintenance. Queries never scan the whole world synchronously.</summary>
public static partial class WorldKnowledgeObserver
{
    public const int MaximumTileReadsPerTick = 16384;
    public const double TargetMillisecondsPerTick = 2;
    private static readonly WorldKnowledgeIndex Index = new();
    public static string LoadEpoch => Index.Epoch;
    private sealed class GroupBuilder
    {
        public int Count;
        public readonly List<WorldTileSample> Samples = new();
        public void Add(int x, int y, int type)
        {
            Count++;
            if (Samples.Count < WorldKnowledgeIndex.SampleLimit &&
                Samples.All(p => Math.Abs(p.X - x) + Math.Abs(p.Y - y) >= 16))
                Samples.Add(new WorldTileSample(x, y, type));
        }
    }
    private static readonly Dictionary<int, GroupBuilder> groups = new();
    private static HashSet<int> stationTypes = new();
    private static string scanningEpoch = "";
    private static ulong scanStartedAtTick;
    private static int chunkX, chunkY, left, top, width, height, cellOffset;
    private static bool scanning, companionPrioritized;
    private static long totalTileReads;
    private static int lastTileReads;
    private static double lastMilliseconds, maximumMilliseconds;

    public static void Reset()
    {
        Clear();
        Index.Reset(Main.worldID, Main.maxTilesX, Main.maxTilesY);
        var needed = Main.recipe.Take(Math.Min(Recipe.numRecipes, Main.recipe.Length))
            .Where(r => r != null && !r.Disabled).SelectMany(r => r.requiredTile).ToHashSet();
        stationTypes = Enumerable.Range(0, Main.tileSolid.Length)
            .Where(type => needed.Any(required => CompanionCrafting.StationMatches(type, required))).ToHashSet();
        Player player = Main.LocalPlayer;
        if (player.active) Index.Prioritize((int)(player.Center.X / 16), (int)(player.Center.Y / 16));
    }

    public static void Clear()
    {
        Index.Clear(); groups.Clear(); stationTypes.Clear(); scanningEpoch = "";
        scanning = companionPrioritized = false;
        cellOffset = lastTileReads = 0; totalTileReads = 0;
        lastMilliseconds = maximumMilliseconds = 0;
    }

    public static void Invalidate(int x, int y)
    {
        if (Index.Epoch.Length == 0) return;
        Index.MarkDirty(x, y);
        // If a tile changes after it was read in this partial chunk, discard that
        // partial observation. It must not overwrite the dirty signal as current.
        if (scanning && x >= left && x < left + width && y >= top && y < top + height)
        { scanning = false; groups.Clear(); Index.Prioritize(x, y); }
    }

    public static void InvalidateArea(int x, int y, int areaWidth, int areaHeight)
    {
        if (Index.Epoch.Length == 0 || areaWidth <= 0 || areaHeight <= 0) return;
        int minX = Math.Max(0, x), minY = Math.Max(0, y);
        int maxX = (int)Math.Min((long)Index.Width - 1, (long)x + areaWidth - 1);
        int maxY = (int)Math.Min((long)Index.Height - 1, (long)y + areaHeight - 1);
        if (minX > maxX || minY > maxY) return;
        int size = WorldKnowledgeIndex.ChunkSize;
        for (int cy = minY / size; cy <= maxY / size; cy++)
        for (int cx = minX / size; cx <= maxX / size; cx++)
        {
            int px = Math.Max(minX, cx * size), py = Math.Max(minY, cy * size);
            Index.MarkDirty(px, py);
            if (scanning && cx == chunkX && cy == chunkY)
            { scanning = false; groups.Clear(); Index.Prioritize(px, py); }
        }
    }

    public static void Tick()
    {
        if (Main.gameMenu || Main.netMode != NetmodeID.SinglePlayer || Index.Epoch.Length == 0) return;
        if (Index.WorldId != Main.worldID || Index.Width != Main.maxTilesX || Index.Height != Main.maxTilesY)
            Reset();
        if (!companionPrioritized && AITeammateSystem.Instance.CompanionNpc is { } companion)
        {
            Index.Prioritize((int)(companion.Center.X / 16), (int)(companion.Center.Y / 16));
            companionPrioritized = true;
        }
        long started = Stopwatch.GetTimestamp();
        lastTileReads = 0;
        while (lastTileReads < MaximumTileReadsPerTick)
        {
            if (!scanning)
            {
                if (!Index.TryTakeNextChunk(out chunkX, out chunkY)) break;
                left = chunkX * WorldKnowledgeIndex.ChunkSize; top = chunkY * WorldKnowledgeIndex.ChunkSize;
                width = Math.Min(WorldKnowledgeIndex.ChunkSize, Main.maxTilesX - left);
                height = Math.Min(WorldKnowledgeIndex.ChunkSize, Main.maxTilesY - top);
                scanningEpoch = Index.Epoch; scanStartedAtTick = Main.GameUpdateCount;
                groups.Clear(); cellOffset = 0; scanning = true;
            }
            int x = left + cellOffset % width, y = top + cellOffset / width;
            Tile tile = Main.tile[x, y];
            if (tile.HasTile)
            {
                int type = tile.TileType;
                if (!groups.TryGetValue(type, out GroupBuilder? group)) groups[type] = group = new GroupBuilder();
                group.Add(x, y, type);
            }
            cellOffset++; lastTileReads++; totalTileReads++;
            if (cellOffset == width * height)
            {
                var chunk = new WorldTileChunk(chunkX, chunkY, left, top, width, height, Main.GameUpdateCount,
                    groups.OrderBy(p => p.Key).Select(p => new WorldTileGroup(p.Key, p.Value.Count,
                        p.Value.Samples.ToArray())).ToArray()) { ScanStartedAtTick = scanStartedAtTick };
                Index.TryReplaceChunk(chunk, scanningEpoch);
                scanning = false;
            }
            if ((lastTileReads & 63) == 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= TargetMillisecondsPerTick)
                break;
        }
        lastMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        maximumMilliseconds = Math.Max(maximumMilliseconds, lastMilliseconds);
    }

    public static object Status() => new
    {
        mode = "omniscient_queries", worldId = Index.WorldId, epoch = Index.Epoch, version = Index.Version,
        ready = Index.Ready, coveredChunks = Index.CoveredChunks, totalChunks = Index.TotalChunks, dirtyChunks = Index.DirtyChunks,
        chunkSize = WorldKnowledgeIndex.ChunkSize, observedAtTick = Main.GameUpdateCount,
        scan = new { lastTileReads, maximumTileReadsPerTick = MaximumTileReadsPerTick, totalTileReads,
            lastMilliseconds, maximumMilliseconds, targetMillisecondsPerTick = TargetMillisecondsPerTick,
            timeLimit = "cooperative_check_every64_reads; one_batch_or_chunk_commit_can_exceed_target" },
        freshness = "tile_chunks_observed_at_different_ticks; changes_invalidate_and_full_world_is_periodically_rescanned; chests_items_npcs_read_live_on_query"
    };
}
