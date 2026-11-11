using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

/// <summary>Lossless references to literal values already present in this exact request's history.</summary>
public static class PlanningHistoryWire
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] PlanningFields = { "workingPlan", "acceptedExecutionPlan", "executionPlans",
        "goalEvidence", "lastDecisionReceipt", "interruptedPlan", "latestDesign", "lastCompletedGoal", "completedConstructionSteps" };
    private static readonly HashSet<string> LiveKnowledgeFields = new(StringComparer.Ordinal)
        { "worldKnowledge", "inventory", "bosses", "stations", "constructionInProgress" };

    public static void Compact(JsonObject frame, IReadOnlyList<LlmMessage> history)
    {
        // Non-primary/malformed histories and missing native identities take the full-frame path.
        if (history.Count == 0 || history.Count > 12 || history.Count % 2 != 0 ||
            history.Where((message, index) => message.Role != (index % 2 == 0 ? "user" : "assistant")).Any() ||
            Scope(frame) is not JsonArray scope || frame.ContainsKey("contextReuse")) return;
        var sources = new List<(int turn, JsonObject root)>();
        for (int index = 0; index < history.Count; index += 2)
        {
            string text = history[index].Content;
            if (!text.AsSpan().TrimStart().StartsWith("{")) continue;
            try
            {
                if (JsonNode.Parse(text) is JsonObject previous && JsonNode.DeepEquals(scope, Scope(previous)))
                    sources.Add((index / 2 + 1, previous));
            }
            catch (JsonException) { }
        }
        if (sources.Count == 0) return;

        var referenced = LocalReferenceTargets(frame);
        var changes = new List<(JsonNode owner, string field, JsonNode original)>();
        int saved = 0;
        void Visit(JsonNode owner, string field, string path)
        {
            JsonNode? value = owner is JsonArray list ? list[int.Parse(field, System.Globalization.CultureInfo.InvariantCulture)] : owner[field];
            if (value == null) return;
            // The native observation decoder must still find every local pointer's
            // complete target. References also retain the frame in which they were created.
            bool target = IsReferenceTarget(path, referenced);
            if (target) return;
            int length = value.ToJsonString(Options).Length;
            if (length < 320) return;
            if (!ContainsReference(value))
            {
                foreach (var source in sources)
                {
                    JsonNode? previous = At(source.root, path);
                    if (previous == null || ContainsReference(previous) || !JsonNode.DeepEquals(value, previous)) continue;
                    var reference = new JsonObject { ["$history"] = source.turn, ["path"] = path };
                    int saving = length - reference.ToJsonString(Options).Length;
                    if (saving < 256) break;
                    changes.Add((owner, field, value));
                    Set(owner, field, reference);
                    saved += saving;
                    return;
                }
            }
            // A row may refer only to the same index with every field identical.
            // Changed/appended/reordered rows keep their own identity and data.
            if (value is JsonObject obj)
                foreach (string key in obj.Select(pair => pair.Key).ToArray()) Visit(obj, key, path + "/" + Escape(key));
            else if (value is JsonArray rows)
                for (int index = 0; index < rows.Count; index++)
                {
                    string key = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Visit(rows, key, path + "/" + key);
                }
        }

        JsonObject state = (JsonObject)frame["state"]!;
        JsonObject planning = (JsonObject)state["planning"]!;
        foreach (string field in PlanningFields) Visit(planning, field, "/state/planning/" + field);
        if (planning["recovery"] is JsonObject recovery)
            foreach (string field in new[] { "recentSuccesses", "recentFailures", "observedGatheredItems", "note" })
                Visit(recovery, field, "/state/planning/recovery/" + field);
        if (state["knowledge"] is JsonObject knowledge)
            foreach (string field in knowledge.Select(pair => pair.Key).Where(field => !LiveKnowledgeFields.Contains(field)).ToArray())
                Visit(knowledge, field, "/state/knowledge/" + Escape(field));
        if (changes.Count == 0) return;
        var metadata = new JsonObject { ["encoding"] = "history_value_v1", ["references"] = changes.Count,
            ["savedCharactersBeforeMetadata"] = saved };
        // A small reference set must pay for its own frame metadata as well.
        if (saved <= metadata.ToJsonString(Options).Length + 20)
        {
            foreach (var change in changes) Set(change.owner, change.field, change.original);
            return;
        }
        frame["contextReuse"] = metadata;
    }

    private static JsonArray? Scope(JsonObject frame)
    {
        string[] numbers = { "/state/planning/world", "/state/planning/intent", "/state/planning/evidence_start_sequence", "/state/knowledge/worldKnowledge/worldId" };
        string[] texts = { "/state/knowledge/worldKnowledge/epoch", "/state/planning/recovery/goalInstanceId",
            "/state/planning/objective", "/state/planning/sourcePlayerRequest" };
        var scope = new JsonArray();
        foreach (string path in numbers)
        {
            if (At(frame, path) is not JsonValue value || !value.TryGetValue(out long number)) return null;
            scope.Add(number);
        }
        foreach (string path in texts)
        {
            if (At(frame, path) is not JsonValue value || !value.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text)) return null;
            scope.Add(text);
        }
        return scope;
    }

    private static JsonNode? At(JsonNode node, string pointer)
    {
        JsonNode? current = node;
        foreach (string part in pointer.Split('/').Skip(1))
        {
            string key = part.Replace("~1", "/").Replace("~0", "~");
            if (current is JsonObject obj) current = obj[key];
            else if (current is JsonArray array && int.TryParse(key, out int index) && index >= 0 && index < array.Count) current = array[index];
            else return null;
        }
        return current;
    }

    private static string Escape(string key) => key.Replace("~", "~0").Replace("/", "~1");
    private static void Set(JsonNode owner, string field, JsonNode value)
    {
        if (owner is JsonArray array) array[int.Parse(field, System.Globalization.CultureInfo.InvariantCulture)] = value;
        else owner[field] = value;
    }
    internal static bool IsReferenceTarget(string path, IEnumerable<string> targets) => targets.Any(pointer =>
        pointer == path || pointer.StartsWith(path + "/", StringComparison.Ordinal) || path.StartsWith(pointer + "/", StringComparison.Ordinal));

    internal static HashSet<string> LocalReferenceTargets(JsonObject frame)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        CollectLocalReferences(frame, targets);
        return targets;
    }
    private static bool ContainsReference(JsonNode? node) => node is JsonObject obj
        ? obj.ContainsKey("$ref") || obj.ContainsKey("$history") || obj.Any(pair => ContainsReference(pair.Value))
        : node is JsonArray array && array.Any(ContainsReference);

    private static void CollectLocalReferences(JsonNode? node, HashSet<string> targets)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue value && value.TryGetValue(out string? pointer) && pointer?.StartsWith("#/", StringComparison.Ordinal) == true)
                targets.Add(pointer[1..]);
            foreach (var pair in obj) CollectLocalReferences(pair.Value, targets);
        }
        else if (node is JsonArray array) foreach (var child in array) CollectLocalReferences(child, targets);
    }
}
