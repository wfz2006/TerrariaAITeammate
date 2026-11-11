using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

/// <summary>Identity of the operation, excluding dialogue and planning metadata.</summary>
public static class PlanTaskIdentity
{
    public static bool AreEquivalent(AIPlan a, AIPlan b) => Fingerprint(a) == Fingerprint(b);

    public static string Fingerprint(AIPlan? task)
    {
        if (task == null) return "";
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(task.Action.ToString());
            writer.WriteStringValue(Normalize(task.Target));
            writer.WriteStringValue(Normalize(task.Item));
            writer.WriteStringValue(Normalize(task.Slot));
            writer.WriteStringValue(task.Action == AIActionType.Build && string.IsNullOrWhiteSpace(task.Blueprint)
                ? "shelter" : Normalize(task.Blueprint));
            writer.WriteNumberValue(EffectiveCount(task));
            if (task.X.HasValue) writer.WriteNumberValue(task.X.Value); else writer.WriteNullValue();
            if (task.Y.HasValue) writer.WriteNumberValue(task.Y.Value); else writer.WriteNullValue();
            writer.WriteNumberValue(task.Width);
            writer.WriteNumberValue(task.Height);
            if (task.RecipeId.HasValue) writer.WriteNumberValue(task.RecipeId.Value); else writer.WriteNullValue();
            if (task.Design.HasValue) WriteCanonical(writer, task.Design.Value); else writer.WriteNullValue();
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    public static int EffectiveCount(AIPlan task) => task.Count > 0 ? Math.Min(task.Count, 999)
        : task.Action is AIActionType.Mine or AIActionType.Drop ? 10
        : task.Action is AIActionType.Deposit or AIActionType.Withdraw or AIActionType.Pickup ? 0 : 1;

    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant();

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
