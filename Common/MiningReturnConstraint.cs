using System;
using Microsoft.Xna.Framework;

namespace TerrariaAITeammate.Common;

public sealed record MiningReturnAnchor(Vector2 Center, float FeetY, bool Grounded, MiningApproachStand? Stand,
    string Epoch, int WorldId, ulong ObservedAtTick)
{
    public bool IsPreview { get; init; }
}
public sealed record MiningReturnCheck(string Status, string Diagnostic, int? WaypointCount = null, MiningApproachStand? Endpoint = null)
{
    public ulong? ObservedAtTick { get; init; }
    public bool ProvenInCurrentGraph => Status is "found" or "already_at_task_origin";
}

/// <summary>A physical Mine dispatch keeps its initial anchor through per-cell reselection. Never starts another task.</summary>
public sealed class MiningTaskReturnState
{
    private object? owner;
    public MiningReturnAnchor? Anchor { get; private set; }
    public MiningReturnAnchor CaptureOnce(object actionToken, Func<MiningReturnAnchor> capture)
    {
        if (!ReferenceEquals(owner, actionToken)) { MiningReturnAnchor captured = capture(); owner = actionToken; Anchor = captured; }
        return Anchor!;
    }
    public bool IsOwner(object? actionToken) => actionToken != null && ReferenceEquals(owner, actionToken);
    public void Reset() { owner = null; Anchor = null; }
}
