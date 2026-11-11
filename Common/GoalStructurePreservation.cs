using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

public sealed record GoalPreservationIdentity(string baseline_id, int worldId, string epoch,
    long goalStartSequence, string sourceGoal, string? sourcePlayerRequest, ulong capturedAtTick,
    int centerX, int centerY, string origin);

public sealed record GoalPreservationComparison(bool Available, bool CoverageComplete, bool Preserved,
    int DiffCount, object Summary, object? RawReport = null);

public sealed record GoalPreservationCapture(object Summary, Func<GoalPreservationComparison> Compare,
    object? RawBaseline = null, Func<GoalCondition, GoalNewHousingComparison>? CompareNewHousing = null,
    Func<int, int, int, int, GoalConstructionProtection>? ReadConstructionProtection = null)
{
    internal Func<byte[]?>? SaveOriginal { get; init; }
}

internal sealed record GoalPreservationSaveSource(GoalPreservationIdentity Identity, Func<byte[]?> EncodeOriginal);

public sealed record GoalProtectionRegion(int X, int Y, int Width, int Height);
public sealed record GoalProtectedCell(int X, int Y, string Reason);

/// <summary>Original goal-mask facts only, never a route, placement or housing approval.</summary>
public sealed record GoalConstructionProtection(bool Required, bool Available, bool Covered, bool? Allowed,
    int ProtectedCellCount, IReadOnlyList<GoalProtectedCell> ProtectedHits, int ProtectedHitsOmitted, string Reason)
{
    public GoalPreservationIdentity? Baseline { get; init; }
    public GoalProtectionRegion? Region { get; init; }
    public GoalProtectionRegion? SampleBounds { get; init; }
    public ulong ObservedAtTick { get; init; }
}

public sealed record GoalNewHousingComparison(bool Available, bool CoverageComplete, bool ValidNew,
    object Summary, object? RawReport = null);

/// <summary>A single immutable native baseline per goal. The model cannot recapture or select a smaller scope.</summary>
public sealed class GoalStructurePreservation
{
    private GoalPreservationIdentity? identity;
    private GoalPreservationIdentity? restoredFrom;
    private GoalPreservationCapture? baseline;
    private GoalPreservationComparison? latest;
    private ulong? latestTick;
    private GoalNewHousingComparison? latestNewHousing;
    private ulong? latestNewHousingTick;
    private string error = "No baseline was captured for this goal.";

    public void Clear()
    {
        identity = restoredFrom = null; baseline = null; latest = null; latestTick = null;
        latestNewHousing = null; latestNewHousingTick = null;
        error = "No baseline was captured for this goal.";
    }

    public GoalConstructionProtection ReadConstructionProtection(bool required, int x, int y, int width, int height,
        int worldId, string epoch, long sequence, string goal, ulong observedAtTick)
    {
        GoalConstructionProtection Unknown(string reason) => new(required, false, false, null, 0,
            Array.Empty<GoalProtectedCell>(), 0, reason)
            { Region = new(x, y, width, height), ObservedAtTick = observedAtTick };
        if (!required) return Unknown("No structure_preserved condition requires this check; preservation was not verified.");
        if (width is < 1 or > 97 || height is < 1 or > 81 || x < 0 || y < 0 ||
            (long)x + width - 1 > int.MaxValue || (long)y + height - 1 > int.MaxValue)
            return Unknown("Construction protection requires a bounded positive rectangle of at most 97x81 cells.");
        if (!Matches(worldId, epoch, sequence, goal) || baseline?.ReadConstructionProtection == null || observedAtTick < identity!.capturedAtTick)
            return Unknown("No immutable construction-protection baseline belongs to this exact world load, goal and tick; it cannot be recaptured.");
        try
        {
            GoalConstructionProtection reading = baseline.ReadConstructionProtection(x, y, width, height);
            return reading with
            {
                Required = true, Baseline = identity, Region = new(x, y, width, height), ObservedAtTick = observedAtTick,
                Allowed = reading.Available && reading.Covered ? reading.Allowed : null
            };
        }
        catch (Exception exception)
        { return Unknown("Native construction protection unavailable: " + exception.GetType().Name); }
    }

    public GoalNativeConditionResult ReadNewHousing(GoalCondition condition, int worldId, string epoch,
        long sequence, string goal, ulong observedAtTick)
    {
        if (!GoalCompletionState.TryNormalize(condition, out GoalCondition normalized, out string invalid) || normalized.Type != "new_housing_valid")
            return new(null, "Invalid new-housing condition: " + invalid);
        if (!normalized.X.HasValue)
            return new(null, "New housing location is unbound; neither an existing room nor a successful Build label proves it.");
        if (!Matches(worldId, epoch, sequence, goal) || baseline?.CompareNewHousing == null || observedAtTick < identity!.capturedAtTick)
            return new(null, "No original new-housing baseline belongs to this exact world load and goal; it cannot be recaptured after construction or borrowed from another goal.");
        try
        {
            latestNewHousing = baseline.CompareNewHousing(normalized);
            latestNewHousingTick = observedAtTick;
            bool? met = latestNewHousing.Available && latestNewHousing.CoverageComplete ? latestNewHousing.ValidNew : null;
            return new(met, JsonSerializer.Serialize(new
            {
                identity!.baseline_id, identity.worldId, identity.epoch, identity.goalStartSequence,
                identity.capturedAtTick, observedAtTick, observation = latestNewHousing.Summary,
                limitation = "New housing must be native-valid inside the immutable goal baseline and pass its original-empty/new-cell comparison; outside coverage is unknown."
            }));
        }
        catch (Exception exception)
        {
            latestNewHousing = null; latestNewHousingTick = observedAtTick;
            return new(null, "Native new-housing comparison unavailable: " + exception.GetType().Name);
        }
    }

    public bool Begin(int worldId, string epoch, long goalStartSequence, string sourceGoal,
        string? sourcePlayerRequest, ulong capturedAtTick, int centerX, int centerY, string origin,
        Func<GoalPreservationCapture> capture)
    {
        if (Matches(worldId, epoch, goalStartSequence, sourceGoal)) return false;
        Clear();
        identity = new(Guid.NewGuid().ToString("N"), worldId, epoch, goalStartSequence,
            sourceGoal, sourcePlayerRequest, capturedAtTick, centerX, centerY, origin);
        if (string.IsNullOrWhiteSpace(epoch) || string.IsNullOrWhiteSpace(sourceGoal) || goalStartSequence < 0)
        { error = "World/goal identity is unavailable; no late replacement baseline will be captured."; return true; }
        try { baseline = capture(); error = ""; }
        catch (Exception exception)
        { error = "Native baseline capture unavailable: " + exception.GetType().Name + "; no late replacement will be captured."; }
        return true;
    }

    private bool Matches(int worldId, string epoch, long sequence, string goal)
        => identity != null && identity.worldId == worldId && identity.epoch == epoch &&
            identity.goalStartSequence == sequence && identity.sourceGoal == goal;

    internal GoalPreservationSaveSource? SavedOriginal(int worldId, string epoch, long sequence, string goal)
        => Matches(worldId, epoch, sequence, goal) && baseline?.SaveOriginal is { } encode
            ? new(restoredFrom ?? identity!, encode)
            : null;

    internal bool RestoreOriginal(GoalPreservationIdentity original, int worldId, string epoch, long sequence, string goal,
        string? request, ulong observedAtTick, GoalPreservationCapture capture)
    {
        if (identity != null || original.worldId != worldId || string.IsNullOrWhiteSpace(epoch) ||
            string.IsNullOrWhiteSpace(original.epoch) || original.epoch == epoch || original.sourceGoal != goal ||
            original.sourcePlayerRequest != request || sequence < 0 || !Guid.TryParseExact(original.baseline_id, "N", out _) ||
            capture.SaveOriginal == null)
            return false;
        restoredFrom = original;
        identity = original with
        {
            epoch = epoch, goalStartSequence = sequence, capturedAtTick = observedAtTick, origin = "saved_original_baseline"
        };
        baseline = capture;
        error = "";
        return true;
    }

    public GoalNativeConditionResult Read(int worldId, string epoch, long sequence, string goal, ulong observedAtTick)
    {
        if (!Matches(worldId, epoch, sequence, goal) || baseline == null || observedAtTick < identity!.capturedAtTick)
            return new(null, !Matches(worldId, epoch, sequence, goal)
                ? "No preservation baseline belongs to this exact world load and goal; another goal's snapshot cannot be reused."
                : observedAtTick < identity!.capturedAtTick ? "Observation precedes this baseline capture." : error);
        try
        {
            // Deliberately re-read on every evaluation, including evaluations in the same
            // tick: another action may already have changed a protected cell this tick.
            latest = baseline.Compare(); latestTick = observedAtTick;
            bool? met = latest.Available && latest.CoverageComplete
                ? latest.Preserved && latest.DiffCount == 0 : null;
            return new(met, JsonSerializer.Serialize(new
            {
                identity!.baseline_id, identity.worldId, identity.epoch, identity.goalStartSequence,
                identity.capturedAtTick, observedAtTick, observation = latest.Summary,
                limitation = "Only this original runtime baseline and its declared protected scope are compared; outside continuation is unknown."
            }));
        }
        catch (Exception exception)
        {
            latest = null; latestTick = observedAtTick;
            error = "Native preservation comparison unavailable: " + exception.GetType().Name;
            return new(null, error);
        }
    }

    public object Snapshot(int worldId, string epoch, long sequence, string goal) => new
    {
        boundToCurrentGoal = Matches(worldId, epoch, sequence, goal),
        baseline = identity, captured = baseline != null, observation = baseline?.Summary,
        latestObservedAtTick = latestTick, latestComparison = latest?.Summary,
        latestNewHousingObservedAtTick = latestNewHousingTick, latestNewHousing = latestNewHousing?.Summary,
        error, recaptureAllowed = false,
        scopePolicy = "The model cannot choose another baseline or shrink its scope. Full native diagnostics are retained separately; summary samples may be truncated."
    };

    /// <summary>Explicit diagnostics only; never part of the ordinary planning frame.</summary>
    public object DebugSnapshot() => new
    {
        baseline = identity, restoredFrom, nativeBaseline = baseline?.RawBaseline,
        latestObservedAtTick = latestTick, nativeComparison = latest?.RawReport,
        latestNewHousingObservedAtTick = latestNewHousingTick, nativeNewHousing = latestNewHousing?.RawReport, error
    };
}
