using System;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

public sealed record StructureAnchor(int x, int y);
public sealed record StructureSamplingInfo(StructureAnchor requestedAnchor, StructureAnchor initialCenter,
    StructureAnchor sampledCenter, int recenters, int samples, int maxRecenters, bool completeGeometry,
    string reason, string note);
public sealed record CalibratedStructure(StructureSurvey observation, StructureSamplingInfo sampling);

/// <summary>Calibrates only the bounded sensor window, never a construction site or building design.</summary>
public static class StructureWindowCalibration
{
    public static string CalibrateJson(int requestedX, int requestedY, int initialCenterX, int initialCenterY,
        Func<int, int, string> sample, Func<int, int, bool> canObserve, int maxRecenters = 2)
    {
        StructureSurvey Read(int x, int y) => JsonSerializer.Deserialize<StructureSurvey>(sample(x, y))
            ?? throw new ArgumentException("Missing structure sample.");
        return JsonSerializer.Serialize(Calibrate(requestedX, requestedY, initialCenterX, initialCenterY,
            Read, canObserve, maxRecenters));
    }

    public static CalibratedStructure Calibrate(int requestedX, int requestedY, int initialCenterX, int initialCenterY,
        Func<int, int, StructureSurvey> sample, Func<int, int, bool> canObserve, int maxRecenters = 2)
    {
        if (maxRecenters < 0 || maxRecenters > 2) throw new ArgumentOutOfRangeException(nameof(maxRecenters));
        int centerX = initialCenterX, centerY = initialCenterY, attempts = 0, samples = 1;
        StructureSurvey current = sample(centerX, centerY);
        string reason = current.found ? "complete_geometry" : "no_structure_identified";
        while (current.truncated && current.bounds != null && attempts < maxRecenters)
        {
            int nextX = current.bounds.x + (current.bounds.width - 1) / 2;
            int nextY = current.bounds.y + (current.bounds.height - 1) / 2;
            if (nextX == centerX && nextY == centerY) { reason = "bounded_window_cannot_expand"; break; }
            if (!canObserve(nextX, nextY)) { reason = "recenter_out_of_observation_range"; break; }
            attempts++; samples++;
            StructureSurvey next = sample(nextX, nextY);
            if (!next.found || next.bounds == null || !Overlaps(current.bounds, next.bounds))
            { reason = "same_structure_not_confirmed"; break; }
            current = next; centerX = nextX; centerY = nextY;
            reason = current.truncated ? "calibration_budget_exhausted" : "complete_geometry";
        }
        if (current.truncated && attempts == maxRecenters) reason = "calibration_budget_exhausted";
        return new CalibratedStructure(current, new StructureSamplingInfo(new StructureAnchor(requestedX, requestedY),
            new StructureAnchor(initialCenterX, initialCenterY), new StructureAnchor(centerX, centerY), attempts,
            samples, maxRecenters, current.found && !current.truncated, reason,
            "Only the sensor window was recentered. The original building-selection anchor is unchanged. If completeGeometry=false, bounds describe only observed extents and full dimensions remain unknown."));
    }

    private static bool Overlaps(StructureBounds left, StructureBounds right) =>
        (long)left.x < (long)right.x + right.width && (long)right.x < (long)left.x + left.width &&
        (long)left.y < (long)right.y + right.height && (long)right.y < (long)left.y + left.height;
}
