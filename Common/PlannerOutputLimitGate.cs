namespace TerrariaAITeammate.Common;

/// <summary>Preserves a known output-limit failure without repeatedly billing an unchanged request.</summary>
public sealed class PlannerOutputLimitGate
{
    private string? failedConfiguration;
    private long failedStateVersion;
    public bool IsBlocked => failedConfiguration != null;
    public PlannerOutputLimitFailure? LastFailure { get; private set; }

    public bool CanRequest(string configuration, long stateVersion = 0)
    {
        if (failedConfiguration == null) return true;
        if (failedConfiguration == configuration && failedStateVersion == stateVersion) return false;
        // A configuration change or actual task outcome permits a new attempt, while its frame explains
        // the preceding failure. A successful decision or new intent clears that fact.
        failedConfiguration = null;
        return true;
    }

    public void Record(string configuration, int maxTokens, int completionTokens, int? reasoningTokens,
        long stateVersion = 0)
    {
        failedConfiguration = configuration;
        failedStateVersion = stateVersion;
        LastFailure = new("output_limit", maxTokens, completionTokens, reasoningTokens, stateVersion);
    }

    public void Clear() { failedConfiguration = null; LastFailure = null; }
}

public sealed record PlannerOutputLimitFailure(string code, int maxResponseTokens,
    int completionTokens, int? reasoningTokens, long stateVersion);
