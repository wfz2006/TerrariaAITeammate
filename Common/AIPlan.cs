using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerrariaAITeammate.Common;

public enum AIActionType
{
    None,
    Follow,
    Come,
    Wait,
    Stop,
    Attack,
    Mine,
    Build,
    Explore,
    Pickup,
    Drop,
    Use,
    Equip,
    Deposit,
    Withdraw,
    Craft,
    Retreat,
    Inspect,
    Place
}

public class AIPlan
{
    [JsonIgnore]
    internal object? NativeObservationReceipt { get; set; }
    public string Reply { get; set; } = "";
    public AIActionType Action { get; set; } = AIActionType.None;
    public string Target { get; set; } = "";
    public int Count { get; set; }
    public string Blueprint { get; set; } = "";
    public string Remember { get; set; } = "";
    public string Item { get; set; } = "";
    public int? RecipeId { get; set; }
    public string Slot { get; set; } = "";
    public List<AIPlan> Steps { get; set; } = new();
    public bool KeepTasks { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ClearTasks { get; set; }
    public bool ReplaceTasks { get; set; }
    public bool Hold { get; set; }
    public bool Resume { get; set; }
    public string Goal { get; set; } = "";
    public bool GoalDone { get; set; }
    public string GoalStatus { get; set; } = "active";
    public string SuccessCriteria { get; set; } = "";
    public string CompletionAction { get; set; } = "";
    public string CompletionTarget { get; set; } = "";
    public List<GoalCondition>? CompletionConditions { get; set; }
    public string ContractRevisionReason { get; set; } = "";
    public WorkingPlanProposal? WorkingPlan { get; set; }
    public string PlanStep { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool ReplanAfterStep { get; set; }
    public bool WaitForPlayer { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; } = 1;
    public int Height { get; set; } = 1;
    public JsonElement? Design { get; set; }
}

public sealed record GoalCondition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
    [JsonPropertyName("item_id")]
    public int? ItemId { get; set; }
    [JsonPropertyName("count")]
    public int? Count { get; set; }
    [JsonPropertyName("horizontal_pixels")]
    public float? HorizontalPixels { get; set; }
    [JsonPropertyName("vertical_pixels")]
    public float? VerticalPixels { get; set; }
    [JsonPropertyName("x")]
    public int? X { get; set; }
    [JsonPropertyName("y")]
    public int? Y { get; set; }
    [JsonPropertyName("width")]
    public int? Width { get; set; }
    [JsonPropertyName("height")]
    public int? Height { get; set; }
    [JsonPropertyName("tile_type")]
    public int? TileType { get; set; }
    [JsonPropertyName("wall_type")]
    public int? WallType { get; set; }
    [JsonPropertyName("npc_type")]
    public int? NpcType { get; set; }
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
    [JsonPropertyName("actions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GoalOrderedAction>? Actions { get; set; }
}

public sealed record GoalCarriedItem(
    [property: JsonPropertyName("item_id")] int ItemId,
    [property: JsonPropertyName("count")] int Count);

public sealed record GoalOrderedAction(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("item_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ItemId = null,
    [property: JsonPropertyName("count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null,
    [property: JsonPropertyName("carrying"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GoalCarriedItem>? Carrying = null);

public sealed class WorkingPlanProposal
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";
    [JsonPropertyName("steps")]
    public List<WorkingPlanStep> Steps { get; set; } = new();
}

public sealed class WorkingPlanStep
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("objective")]
    public string Objective { get; set; } = "";
    [JsonPropertyName("success_criteria")]
    public string SuccessCriteria { get; set; } = "";
    [JsonPropertyName("depends_on")]
    public List<string> DependsOn { get; set; } = new();
}

/// <summary>Facts collected by the native observer, never accepted from model JSON. Null means not observed.</summary>
public sealed class GoalEvidenceSnapshot
{
    public IReadOnlyDictionary<int, int>? Inventory { get; set; }
    public IReadOnlySet<int>? Equipped { get; set; }
    public float? HorizontalPixels { get; set; }
    public float? VerticalPixels { get; set; }
    public IReadOnlyDictionary<int, int>? BossDefeats { get; set; }
    public IReadOnlyDictionary<int, int>? BossLootPickups { get; set; }
    public IReadOnlyDictionary<int, GoalNativeConditionResult> NativeConditions { get; set; }
        = new Dictionary<int, GoalNativeConditionResult>();
}

public sealed record GoalNativeConditionResult(bool? Met, string Detail);
public sealed record GoalConditionEvaluation(int Index, string Type, string Status, string Detail);
public sealed class GoalCompletionEvaluation
{
    public bool Verified { get; init; }
    public int SpecCount { get; init; }
    public string Status { get; init; } = "pending";
    public IReadOnlyList<GoalConditionEvaluation> Conditions { get; init; } = new List<GoalConditionEvaluation>();
}
