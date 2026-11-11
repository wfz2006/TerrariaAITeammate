using System;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

internal sealed partial class ShelterWorldAudit
{
    private static ShelterAuditBounds BoundsAround(int centerX, int centerY)
    {
        int width = Math.Min(Width, Main.maxTilesX - 4), height = Math.Min(Height, Main.maxTilesY - 4);
        return new(Math.Clamp(centerX - width / 2, 2, Main.maxTilesX - 2 - width),
            Math.Clamp(centerY - height / 2, 2, Main.maxTilesY - 2 - height), width, height);
    }

    private ShelterWorldAudit(ShelterAuditCheckpoint saved)
    {
        originalReport = saved.Report;
        worldId = Main.worldID;
        epoch = WorldKnowledgeObserver.LoadEpoch;
        bounds = originalReport.sampleBounds;
        baseline = saved.Cells;
        mask = saved.Mask;
        primaryMask = saved.PrimaryMask.Select(value => value != 0).ToArray();
        baselineDoors = saved.Doors.ToDictionary(door => door.key);
        baselineIssues = originalReport.issues.ToList();
        BaselineReport = originalReport with { epoch = epoch, observedAtTick = Main.GameUpdateCount };
    }

    public byte[]? SaveOriginal() => new ShelterAuditCheckpoint(originalReport, baseline, mask,
        primaryMask.Select(value => value ? (byte)1 : (byte)0).ToArray(), baselineDoors.Values.ToArray()).Encode();

    public static ShelterWorldAudit? FromOriginal(GoalPreservationIdentity identity, byte[] payload)
    {
        if (identity.worldId != Main.worldID || !WorldGen.InWorld(identity.centerX, identity.centerY, 2) ||
            string.IsNullOrEmpty(WorldKnowledgeObserver.LoadEpoch))
            return null;
        ShelterAuditCheckpoint? saved = ShelterAuditCheckpoint.Decode(payload);
        if (saved == null || saved.Report.worldId != identity.worldId || saved.Report.epoch != identity.epoch ||
            saved.Report.observedAtTick != identity.capturedAtTick || saved.Report.sampleBounds != BoundsAround(identity.centerX, identity.centerY) ||
            saved.Cells.Any(cell => cell.tileType < 0 || cell.tileType >= TileLoader.TileCount ||
                cell.wallType < 0 || cell.wallType >= WallLoader.WallCount))
            return null;
        return new(saved);
    }

    // Native IDs can change when the runtime or loaded mod content changes.
    public static string CheckpointContentIdentity() => typeof(Main).Module.ModuleVersionId.ToString("N") + "\n" +
        string.Join("\n", ModLoader.Mods.Select(mod => $"{mod.Name}:{mod.Version}")) + "\n" +
        string.Join("\n", Enumerable.Range(TileID.Count, TileLoader.TileCount - TileID.Count).Select(id => TileLoader.GetTile(id)?.FullName)) + "\n" +
        string.Join("\n", Enumerable.Range(WallID.Count, WallLoader.WallCount - WallID.Count).Select(id => WallLoader.GetWall(id)?.FullName));
}
