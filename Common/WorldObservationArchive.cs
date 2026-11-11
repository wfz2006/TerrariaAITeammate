using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

public sealed record WorldObservationQuery(string item, int? x, int? y);
public sealed record WorldObservationDescriptor(string id, long originalSequence, WorldObservationQuery query,
    int worldId, string worldLoadEpoch, long goalStartSequence, ulong observedAtTick, JsonElement? origin,
    int resultUtf8Bytes, string topic = "world")
{
    public bool available => true;
}
public sealed record CompleteWorldObservation(string Id, long OriginalSequence, string ContentHash, long ReadOutcomeSequence = 0);

/// <summary>Engine-owned proof of what was in the actual outgoing frame, never populated by the decision parser.</summary>
public sealed record WorldObservationReceipt(int WorldId, string Epoch, long GoalStartSequence, string SourceGoal,
    long VisibleOutcomeSequence, IReadOnlyList<CompleteWorldObservation> CompleteWorlds, IReadOnlyList<long>? ObservationOutcomeSequences = null)
{
    public static WorldObservationReceipt? FromPlanningFrame(string frame)
    {
        using JsonDocument document = JsonDocument.Parse(frame);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("state", out JsonElement state) ||
            !state.TryGetProperty("planning", out JsonElement planning) ||
            !planning.TryGetProperty("objective", out JsonElement objective) || objective.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(objective.GetString()) ||
            !planning.TryGetProperty("evidence_start_sequence", out JsonElement boundary) || !boundary.TryGetInt64(out long start) ||
            !state.TryGetProperty("knowledge", out JsonElement knowledge) ||
            !knowledge.TryGetProperty("worldKnowledge", out JsonElement world) ||
            !world.TryGetProperty("worldId", out JsonElement wid) || !wid.TryGetInt32(out int worldId) ||
            !world.TryGetProperty("epoch", out JsonElement epochValue) || epochValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(epochValue.GetString())) return null;
        string epoch = epochValue.GetString()!;
        var directory = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        if (planning.TryGetProperty("worldObservations", out JsonElement records) && records.ValueKind == JsonValueKind.Array)
            foreach (JsonElement entry in records.EnumerateArray())
                if (Matches(entry, worldId, epoch, start) && entry.TryGetProperty("available", out JsonElement available) &&
                    available.ValueKind == JsonValueKind.True && entry.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                {
                    string key = id.GetString()!;
                    if (directory.ContainsKey(key)) { directory.Remove(key); duplicateIds.Add(key); }
                    else if (!duplicateIds.Contains(key)) directory[key] = entry;
                }
        long visible = start;
        var complete = new List<CompleteWorldObservation>();
        var observationOutcomes = new List<long>();
        if (planning.TryGetProperty("outcomes", out JsonElement outcomes) && outcomes.ValueKind == JsonValueKind.Array)
            foreach (JsonElement outcome in outcomes.EnumerateArray())
            {
                if (!outcome.TryGetProperty("sequence", out JsonElement seq) || !seq.TryGetInt64(out long sequence) || sequence <= start) continue;
                visible = Math.Max(visible, sequence);
                if (outcome.TryGetProperty("task", out JsonElement task) && task.ValueKind == JsonValueKind.Object &&
                    task.TryGetProperty("target", out JsonElement target) && target.ValueKind == JsonValueKind.String && target.GetString() == "observation")
                    observationOutcomes.Add(sequence);
                if (!outcome.TryGetProperty("result", out JsonElement raw)) continue;
                JsonElement reading = raw;
                if (raw.ValueKind == JsonValueKind.String)
                {
                    string text = raw.GetString()!;
                    // Action text counts toward the visible sequence. Observation bodies are objects.
                    if (!text.AsSpan().TrimStart().StartsWith("{")) continue;
                    try { reading = JsonSerializer.Deserialize<JsonElement>(text); }
                    catch (JsonException) { continue; }
                }
                if (reading.ValueKind != JsonValueKind.Object || !reading.TryGetProperty("topic", out JsonElement topic)) continue;
                string? readingTopic = topic.ValueKind == JsonValueKind.String ? topic.GetString() : null;
                string? id = null; JsonElement full = reading; long original = sequence;
                if (WorldObservationArchive.SupportedTopic(readingTopic) &&
                    outcome.TryGetProperty("observationId", out JsonElement directId) && directId.ValueKind == JsonValueKind.String)
                    id = directId.GetString();
                else if (readingTopic == "observation" && Matches(reading, worldId, epoch, start) &&
                    reading.TryGetProperty("available", out JsonElement found) && found.ValueKind == JsonValueKind.True &&
                    reading.TryGetProperty("historical", out JsonElement historical) && historical.ValueKind == JsonValueKind.True &&
                    reading.TryGetProperty("currentWorldRechecked", out JsonElement rechecked) && rechecked.ValueKind == JsonValueKind.False &&
                    reading.TryGetProperty("id", out JsonElement retrievedId) && retrievedId.ValueKind == JsonValueKind.String &&
                    reading.TryGetProperty("originalSequence", out JsonElement originalSeq) && originalSeq.TryGetInt64(out original) &&
                    reading.TryGetProperty("observation", out full)) id = retrievedId.GetString();
                if (id != null) observationOutcomes.Add(sequence);
                if (id == null || !directory.TryGetValue(id, out JsonElement descriptor) ||
                    !descriptor.TryGetProperty("originalSequence", out JsonElement descriptorSeq) || !descriptorSeq.TryGetInt64(out long recordedSeq) || recordedSeq != original || original > sequence ||
                    full.ValueKind != JsonValueKind.Object || !full.TryGetProperty("topic", out JsonElement originalTopic) ||
                    originalTopic.ValueKind != JsonValueKind.String || !WorldObservationArchive.SupportedTopic(originalTopic.GetString()) ||
                    (descriptor.TryGetProperty("topic", out JsonElement savedTopic) ? savedTopic.ValueKind != JsonValueKind.String || savedTopic.GetString() != originalTopic.GetString() : originalTopic.GetString() != "world") ||
                    readingTopic == "observation" &&
                        ((reading.TryGetProperty("originalTopic", out JsonElement retrievedTopic) ? retrievedTopic.ValueKind != JsonValueKind.String || retrievedTopic.GetString() != originalTopic.GetString() : originalTopic.GetString() != "world") ||
                            !SameFields(reading, descriptor, "query", "origin", "observedAtTick")) ||
                    !new LosslessFrameDecoder(root).TryDecode(full, out JsonElement decoded) ||
                    !BodyIdentityMatches(decoded, worldId, epoch) ||
                    originalTopic.GetString() == "sources" && !SourceProofMatches(descriptor, decoded, outcome, readingTopic == "observation", id, worldId, epoch)) continue;
                complete.Add(new(id, original, WorldObservationArchive.ContentHash(decoded), sequence));
            }
        return new(worldId, epoch, start, objective.GetString()!, visible, complete.AsReadOnly(), observationOutcomes.AsReadOnly());
    }

    private static bool SourceProofMatches(JsonElement descriptor, JsonElement body, JsonElement outcome, bool retrieved, string id, int worldId, string epoch)
    {
        if (!descriptor.TryGetProperty("observedAtTick", out JsonElement tickValue) || !tickValue.TryGetUInt64(out ulong completed) ||
            !SourceObservationWire.IsCompletePage(body, worldId, epoch, completed) || !SameFields(descriptor, body, "origin") ||
            !outcome.TryGetProperty("task", out JsonElement task) || task.ValueKind != JsonValueKind.Object ||
            !task.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String || type.GetString() != "inspect" ||
            !task.TryGetProperty("target", out JsonElement target) || target.ValueKind != JsonValueKind.String || target.GetString() != (retrieved ? "observation" : "sources")) return false;
        return retrieved ? task.TryGetProperty("item", out JsonElement item) && item.ValueKind == JsonValueKind.String && item.GetString() == id :
            descriptor.TryGetProperty("query", out JsonElement query) && query.ValueKind == JsonValueKind.Object && SameFields(query, task, "item", "x", "y");
    }

    private static bool Matches(JsonElement record, int worldId, string epoch, long start)
        => record.ValueKind == JsonValueKind.Object && record.TryGetProperty("worldId", out JsonElement w) && w.TryGetInt32(out int actualWorld) && actualWorld == worldId &&
            record.TryGetProperty("worldLoadEpoch", out JsonElement e) && e.ValueKind == JsonValueKind.String && e.GetString() == epoch &&
            record.TryGetProperty("goalStartSequence", out JsonElement s) && s.TryGetInt64(out long actualStart) && actualStart == start;

    private static bool BodyIdentityMatches(JsonElement body, int worldId, string epoch)
    {
        bool Stamp(JsonElement value, string worldField, string epochField) =>
            (!value.TryGetProperty(worldField, out JsonElement w) || w.TryGetInt32(out int id) && id == worldId) &&
            (!value.TryGetProperty(epochField, out JsonElement e) || e.ValueKind == JsonValueKind.String && e.GetString() == epoch);
        return Stamp(body, "worldId", "epoch") &&
            (!body.TryGetProperty("world", out JsonElement world) || world.ValueKind == JsonValueKind.Object && Stamp(world, "worldId", "epoch"));
    }

    private static bool SameFields(JsonElement first, JsonElement second, params string[] fields)
        => fields.All(field => first.TryGetProperty(field, out JsonElement a) && second.TryGetProperty(field, out JsonElement b) &&
            JsonNode.DeepEquals(JsonNode.Parse(a.GetRawText()), JsonNode.Parse(b.GetRawText())));

    /// <summary>Only the exact lossless table/ref forms emitted in this same request. No overview or external data.</summary>
    private sealed class LosslessFrameDecoder(JsonElement frame)
    {
        private const string Table = "columns_and_rows_v1";
        private const string TableNote = "Each row is one original object: set columns[i] to row[i], including null. Order and every field are preserved.";
        private const string RefNote = "Exactly equal data in this request; each observation keeps its own historical identity and timestamp.";
        private const string ObjectDefaults = "objects_with_defaults_v1";
        private const string ObjectDefaultsNote = "Reconstruct each original object by recursively adding missing properties from defaults to each row. Explicit row properties, including false and null, win. Recurse only when both values are objects; arrays are complete values and are never merged. Every default property existed in every corresponding original object. Preserve row order and every unknown field. If the surrounding page has summaryDefaults, expand this table first, then apply summaryDefaults; historical omission markers still apply.";
        private int nodes = 500000, characters = WorldObservationArchive.MaximumResultUtf8Bytes, references = 256;

        public bool TryDecode(JsonElement value, out JsonElement decoded)
        {
            decoded = default;
            try
            {
                JsonNode? node = Decode(value, 0, new HashSet<string>(StringComparer.Ordinal));
                string text = node!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                if (Encoding.UTF8.GetByteCount(text) > WorldObservationArchive.MaximumResultUtf8Bytes) return false;
                decoded = JsonSerializer.Deserialize<JsonElement>(text); return true;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or OverflowException)
            { return false; }
        }

        private JsonNode? Decode(JsonElement value, int depth, HashSet<string> activeReferences)
        {
            if (--nodes < 0 || depth > 96 || (characters -= 2) < 0) throw new InvalidOperationException("Lossless decoding budget exceeded.");
            if (value.ValueKind == JsonValueKind.Array)
            {
                var array = new JsonArray();
                foreach (JsonElement item in value.EnumerateArray()) array.Add(Decode(item, depth + 1, activeReferences));
                return array;
            }
            if (value.ValueKind != JsonValueKind.Object)
            {
                if ((characters -= value.GetRawText().Length) < 0) throw new InvalidOperationException("Lossless decoding size exceeded.");
                return JsonNode.Parse(value.GetRawText());
            }
            JsonProperty[] properties = value.EnumerateObject().ToArray();
            if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                value.TryGetProperty("wireView", out _) || value.TryGetProperty("detailOmittedFromWire", out JsonElement omitted) && omitted.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException("Duplicate properties or omitted detail cannot prove a complete read.");
            if (value.TryGetProperty("$ref", out JsonElement pointer))
            {
                if (properties.Length != 2 || pointer.ValueKind != JsonValueKind.String ||
                    !value.TryGetProperty("note", out JsonElement note) || note.ValueKind != JsonValueKind.String || note.GetString() != RefNote || --references < 0)
                    throw new InvalidOperationException("Unknown reference encoding.");
                string path = pointer.GetString()!;
                if (!activeReferences.Add(path)) throw new InvalidOperationException("Cyclic reference.");
                try { return Decode(Resolve(path), depth + 1, activeReferences); }
                finally { activeReferences.Remove(path); }
            }
            if (value.TryGetProperty("encoding", out JsonElement defaultsEncoding) &&
                defaultsEncoding.ValueKind == JsonValueKind.String && defaultsEncoding.GetString() == ObjectDefaults)
            {
                if (properties.Length != 4 || !value.TryGetProperty("decoding", out JsonElement decoding) ||
                    decoding.ValueKind != JsonValueKind.String || decoding.GetString() != ObjectDefaultsNote ||
                    !value.TryGetProperty("defaults", out JsonElement defaults) || defaults.ValueKind != JsonValueKind.Object ||
                    !defaults.EnumerateObject().Any() || !value.TryGetProperty("rows", out JsonElement rows) ||
                    rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 2 or > 250000 ||
                    HasNestedWireEncoding(defaults, depth + 1) || rows.EnumerateArray().Any(row =>
                        row.ValueKind != JsonValueKind.Object || HasNestedWireEncoding(row, depth + 1)))
                    throw new InvalidOperationException("Unknown object defaults encoding.");
                // Validate every default, even when all rows explicitly override it.
                // Each expanded copy below is separately charged to the decode budget.
                Decode(defaults, depth + 1, activeReferences);
                var objects = new JsonArray();
                foreach (JsonElement row in rows.EnumerateArray())
                {
                    var obj = (JsonObject)Decode(row, depth + 1, activeReferences)!;
                    AddMissingDefaults(obj, defaults, depth + 1, activeReferences);
                    objects.Add(obj);
                }
                return objects;
            }
            if (value.TryGetProperty("encoding", out JsonElement encoding) &&
                (encoding.ValueKind == JsonValueKind.String && encoding.GetString() == Table || value.TryGetProperty("columns", out _) && value.TryGetProperty("rows", out _)))
            {
                if (properties.Length != 4 || encoding.ValueKind != JsonValueKind.String || encoding.GetString() != Table ||
                    !value.TryGetProperty("decoding", out JsonElement decoding) || decoding.ValueKind != JsonValueKind.String || decoding.GetString() != TableNote ||
                    !value.TryGetProperty("columns", out JsonElement columnValues) || columnValues.ValueKind != JsonValueKind.Array ||
                    !value.TryGetProperty("rows", out JsonElement rowValues) || rowValues.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Unknown table encoding.");
                JsonElement[] columns = columnValues.EnumerateArray().ToArray();
                if (columns.Length is < 1 or > 256 || columns.Any(c => c.ValueKind != JsonValueKind.String) ||
                    columns.Select(c => c.GetString()).Distinct(StringComparer.Ordinal).Count() != columns.Length ||
                    rowValues.GetArrayLength() < 4 || (long)columns.Length * rowValues.GetArrayLength() > 250000)
                    throw new InvalidOperationException("Invalid table columns or size.");
                var rows = new JsonArray();
                foreach (JsonElement row in rowValues.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != columns.Length)
                        throw new InvalidOperationException("Table row/column mismatch.");
                    var obj = new JsonObject(); int index = 0;
                    foreach (JsonElement cell in row.EnumerateArray())
                    {
                        string key = columns[index++].GetString()!;
                        if ((characters -= key.Length + 3) < 0) throw new InvalidOperationException("Lossless table size exceeded.");
                        obj.Add(key, Decode(cell, depth + 1, activeReferences));
                    }
                    rows.Add(obj);
                }
                return rows;
            }
            var result = new JsonObject();
            foreach (JsonProperty property in properties)
            {
                if ((characters -= property.Name.Length + 3) < 0) throw new InvalidOperationException("Lossless object size exceeded.");
                result.Add(property.Name, Decode(property.Value, depth + 1, activeReferences));
            }
            return result;
        }

        private void AddMissingDefaults(JsonObject row, JsonElement defaults, int depth, HashSet<string> activeReferences)
        {
            if (--nodes < 0 || depth > 96 || (characters -= 2) < 0)
                throw new InvalidOperationException("Lossless defaults expansion budget exceeded.");
            foreach (JsonProperty property in defaults.EnumerateObject())
            {
                if (!row.TryGetPropertyValue(property.Name, out JsonNode? explicitValue))
                {
                    if ((characters -= property.Name.Length + 3) < 0)
                        throw new InvalidOperationException("Lossless defaults size exceeded.");
                    row.Add(property.Name, Decode(property.Value, depth + 1, activeReferences));
                }
                else if (explicitValue is JsonObject nested && property.Value.ValueKind == JsonValueKind.Object)
                    AddMissingDefaults(nested, property.Value, depth + 1, activeReferences);
            }
        }

        private static bool HasNestedWireEncoding(JsonElement value, int depth)
        {
            if (depth > 96) return true;
            if (value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray().Any(item => HasNestedWireEncoding(item, depth + 1));
            return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any(property =>
                property.Name is "encoding" or "$ref" || HasNestedWireEncoding(property.Value, depth + 1));
        }

        private JsonElement Resolve(string pointer)
        {
            // Only references to observations in this exact outgoing frame are a supported encoding.
            if (!pointer.StartsWith("#/state/planning/outcomes/", StringComparison.Ordinal)) throw new InvalidOperationException("External or unsupported pointer.");
            string[] parts = pointer[2..].Split('/');
            if (parts.Length < 5 || parts[4] != "result") throw new InvalidOperationException("Unsupported pointer target.");
            JsonElement current = frame;
            foreach (string encoded in parts)
            {
                for (int i = 0; i < encoded.Length; i++)
                    if (encoded[i] == '~' && (++i >= encoded.Length || encoded[i] is not ('0' or '1')))
                        throw new InvalidOperationException("Invalid pointer escape.");
                string part = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out JsonElement property)) current = property;
                else if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, out int index) && index >= 0 &&
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture) == part && index < current.GetArrayLength()) current = current[index];
                else throw new InvalidOperationException("Missing pointer target.");
            }
            return current;
        }
    }
}

/// <summary>Goal-owned immutable result text; bounded by64 entries and4MiB of original UTF8 result content.</summary>
public sealed class WorldObservationArchive
{
    public const int MaximumEntries = 64, MaximumResultUtf8Bytes = 4 * 1024 * 1024;
    private sealed record Entry(WorldObservationDescriptor Descriptor, string Result, string Hash);
    private readonly int entryLimit, byteLimit;
    private readonly LinkedList<Entry> entries = new();
    private readonly Dictionary<string, Entry> byId = new(StringComparer.Ordinal);
    private readonly HashSet<string> acknowledged = new(StringComparer.Ordinal);
    private int worldId, bytes;
    private string epoch = "", goal = "";
    private long start;
    public long AcknowledgedOutcomeSequence { get; private set; }
    public int Count => entries.Count;
    public int ResultUtf8Bytes => bytes;
    internal static bool SupportedTopic(string? topic) => topic is "world" or "terrain" or "site" or "route" or "sources";

    public WorldObservationArchive(int maxEntries = MaximumEntries, int maxResultUtf8Bytes = MaximumResultUtf8Bytes)
    {
        if (maxEntries < 1 || maxEntries > MaximumEntries || maxResultUtf8Bytes < 1 || maxResultUtf8Bytes > MaximumResultUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        entryLimit = maxEntries; byteLimit = maxResultUtf8Bytes;
    }
    public void Clear()
    { entries.Clear(); byId.Clear(); acknowledged.Clear(); bytes = 0; worldId = 0; epoch = goal = ""; start = 0; AcknowledgedOutcomeSequence = 0; }
    public void Begin(int currentWorldId, string worldLoadEpoch, long goalStartSequence, string sourceGoal)
    {
        if (Matches(currentWorldId, worldLoadEpoch, goalStartSequence) && goal == sourceGoal) return;
        Clear(); worldId = currentWorldId; epoch = worldLoadEpoch; start = goalStartSequence; goal = sourceGoal;
    }
    private bool Matches(int currentWorldId, string worldLoadEpoch, long goalStartSequence)
        => epoch.Length > 0 && worldId == currentWorldId && epoch == worldLoadEpoch && start == goalStartSequence;
    public IReadOnlyList<WorldObservationDescriptor> Directory(int currentWorldId, string worldLoadEpoch, long goalStartSequence)
        => Matches(currentWorldId, worldLoadEpoch, goalStartSequence) ? entries.Select(e => e.Descriptor).ToArray() : Array.Empty<WorldObservationDescriptor>();
    public string? IdFor(long sequence, int currentWorldId, string worldLoadEpoch, long goalStartSequence)
        => Directory(currentWorldId, worldLoadEpoch, goalStartSequence).FirstOrDefault(e => e.originalSequence == sequence)?.id;
    public string? Record(AgentOutcome outcome, int currentWorldId, string worldLoadEpoch, long goalStartSequence, ulong tick)
    {
        if (!Matches(currentWorldId, worldLoadEpoch, goalStartSequence) || outcome.Sequence <= start ||
            outcome.Task.Action != AIActionType.Inspect || !SupportedTopic(outcome.Task.Target.ToLowerInvariant())) return null;
        Entry? existing = entries.FirstOrDefault(e => e.Descriptor.originalSequence == outcome.Sequence);
        if (existing != null) return existing.Result == outcome.Result ? existing.Descriptor.id : null;
        int size = Encoding.UTF8.GetByteCount(outcome.Result);
        if (size > byteLimit) return null;
        JsonElement reading;
        try { reading = JsonSerializer.Deserialize<JsonElement>(outcome.Result); }
        catch (JsonException) { return null; }
        if (reading.ValueKind != JsonValueKind.Object || !reading.TryGetProperty("topic", out JsonElement topic) || topic.ValueKind != JsonValueKind.String ||
            !SupportedTopic(topic.GetString()) || !outcome.Task.Target.Equals(topic.GetString(), StringComparison.OrdinalIgnoreCase)) return null;
        string originalTopic = topic.GetString()!;
        if (originalTopic == "sources" && (outcome.Status != "succeeded" || !SourceObservationWire.IsCompletePage(reading, worldId, epoch, tick))) return null;
        string id = "worldobs:" + Guid.NewGuid().ToString("N");
        var descriptor = new WorldObservationDescriptor(id, outcome.Sequence, new(outcome.Task.Item, outcome.Task.X, outcome.Task.Y),
            worldId, epoch, start, tick, ObservationOrigin(reading, originalTopic), size, originalTopic);
        var entry = new Entry(descriptor, outcome.Result, ContentHash(reading));
        while (entries.Count >= entryLimit || bytes + size > byteLimit)
        {
            Entry old = entries.First!.Value; entries.RemoveFirst(); byId.Remove(old.Descriptor.id);
            acknowledged.Remove(old.Descriptor.id); bytes -= old.Descriptor.resultUtf8Bytes;
        }
        entries.AddLast(entry); byId[id] = entry; bytes += size;
        return id;
    }
    public object Read(string id, int currentWorldId, string worldLoadEpoch, long goalStartSequence)
    {
        if (!Matches(currentWorldId, worldLoadEpoch, goalStartSequence) || !byId.TryGetValue(id, out Entry? entry))
            return new { topic = "observation", historical = true, available = false, id,
                reason = !Matches(currentWorldId, worldLoadEpoch, goalStartSequence) ? "world_or_goal_archive_unavailable" : "unknown_or_evicted_observation_id",
                currentWorldRechecked = false };
        WorldObservationDescriptor d = entry.Descriptor;
        return new { topic = "observation", historical = true, available = true, d.id, d.originalSequence, d.query,
            d.worldId, d.worldLoadEpoch, d.goalStartSequence, d.observedAtTick, d.origin, originalTopic = d.topic,
            observation = JsonSerializer.Deserialize<JsonElement>(entry.Result), currentWorldRechecked = false };
    }
    public void Acknowledge(WorldObservationReceipt? receipt, long latestNativeOutcomeSequence)
    {
        if (receipt == null || !Matches(receipt.WorldId, receipt.Epoch, receipt.GoalStartSequence) || receipt.SourceGoal != goal) return;
        long proposed = Math.Min(receipt.VisibleOutcomeSequence, latestNativeOutcomeSequence);
        var fullyReadOutcomes = new HashSet<long>();
        foreach (CompleteWorldObservation proof in receipt.CompleteWorlds)
            if (proof.OriginalSequence <= proposed && byId.TryGetValue(proof.Id, out Entry? entry) &&
                entry.Descriptor.originalSequence == proof.OriginalSequence && entry.Hash == proof.ContentHash)
            { acknowledged.Add(proof.Id); fullyReadOutcomes.Add(proof.ReadOutcomeSequence == 0 ? proof.OriginalSequence : proof.ReadOutcomeSequence); }
        // Reading a historical ID is a new outcome: its original page's old ACK
        // cannot turn a summary, corrupt body or missing full proof into a new ACK.
        if (receipt.ObservationOutcomeSequences != null)
            foreach (long sequence in receipt.ObservationOutcomeSequences)
                if (sequence > AcknowledgedOutcomeSequence && sequence <= proposed && !fullyReadOutcomes.Contains(sequence)) proposed = Math.Min(proposed, sequence - 1);
        // A journal-window gap is not proof that the missing complete result was read.
        foreach (Entry entry in entries)
            if (entry.Descriptor.originalSequence <= proposed && !acknowledged.Contains(entry.Descriptor.id))
                proposed = Math.Min(proposed, entry.Descriptor.originalSequence - 1);
        AcknowledgedOutcomeSequence = Math.Max(AcknowledgedOutcomeSequence, proposed);
    }

    internal static string ContentHash(JsonElement reading)
    {
        JsonNode? Normalize(JsonNode? node) => node is JsonObject obj
            ? new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Normalize(p.Value))))
            : node is JsonArray array ? new JsonArray(array.Select(Normalize).ToArray()) : node?.DeepClone();
        string canonical = Normalize(JsonNode.Parse(reading.GetRawText()))!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static JsonElement? ObservationOrigin(JsonElement reading, string topic)
    {
        if (topic is "world" or "sources") return reading.TryGetProperty("origin", out JsonElement origin) ? origin.Clone() : null;
        return reading.TryGetProperty("measurement", out JsonElement measurement) && measurement.ValueKind == JsonValueKind.Object &&
            measurement.TryGetProperty(topic == "route" ? "actualStart" : "bounds", out JsonElement position) ? position.Clone() : null;
    }
}
