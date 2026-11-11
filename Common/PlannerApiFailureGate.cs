using System;
using System.Net.Http;

namespace TerrariaAITeammate.Common;

/// <summary>A terminal account/configuration rejection is not repaired by another automatic identical HTTP call.</summary>
public sealed class PlannerApiFailureGate
{
    private string? failedConfiguration;
    public bool IsBlocked => failedConfiguration != null;
    public PlannerApiFailure? LastFailure { get; private set; }

    public bool CanRequest(string configuration)
    {
        if (failedConfiguration == null) return true;
        if (failedConfiguration == configuration) return false;
        failedConfiguration = null;
        return true;
    }

    public bool RecordIfTerminal(Exception error, string configuration)
    {
        if (error is not HttpRequestException { StatusCode: { } status }) return false;
        int code = (int)status;
        if (code is < 400 or >= 500 or 408 or 409 or 425 or 429) return false;
        failedConfiguration = configuration;
        LastFailure = new("terminal_api_rejection", code, false,
            "Goal and completed work are retained. Correct account/configuration or explicitly resume before another attempt; no automatic model switch.");
        return true;
    }

    // Account/configuration failures do not become recoverable when a physical
    // action completes during the HTTP request. Old intent/world/request errors
    // must still be discarded before they can latch the replacement request.
    public bool RecordIfLatestTerminal(Exception error, string configuration, DecisionGate decisions, DecisionTicket ticket)
        => decisions.IsLatestRequest(ticket) && RecordIfTerminal(error, configuration);

    public void Clear() { failedConfiguration = null; LastFailure = null; }
}

public sealed record PlannerApiFailure(string code, int httpStatus, bool automaticRetry, string note);
