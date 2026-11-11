using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Terraria;

namespace TerrariaAITeammate.Common;

public class AIMemory
{
    private readonly List<string> events = new();
    private readonly List<string> facts = new();
    private readonly Dictionary<string, string> observations = new(StringComparer.Ordinal);
    private string filePath = "";

    public string FactsSnapshot() => string.Join("\n", facts);
    public string Snapshot() => string.Join("\n", events);
    public string ObservedFactsSnapshot() => string.Join("\n", observations.Select(pair => $"{pair.Key}: {pair.Value}"));
    public string FilePath => filePath;

    public void Initialize(string worldName, string playerName)
    {
        string root = Path.Combine(Main.SavePath, "AITeammate");
        Directory.CreateDirectory(root);

        string safeWorld = Sanitize(worldName);
        string safePlayer = Sanitize(playerName);
        filePath = Path.Combine(root, $"memory_{safeWorld}_{safePlayer}.json");

        events.Clear();
        facts.Clear();
        observations.Clear();

        if (!File.Exists(filePath))
            return;

        try
        {
            MemoryData? data = JsonSerializer.Deserialize<MemoryData>(File.ReadAllText(filePath));
            if (data == null)
                return;

            events.AddRange((data.Events ?? new()).TakeLast(20));
            foreach (string fact in (data.Facts ?? new()).TakeLast(30))
                AddUniqueFact(fact);
            foreach (var pair in (data.Observations ?? new()).TakeLast(20))
                observations[pair.Key] = pair.Value;
        }
        catch
        {
            // A corrupt memory file must not block world loading.
        }
    }

    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        text = text.Trim();
        if (events.Count > 0 && Normalize(events[^1]) == Normalize(text)) return;
        events.Add(text);
        if (events.Count > 20)
            events.RemoveAt(0);

        Save();
    }

    public void Remember(string fact)
    {
        if (string.IsNullOrWhiteSpace(fact))
            return;

        AddUniqueFact(fact);
        Save();
    }

    public void Observe(string scope, string fact)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(fact)) return;
        scope = scope.Trim(); fact = fact.Trim();
        if (observations.TryGetValue(scope, out string? previous) && previous == fact) return;
        observations[scope] = fact;
        if (observations.Count > 20) observations.Remove(observations.Keys.First());
        Save();
    }

    private void AddUniqueFact(string fact)
    {
        if (string.IsNullOrWhiteSpace(fact)) return;
        string normalized = Normalize(fact);
        facts.RemoveAll(existing => Normalize(existing) == normalized);
        facts.Add(fact.Trim());
        if (facts.Count > 30) facts.RemoveAt(0);
    }

    private static string Normalize(string fact) => Regex.Replace(fact.Trim(), @"\s+", " ")
        .TrimEnd('。', '.', '！', '!', '；', ';').ToUpperInvariant();

    public void Clear()
    {
        events.Clear();
        facts.Clear();
        observations.Clear();
        Save();
    }

    public void Save()
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            var data = new MemoryData
            {
                Events = events.ToList(),
                Facts = facts.ToList(),
                Observations = new Dictionary<string, string>(observations)
            };
            File.WriteAllText(filePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort persistence; gameplay continues if the write fails.
        }
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }

    private class MemoryData
    {
        public List<string> Events { get; set; } = new();
        public List<string> Facts { get; set; } = new();
        public Dictionary<string, string> Observations { get; set; } = new();
    }
}
