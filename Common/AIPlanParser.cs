using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

/// <summary>Validates decisions before they may affect the world. No game or network dependencies.</summary>
public static class AIPlanParser
{
    public const int MaxTasks = 6;

    public static AIPlan Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new JsonException("The planner returned an empty decision.");

        string json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            int newline = json.IndexOf('\n');
            if (newline < 0 || !json.EndsWith("```", StringComparison.Ordinal))
                throw new JsonException("The planner returned an incomplete JSON fence.");
            json = json[(newline + 1)..^3].Trim();
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("A planner decision must be a JSON object.");

        var plan = new AIPlan
        {
            Reply = Text(root, "reply"),
            Goal = Text(root, "goal"),
            Remember = Text(root, "remember"),
            Reason = Text(root, "reason"),
            SuccessCriteria = Text(root, "success_criteria"),
            CompletionAction = Text(root, "completion_action").ToLowerInvariant(),
            CompletionTarget = root.TryGetProperty("completion_target", out JsonElement completionTarget) &&
                completionTarget.ValueKind == JsonValueKind.Number && completionTarget.TryGetInt32(out int targetId)
                ? targetId.ToString(System.Globalization.CultureInfo.InvariantCulture) : Text(root, "completion_target"),
            CompletionConditions = root.TryGetProperty("completion_conditions", out JsonElement conditions)
                ? ParseConditions(conditions) : null,
            ContractRevisionReason = root.TryGetProperty("contract_revision_reason", out JsonElement revisionReason) &&
                revisionReason.ValueKind == JsonValueKind.Null ? "" : Text(root, "contract_revision_reason"),
            WorkingPlan = root.TryGetProperty("working_plan", out JsonElement workingPlan)
                ? ParseWorkingPlan(workingPlan) : null,
            Hold = Flag(root, "hold"),
            Resume = Flag(root, "resume"),
            ClearTasks = Flag(root, "clear_tasks"),
            WaitForPlayer = Flag(root, "wait_for_player"),
            ReplanAfterStep = Flag(root, "replan_after_step", false),
            GoalStatus = Text(root, "goal_status", "active").ToLowerInvariant(),
            GoalDone = Flag(root, "goal_done")
        };

        if (plan.CompletionAction is not ("" or "come" or "attack" or "retreat" or "mine" or
            "build" or "explore" or "pickup" or "drop" or "use" or "equip" or "deposit" or
            "withdraw" or "craft" or "place"))
        {
            // Completion is checked against the validated native contract. A model may
            // repeat one of its condition labels in this obsolete action hint; do not
            // discard an otherwise executable plan or turn that label into an action.
            if (plan.CompletionConditions?.Any(condition => condition.Type == plan.CompletionAction) == true)
                plan.CompletionAction = "";
            else
                throw new JsonException("completion_action must name a supported physical task, not a query or idle state.");
        }

        if (plan.GoalStatus is not ("active" or "completed" or "blocked" or "cancelled"))
            throw new JsonException("Unknown goal_status; expected active, completed, blocked or cancelled.");
        if (plan.GoalStatus == "cancelled" && plan.GoalDone)
            throw new JsonException("A cancelled goal cannot also claim completion.");
        if (plan.GoalDone)
            plan.GoalStatus = "completed";
        plan.GoalDone = plan.GoalStatus == "completed";
        if (plan.Resume && (plan.Hold || plan.WaitForPlayer || plan.GoalDone || plan.GoalStatus == "cancelled"))
            throw new JsonException("resume cannot be combined with hold, waiting, completion or cancellation.");

        bool hasTasks = false;
        if (root.TryGetProperty("tasks", out JsonElement tasks) ||
            root.TryGetProperty("actions", out tasks))
        {
            hasTasks = true;
            if (tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() > MaxTasks)
                throw new JsonException($"tasks must be an array with at most {MaxTasks} entries.");
            foreach (JsonElement task in tasks.EnumerateArray())
                plan.Steps.Add(ParseStep(task));
        }
        else if (root.TryGetProperty("action", out JsonElement action))
        {
            hasTasks = true;
            plan.Steps.Add(ParseStep(action));
        }

        if (plan.Steps.Count > 1 && plan.Steps.Exists(step => step.Action == AIActionType.Stop))
            throw new JsonException("stop is a standalone pause control and cannot be mixed with execution tasks.");
        if (plan.WorkingPlan != null)
        {
            var stepIds = new HashSet<string>(plan.WorkingPlan.Steps.ConvertAll(step => step.Id), StringComparer.Ordinal);
            if (plan.Steps.Exists(step => step.PlanStep.Length > 0 && !stepIds.Contains(step.PlanStep)))
                throw new JsonException("plan_step must reference a step in the proposed working_plan.");
        }
        bool keep = Flag(root, "keep_tasks", !hasTasks && !plan.ClearTasks);
        if (plan.Steps.Count > 0 && (keep || plan.GoalDone || plan.GoalStatus == "cancelled" || plan.WaitForPlayer || plan.Hold))
            throw new JsonException("Executable tasks conflict with keep_tasks, completion, waiting or hold.");
        if (plan.ClearTasks && (keep || plan.Steps.Count > 0 || plan.Resume || plan.Hold || plan.WaitForPlayer ||
            plan.GoalStatus != "active" || plan.Goal.Length > 0 || plan.WorkingPlan != null ||
            plan.CompletionConditions is { Count: > 0 } || plan.SuccessCriteria.Length > 0 ||
            plan.ContractRevisionReason.Length > 0 || plan.CompletionAction.Length > 0 || plan.CompletionTarget.Length > 0 ||
            plan.ReplanAfterStep))
            throw new JsonException("clear_tasks may only clear the existing queue and cannot change its goal, pause or contract.");
        if (plan.GoalDone && plan.WaitForPlayer)
            throw new JsonException("A completed goal cannot wait for player input.");

        plan.KeepTasks = keep;
        plan.ReplaceTasks = (hasTasks || plan.ClearTasks) && !keep;
        if (plan.Steps.Count > 0)
        {
            AIPlan first = plan.Steps[0];
            plan.Action = first.Action;
            plan.Target = first.Target;
            plan.Item = first.Item;
            plan.RecipeId = first.RecipeId;
            plan.PlanStep = first.PlanStep;
            plan.Slot = first.Slot;
            plan.Count = first.Count;
            plan.Blueprint = first.Blueprint;
            plan.X = first.X;
            plan.Y = first.Y;
            plan.Width = first.Width;
            plan.Height = first.Height;
            plan.Design = first.Design?.Clone();
        }
        return plan;
    }

    private static AIPlan ParseStep(JsonElement task)
    {
        if (task.ValueKind != JsonValueKind.Object)
            throw new JsonException("Every task must be a JSON object.");

        string type = Text(task, "type").ToLowerInvariant();
        AIActionType action = type switch
        {
            "follow" => AIActionType.Follow,
            "come" => AIActionType.Come,
            "wait" => AIActionType.Wait,
            "stop" => AIActionType.Stop,
            "attack" => AIActionType.Attack,
            "retreat" => AIActionType.Retreat,
            "mine" => AIActionType.Mine,
            "build" => AIActionType.Build,
            "explore" => AIActionType.Explore,
            "pickup" => AIActionType.Pickup,
            "drop" => AIActionType.Drop,
            "use" => AIActionType.Use,
            "equip" => AIActionType.Equip,
            "deposit" => AIActionType.Deposit,
            "withdraw" => AIActionType.Withdraw,
            "craft" => AIActionType.Craft,
            "inspect" => AIActionType.Inspect,
            "place" => AIActionType.Place,
            _ => throw new JsonException($"Unsupported task type: {type}.")
        };

        int count = Number(task, "count") ?? 0;
        int? x = Number(task, "x");
        int? y = Number(task, "y");
        int? recipeId = Number(task, "recipe_id");
        if (recipeId < 0 || recipeId.HasValue && action != AIActionType.Craft)
            throw new JsonException("recipe_id must be a nonnegative native recipe index on a craft task.");
        if (count < 0 || count > 999)
            throw new JsonException("Task count must be between 0 and 999.");
        if (x.HasValue != y.HasValue || x < 0 || y < 0 || x > 100000 || y > 100000)
            throw new JsonException("Tile coordinates must be supplied as a nonnegative x/y pair.");
        if (action == AIActionType.Pickup && IsExplicitPickupWithdrawal(task, count, x, y))
            action = AIActionType.Withdraw;

        int width = Number(task, "width") ?? 1;
        int height = Number(task, "height") ?? 1;
        if (width < 1 || height < 1 || width > 30 || height > 30 || width * height > 300)
            throw new JsonException("width/height must be 1-30 and the area at most 300 tiles.");
        if ((width > 1 || height > 1) && (!x.HasValue || action != AIActionType.Place))
            throw new JsonException("width/height are only valid for place tasks with x/y.");

        string target = action == AIActionType.Attack
            ? TextOrPositiveNativeId(task, "target") : Text(task, "target");
        bool acceptsItemId = action is AIActionType.Pickup or AIActionType.Drop or AIActionType.Use or
            AIActionType.Equip or AIActionType.Deposit or AIActionType.Withdraw or AIActionType.Craft or AIActionType.Place ||
            action == AIActionType.Inspect && target.ToLowerInvariant() is "recipe" or "item" or "boss";
        var plan = new AIPlan
        {
            Action = action,
            Target = target,
            Item = acceptsItemId ? TextOrPositiveNativeId(task, "item") : Text(task, "item"),
            RecipeId = recipeId,
            PlanStep = Text(task, "plan_step"),
            Slot = Text(task, "slot"),
            Blueprint = Text(task, "blueprint"),
            Reason = Text(task, "reason"),
            SuccessCriteria = Text(task, "success_criteria"),
            Count = count,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Design = task.TryGetProperty("design", out JsonElement design) ? design.Clone() : null
        };
        NormalizeExplicitSummonerUse(task, plan);
        action = plan.Action;
        if (action is AIActionType.Place or AIActionType.Craft or AIActionType.Use or AIActionType.Equip &&
            string.IsNullOrWhiteSpace(plan.Item))
            throw new JsonException($"Task {type} requires item.");
        if (action == AIActionType.Mine)
        {
            plan.Target = plan.Target.ToLowerInvariant();
            if (plan.Target is not ("wood" or "stone" or "iron" or "copper" or "gold" or "silver" or
                "lead" or "tin" or "tungsten" or "platinum" or "meteorite" or "ruby" or "dirt" or "grass"))
                throw new JsonException("mine target must name a supported resource or explicit natural dirt/grass.");
            if (plan.Target is "dirt" or "grass" && !plan.X.HasValue)
                throw new JsonException("Natural dirt/grass excavation requires an explicit paired x/y seed; it cannot choose terrain automatically.");
        }
        if (action == AIActionType.Inspect && plan.Target.ToLowerInvariant() is not ("recipe" or "build" or "item" or "stations" or "nearby" or "boss" or "chest" or "structure" or "house" or "structure_detail" or "site" or "terrain" or "route" or "mine" or "sources" or "design" or "world" or "observation"))
            throw new JsonException("inspect target must be recipe, build, item, stations, nearby, boss, chest, structure, house, structure_detail, site, terrain, route, mine, sources, design, world or observation.");
        if (action == AIActionType.Inspect && plan.Target.Equals("observation", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(plan.Item) || x.HasValue || y.HasValue))
            throw new JsonException("observation requires the stored opaque item ID without x/y; it reads historical data, not a fresh world query.");

        plan.Item = NormalizeWorldOptions(task, action, plan.Target, plan.Item, x, y);

        return plan;
    }

    // An observed Flash reply marked a pickup as an explicit chest transfer.
    // It must name the same item, quantity and chest that a Withdraw would use.
    private static bool IsExplicitPickupWithdrawal(JsonElement task, int count, int? x, int? y)
    {
        JsonProperty[] markers = task.EnumerateObject().Where(field =>
            field.Name.Equals("withdraw", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (markers.Length == 0) return false;
        if (markers.Length != 1 || !markers[0].NameEquals("withdraw"))
            throw new JsonException("Pickup withdrawal must use one unambiguous withdraw marker.");
        if (!Flag(task, "withdraw")) return false;

        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty field in task.EnumerateObject())
            if (field.Name is "type" or "item" or "count" or "x" or "y" && !fields.Add(field.Name))
                throw new JsonException("Explicit pickup withdrawal cannot repeat " + field.Name + ".");
        if (!x.HasValue || !y.HasValue || count <= 0 || Number(task, "item") is not > 0)
            throw new JsonException("Explicit pickup withdrawal requires paired chest coordinates, a positive numeric item ID and a positive count.");
        return true;
    }

    // Recorded Flash decisions put an explicit use marker on equip. Only normalize
    // supported summoner IDs and never infer use from an equip request alone.
    private static void NormalizeExplicitSummonerUse(JsonElement task, AIPlan plan)
    {
        if (plan.Action != AIActionType.Equip || !task.TryGetProperty("use", out JsonElement use)) return;
        string reference = use.ValueKind switch
        {
            JsonValueKind.True => plan.Item,
            JsonValueKind.Number when use.TryGetInt32(out int number) => number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => (use.GetString() ?? "").Trim(),
            _ => ""
        };
        if (!int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out int itemId) ||
            BossRules.BossTypeForItem(itemId) == 0) return;

        var fields = new HashSet<string>(StringComparer.Ordinal);
        bool conflictingFields = task.EnumerateObject().Any(property => !fields.Add(property.Name) ||
            property.Name is not ("type" or "item" or "use" or "plan_step" or "slot" or "count" or "reason" or "success_criteria"));
        bool conflictingItem = plan.Item.Length > 0 &&
            (!int.TryParse(plan.Item, NumberStyles.None, CultureInfo.InvariantCulture, out int equipItem) || equipItem != itemId);
        if (conflictingFields || conflictingItem || plan.Slot.Length > 0 || plan.Count > 1)
            throw new JsonException("Conflicting summoner use fields. Use a standalone task with type=use and one native item ID.");

        plan.Action = AIActionType.Use;
        plan.Item = itemId.ToString(CultureInfo.InvariantCulture);
    }

    // Keep these query-only options in Item so existing executor and saved-task DTOs
    // receive the exact same grammar as an inline world query, without extra fields.
    private static string NormalizeWorldOptions(JsonElement task, AIActionType action, string target, string query, int? x, int? y)
    {
        bool acceptsSourceRefinement = action == AIActionType.Inspect && target.Equals("sources", StringComparison.OrdinalIgnoreCase);
        bool acceptsWorldOptions = action == AIActionType.Inspect &&
            (target.Equals("world", StringComparison.OrdinalIgnoreCase) || acceptsSourceRefinement);
        static string? Key(string name) => name.ToLowerInvariant() switch
        {
            "mindistance" => "minDistance", "maxdistance" => "maxDistance",
            "limit" => "limit", "offset" => "offset", _ => null
        };
        static string? RefinementKey(string name) => name.ToLowerInvariant() switch
        {
            "refineoffset" => "refineOffset", "refinescope" => "refineScope", _ => null
        };
        static void Validate(string key, int value)
        {
            int minimum = key == "limit" ? 1 : 0;
            int maximum = key == "limit" ? 8 : key == "offset" ? 50000 : 100000;
            if (value < minimum || value > maximum)
                throw new JsonException($"World {key} must be an integer from {minimum} through {maximum}.");
        }
        var structured = new Dictionary<string, int>(StringComparer.Ordinal);
        var structuredRefinement = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in task.EnumerateObject())
        {
            string? refinementKey = RefinementKey(property.Name);
            if (refinementKey != null)
            {
                if (!acceptsSourceRefinement)
                    throw new JsonException($"{property.Name} is only valid on inspect target=sources tasks.");
                string refinementValue;
                if (refinementKey == "refineOffset")
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int offset))
                        throw new JsonException("Source refineOffset must be an integer JSON number.");
                    refinementValue = offset.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                        throw new JsonException("Source refineScope must be a string.");
                    refinementValue = property.Value.GetString()!;
                    if (refinementValue.Contains(';') || refinementValue.Contains('='))
                        throw new JsonException("Source refineScope cannot contain query delimiters.");
                }
                if (!structuredRefinement.TryAdd(refinementKey, refinementValue))
                    throw new JsonException("Duplicate source refinement option: " + refinementKey);
                continue;
            }
            string? key = Key(property.Name);
            if (key == null) continue;
            if (!acceptsWorldOptions)
                throw new JsonException($"{property.Name} is only valid on inspect target=world or sources tasks.");
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int value))
                throw new JsonException($"World {property.Name} must be an integer JSON number.");
            Validate(key, value);
            if (!structured.TryAdd(key, value)) throw new JsonException("Duplicate world query option: " + key);
        }
        if (!acceptsWorldOptions)
        {
            foreach (string part in query.Split(';').Skip(1))
            {
                int equals = part.IndexOf('=');
                if (RefinementKey((equals < 0 ? part : part[..equals]).Trim()) != null)
                    throw new JsonException("Source refinement options are only valid on inspect target=sources tasks.");
            }
            return query;
        }

        var combined = new Dictionary<string, int>(StringComparer.Ordinal);
        var refinement = new HashSet<string>(StringComparer.Ordinal);
        string[] parts = query.Split(';');
        for (int index = 1; index < parts.Length; index++)
        {
            int equals = parts[index].IndexOf('=');
            string? refinementKey = equals > 0 ? RefinementKey(parts[index][..equals].Trim()) : null;
            if (refinementKey != null)
            {
                if (!acceptsSourceRefinement)
                    throw new JsonException("Source refinement options are only valid on inspect target=sources tasks.");
                if (equals != parts[index].LastIndexOf('='))
                    throw new JsonException("Malformed source refinement option: " + parts[index]);
                if (!refinement.Add(refinementKey))
                    throw new JsonException("Duplicate source refinement option: " + refinementKey);
                continue;
            }
            string? key = equals > 0 && equals == parts[index].LastIndexOf('=') ? Key(parts[index][..equals].Trim()) : null;
            if (key == null || !int.TryParse(parts[index][(equals + 1)..].Trim(),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value))
                throw new JsonException("Invalid inline world query option: " + parts[index]);
            Validate(key, value);
            if (!combined.TryAdd(key, value)) throw new JsonException("Duplicate world query option: " + key);
        }
        foreach (var option in structured)
            if (!combined.TryAdd(option.Key, option.Value)) throw new JsonException("Duplicate world query option: " + option.Key);
        foreach (var option in structuredRefinement)
            if (!refinement.Add(option.Key)) throw new JsonException("Duplicate source refinement option: " + option.Key);
        if (combined.TryGetValue("minDistance", out int minimumDistance) &&
            combined.TryGetValue("maxDistance", out int maximumDistance) && minimumDistance > maximumDistance)
            throw new JsonException("World minDistance must not exceed maxDistance.");
        string subject = parts[0].Trim().ToLowerInvariant();
        if ((combined.ContainsKey("minDistance") || combined.ContainsKey("maxDistance")) && subject is "" or "overview" or "chest")
            throw new JsonException("Distance filters apply to world location lists, not overview or an exact chest detail query.");
        foreach (var option in structured)
            query += ";" + option.Key + "=" + option.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var option in structuredRefinement)
            query += ";" + option.Key + "=" + option.Value;
        if (refinement.Count > 0)
        {
            // Native sources owns the cursor range, scope shape and resource grammar.
            // The live observer separately validates that this opaque scope still matches its enumeration.
            try { MiningSourceRefinementRequest.Parse(query, x, y); }
            catch (ArgumentException error) { throw new JsonException(error.Message, error); }
        }
        return query;
    }

    private static List<GoalCondition>? ParseConditions(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Null) return null;
        if (raw.ValueKind == JsonValueKind.Object)
        {
            JsonElement wrapped = default;
            int keys = 0;
            foreach (JsonProperty property in raw.EnumerateObject())
            {
                keys++;
                if (property.Name != "conditions")
                    throw new JsonException("completion_conditions wrapper may contain only conditions; operators or other semantics cannot be discarded.");
                wrapped = property.Value;
            }
            if (keys != 1 || wrapped.ValueKind != JsonValueKind.Array)
                throw new JsonException("completion_conditions wrapper must contain exactly one conditions array.");
            raw = wrapped;
        }
        if (raw.ValueKind != JsonValueKind.Array)
            throw new JsonException("completion_conditions must be an array or a single-key object containing its conditions array.");
        var result = new List<GoalCondition>();
        foreach (JsonElement row in raw.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new JsonException("Every goal condition must be an object.");
            string typeKey = "";
            foreach (JsonProperty property in row.EnumerateObject())
                if (property.Name is "type" or "kind")
                {
                    if (typeKey.Length > 0)
                        throw new JsonException("A goal condition must declare type or its kind alias exactly once; duplicate or combined declarations are not accepted.");
                    typeKey = property.Name;
                }
            string type = typeKey.Length == 0 ? "" : Text(row, typeKey).ToLowerInvariant();
            var condition = new GoalCondition
            {
                Type = type, ItemId = Number(row, "item_id"), Count = Number(row, "count"),
                HorizontalPixels = Real(row, "horizontal_pixels"), VerticalPixels = Real(row, "vertical_pixels"),
                X = Number(row, "x"), Y = Number(row, "y"), Width = Number(row, "width"), Height = Number(row, "height"),
                TileType = Number(row, "tile_type"), WallType = Number(row, "wall_type"), NpcType = ParseConditionNpcType(row, type),
                Description = Text(row, "description"),
                Actions = row.TryGetProperty("actions", out JsonElement actions) ? ParseOrderedActions(actions) : null
            };
            if (string.Equals(condition.Type, "ordered_actions", StringComparison.OrdinalIgnoreCase))
            {
                ValidateOrderedFields(row, "type", "kind", "description", "actions");
                if (result.Exists(previous => previous.Type == "ordered_actions"))
                    throw new JsonException("Each goal may bind one ordered_actions sequence.");
            }
            if (string.Equals(condition.Type.Trim(), "structure_preserved", StringComparison.OrdinalIgnoreCase))
            {
                var declared = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in row.EnumerateObject())
                    if (property.Name is not ("type" or "kind" or "description") || !declared.Add(property.Name))
                        throw new JsonException("structure_preserved accepts only type (or kind) and optional description; its baseline and full scope are owned by the current runtime goal.");
            }
            if (string.Equals(condition.Type.Trim(), "new_housing_valid", StringComparison.OrdinalIgnoreCase))
            {
                var declared = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in row.EnumerateObject())
                    if (property.Name is not ("type" or "kind" or "description" or "x" or "y" or "width" or "height") || !declared.Add(property.Name))
                        throw new JsonException("new_housing_valid accepts only type (or kind), optional description and paired x/y/width/height; newness and the baseline are native goal facts, not model assertions.");
            }
            if (!GoalCompletionState.TryNormalize(condition, out GoalCondition normalized, out string error))
                throw new JsonException(error);
            result.Add(normalized);
        }
        return result;
    }

    private static int? ParseConditionNpcType(JsonElement row, string conditionType)
    {
        if (conditionType is not ("boss_defeated" or "boss_loot") ||
            !row.TryGetProperty("npc_type", out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return Number(row, "npc_type");

        int id = BossRules.ResolveNpcName(Text(row, "npc_type"));
        if (id == 0) throw new JsonException("npc_type must be a positive native NPC ID or a supported boss name.");
        return id;
    }

    private static IReadOnlyList<GoalOrderedAction> ParseOrderedActions(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 2 or > 6)
            throw new JsonException("ordered_actions.actions must contain 2-6 actions.");
        var actions = new List<GoalOrderedAction>();
        foreach (JsonElement action in value.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object) throw new JsonException("Each ordered action must be an object.");
            string type = Text(action, "type").ToLowerInvariant();
            if (type == "craft")
            {
                ValidateOrderedFields(action, "type", "item_id", "count");
                actions.Add(new(type, Number(action, "item_id"), Number(action, "count")));
                continue;
            }
            if (type != "come") throw new JsonException("Ordered action type must be come or craft.");
            ValidateOrderedFields(action, "type", "carrying");
            var carrying = new List<GoalCarriedItem>();
            if (action.TryGetProperty("carrying", out JsonElement items))
            {
                if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 8)
                    throw new JsonException("come.carrying must be an array of at most 8 item quantities.");
                foreach (JsonElement item in items.EnumerateArray())
                {
                    ValidateOrderedFields(item, "item_id", "count");
                    carrying.Add(new(Number(item, "item_id") ?? 0, Number(item, "count") ?? 0));
                }
            }
            actions.Add(new(type, Carrying: carrying));
        }
        return actions;
    }

    private static void ValidateOrderedFields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Ordered action requirements must be objects.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty field in value.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name))
                throw new JsonException("Unknown or duplicate ordered action field: " + field.Name);
    }

    private static WorkingPlanProposal? ParseWorkingPlan(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Null) return null;
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("steps", out JsonElement rows) ||
            rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 12)
            throw new JsonException("working_plan requires an object with at most12 steps.");
        var result = new WorkingPlanProposal { Summary = Text(raw, "summary") };
        if (result.Summary.Length == 0) throw new JsonException("working_plan requires a summary.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new JsonException("Every working-plan step must be an object.");
            var step = new WorkingPlanStep { Id = Text(row, "id"), Objective = Text(row, "objective"), SuccessCriteria = Text(row, "success_criteria") };
            if (step.Id.Length == 0 || step.Objective.Length == 0 || step.SuccessCriteria.Length == 0 || !ids.Add(step.Id))
                throw new JsonException("Working-plan steps require unique nonempty id, objective and success_criteria.");
            if (row.TryGetProperty("depends_on", out JsonElement dependencies))
            {
                if (dependencies.ValueKind != JsonValueKind.Array) throw new JsonException("depends_on must be an array of step ids.");
                foreach (JsonElement dependency in dependencies.EnumerateArray())
                {
                    if (dependency.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(dependency.GetString()))
                        throw new JsonException("depends_on must contain nonempty step ids.");
                    step.DependsOn.Add(dependency.GetString()!.Trim());
                }
            }
            result.Steps.Add(step);
        }
        var byId = new Dictionary<string, WorkingPlanStep>(StringComparer.Ordinal);
        foreach (WorkingPlanStep step in result.Steps) byId.Add(step.Id, step);
        foreach (WorkingPlanStep step in result.Steps)
            if (step.DependsOn.Exists(id => !ids.Contains(id))) throw new JsonException("depends_on references an unknown working-plan step.");
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new JsonException("working_plan dependencies must be acyclic.");
            foreach (string dependency in byId[id].DependsOn) Visit(dependency);
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (string id in ids) Visit(id);
        return result;
    }

    private static float? Real(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetSingle(out float result) || !float.IsFinite(result))
            throw new JsonException($"{name} must be a finite number.");
        return result;
    }

    // Only call for fields whose existing executor/query accepts an item or boss ID string.
    // Do not turn resource names, coordinate query grammars or ordinary prose into identifiers.
    private static string TextOrPositiveNativeId(JsonElement value, string name)
    {
        if (value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Number)
        {
            if (!property.TryGetInt32(out int id) || id <= 0)
                throw new JsonException($"{name} must be a string or a positive integer native ID.");
            return id.ToString(CultureInfo.InvariantCulture);
        }
        return Text(value, name);
    }

    private static string Text(JsonElement value, string name, string fallback = "")
    {
        if (!value.TryGetProperty(name, out JsonElement property))
            return fallback;
        if (property.ValueKind != JsonValueKind.String)
            throw new JsonException($"{name} must be a string.");
        string text = (property.GetString() ?? "").Trim();
        if (text.Length > 2000)
            throw new JsonException($"{name} exceeds 2000 characters.");
        return text;
    }

    private static bool Flag(JsonElement value, string name, bool fallback = false)
    {
        if (!value.TryGetProperty(name, out JsonElement property))
            return fallback;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException($"{name} must be a boolean.");
        return property.GetBoolean();
    }

    private static int? Number(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out JsonElement property))
            return null;
        if (property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int result))
            throw new JsonException($"{name} must be an integer.");
        return result;
    }
}
