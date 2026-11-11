using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerrariaAITeammate.Common;

/// <summary>Keep exact prior turns cacheable, and send each fresh observation only once.</summary>
public static class PlanningRequest
{
    internal const string GoalReviewMode = "执行后复盘";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static List<LlmMessage> Build(string system, string mode, string input, string stateJson,
        IReadOnlyList<LlmMessage> history, string? feedback = null, bool usePhysicsStructureView = false, bool useStructureOverview = false)
    {
        var designResults = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var worldResults = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        JsonObject state = CompactState(stateJson, designResults, worldResults, usePhysicsStructureView, useStructureOverview);
        var frame = new JsonObject
        {
            ["goal"] = state["planning"]?["objective"]?.DeepClone() ?? state["goal"]?.DeepClone(),
            ["mode"] = mode,
            ["instruction"] = input,
            ["state"] = state
        };
        // Keep the player's full requirements beside the model's shorter objective;
        // changing plans or rebasing history must not make a shortcut look authorized.
        if (state["planning"]?["sourcePlayerRequest"] is JsonValue originalRequest &&
            originalRequest.TryGetValue(out string? requestText) && !string.IsNullOrWhiteSpace(requestText))
            frame["playerRequest"] = requestText;
        if (mode == GoalReviewMode && frame.ContainsKey("playerRequest") &&
            state["planning"]?["completion_evidence"] is JsonObject evidence &&
            evidence["verified"] is JsonValue verified && verified.TryGetValue(out bool allMet) && allMet &&
            evidence["conditions"] is JsonArray conditions && conditions.OfType<JsonObject>().Any(condition =>
                condition["type"]?.GetValue<string>() == "boss_loot" && condition["status"]?.GetValue<string>() == "met"))
            frame["instruction"] = "本轮已绑定条件全部满足。带本次Boss来源的红心也计入实际拾取。按playerRequest核对；只有玩家明确要求收齐、指定物品或数量且尚有缺额时才继续拾取。全部原要求满足时用goal_done=true、tasks=[]收尾，只汇报actualItems确认过的掉落。";
        JsonNode? latest = (state["planning"]?["outcomes"] as JsonArray)?.LastOrDefault()?["result"];
        if (mode == GoalReviewMode && latest is JsonObject sourceResult &&
            (sourceResult["nextSourceSearch"] ?? sourceResult["widerSearchTask"]) is JsonObject sourceRecovery)
        {
            frame["sourceRecoveryObservation"] = sourceRecovery.DeepClone();
            frame["instruction"] = frame["instruction"]!.GetValue<string>() +
                " 若此来源缺额仍未解决，可复制sourceRecoveryObservation换范围；已经有可执行的独立步骤时也可先做。";
        }
        if (mode == GoalReviewMode && latest is JsonObject navigationResult && navigationResult["platformRecovery"] is JsonObject platformRecovery)
        {
            frame["platformRecoveryObservation"] = platformRecovery.DeepClone();
            frame["instruction"] = frame["instruction"]!.GetValue<string>() +
                " 步行已受阻。需要临时平台时可选择platformRecoveryObservation的现场预检，先核对用料并实际逐级走。";
        }
        if (!string.IsNullOrEmpty(feedback))
        {
            JsonNode? structuredFeedback = Structured(feedback);
            structuredFeedback = SharedDesignResult(structuredFeedback, worldResults);
            PackObservationWire(structuredFeedback, usePhysicsStructureView, useStructureOverview);
            DeduplicateStructureGrid(structuredFeedback, state);
            ShareTerrainSections(structuredFeedback, state);
            structuredFeedback = SharedDesignResult(structuredFeedback, designResults);
            structuredFeedback = SharedDesignResult(structuredFeedback, worldResults);
            if (!JsonNode.DeepEquals(latest, structuredFeedback)) frame["feedback"] = structuredFeedback;
        }
        SharePlanningTaskParameters(frame, state);
        PlanningStaticKnowledge.Lift(frame, system);
        PlanningHistoryWire.Compact(frame, history);
        var messages = new List<LlmMessage> { new("system", system) };
        messages.AddRange(history);
        messages.Add(new LlmMessage("user", Normalize(frame)!.ToJsonString(JsonOptions)));
        return messages;
    }

    public static JsonObject CompactState(string stateJson, bool usePhysicsStructureView = false, bool useStructureOverview = false) => CompactState(stateJson,
        new Dictionary<string, JsonNode>(StringComparer.Ordinal), new Dictionary<string, JsonNode>(StringComparer.Ordinal), usePhysicsStructureView, useStructureOverview);

    private static JsonObject CompactState(string stateJson, Dictionary<string, JsonNode> designResults, Dictionary<string, JsonNode> worldResults, bool usePhysicsStructureView, bool useStructureOverview)
    {
        JsonObject state = JsonNode.Parse(stateJson) as JsonObject ?? throw new JsonException("Game state must be a JSON object.");
        JsonNode? knowledge = state["knowledge"], companion = state["companion"], planning = state["planning"];
        PackObservationWire(knowledge?["selectedStructure"], usePhysicsStructureView, useStructureOverview);
        DeduplicateStructureGrid(knowledge?["selectedStructure"], state);
        bool covered = InventoryCovered(state["inventory"] as JsonArray, knowledge?["inventory"] as JsonArray);
        if (covered)
        {
            state.Remove("inventory");
            (companion as JsonObject)?.Remove("inventory");
        }
        if (state.ContainsKey("currentTask") && companion is JsonObject c && c.ContainsKey("task") &&
            JsonNode.DeepEquals(state["currentTask"], c["task"])) state.Remove("currentTask");
        if (state.ContainsKey("goal") && planning is JsonObject p && p.ContainsKey("objective") &&
            JsonNode.DeepEquals(state["goal"], p["objective"])) state.Remove("goal");

        if (planning?["outcomes"] is JsonArray outcomes)
        {
            foreach (JsonObject outcome in outcomes.OfType<JsonObject>())
            {
                if (outcome["result"] is JsonValue value && value.TryGetValue(out string? result))
                    outcome["result"] = Structured(result ?? "");
            }
            ShareBlockedResult(state, outcomes);
            foreach (WorldObservationWire.Replacement replacement in WorldObservationWire.Compact(state))
                worldResults[ResultKey(replacement.Original)] = replacement.Summary;
            foreach (SourcePreflightWire.Replacement replacement in SourcePreflightWire.Compact(state))
                worldResults[ResultKey(replacement.Original)] = replacement.Shared;
            foreach (JsonObject outcome in outcomes.OfType<JsonObject>())
            {
                PackObservationWire(outcome["result"], usePhysicsStructureView, useStructureOverview);
                DeduplicateStructureGrid(outcome["result"], state);
                ShareTerrainSections(outcome["result"], state);
            }
            CompactDesignResults(state, designResults);
        }
        else CompactDesignResults(state, designResults);
        if ((planning?["outcomes"] is JsonArray || usePhysicsStructureView || useStructureOverview) &&
            state.ContainsKey("lastStepResult"))
        {
            JsonNode? structuredLast = state["lastStepResult"] is JsonValue last && last.TryGetValue(out string? text)
                ? Structured(text ?? "") : state["lastStepResult"]?.DeepClone();
            structuredLast = SharedDesignResult(structuredLast, worldResults);
            PackObservationWire(structuredLast, usePhysicsStructureView, useStructureOverview);
            DeduplicateStructureGrid(structuredLast, state);
            ShareTerrainSections(structuredLast, state);
            structuredLast = SharedDesignResult(structuredLast, designResults);
            structuredLast = SharedDesignResult(structuredLast, worldResults);
            JsonNode? latest = (planning?["outcomes"] as JsonArray)?.LastOrDefault()?["result"];
            if (JsonNode.DeepEquals(structuredLast, latest)) state.Remove("lastStepResult");
            else state["lastStepResult"] = structuredLast;
        }
        if (state["facts"] is JsonValue facts && facts.TryGetValue(out string? lines) && lines != null)
            state["facts"] = string.Join("\n", lines.Split('\n').Distinct(StringComparer.Ordinal));
        return state;
    }

    private static void ShareBlockedResult(JsonObject state, JsonArray outcomes)
    {
        if (state["blockedTask"] is not JsonObject blocked) return;
        foreach (string field in new[] { "result", "reason" })
        {
            JsonNode? value = blocked[field] is JsonValue raw && raw.TryGetValue(out string? text)
                ? Structured(text ?? "") : blocked[field];
            if (value is not (JsonObject or JsonArray) || value is JsonObject reference && reference.ContainsKey("$ref")) continue;
            for (int index = outcomes.Count - 1; index >= 0; index--)
                if (JsonNode.DeepEquals(value, outcomes[index]?["result"]))
                {
                    blocked[field] = new JsonObject { ["$ref"] = $"#/state/planning/outcomes/{index}/result",
                        ["note"] = "Exactly equal result in this request; task metadata remains separate." };
                    break;
                }
        }
    }

    private static void SharePlanningTaskParameters(JsonObject frame, JsonObject state)
    {
        if (state["planning"] is not JsonObject planning) return;
        var targets = PlanningHistoryWire.LocalReferenceTargets(frame);
        // A shared task describes identical proposed parameters only. Each record
        // retains its own sequence, status, quantities actually gained and result.
        var canonical = new List<(JsonObject task, string path)>();
        var groups = new (JsonNode? rows, string path)[]
        {
            (planning["acceptedExecutionPlan"]?["tasks"], "acceptedExecutionPlan/tasks"),
            (planning["goalEvidence"]?["recentPhysicalOutcomes"], "goalEvidence/recentPhysicalOutcomes"),
            (planning["recovery"]?["recentSuccesses"], "recovery/recentSuccesses"),
            (planning["recovery"]?["recentFailures"], "recovery/recentFailures")
        };
        foreach (var group in groups)
        {
            if (group.rows is not JsonArray rows) continue;
            for (int index = 0; index < rows.Count; index++)
            {
                if (rows[index] is not JsonObject row || row["task"] is not JsonObject task ||
                    task["identity"] is not JsonValue id || !id.TryGetValue(out string? identity) || string.IsNullOrEmpty(identity)) continue;
                string path = "#/state/planning/" + group.path + "/" + index + "/task";
                if (PlanningHistoryWire.IsReferenceTarget(path[1..], targets))
                {
                    canonical.Add((task, path));
                    continue;
                }
                var match = canonical.FirstOrDefault(item => JsonNode.DeepEquals(item.task, task));
                var reference = match.task == null ? null : new JsonObject { ["$ref"] = match.path };
                if (reference != null && reference.ToJsonString(JsonOptions).Length + 32 < task.ToJsonString(JsonOptions).Length)
                    row["task"] = reference;
                else canonical.Add((task, path));
            }
        }
    }

    private const string LatestDesignReviewPointer = "#/state/planning/latestDesign/reviewResult";

    private static long? Sequence(JsonNode? node) => node is JsonValue value && value.TryGetValue(out long sequence)
        ? sequence : null;

    private static string ResultKey(JsonNode node) => Normalize(node)!.ToJsonString(JsonOptions);

    private static JsonNode? SharedDesignResult(JsonNode? result, Dictionary<string, JsonNode> replacements)
        => result != null && replacements.TryGetValue(ResultKey(result), out JsonNode? shared)
            ? shared.DeepClone() : result;

    private static void CompactDesignResults(JsonObject state, Dictionary<string, JsonNode> replacements)
    {
        if (state["planning"] is not JsonObject planning || planning["latestDesign"] is not JsonObject draft) return;
        long? start = Sequence(planning["evidence_start_sequence"]), submitted = Sequence(draft["sequence"]);
        if (!start.HasValue || Sequence(draft["goalStartSequence"]) != start || !submitted.HasValue ||
            submitted <= start || draft["design"] == null)
        {
            // Never bind an old goal's draft or its issue indices to the new goal.
            planning["latestDesign"] = null;
            return;
        }

        if (draft["reviewResult"] is JsonValue rawReview && rawReview.TryGetValue(out string? reviewText))
            draft["reviewResult"] = Structured(reviewText ?? "");
        JsonNode? fullReview = draft["reviewResult"];
        long? reviewed = Sequence(draft["reviewSequence"]);
        if (fullReview is JsonObject reviewObject && reviewObject["topic"]?.GetValue<string>() == "design" && reviewed.HasValue)
        {
            replacements[ResultKey(fullReview)] = new JsonObject
            {
                ["$ref"] = LatestDesignReviewPointer,
                ["note"] = "Complete review of the current unexecuted model proposal; operation indices refer to latestDesign.design."
            };
        }

        if (planning["outcomes"] is not JsonArray outcomes) return;
        foreach (JsonObject outcome in outcomes.OfType<JsonObject>())
        {
            if (outcome["task"] is not JsonObject task ||
                !string.Equals(task["type"]?.GetValue<string>(), "inspect", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(task["target"]?.GetValue<string>(), "design", StringComparison.OrdinalIgnoreCase) ||
                outcome["result"] is not JsonObject result || result["topic"]?.GetValue<string>() != "design") continue;
            long? sequence = Sequence(outcome["sequence"]);
            if (!sequence.HasValue) continue;
            if (sequence == reviewed && fullReview != null && JsonNode.DeepEquals(result, fullReview))
            {
                outcome["result"] = replacements[ResultKey(fullReview)].DeepClone();
                continue;
            }
            if (sequence <= start || sequence >= submitted ||
                result["superseded"]?.GetValue<bool>() == true ||
                draft["reviewIsReadOnly"]?.GetValue<bool>() != true ||
                draft["constructionExecuted"]?.GetValue<bool>() != false ||
                result["worldModified"]?.GetValue<bool>() != false ||
                result["inventoryModified"]?.GetValue<bool>() == true ||
                result["actorsMoved"]?.GetValue<bool>() == true ||
                result["review"] is not JsonObject rejected || rejected["status"]?.GetValue<string>() != "rejected" ||
                outcome["status"] != null && outcome["status"]?.GetValue<string>() != "succeeded") continue;

            // Only obsolete, read-only rejected proposals are summarized. Native execution
            // results and the current full review remain untouched in this wire frame.
            var summary = new JsonObject();
            foreach (string field in new[] { "topic", "source", "worldId", "observedAtTick", "available", "callSucceeded",
                "reviewStatus", "requiresRevision", "worldModified", "inventoryModified", "actorsMoved" })
                if (result.ContainsKey(field)) summary[field] = result[field]?.DeepClone();
            var reviewSummary = new JsonObject();
            foreach (string field in new[] { "inputValid", "staticConstraintsPassed", "status", "issueCount", "issueSummary", "bill",
                "mustOmitOperationIndices", "nativeActualPlacementValidated", "nativeTravelValidated", "nativeHousingValidated" })
                if (rejected.ContainsKey(field)) reviewSummary[field] = rejected[field]?.DeepClone();
            summary["review"] = reviewSummary;
            summary["superseded"] = true;
            summary["appliesToCurrentDesign"] = false;
            summary["reviewedDesignSequence"] = sequence;
            summary["supersededByDesignSequence"] = submitted;
            summary["detailOmittedFromWire"] = true;
            summary["note"] = "Earlier rejected read-only proposal. Issue indices concern that older draft. Detailed coordinates and recipe facts are omitted here; native journal data is unchanged.";
            replacements.TryAdd(ResultKey(result), summary.DeepClone());
            outcome["result"] = summary;
        }
    }

    private static void DeduplicateStructureGrid(JsonNode? historicalResult, JsonObject state)
    {
        JsonNode? selected = state["knowledge"]?["selectedStructure"];
        if (selected?["available"] is not JsonValue availability ||
            !availability.TryGetValue(out bool available) || !available ||
            selected["observation"]?["source"]?.GetValue<string>() != "live_world" ||
            selected["observation"]?["grid"] is not JsonObject currentGrid ||
            !(currentGrid["rows"] is JsonArray && HasCompleteLegend(currentGrid) ||
              IsStructureOverview(currentGrid) && currentGrid["shapeRows"] is JsonArray) ||
            historicalResult is not JsonObject historicalObject ||
            historicalObject["observation"] is not JsonObject observation) return;

        foreach (string field in GeometryFields)
        {
            if (ReferenceEquals(historicalResult, selected)) continue;
            ShareExact(observation, field, selected["observation"]?[field],
                "#/state/knowledge/selectedStructure/observation/" + field, always: field == "grid");
        }
        if (historicalObject["referenceAtSelection"] is JsonObject reference)
        {
            foreach (string field in GeometryFields)
                ShareExact(reference, field, selected["observation"]?[field],
                    "#/state/knowledge/selectedStructure/observation/" + field);
            if (!ReferenceEquals(historicalResult, selected))
                ShareExact(historicalObject, "referenceAtSelection", selected["referenceAtSelection"],
                    "#/state/knowledge/selectedStructure/referenceAtSelection");
        }
        if (!ReferenceEquals(historicalResult, selected))
            ShareExact(historicalObject, "placementCatalog", selected["placementCatalog"],
                "#/state/knowledge/selectedStructure/placementCatalog");
    }

    private static readonly string[] GeometryFields = { "grid", "objects", "surfaces", "standingPositions", "rooms", "candidates", "materialCellCounts" };

    private static void ShareExact(JsonObject owner, string field, JsonNode? canonical, string pointer, bool always = false)
    {
        if (owner[field] is not { } value || canonical == null || value is JsonObject obj && obj.ContainsKey("$ref") ||
            !JsonNode.DeepEquals(value, canonical)) return;
        var reference = new JsonObject
        {
            ["$ref"] = pointer,
            ["note"] = "Exactly equal data in this request; each observation keeps its own historical identity and timestamp."
        };
        if (always || reference.ToJsonString(JsonOptions).Length < value.ToJsonString(JsonOptions).Length) owner[field] = reference;
    }

    private static void ShareTerrainSections(JsonNode? result, JsonObject state)
    {
        if (result is not JsonObject terrain || terrain["topic"]?.GetValue<string>() != "terrain" || terrain.ContainsKey("wireView") ||
            terrain.ContainsKey("detailOmittedFromWire") ||
            state["planning"]?["outcomes"] is not JsonArray outcomes) return;
        for (int index = outcomes.Count - 1; index >= 0; index--)
        {
            if (outcomes[index]?["result"] is not JsonObject canonical ||
                canonical["topic"]?.GetValue<string>() != "terrain" || canonical["available"]?.GetValue<bool>() != true ||
                canonical.ContainsKey("wireView") || canonical.ContainsKey("detailOmittedFromWire")) continue;
            if (ReferenceEquals(result, canonical) || JsonNode.DeepEquals(result, canonical)) return;
            foreach (string field in new[] { "measurement", "standingCandidates", "miningFacts", "requestedWindow", "tileTypes", "wallTypes", "movementLimits" })
                ShareExact(terrain, field, canonical[field], $"#/state/planning/outcomes/{index}/result/{field}");
            return;
        }
    }

    private const string TableEncoding = "columns_and_rows_v1";

    private static bool IsStructureOverview(JsonNode? node) => node is JsonObject grid &&
        grid["view"] is JsonValue value && value.TryGetValue(out string? view) && view == StructureOverviewView.View;

    private static void PackObservationWire(JsonNode? result, bool usePhysicsStructureView, bool useStructureOverview)
    {
        if (result is not JsonObject reading) return;
        if (reading["topic"]?.GetValue<string>() == "build") CompactShelterPreflight(reading);
        // The detail marker belongs to this immutable response, never to global selection state.
        if (reading["topic"]?.GetValue<string>() != "structure_detail")
        {
            if (useStructureOverview)
            {
                JsonObject overview = StructureOverviewView.Project(reading);
                if (IsStructureOverview(overview["observation"]?["grid"]))
                    reading["observation"] = overview["observation"]!.DeepClone();
                if (reading["referenceAtSelection"] is JsonObject historicalReference && historicalReference["grid"] is JsonObject)
                {
                    // A historical reference has its own cells and identity. Project those
                    // cells independently; never substitute the current observation.
                    var historical = StructureOverviewView.Project(new JsonObject
                    {
                        ["topic"] = "structure", ["observation"] = historicalReference.DeepClone()
                    });
                    if (IsStructureOverview(historical["observation"]?["grid"]))
                        reading["referenceAtSelection"] = historical["observation"]!.DeepClone();
                }
            }
            else if (usePhysicsStructureView && result["observation"] is JsonObject observed && observed["grid"] is JsonObject nativeGrid)
                observed["grid"] = StructurePhysicsView.Project(nativeGrid);
        }
        PackStructureLegend(result);
        if (result?["observation"] is JsonObject observation)
        {
            PackGeometryTables(observation);
            if (observation["grid"] is JsonObject grid) PackTable(grid, "legendBases");
        }
        if (result?["referenceAtSelection"] is JsonObject reference) PackGeometryTables(reference);
        if (result?["placementCatalog"] is JsonObject catalog) PackCatalogTables(catalog);
        if (result?["topic"]?.GetValue<string>() is "site" or "terrain") PackCatalogTables(result.AsObject());
        if (result?["topic"]?.GetValue<string>() == "terrain" && result["standingCandidates"] is JsonObject candidates)
            PackTable(candidates, "listed");
    }

    private static readonly string[] ShelterCandidateFields =
    {
        "centerX", "feetReferenceY", "status", "geometryStatus", "groundY", "resolvedTopLeft",
        "blockingCell", "blockingTileType", "actorIntersects", "protection", "protectionStatus", "allowedByCurrentChecks"
    };

    private static readonly string[] ShelterProtectionFields =
    {
        "required", "available", "covered", "allowed", "protectedCellCount", "protectedHits", "protectedHitsOmitted",
        "reason", "region", "observedAtTick", "baseline_id", "baseline_ref"
    };

    private static void CompactShelterPreflight(JsonObject reading)
    {
        // Only change the model's read-only automatic survey. Explicit position checks,
        // execution failures and the native journal keep the original diagnostics.
        if (reading["name"]?.GetValue<string>() != "shelter" || reading["source"]?.GetValue<string>() != "predefined_template" ||
            reading["available"]?.GetValue<bool>() != true ||
            new[] { "worldModified", "inventoryModified", "actorsMoved", "constructionExecuted" }
                .Any(field => reading[field]?.GetValue<bool>() != false) ||
            reading["siteSearch"] is not JsonObject search || search["mode"]?.GetValue<string>() != "automatic_current_actor" ||
            !search.ContainsKey("requestedTopLeft") || search["requestedTopLeft"] != null || search.ContainsKey("candidateView") ||
            search.ContainsKey("rejectedCandidates") || search["candidates"] is not JsonArray candidates || candidates.Count < 4 ||
            Sequence(search["candidateCount"]) != candidates.Count) return;

        var usable = new JsonArray();
        var rejected = new JsonArray();
        var reasons = new JsonArray();
        var reasonIndices = new Dictionary<(string Geometry, string Protection), int>();
        for (int index = 0; index < candidates.Count; index++)
        {
            if (candidates[index] is not JsonObject row || !ExactFields(row, ShelterCandidateFields) ||
                row["allowedByCurrentChecks"] is not JsonValue allowedValue || !allowedValue.TryGetValue(out bool allowed)) return;
            if (allowed)
            {
                usable.Add(row.DeepClone());
                continue;
            }

            if (row["geometryStatus"] is not JsonValue geometryValue || !geometryValue.TryGetValue(out string? geometry) || geometry == null ||
                !JsonNode.DeepEquals(row["status"], geometryValue) ||
                row["protectionStatus"] is not JsonValue protectionValue || !protectionValue.TryGetValue(out string? protectionStatus) ||
                protectionStatus == null) return;
            JsonObject? protection = row["protection"] as JsonObject;
            if (protection == null ? row["protection"] != null || row["resolvedTopLeft"] != null :
                !ExactFields(protection, ShelterProtectionFields) || !JsonNode.DeepEquals(protection["reason"], protectionValue)) return;
            var reason = (geometry, protectionStatus);
            if (!reasonIndices.TryGetValue(reason, out int reasonIndex))
            {
                reasonIndex = reasons.Count;
                reasonIndices.Add(reason, reasonIndex);
                reasons.Add(new JsonObject { ["geometryStatus"] = geometry, ["protectionStatus"] = protectionStatus });
            }
            rejected.Add(new JsonArray(index, reasonIndex, row["centerX"]?.DeepClone(), row["feetReferenceY"]?.DeepClone(),
                row["groundY"]?.DeepClone(), row["resolvedTopLeft"]?.DeepClone(), row["blockingCell"]?.DeepClone(),
                row["blockingTileType"]?.DeepClone(), row["actorIntersects"]?.DeepClone(), protection?["required"]?.DeepClone(),
                protection?["available"]?.DeepClone(), protection?["covered"]?.DeepClone(), protection?["allowed"]?.DeepClone(),
                protection?["protectedCellCount"]?.DeepClone()));
        }

        if (rejected.Count == 0 || Sequence(search["allowedCandidateCount"]) != usable.Count) return;
        var view = search.DeepClone().AsObject();
        view["candidateView"] = "usable_and_rejected_v1";
        view["candidates"] = usable;
        view["rejectedCandidates"] = new JsonObject
        {
            ["columns"] = new JsonArray("originalIndex", "reasonIndex", "centerX", "feetReferenceY", "groundY", "resolvedTopLeft",
                "blockingCell", "blockingTileType", "actorIntersects", "protectionRequired", "protectionAvailable",
                "protectionCovered", "protectionAllowed", "protectedCellCount"),
            ["rows"] = rejected,
            ["reasons"] = reasons,
            ["note"] = "Each row is a rejected native candidate; reasonIndex selects its geometry/protection reasons. Null means unmeasured or unavailable, never safe. Detailed rejected protection hits are omitted. For current detail, inspect build with the same material and a resolved x/y; this rechecks ground and protection."
        };
        if (view.ToJsonString(JsonOptions).Length < search.ToJsonString(JsonOptions).Length) reading["siteSearch"] = view;
    }

    private static bool ExactFields(JsonObject row, string[] fields) => row.Count == fields.Length && fields.All(row.ContainsKey);

    private static void PackGeometryTables(JsonObject geometry)
    {
        foreach (string field in GeometryFields.Where(field => field != "grid")) PackTable(geometry, field);
    }

    private static void PackCatalogTables(JsonObject catalog)
    {
        PackTable(catalog, "tileTypes");
        PackTable(catalog, "wallTypes");
    }

    private static void PackTable(JsonObject owner, string field)
    {
        if (owner[field] is not JsonArray array || array.Count < 4 || array[0] is not JsonObject first) return;
        string[] columns = first.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (columns.Length == 0 || array.Any(entry => entry is not JsonObject obj || obj.Count != columns.Length || columns.Any(column => !obj.ContainsKey(column)))) return;
        var rows = new JsonArray();
        foreach (JsonObject entry in array.Cast<JsonObject>())
            rows.Add(new JsonArray(columns.Select(column => entry[column]?.DeepClone()).ToArray()));
        var table = new JsonObject
        {
            ["encoding"] = TableEncoding,
            ["columns"] = new JsonArray(columns.Select(column => (JsonNode?)JsonValue.Create(column)).ToArray()),
            ["rows"] = rows,
            ["decoding"] = "Each row is one original object: set columns[i] to row[i], including null. Order and every field are preserved."
        };
        if (table.ToJsonString(JsonOptions).Length < array.ToJsonString(JsonOptions).Length) owner[field] = table;
    }

        // Only structurally identical complete grid data is shared. Historical identity,
        // timestamps, measured bounds and all other observation fields stay untouched.
    private const string LegendEncoding = "base_properties_and_frame_variants_v1";

    private static bool HasCompleteLegend(JsonObject grid) => grid["legend"] is JsonArray ||
        (grid["legendEncoding"]?.GetValue<string>() == LegendEncoding &&
         (grid["legendBases"] is JsonArray || grid["legendBases"]?["encoding"]?.GetValue<string>() == TableEncoding) && grid["legendVariants"] is JsonArray);

    private static void PackStructureLegend(JsonNode? result)
    {
        if (result is not JsonObject structureResult ||
            structureResult["observation"]?["grid"] is not JsonObject grid ||
            grid.ContainsKey("legendEncoding") || grid["legend"] is not JsonArray legend || legend.Count == 0) return;

        var bases = new JsonArray();
        var variants = new JsonArray();
        var baseKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonNode? entry in legend)
        {
            // Leave unfamiliar contracts intact instead of dropping fields or guessing defaults.
            if (entry is not JsonObject row || row["key"] is not JsonValue keyValue ||
                row["frameX"] is not JsonValue frameXValue || row["frameY"] is not JsonValue frameYValue ||
                !keyValue.TryGetValue(out int key) || !frameXValue.TryGetValue(out int frameX) ||
                !frameYValue.TryGetValue(out int frameY)) return;
            JsonObject properties = row.DeepClone().AsObject();
            properties.Remove("key"); properties.Remove("frameX"); properties.Remove("frameY");
            string identity = Normalize(properties)!.ToJsonString(JsonOptions);
            if (!baseKeys.TryGetValue(identity, out int baseIndex))
            {
                baseIndex = bases.Count; baseKeys.Add(identity, baseIndex); bases.Add(properties);
            }
            variants.Add(new JsonArray(key, baseIndex, frameX, frameY));
        }

        var packed = new JsonObject
        {
            ["legendEncoding"] = LegendEncoding,
            ["legendBases"] = bases,
            ["legendVariantColumns"] = new JsonArray("key", "baseIndex", "frameX", "frameY"),
            ["legendVariants"] = variants,
            ["legendDecoding"] = "Decode legendBases first if it uses columns_and_rows_v1. For each [key,baseIndex,frameX,frameY], copy every property from legendBases[baseIndex] and add key/frameX/frameY. This reconstructs each complete original legend entry without losing any field. Grid rows still reference the original key."
        };
        // A small unique legend may be cheaper in its original object form.
        if (packed.ToJsonString(JsonOptions).Length >= legend.ToJsonString(JsonOptions).Length + 9) return;
        grid.Remove("legend");
        foreach (var field in packed) grid[field.Key] = field.Value?.DeepClone();
    }

    private static bool InventoryCovered(JsonArray? original, JsonArray? richer)
    {
        if (original == null || richer == null || original.Count != richer.Count) return false;
        for (int index = 0; index < original.Count; index++)
        {
            if (original[index] is not JsonObject item || richer[index] is not JsonObject reference) return false;
            foreach (var field in item)
            {
                string name = field.Key == "createTile" ? "placeTile" : field.Key;
                if (!reference.ContainsKey(name) || !JsonNode.DeepEquals(field.Value, reference[name])) return false;
            }
        }
        return true;
    }

    private static JsonNode? Structured(string text)
    {
        string trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try { return JsonNode.Parse(text); }
            catch (JsonException) { }
        }
        return JsonValue.Create(text);
    }

    private static JsonNode? Normalize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var field in obj.OrderBy(field => field.Key, StringComparer.Ordinal)) sorted[field.Key] = Normalize(field.Value);
            return sorted;
        }
        if (node is JsonArray array) return new JsonArray(array.Select(Normalize).ToArray());
        return node?.DeepClone();
    }
}
