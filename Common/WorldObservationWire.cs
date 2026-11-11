using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

/// <summary>Wire-only projection of already acknowledged, currently retrievable native resource pages.</summary>
public static class WorldObservationWire
{
    public const string View = "acknowledged_resource_summary_v1";
    public sealed record Replacement(JsonNode Original, JsonNode Summary);
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<Replacement> Compact(JsonObject state)
    {
        if (state["planning"] is not JsonObject planning || planning["outcomes"] is not JsonArray outcomes ||
            planning["worldObservations"] is not JsonArray directory || state["knowledge"]?["worldKnowledge"] is not JsonObject current ||
            Number(planning["acknowledgedOutcomeSequence"]) is not long acknowledged ||
            Number(planning["evidence_start_sequence"]) is not long goalStart ||
            Number(current["worldId"]) is not long worldId || Text(current["epoch"]) is not string epoch) return Array.Empty<Replacement>();

        var changed = new Replacement?[outcomes.Count];
        var unchanged = new List<JsonNode>();
        var newestSourceSequences = new Dictionary<string, long>(StringComparer.Ordinal);
        // Reading an old archive ID creates a new outcome. Keep the newest
        // native scan per resource by its original observation sequence.
        foreach (int index in Enumerable.Range(0, outcomes.Count).OrderByDescending(i => ObservationSequence(outcomes[i])))
        {
            if (outcomes[index] is not JsonObject outcome) continue;
            if (outcome["result"] is not JsonNode original) continue;
            JsonNode? projected = null;
            if (Number(outcome["sequence"]) is long sequence && sequence > goalStart && sequence <= acknowledged &&
                outcome["task"] is JsonObject task && Is(task["type"], "inspect") && original is JsonObject body)
            {
                if (Text(task["target"]) is "world" or "terrain" or "site" or "route" or "sources" && Text(outcome["observationId"]) is string id &&
                    FindEntry(directory, id) is JsonObject entry &&
                    Is(task["target"], EntryTopic(entry) ?? "") &&
                    Matches(entry, body, sequence, goalStart, worldId, epoch) && QueryMatches(entry["query"], task) &&
                    !HasDetailReference(state, index))
                    projected = ProjectObservation(body, id, sequence, newestSourceSequences);
                else if (Is(task["target"], "observation") && Is(body["topic"], "observation") &&
                    True(body["available"]) && True(body["historical"]) && False(body["currentWorldRechecked"]) &&
                    Text(body["id"]) is string retrievedId && Text(task["item"]) == retrievedId &&
                    FindEntry(directory, retrievedId) is JsonObject archived &&
                    Number(body["originalSequence"]) is long originalSequence && originalSequence <= acknowledged &&
                    body["observation"] is JsonObject full &&
                    (body.ContainsKey("originalTopic") ? Is(body["originalTopic"], EntryTopic(archived) ?? "") : EntryTopic(archived) == "world") &&
                    Matches(archived, full, originalSequence, goalStart, worldId, epoch) &&
                    Same(body, archived, "worldId", "worldLoadEpoch", "goalStartSequence", "observedAtTick", "query", "origin") &&
                    !HasDetailReference(state, index) && ProjectObservation(full, retrievedId, originalSequence, newestSourceSequences) is JsonObject summary)
                {
                    var copy = body.DeepClone().AsObject();
                    copy["observation"] = summary;
                    projected = copy;
                }
            }
            if (projected == null) unchanged.Add(original);
            else
            {
                changed[index] = new Replacement(original.DeepClone(), projected.DeepClone());
                outcome["result"] = projected;
            }
        }
        ShareAcknowledgedSpatialReadbacks(outcomes, changed);
        // A new unread result can be byte-for-byte equal to an older page. Never let
        // its feedback/lastStepResult inherit that older page's acknowledgement.
        return changed.OfType<Replacement>().Where(pair => !unchanged.Any(full => JsonNode.DeepEquals(pair.Original, full))).ToArray();
    }

    private static void ShareAcknowledgedSpatialReadbacks(JsonArray outcomes, Replacement?[] changed)
    {
        for (int index = 0; index < changed.Length; index++)
        {
            if (changed[index] is not { } readback || readback.Summary is not JsonObject summary ||
                !Is(summary["topic"], "observation") || Text(summary["id"]) is not string id ||
                Number(summary["originalSequence"]) is not long sequence ||
                summary["observation"] is not JsonObject body || !Is(body["wireView"], SpatialObservationWire.View)) continue;

            // Both bodies already passed the same archive/epoch/ACK checks. Use
            // a direct outcome only if the original bodies and projections match.
            for (int target = 0; target < changed.Length; target++)
            {
                if (changed[target] is not { } direct || outcomes[target] is not JsonObject outcome ||
                    !Is(outcome["observationId"], id) || Number(outcome["sequence"]) != sequence ||
                    !JsonNode.DeepEquals(direct.Original, readback.Original["observation"]) ||
                    !JsonNode.DeepEquals(direct.Summary, body)) continue;
                // Keep the historical body and its coverage directly readable.
                // Only repeatable explanatory fields and the movement contract share data.
                string path = $"#/state/planning/outcomes/{target}/result/";
                foreach (string field in new[] { "movementLimits", "note", "actorFootprints" })
                    ShareSpatialField(body, field, path);
                if (body["measurement"] is JsonObject measurement)
                    ShareSpatialField(measurement, "note", path + "measurement/");
                outcomes[index]!["result"] = summary.DeepClone();
                break;
            }
        }
    }

    private static void ShareSpatialField(JsonObject owner, string field, string path)
    {
        if (owner[field] is not JsonNode value) return;
        var reference = new JsonObject
        {
            ["$ref"] = path + field,
            ["note"] = "Exactly equal data in this request; each observation keeps its own historical identity and timestamp."
        };
        if (reference.ToJsonString(Options).Length < value.ToJsonString(Options).Length) owner[field] = reference;
    }

    private static JsonObject? FindEntry(JsonArray directory, string id)
    {
        JsonObject[] matches = directory.OfType<JsonObject>().Where(entry => Text(entry["id"]) == id).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool Matches(JsonObject entry, JsonObject body, long sequence, long goalStart, long worldId, string epoch)
    {
        string? topic = EntryTopic(entry);
        if (topic is not ("world" or "terrain" or "site" or "route" or "sources") || !Is(body["topic"], topic)) return false;
        if (topic == "sources")
            return True(entry["available"]) && sequence > goalStart && Number(entry["originalSequence"]) == sequence &&
                Number(entry["goalStartSequence"]) == goalStart && Number(entry["worldId"]) == worldId && Text(entry["worldLoadEpoch"]) == epoch &&
                Number(entry["observedAtTick"]) is long completed && completed >= 0 && worldId is >= int.MinValue and <= int.MaxValue &&
                entry["origin"] is JsonObject && body["origin"] is JsonObject && JsonNode.DeepEquals(entry["origin"], body["origin"]) &&
                entry["query"] is JsonObject && SourceObservationWire.IsCompletePage(JsonSerializer.SerializeToElement(body, Options), (int)worldId, epoch, (ulong)completed);
        JsonObject? stamp = topic == "world" ? body["world"] as JsonObject : body;
        JsonNode? origin = topic == "world" ? body["origin"] : SpatialObservationWire.Origin(body, topic);
        return True(entry["available"]) && sequence > goalStart && Number(entry["originalSequence"]) == sequence &&
            Number(entry["goalStartSequence"]) == goalStart && Number(entry["worldId"]) == worldId &&
            Text(entry["worldLoadEpoch"]) == epoch && stamp != null && Number(stamp["worldId"]) == worldId &&
            (topic != "world" && !stamp.ContainsKey("epoch") || Text(stamp["epoch"]) == epoch) &&
            (!body.ContainsKey("currentWorldId") || Number(body["currentWorldId"]) == worldId) &&
            (!body.ContainsKey("currentEpoch") || Text(body["currentEpoch"]) == epoch) &&
            Number(entry["observedAtTick"]) is long tick && Number(stamp["observedAtTick"]) == tick &&
            entry["origin"] is JsonObject && origin is JsonObject && JsonNode.DeepEquals(entry["origin"], origin) && entry["query"] is JsonObject;
    }

    private static string? EntryTopic(JsonObject entry) => entry.ContainsKey("topic") ? Text(entry["topic"]) : "world";

    private static long ObservationSequence(JsonNode? node)
    {
        if (node is not JsonObject outcome) return long.MinValue;
        if (outcome["result"] is JsonObject readback && Is(readback["topic"], "observation") &&
            readback["observation"] is JsonObject body && Is(body["topic"], "sources") &&
            Number(readback["originalSequence"]) is long originalSequence) return originalSequence;
        return Number(outcome["sequence"]) ?? long.MinValue;
    }

    private static JsonObject? ProjectObservation(JsonObject body, string id, long sequence, Dictionary<string, long> newestSourceSequences)
    {
        if (Is(body["topic"], "world")) return Project(body, id);
        if (!Is(body["topic"], "sources")) return SpatialObservationWire.Project(body, id);
        bool olderPage = false;
        if (Text(body["resource"]) is { Length: > 0 } resource)
        {
            newestSourceSequences.TryAdd(resource, sequence);
            olderPage = sequence < newestSourceSequences[resource];
        }
        return SourceObservationWire.Project(body, id, olderPage);
    }

    private static bool HasDetailReference(JsonNode node, int outcomeIndex)
    {
        string prefix = $"#/state/planning/outcomes/{outcomeIndex}/result/";
        bool Find(JsonNode? current)
        {
            if (current is JsonObject obj)
                return Text(obj["$ref"])?.StartsWith(prefix, StringComparison.Ordinal) == true || obj.Any(property => Find(property.Value));
            return current is JsonArray array && array.Any(Find);
        }
        return Find(node);
    }

    private static bool QueryMatches(JsonNode? query, JsonObject task)
        => query is JsonObject original && Text(original["item"]) == Text(task["item"]) &&
            JsonNode.DeepEquals(original["x"], task["x"]) && JsonNode.DeepEquals(original["y"], task["y"]);

    private static bool Same(JsonObject first, JsonObject second, params string[] fields)
        => fields.All(field => first.ContainsKey(field) && second.ContainsKey(field) && JsonNode.DeepEquals(first[field], second[field]));

    private static JsonObject? Project(JsonObject original, string id)
    {
        if (!Is(original["topic"], "world") || Text(original["source"]) != "omniscient_native_world" ||
            !True(original["available"]) || !True(original["inputValid"]) ||
            original["query"] is not JsonObject query ||
            !(Is(query["Kind"], "resource") && !query.ContainsKey("kind") || Is(query["kind"], "resource") && !query.ContainsKey("Kind")) ||
            original["data"] is not JsonObject data || data["entries"] is not JsonArray entries ||
            entries.Any(node => node is not JsonObject entry || entry["samples"] is not JsonArray samples || samples.Any(sample => sample is not JsonObject)) ||
            new[] { "wireView", "detailOmittedFromWire", "omittedFields", "summaryDefaults", "summaryDecoding", "retrieval" }.Any(original.ContainsKey)) return null;

        var result = original.DeepClone().AsObject();
        var omitted = new SortedSet<string>(StringComparer.Ordinal);
        void Remove(JsonObject? owner, string field, string path)
        {
            if (owner != null && owner.Remove(field)) omitted.Add(path + "." + field);
        }
        // Preserve unfamiliar scan metadata instead of deleting the entire object.
        if (result["world"]?["scan"] is JsonObject scan)
        {
            foreach (string field in new[] { "lastTileReads", "maximumTileReadsPerTick", "totalTileReads", "lastMilliseconds", "maximumMilliseconds", "targetMillisecondsPerTick", "timeLimit" })
                Remove(scan, field, "world.scan");
        }
        Remove(result["data"]?["miningAccessScope"] as JsonObject, "elapsedMilliseconds", "data.miningAccessScope");
        var entryRows = result["data"]!["entries"]!.AsArray().OfType<JsonObject>().ToArray();
        var sampleRows = entryRows.SelectMany(entry => entry["samples"]!.AsArray().OfType<JsonObject>()).ToArray();
        foreach (JsonObject sample in sampleRows)
        {
            Remove(sample["treeProduct"] as JsonObject, "nativeReadMilliseconds", "data.entries[].samples[].treeProduct");
            foreach (string field in new[] { "elapsedMilliseconds", "standSamples", "standSamplesOmitted" })
                Remove(sample["miningAccess"] as JsonObject, field, "data.entries[].samples[].miningAccess");
        }

        var defaults = new JsonObject();
        Factor(defaults, "entry", entryRows, new[] { "kind", "tileType", "tileName", "countMeaning", "sampleMeaning", "distanceMeaning", "scanStartedAtTick", "scanCompletedAtTick", "sampleVerifiedAtTick" });
        Factor(defaults, "sample", sampleRows, new[] { "standingEvidence" });
        Factor(defaults, "treeProduct", sampleRows.Select(sample => sample["treeProduct"]).ToArray(), new[] {
            "status", "reason", "quantity", "itemId", "itemName", "nativeTreeType", "sampleProtected", "sampleNativeCanKill", "baseProtected", "baseNativeCanKill", "observedAtTick" });
        Factor(defaults, "miningAccess", sampleRows.Select(sample => sample["miningAccess"]).ToArray(), new[] {
            "geometryStatus", "geometryFinding", "GridChecks", "enumerationComplete", "actualObserver", "actualWithinToolRangeByDistance", "maximumToolRangePixels",
            "structureProtected", "nativeCanKill", "actuated", "route", "tool", "returnToOrigin", "cascade", "observedAtTick" });
        result["wireView"] = View;
        result["detailOmittedFromWire"] = true;
        result["omittedFields"] = new JsonArray(omitted.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
        result["summaryDefaults"] = defaults;
        result["summaryDecoding"] = "Merge summaryDefaults.entry/sample/treeProduct/miningAccess into the corresponding named objects only where that object exists; explicit properties override defaults. Each default field existed and was identical in every source object. Missing/null and unknown fields are retained. Omitted detail is historical, retrievable by ID; this is not a new world check or route/tool/cascade approval.";
        result["retrieval"] = new JsonObject { ["type"] = "inspect", ["target"] = "observation", ["item"] = id,
            ["historical"] = true, ["currentWorldRechecked"] = false };
        return result.ToJsonString(Options).Length < original.ToJsonString(Options).Length ? result : null;
    }

    private static void Factor(JsonObject defaults, string group, IReadOnlyList<JsonNode?> candidates, IReadOnlyList<string> fields)
    {
        if (candidates.Count < 2 || candidates.Any(candidate => candidate is not JsonObject)) return;
        var rows = candidates.Cast<JsonObject>().ToArray();
        var common = new JsonObject();
        foreach (string field in fields)
        {
            if (!rows[0].ContainsKey(field) || !rows.All(row => row.ContainsKey(field) && JsonNode.DeepEquals(rows[0][field], row[field]))) continue;
            common[field] = rows[0][field]?.DeepClone();
            foreach (JsonObject row in rows) row.Remove(field);
        }
        if (common.Count > 0) defaults[group] = common;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text) ? text : null;
    private static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out long number) ? number : null;
    private static bool Is(JsonNode? node, string text) => string.Equals(Text(node), text, StringComparison.OrdinalIgnoreCase);
    private static bool True(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool flag) && flag;
    private static bool False(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool flag) && !flag;
}
