namespace TerrariaAITeammate.Common;

public readonly record struct DecisionTicket(long World, long Intent, long State, long Request, string Kind);

public sealed class DecisionGate
{
    public long World { get; private set; }
    public long Intent { get; private set; }
    public long State { get; private set; }
    private long request;
    public DecisionTicket Begin(string kind) => new(World, Intent, State, ++request, kind);
    public void NewIntent() { Intent++; request++; }
    public void StateChanged() => State++;
    public void ResetWorld() { World++; Intent++; State = 0; request++; }
    public bool SameIntent(DecisionTicket ticket) => ticket.World == World && ticket.Intent == Intent;
    public bool IsLatestRequest(DecisionTicket ticket) => SameIntent(ticket) && ticket.Request == request;
    public bool IsCurrent(DecisionTicket ticket) => IsLatestRequest(ticket) && ticket.State == State;
}
