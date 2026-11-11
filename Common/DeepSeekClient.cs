using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TerrariaAITeammate.Common;

public enum LlmThinkingMode { ProviderDefault, Enabled, Disabled }
public enum LlmReasoningEffort { ProviderDefault, Low, High }

public record LlmSettings(string ApiKey, string BaseUrl, string Model, int MaxTokens = 1600,
    LlmThinkingMode ThinkingMode = LlmThinkingMode.ProviderDefault,
    LlmReasoningEffort ReasoningEffort = LlmReasoningEffort.ProviderDefault)
{
    public bool UsePhysicsStructureView { get; init; }
    public bool UseStructureOverview { get; init; }
}

public sealed record LlmUsage(int PromptTokens, int CompletionTokens, int CacheHitTokens, int CacheMissTokens)
{
    public int? ReasoningTokens { get; init; }
    public double CacheHitRatio => PromptTokens <= 0 ? 0 : Math.Clamp(CacheHitTokens / (double)PromptTokens, 0, 1);
    public string LogSummary => $"prompt={PromptTokens} hit={CacheHitTokens} miss={CacheMissTokens} " +
        $"completion={CompletionTokens} hitRate={(100 * CacheHitRatio).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}%" +
        (ReasoningTokens.HasValue ? $" reasoning={ReasoningTokens.Value}" : "");
}

public sealed record LlmResponse(string Content, LlmUsage Usage)
{
    public string? ActualModel { get; init; }
}

public sealed class LlmOutputLimitException : JsonException
{
    public LlmUsage Usage { get; }
    public string? ActualModel { get; }

    public LlmOutputLimitException(LlmUsage usage, string? actualModel)
        : base("Planner output reached its token limit; incomplete decisions are not executed.")
    { Usage = usage; ActualModel = actualModel; }
}

public class DeepSeekClient
{
    private static readonly HttpClient SharedHttp = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly HttpClient http;
    private readonly Func<TimeSpan, CancellationTokenSource> createDeadline;

    public DeepSeekClient() : this(SharedHttp) { }

    public DeepSeekClient(HttpClient http) : this(http, duration => new CancellationTokenSource(duration)) { }

    internal DeepSeekClient(HttpClient http, Func<TimeSpan, CancellationTokenSource> createDeadline)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.createDeadline = createDeadline ?? throw new ArgumentNullException(nameof(createDeadline));
    }

    public Task<LlmResponse> ChatJsonAsync(LlmSettings settings, IReadOnlyList<LlmMessage> messages,
        CancellationToken cancellationToken = default)
        => ChatJsonAsync(settings, messages, cancellationToken, null);

    internal async Task<LlmResponse> ChatJsonAsync(LlmSettings settings, IReadOnlyList<LlmMessage> messages,
        CancellationToken cancellationToken, Action<LlmUsage>? onUsage)
    {
        string url = settings.BaseUrl.TrimEnd('/') + "/chat/completions";
        var payload = new Dictionary<string, object>
        {
            ["model"] = settings.Model,
            ["messages"] = messages.Select(message => new { role = message.Role, content = message.Content }).ToArray(),
            ["temperature"] = 0.3,
            ["max_tokens"] = Math.Clamp(settings.MaxTokens, 1400, 32768),
            ["response_format"] = new { type = "json_object" },
            ["stream"] = false
        };
        if (settings.ThinkingMode != LlmThinkingMode.ProviderDefault)
        {
            string mode = settings.ThinkingMode switch
            {
                LlmThinkingMode.Enabled => "enabled",
                LlmThinkingMode.Disabled => "disabled",
                _ => throw new ArgumentOutOfRangeException(nameof(settings.ThinkingMode))
            };
            payload["thinking"] = new { type = mode };
        }
        if (settings.ThinkingMode != LlmThinkingMode.Disabled && settings.ReasoningEffort != LlmReasoningEffort.ProviderDefault)
        {
            payload["reasoning_effort"] = settings.ReasoningEffort switch
            {
                LlmReasoningEffort.Low => "low",
                LlmReasoningEffort.High => "high",
                _ => throw new ArgumentOutOfRangeException(nameof(settings.ReasoningEffort))
            };
        }

        string json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (settings.ApiKey.Contains('\r') || settings.ApiKey.Contains('\n'))
            throw new ArgumentException("API key contains invalid characters.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        TimeSpan requestTimeout = TimeSpan.FromSeconds(settings.ThinkingMode == LlmThinkingMode.Enabled
            ? settings.MaxTokens > 8192 ? 180 : 90
            : 45);
        using var deadline = createDeadline(requestTimeout);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, requestCancellation.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(requestCancellation.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"API {(int)response.StatusCode}: {Shorten(Redact(body, settings.ApiKey), 300)}",
                    null, response.StatusCode);

            using JsonDocument doc = JsonDocument.Parse(body);
            LlmUsage? reportedUsage = ParseUsage(doc.RootElement);
            if (reportedUsage != null) onUsage?.Invoke(reportedUsage);
            JsonElement choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0)
                throw new JsonException("API returned no choices");

            LlmUsage usage = reportedUsage ?? new LlmUsage(0, 0, 0, 0);
            string? actualModel = ParseActualModel(doc.RootElement, settings.ApiKey);
            if (choices[0].TryGetProperty("finish_reason", out JsonElement finish) &&
                finish.ValueKind == JsonValueKind.String && finish.GetString() == "length")
                throw new LlmOutputLimitException(usage, actualModel);

            string content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(content))
                throw new JsonException("API returned empty content");

            return new LlmResponse(content, usage)
            {
                ActualModel = actualModel
            };
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"LLM request timed out after {requestTimeout.TotalSeconds:0} seconds.", exception);
        }
    }

    private static LlmUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        int prompt = ReadInt(usage, "prompt_tokens");
        int completion = ReadInt(usage, "completion_tokens");
        int hit = ReadInt(usage, "prompt_cache_hit_tokens");
        int miss = ReadInt(usage, "prompt_cache_miss_tokens");

        if (usage.TryGetProperty("prompt_tokens_details", out JsonElement details) &&
            details.ValueKind == JsonValueKind.Object)
        {
            hit = Math.Max(hit, ReadInt(details, "cached_tokens"));
        }

        hit = Math.Clamp(hit, 0, Math.Max(0, prompt));
        if (!usage.TryGetProperty("prompt_cache_miss_tokens", out _))
            miss = Math.Max(0, prompt - hit);
        int? reasoning = null;
        if (usage.TryGetProperty("completion_tokens_details", out JsonElement completionDetails) &&
            completionDetails.ValueKind == JsonValueKind.Object)
            reasoning = ReadOptionalCount(completionDetails, "reasoning_tokens");
        reasoning ??= ReadOptionalCount(usage, "reasoning_tokens");
        return new LlmUsage(prompt, completion, hit, Math.Max(0, miss)) { ReasoningTokens = reasoning };
    }

    private static int? ReadOptionalCount(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int result) && result >= 0 ? result : null;

    private static string? ParseActualModel(JsonElement root, string apiKey)
    {
        if (!root.TryGetProperty("model", out JsonElement model) || model.ValueKind != JsonValueKind.String)
            return null;
        string safe = Redact(new string((model.GetString() ?? "").Where(c => !char.IsControl(c)).ToArray()), apiKey);
        return string.IsNullOrWhiteSpace(safe) ? null : Shorten(safe, 128);
    }

    private static int ReadInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result
            : 0;
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max];

    private static string Redact(string text, string key) => string.IsNullOrEmpty(key)
        ? text : text.Replace(key, "[redacted]", StringComparison.Ordinal);
}
