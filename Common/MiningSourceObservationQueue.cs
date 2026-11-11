using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrariaAITeammate.Common;

/// <summary>Bounded observation scheduling only. A geometry mark defers neighboring candidates, never rejects them.</summary>
public sealed class MiningSourceObservationQueue<T> where T : notnull
{
    public const int MaximumEntries = 32768;
    private sealed record Entry(T Value, int Cursor, (int Hint, double Distance, int Cursor, int Order) Priority);
    private sealed class Bucket
    {
        public readonly (int X, int Y) Key;
        public readonly PriorityQueue<Entry, (int Hint, double Distance, int Cursor, int Order)> Entries = new();
        public bool GeometryObserved;
        public Bucket((int X, int Y) key) => Key = key;
    }
    private sealed record BucketHead(Bucket Bucket, Entry Entry);
    private static readonly IComparer<BucketHead> HeadComparer = Comparer<BucketHead>.Create(
        (first, second) => first.Entry.Priority.CompareTo(second.Entry.Priority));

    private readonly bool diversifyGeometry;
    private readonly int bucketSize;
    private readonly Dictionary<(int X, int Y), Bucket> buckets = new();
    private readonly HashSet<T> values = new();
    private readonly SortedDictionary<int, int> cursorCounts = new();
    private readonly SortedSet<BucketHead> readyHeads = new(HeadComparer);
    private readonly SortedSet<BucketHead> deferredHeads = new(HeadComparer);
    private Bucket? pendingBucket, lastDequeuedBucket;
    private bool started;
    private int nextOrder;

    public int Count { get; private set; }
    public int? EarliestCursor => cursorCounts.Count == 0 ? null : cursorCounts.First().Key;
    public int GeometryObservedBucketCount { get; private set; }
    public int DeferredCandidateCount { get; private set; }

    public MiningSourceObservationQueue(bool diversifyGeometry, int bucketSize = 8)
    {
        if (bucketSize <= 0) throw new ArgumentOutOfRangeException(nameof(bucketSize));
        this.diversifyGeometry = diversifyGeometry;
        this.bucketSize = bucketSize;
    }

    /// <summary>All entries must be supplied before the first successful Dequeue. Values must be unique; cursors may repeat.</summary>
    public void Enqueue(T value, int x, int y, int cursor, (int Hint, double Distance, int Cursor) priority)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (started) throw new InvalidOperationException("Observation queue is sealed after dequeue starts.");
        if (cursor < 0) throw new ArgumentOutOfRangeException(nameof(cursor));
        if (double.IsNaN(priority.Distance)) throw new ArgumentOutOfRangeException(nameof(priority));
        if (Count >= MaximumEntries) throw new InvalidOperationException("Observation queue entry bound exceeded.");
        if (!values.Add(value)) throw new ArgumentException("Observation values must be unique.", nameof(value));
        var key = Key(x, y);
        if (!buckets.TryGetValue(key, out Bucket? bucket)) buckets[key] = bucket = new Bucket(key);
        var entry = new Entry(value, cursor, (priority.Hint, priority.Distance, priority.Cursor, nextOrder++));
        bucket.Entries.Enqueue(entry, entry.Priority);
        cursorCounts[cursor] = cursorCounts.GetValueOrDefault(cursor) + 1;
        Count++;
    }

    public T Dequeue()
    {
        if (Count == 0) throw new InvalidOperationException("Observation queue is empty.");
        if (!started)
        {
            started = true;
            foreach (Bucket bucket in buckets.Values) AddHead(bucket);
        }
        if (pendingBucket != null)
        {
            AddHead(pendingBucket);
            pendingBucket = null;
        }
        SortedSet<BucketHead> heads = readyHeads.Count > 0 ? readyHeads : deferredHeads;
        BucketHead head = heads.Min!;
        heads.Remove(head);
        Entry entry = head.Bucket.Entries.Dequeue();
        if (diversifyGeometry && head.Bucket.GeometryObserved) DeferredCandidateCount--;
        Count--;
        if (--cursorCounts[entry.Cursor] == 0) cursorCounts.Remove(entry.Cursor);
        // Keep the just-dequeued bucket outside both sets until its actual
        // geometry result can be marked. Its remaining entries still count.
        lastDequeuedBucket = pendingBucket = head.Bucket;
        return entry.Value;
    }

    /// <summary>Call only after actual geometry for the latest dequeued bucket, never for a cheap protection/legality rejection.</summary>
    public void MarkGeometryObserved(int x, int y)
    {
        if (lastDequeuedBucket == null || lastDequeuedBucket.Key != Key(x, y))
            throw new InvalidOperationException("Geometry mark must match the latest dequeued bucket.");
        if (lastDequeuedBucket.GeometryObserved) return;
        lastDequeuedBucket.GeometryObserved = true;
        GeometryObservedBucketCount++;
        if (diversifyGeometry) DeferredCandidateCount += lastDequeuedBucket.Entries.Count;
    }

    private void AddHead(Bucket bucket)
    {
        if (bucket.Entries.Count == 0) return;
        HeadsFor(bucket).Add(new BucketHead(bucket, bucket.Entries.Peek()));
    }

    private SortedSet<BucketHead> HeadsFor(Bucket bucket)
        => diversifyGeometry && bucket.GeometryObserved ? deferredHeads : readyHeads;

    private (int X, int Y) Key(int x, int y) => (FloorDivide(x), FloorDivide(y));
    private int FloorDivide(int value)
    {
        int quotient = value / bucketSize;
        return value % bucketSize < 0 ? quotient - 1 : quotient;
    }
}
