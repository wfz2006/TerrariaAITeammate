using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

internal sealed record ShelterAuditBounds(int x, int y, int width, int height)
{
    public bool Contains(int tx, int ty) => tx >= x && ty >= y && tx < x + width && ty < y + height;
    public bool Contains(ShelterAuditBounds other) => Contains(other.x, other.y) && Contains(other.x + other.width - 1, other.y + other.height - 1);
}
internal sealed record ShelterAuditCounts(int tileCells, int wallCells, int safeWallCells, int structuralCells, int furnitureCells, int doorCells, int platformCells);
internal sealed record ShelterNativeRoom(int queryX, int queryY, bool available, bool nativeValid, ShelterAuditBounds? bounds, string error);
internal sealed record ShelterAuditEdgeContact(int x, int y, int tileType, string tileName, int wallType,
    bool primaryComponent, bool safeWall, bool structural, bool furniture, bool platform, bool door, bool clearance);
internal sealed record ShelterBaselineReport(bool available, int worldId, string epoch, ulong observedAtTick,
    ShelterAuditBounds sampleBounds, int protectedCells, int protectedAirCells, int protectedDoors, bool protectedTouchesBoundary,
    ShelterAuditCounts sampledCounts, IReadOnlyList<ShelterNativeRoom> existingValidRooms, int roomProbeCount,
    bool roomProbesTruncated, IReadOnlyList<string> issues, string scope)
{
    public ShelterAuditBounds? primaryComponentBounds { get; init; }
    public int primaryProtectedCells { get; init; }
    public bool primaryProtectedTouchesBoundary { get; init; }
    public bool primaryCoverageComplete { get; init; }
    public int? primarySeedX { get; init; }
    public int? primarySeedY { get; init; }
    public IReadOnlyList<ShelterAuditEdgeContact> edgeContacts { get; init; } = Array.Empty<ShelterAuditEdgeContact>();
    public string primarySelection { get; init; } = "Nearest original safe housing-wall cell to the supplied audit center, then the eight-connected protected construction/interior/clearance component. Other protected components remain compared inside the wide sample; their outside continuation is unknown.";
}

internal sealed record ShelterAuditDoorKey(int anchorX, int anchorY, int closedType, int style, int height);
internal sealed record ShelterAuditDoor(ShelterAuditDoorKey key, int[] cells, string[] rowAttributes);
internal sealed record ShelterAuditCell(bool hasTile, int tileType, int wallType, byte tileColor, byte wallColor,
    short frameX, short frameY, int wallFrameX, int wallFrameY, int frameNumber, int wallFrameNumber,
    bool half, int slope, bool actuated, bool actuator, bool tileInvisible, bool tileFullbright,
    bool wallInvisible, bool wallFullbright, bool redWire, bool blueWire, bool greenWire, bool yellowWire,
    byte liquidAmount, int liquidType, bool solid, bool platform, bool door, bool furniture, bool structural, bool safeWall, bool frameImportant);

/// <summary>Lossless native original samples. The enclosing world sidecar compresses the fixed-size cell data.</summary>
internal sealed record ShelterAuditCheckpoint(ShelterBaselineReport Report, ShelterAuditCell[] Cells, byte[] Mask,
    byte[] PrimaryMask, ShelterAuditDoor[] Doors)
{
    private sealed record Metadata(ShelterBaselineReport Report, ShelterAuditDoor[] Doors);
    private const int Version = 1;
    private const int CellBytes = 58;
    private const int MaxMetadataBytes = 2 * 1024 * 1024;
    internal const int MaxBytes = 12 * 1024 * 1024;

    public byte[]? Encode()
    {
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new Metadata(Report, Doors));
        if (metadata.Length > MaxMetadataBytes) return null;
        int size = 8 + metadata.Length + Cells.Length * (CellBytes + 2);
        if (size > MaxBytes) return null;
        using var buffer = new MemoryStream(size);
        using var writer = new BinaryWriter(buffer);
        writer.Write(Version);
        writer.Write(metadata.Length);
        writer.Write(metadata);
        writer.Write(Mask);
        writer.Write(PrimaryMask);
        foreach (ShelterAuditCell cell in Cells) WriteCell(writer, cell);
        return buffer.Length == size ? buffer.ToArray() : null;
    }

    public static ShelterAuditCheckpoint? Decode(byte[] data)
    {
        if (data.Length < 8 || data.Length > MaxBytes) return null;
        using var buffer = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(buffer);
        if (reader.ReadInt32() != Version) return null;
        int metadataLength = reader.ReadInt32();
        if (metadataLength is <= 0 or > MaxMetadataBytes || metadataLength > data.Length - 8) return null;
        Metadata? metadata;
        try { metadata = JsonSerializer.Deserialize<Metadata>(reader.ReadBytes(metadataLength)); }
        catch (JsonException) { return null; }
        if (metadata?.Report is not { available: true } report || report.sampleBounds is not { x: >= 2, y: >= 2 } bounds ||
            bounds.width is < 1 or > 512 || bounds.height is < 1 or > 320 || string.IsNullOrWhiteSpace(report.epoch) ||
            report.issues == null || report.sampledCounts == null || report.existingValidRooms == null || report.edgeContacts == null ||
            metadata.Doors == null || report.protectedDoors != metadata.Doors.Length)
            return null;
        int count = bounds.width * bounds.height;
        if (data.Length != 8 + metadataLength + count * (CellBytes + 2)) return null;
        byte[] mask = reader.ReadBytes(count), primary = reader.ReadBytes(count);
        if (mask.Count(value => value != 0) != report.protectedCells || primary.Count(value => value != 0) != report.primaryProtectedCells ||
            report.primaryCoverageComplete && (report.primaryProtectedCells <= 0 ||
                report.primarySeedX is not int x || report.primarySeedY is not int y || !bounds.Contains(x, y)))
            return null;
        var cells = new ShelterAuditCell[count];
        for (int index = 0; index < count; index++)
        {
            if (mask[index] > 3 || primary[index] > 1 || primary[index] != 0 && mask[index] == 0) return null;
            cells[index] = ReadCell(reader);
        }
        var doorKeys = new HashSet<ShelterAuditDoorKey>();
        if (metadata.Doors.Any(door => door?.key == null || !doorKeys.Add(door.key) ||
                door.cells is not { Length: > 0 } || door.cells.Any(index => index < 0 || index >= count || !cells[index].door) ||
                door.rowAttributes == null || door.rowAttributes.Length != door.key.height ||
                door.rowAttributes.Any(string.IsNullOrEmpty)))
            return null;
        return new(report, cells, mask, primary, metadata.Doors);
    }

    private static void WriteCell(BinaryWriter writer, ShelterAuditCell cell)
    {
        writer.Write(cell.hasTile); writer.Write(cell.tileType); writer.Write(cell.wallType);
        writer.Write(cell.tileColor); writer.Write(cell.wallColor); writer.Write(cell.frameX); writer.Write(cell.frameY);
        writer.Write(cell.wallFrameX); writer.Write(cell.wallFrameY); writer.Write(cell.frameNumber); writer.Write(cell.wallFrameNumber);
        writer.Write(cell.half); writer.Write(cell.slope); writer.Write(cell.actuated); writer.Write(cell.actuator);
        writer.Write(cell.tileInvisible); writer.Write(cell.tileFullbright); writer.Write(cell.wallInvisible); writer.Write(cell.wallFullbright);
        writer.Write(cell.redWire); writer.Write(cell.blueWire); writer.Write(cell.greenWire); writer.Write(cell.yellowWire);
        writer.Write(cell.liquidAmount); writer.Write(cell.liquidType); writer.Write(cell.solid); writer.Write(cell.platform);
        writer.Write(cell.door); writer.Write(cell.furniture); writer.Write(cell.structural); writer.Write(cell.safeWall);
        writer.Write(cell.frameImportant);
    }

    private static ShelterAuditCell ReadCell(BinaryReader reader) => new(reader.ReadBoolean(), reader.ReadInt32(), reader.ReadInt32(),
        reader.ReadByte(), reader.ReadByte(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
        reader.ReadInt32(), reader.ReadBoolean(), reader.ReadInt32(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean(),
        reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean(),
        reader.ReadBoolean(), reader.ReadByte(), reader.ReadInt32(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean(),
        reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean());
}
