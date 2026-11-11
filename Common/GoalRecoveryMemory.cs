using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

public sealed record GoalRecoveryItem(int itemId, long amount);
public sealed record GoalRecoverySource(int x, int y);
public sealed record GoalMiningRecoveryFacts(string target, string selectionMode, GoalRecoverySource? requestedSeed,
    long? actualGained, bool actualYieldsKnown, IReadOnlyList<GoalRecoveryItem> actualYields,
    bool? sourceExhausted, bool? sourceCapped, string? routeFailureCode, string? routeSearchStatus, string? returnStatus);
public sealed record GoalRecoveryOutcome(long sequence, GoalTaskView task, string status, string resultKind,
    string detail, bool detailTruncated, GoalMiningRecoveryFacts? mining);
public sealed record GoalRecoveryView(long goalStartSequence, string goalInstanceId, int? worldId, string worldLoadEpoch,
    long lastOutcomeSequence, IReadOnlyList<GoalRecoveryOutcome> recentSuccesses,
    IReadOnlyList<GoalRecoveryOutcome> recentFailures, long omittedSuccessOutcomes, long omittedFailureOutcomes,
    IReadOnlyList<GoalRecoveryItem> observedGatheredItems, long unretainedYieldEntries, bool yieldTotalsSaturated)
{
    public bool inventoryIsCurrent => false;
    public bool completionProven => false;
    public string note => "Historical native physical outcomes for this goal, not the current queue, current inventory or completion proof. Inspections never create acquisition. observedGatheredItems sums explicitly reported native Mine item yields, including partial blocked attempts; spent/dropped/deposited items are not subtracted. Use live self inventory and the bound completion contract. Success/failure windows are independent and bounded; omissions and unknown quantities are explicit. Exhausted source or failed bounded route does not mean the whole world is unavailable.";
}

/// <summary>Retains short native recovery facts without replaying plans, inferring quantities or changing execution.</summary>
internal sealed class GoalRecoveryMemory
{
    private const int Window = 8, MaximumItemTypes = 64, MaximumResultCharacters = 131072;
    private long start, last, successes, failures, unretained;
    private string instance = "", epoch = "";
    private int? world;
    private bool saturated;
    private readonly List<GoalRecoveryOutcome> succeeded = new(), blocked = new();
    private readonly Dictionary<int, long> gathered = new();

    public GoalRecoveryView View => new(start, instance, world, epoch, last,
        Array.AsReadOnly(succeeded.ToArray()), Array.AsReadOnly(blocked.ToArray()),
        successes - succeeded.Count, failures - blocked.Count,
        Array.AsReadOnly(gathered.OrderBy(p => p.Key).Select(p => new GoalRecoveryItem(p.Key, p.Value)).ToArray()),
        unretained, saturated);

    public void Reset(long boundary = 0, string goalInstance = "")
    {
        start = last = boundary; instance = goalInstance; world = null; epoch = "";
        successes = failures = unretained = 0; saturated = false;
        succeeded.Clear(); blocked.Clear(); gathered.Clear();
    }

    public void BeginWorld(int worldId, string loadEpoch)
    {
        if (world.HasValue && (world.Value != worldId || epoch != loadEpoch)) Reset(start, instance);
        world = worldId; epoch = loadEpoch;
    }

    public void Record(AgentOutcome outcome)
    {
        if (outcome.Sequence <= start || outcome.Sequence <= last) return;
        last = outcome.Sequence;
        if (outcome.Status is not ("succeeded" or "blocked") || outcome.Task.Action is
            AIActionType.None or AIActionType.Inspect or AIActionType.Wait or AIActionType.Follow or AIActionType.Stop) return;

        string kind = "unparsed", detail = outcome.Result;
        GoalMiningRecoveryFacts? mining = null;
        // Equip/use and some movement results are deliberately plain text.
        // They are excerpts, not malformed JSON: avoid first-chance exceptions
        // (and tModLoader warning stacks) for every ordinary completed action.
        if (outcome.Result.Length <= MaximumResultCharacters &&
            outcome.Result.AsSpan().TrimStart().StartsWith("{"))
        {
            try
            {
                using var document = JsonDocument.Parse(outcome.Result);
                JsonElement root = document.RootElement;
                if (UniqueObject(root))
                {
                    kind = Text(root, "kind") ?? "unrecognized_object";
                    detail = Text(root, "reason") ?? outcome.Result;
                    mining = ReadMining(root, outcome);
                }
            }
            catch (JsonException) { } // Unknown/non-JSON native messages remain excerpts, never item evidence.
        }
        var entry = new GoalRecoveryOutcome(outcome.Sequence, Task(outcome.Task), outcome.Status,
            Cut(kind, 80), Cut(detail, 180), detail.Length > 180, mining);
        List<GoalRecoveryOutcome> window;
        if (outcome.Status == "blocked") { failures++; window = blocked; }
        else { successes++; window = succeeded; }
        window.Add(entry); if (window.Count > Window) window.RemoveAt(0);
        if (mining is not { actualYieldsKnown: true }) return;
        foreach (GoalRecoveryItem item in mining.actualYields)
        {
            if (item.amount == 0) continue;
            if (!gathered.TryGetValue(item.itemId, out long previous) && gathered.Count >= MaximumItemTypes)
            { unretained++; continue; }
            if (previous > long.MaxValue - item.amount) { gathered[item.itemId] = long.MaxValue; saturated = true; }
            else gathered[item.itemId] = previous + item.amount;
        }
    }

    private static GoalMiningRecoveryFacts? ReadMining(JsonElement root, AgentOutcome outcome)
    {
        if (outcome.Task.Action != AIActionType.Mine || Text(root, "kind") is not ("mining_result" or "mining_blocked") ||
            !string.Equals(Text(root, "target"), outcome.Task.Target, StringComparison.OrdinalIgnoreCase) ||
            Bool(root, "success") != (outcome.Status == "succeeded")) return null;

        GoalRecoverySource? source = null;
        if (root.TryGetProperty("requestedSeed", out JsonElement seed) && UniqueObject(seed) &&
            Integer(seed, "x") is >= 0 and <= int.MaxValue and var x &&
            Integer(seed, "y") is >= 0 and <= int.MaxValue and var y)
            source = new((int)x, (int)y);
        long? gained = Integer(root, "actualGained");
        if (gained < 0) gained = null;
        bool known = ReadYields(root, out GoalRecoveryItem[] yields);
        if (known && gained.HasValue && yields.Sum(y => y.amount) != gained.Value)
        { known = false; yields = Array.Empty<GoalRecoveryItem>(); }
        return new(outcome.Task.Target, Cut(Text(root, "selectionMode") ?? "unknown", 80), source,
            gained, known, Array.AsReadOnly(yields), Bool(root, "sourceExhausted"), Bool(root, "sourceCapped"),
            Nested(root, "routeFailure", "code"), Nested(root, "routeFailure", "searchStatus"), Nested(root, "returnSafety", "returnStatus"));
    }

    private static bool ReadYields(JsonElement root, out GoalRecoveryItem[] yields)
    {
        yields = Array.Empty<GoalRecoveryItem>();
        var values = new Dictionary<int, long>();
        bool Add(JsonElement value)
        {
            if (!UniqueObject(value) || Integer(value, "itemType") is not (> 0 and <= int.MaxValue) ||
                Integer(value, "amount") is not (>= 0 and <= int.MaxValue)) return false;
            int item = (int)Integer(value, "itemType")!.Value;
            long amount = Integer(value, "amount")!.Value;
            values[item] = values.GetValueOrDefault(item) + amount;
            return true;
        }
        if (root.TryGetProperty("actualYields", out JsonElement array))
        {
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumItemTypes) return false;
            foreach (JsonElement value in array.EnumerateArray()) if (!Add(value)) return false;
        }
        else if (!root.TryGetProperty("actualYield", out JsonElement single) || !Add(single)) return false;
        yields = values.OrderBy(p => p.Key).Select(p => new GoalRecoveryItem(p.Key, p.Value)).ToArray();
        return true;
    }

    private static bool UniqueObject(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();
    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Integer(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;
    private static bool? Bool(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static string? Nested(JsonElement root, string section, string name) => root.TryGetProperty(section, out JsonElement value) &&
        UniqueObject(value) && Text(value, name) is string text ? Cut(text, 100) : null;
    private static string Cut(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private static GoalTaskView Task(AIPlan task) => new(PlanTaskIdentity.Fingerprint(task), task.Action.ToString().ToLowerInvariant(),
        Cut(task.Target, 100), Cut(task.Item, 100), Cut(task.Slot, 40), task.RecipeId, task.Count, Cut(task.Blueprint, 100),
        task.X, task.Y, task.Width, task.Height, "", "", Cut(task.PlanStep, 80), task.Design.HasValue);
}
