using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrariaAITeammate.Common;

public sealed record LlmMessage(string Role, string Content);

public sealed record LlmHistorySnapshot(long WorldVersion, long KeyVersion, List<LlmMessage> Messages, bool Exact = false);

public static class LlmConversationStore
{
    public static string CompactObservation(string text)
    {
        int stateStart = text.IndexOf("当前状态：", StringComparison.Ordinal);
        string summary = stateStart < 0 ? text : text[..stateStart].Trim();
        return summary.Length <= 1200 ? summary : summary[..1200];
    }

    private static readonly Dictionary<string, List<LlmMessage>> Histories = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> KeyVersions = new();
    private static long worldVersion;

    public static LlmHistorySnapshot CaptureForRequest(string key, int maxMessages = 12, int maxCharacters = 48000)
    {
        lock (Gate)
        {
            if (maxMessages < 2 || maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxMessages));
            if (Histories.TryGetValue(key, out List<LlmMessage>? history) &&
                (history.Count >= maxMessages || history.Sum(message => (long)message.Content.Length) > maxCharacters))
                Reset(key);
            return new LlmHistorySnapshot(worldVersion, KeyVersions.GetValueOrDefault(key),
                Histories.TryGetValue(key, out history) ? history.ToList() : new List<LlmMessage>(), Exact: true);
        }
    }

    public static LlmHistorySnapshot Capture(string key, int maxMessages)
    {
        lock (Gate)
            return new LlmHistorySnapshot(worldVersion, KeyVersions.GetValueOrDefault(key), Snapshot(key, maxMessages));
    }

    public static List<LlmMessage> Snapshot(string key, int maxMessages)
    {
        lock (Gate)
        {
            if (!Histories.TryGetValue(key, out List<LlmMessage>? history))
                return new List<LlmMessage>();

            return history.TakeLast(maxMessages).ToList();
        }
    }

    public static void AppendPair(string key, string user, string assistant, int maxMessages)
    {
        lock (Gate)
        {
            if (!Histories.TryGetValue(key, out List<LlmMessage>? history))
            {
                history = new List<LlmMessage>();
                Histories[key] = history;
            }

            history.Add(new LlmMessage("user", user));
            history.Add(new LlmMessage("assistant", assistant));

            int excess = history.Count - Math.Max(2, maxMessages);
            if (excess > 0)
                history.RemoveRange(0, excess);
        }
    }

    public static bool TryAppendPair(string key, LlmHistorySnapshot snapshot,
        string user, string assistant, int maxMessages)
    {
        lock (Gate)
        {
            if (snapshot.WorldVersion != worldVersion || snapshot.KeyVersion != KeyVersions.GetValueOrDefault(key))
                return false;
            if (snapshot.Exact && !snapshot.Messages.SequenceEqual(
                Histories.TryGetValue(key, out List<LlmMessage>? current) ? current : Enumerable.Empty<LlmMessage>()))
                return false;
            AppendPair(key, user, assistant, maxMessages);
            return true;
        }
    }

    public static void Reset(string key)
    {
        lock (Gate)
        {
            Histories.Remove(key);
            KeyVersions[key] = KeyVersions.GetValueOrDefault(key) + 1;
        }
    }

    public static void ResetAll()
    {
        lock (Gate)
        {
            Histories.Clear();
            KeyVersions.Clear();
            worldVersion++;
        }
    }

    public static void InvalidatePending()
    {
        lock (Gate)
            worldVersion++;
    }
}
