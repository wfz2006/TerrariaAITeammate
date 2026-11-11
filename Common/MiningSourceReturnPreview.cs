using System;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

/// <summary>Independent read-only return-to-observer preview; never modifies forward admission or a live task.</summary>
public static class MiningSourceReturnPreview
{
    public const int MaximumCombinedRouteProbes = 16;
    public const int MaximumReverseRouteProbes = 1;

    public static JsonObject Observe(JsonObject forwardPreflight, JsonObject observerStart,
        Func<JsonObject?, JsonObject> inspectReverse)
    {
        ArgumentNullException.ThrowIfNull(forwardPreflight);
        ArgumentNullException.ThrowIfNull(observerStart);
        ArgumentNullException.ThrowIfNull(inspectReverse);
        var approach = forwardPreflight["approach"] as JsonObject;
        bool knownForwardCount = Integer(approach?["routeProbeCount"], out int forwardCount) &&
            forwardCount >= 0 && forwardCount <= MaximumCombinedRouteProbes;
        var budget = new JsonObject
        {
            ["maximumCombinedRouteProbes"] = MaximumCombinedRouteProbes,
            ["maximumReverseRouteProbes"] = MaximumReverseRouteProbes,
            ["forwardRouteProbeCount"] = knownForwardCount ? JsonValue.Create(forwardCount) : null,
            ["reverseRouteProbeCount"] = 0,
            ["totalRouteProbeCount"] = knownForwardCount ? JsonValue.Create(forwardCount) : null,
            ["reverseInvocationCount"] = 0
        };
        var result = new JsonObject
        {
            ["status"] = "not_checked", ["returnProvenInCurrentBoundedGraph"] = false,
            ["returnability"] = "unknown",
            ["observerStart"] = observerStart.DeepClone(), ["launchStand"] = null,
            ["observerStartMeaning"] = "current_readonly_observer_start_not_future_task_anchor_or_player",
            ["prospective"] = true, ["beforeExcavation"] = true, ["travelExecuted"] = false,
            ["returnToPlayerChecked"] = false, ["forwardNativePreflightUnchanged"] = true,
            ["budget"] = budget, ["nativeResult"] = null,
            ["scope"] = "Independent prospective return to this read-only observation origin in the current bounded graph only. It does not change source selection, forward admission, tools, protection or tasks, execute travel/mining, check return to player, or prove safety after excavation or future world changes. Unknown/exhausted is neither safe nor globally unreachable."
        };
        JsonObject Unknown(string status)
        { result["status"] = status; return result; }
        if (!Flag(forwardPreflight["available"], out bool available) || !available ||
            !Flag(forwardPreflight["inputValid"], out bool inputValid) || !inputValid ||
            !Flag(forwardPreflight["canAttempt"], out bool canAttempt) || !canAttempt)
            return Unknown("not_checked_forward_not_eligible");
        if (!Integer(forwardPreflight["worldId"], out int forwardWorld) ||
            !Integer(observerStart["worldId"], out int observerWorld) || forwardWorld != observerWorld ||
            Text(observerStart["epoch"]) is not { Length: > 0 } epoch ||
            string.IsNullOrWhiteSpace(epoch) ||
            !OptionalWorldMatches(forwardPreflight, observerWorld, epoch))
            return Unknown("not_checked_world_identity_unknown_or_mismatch");
        if (!Tick(observerStart["capturedAtTick"], out ulong capturedTick) ||
            !Tick(forwardPreflight["observedAtTick"], out ulong forwardTick) || forwardTick < capturedTick ||
            !Finite(observerStart["centerTileX"]) || !Finite(observerStart["centerTileY"]) || !Finite(observerStart["feetTileY"]) ||
            !Flag(observerStart["grounded"], out bool originGrounded) ||
            !observerStart.ContainsKey("standX") || !observerStart.ContainsKey("standRow"))
            return Unknown("not_checked_observer_origin_unknown");
        bool originStandKnown = Integer(observerStart["standX"], out int originX) && originX >= 0 &&
            Integer(observerStart["standRow"], out int originRow) && originRow >= 0;
        if (!originStandKnown && (observerStart["standX"] != null || observerStart["standRow"] != null))
            return Unknown("not_checked_observer_origin_unknown");
        if (!knownForwardCount) return Unknown("not_checked_forward_probe_count_unknown_or_invalid");
        if (forwardCount == MaximumCombinedRouteProbes) return Unknown("not_checked_route_budget_exhausted");
        if (!Flag(forwardPreflight["actualWithinToolRange"], out bool actualPose))
            return Unknown("not_checked_launch_unknown");
        JsonObject? launch = null;
        if (!actualPose)
        {
            if (approach?["selectedStand"] is not JsonObject selected ||
                !Integer(selected["x"], out int standX) || standX < 0 ||
                !Integer(selected["standRow"], out int standRow) || standRow < 0)
                return Unknown("not_checked_selected_stand_unknown");
            launch = selected.DeepClone().AsObject();
            result["launchStand"] = launch.DeepClone();
        }
        result["fromActualObserverPose"] = actualPose;
        budget["reverseInvocationCount"] = 1;
        budget["reverseRouteProbeCount"] = null;
        budget["totalRouteProbeCount"] = null;
        JsonObject? native;
        try { native = inspectReverse(launch?.DeepClone().AsObject())?.DeepClone().AsObject(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            result["diagnostic"] = "Reverse preview delegate failed: " + error.GetType().Name;
            return Unknown("unknown_reverse_delegate_error");
        }
        result["nativeResult"] = native;
        if (native == null) return Unknown("unknown_malformed_native_result");
        bool knownReverseCount = Integer(native["routeProbeCount"], out int reverseCount) &&
            reverseCount >= 0 && reverseCount <= MaximumReverseRouteProbes;
        if (knownReverseCount)
        {
            budget["reverseRouteProbeCount"] = reverseCount;
            budget["totalRouteProbeCount"] = forwardCount + reverseCount;
        }
        string? nativeStatus = Text(native["status"]);
        if (string.IsNullOrWhiteSpace(nativeStatus) || !knownReverseCount ||
            !Flag(native["returnProvenInCurrentBoundedGraph"], out bool nativeProven) ||
            !Tick(native["observedAtTick"], out ulong checkedTick) || checkedTick < forwardTick ||
            Text(native["diagnostic"]) == null || !OptionalWorldMatches(native, observerWorld, epoch))
            return Unknown("unknown_malformed_native_result");
        bool positiveContract = originGrounded &&
            (nativeStatus == "found" && reverseCount == 1 && originStandKnown ||
                nativeStatus == "already_at_task_origin" && actualPose && reverseCount == 0);
        if (nativeProven ? !positiveContract : nativeStatus is "found" or "already_at_task_origin")
            return Unknown("unknown_inconsistent_native_result");
        result["status"] = nativeStatus;
        result["returnProvenInCurrentBoundedGraph"] = nativeProven;
        if (nativeProven) result["returnability"] = "proven_in_current_bounded_graph_before_excavation_only";
        return result;
    }

    private static bool OptionalWorldMatches(JsonObject value, int worldId, string epoch)
        => (!value.ContainsKey("worldId") || Integer(value["worldId"], out int actualWorld) && actualWorld == worldId) &&
           (!value.ContainsKey("epoch") || Text(value["epoch"]) == epoch);
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    private static bool Flag(JsonNode? node, out bool flag)
    { flag = false; return node is JsonValue value && value.TryGetValue(out flag); }
    private static bool Integer(JsonNode? node, out int number)
    { number = 0; return node is JsonValue value && value.TryGetValue(out number); }
    private static bool Tick(JsonNode? node, out ulong tick)
    {
        tick = 0;
        if (node is not JsonValue value) return false;
        if (value.TryGetValue(out tick)) return true;
        if (value.TryGetValue(out long wide) && wide >= 0) { tick = (ulong)wide; return true; }
        if (value.TryGetValue(out int narrow) && narrow >= 0) { tick = (ulong)narrow; return true; }
        return false;
    }
    private static bool Finite(JsonNode? node)
        => node is JsonValue value && (value.TryGetValue(out double number) && double.IsFinite(number) ||
            value.TryGetValue(out float single) && float.IsFinite(single) ||
            value.TryGetValue(out int _) || value.TryGetValue(out long _) || value.TryGetValue(out ulong _) || value.TryGetValue(out decimal _));
}
