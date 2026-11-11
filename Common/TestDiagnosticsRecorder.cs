using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TerrariaAITeammate.Common;

internal sealed class TestDiagnosticsRecorder
{
    private const int MaximumJournalBytes = 2 * 1024 * 1024;
    private const int MaximumEventBytes = 64 * 1024;
    private const int MaximumDescriptionLength = 500;

    private static readonly object StorageLock = new();
    private static readonly JsonSerializerOptions EventJson = new() { MaxDepth = 24 };
    private readonly string directory;
    private readonly string currentJournal;
    private readonly string previousJournal;
    private readonly string modVersion;
    private readonly string sessionId = Guid.NewGuid().ToString("N");

    private bool isRecording;
    private bool storageFailed;
    private int sessionEventCount;

    public TestDiagnosticsRecorder(string directory, string modVersion)
    {
        this.directory = Path.GetFullPath(directory);
        this.modVersion = modVersion;
        currentJournal = Path.Combine(this.directory, "events.jsonl");
        previousJournal = Path.Combine(this.directory, "events.previous.jsonl");
    }

    public bool IsRecording { get { lock (StorageLock) return isRecording; } }
    public bool StorageFailed { get { lock (StorageLock) return storageFailed; } }
    public int SessionEventCount { get { lock (StorageLock) return sessionEventCount; } }

    public void Start()
    {
        lock (StorageLock)
        {
            if (isRecording)
                return;

            Append("recording_started", null);
            isRecording = true;
        }
    }

    public void Stop()
    {
        lock (StorageLock)
        {
            if (!isRecording)
                return;

            isRecording = false;
            Append("recording_stopped", null);
        }
    }

    // The caller supplies an allowlisted payload. Automatic recording can fail without interrupting gameplay.
    public void Record(string kind, object data)
    {
        lock (StorageLock)
        {
            if (!isRecording)
                return;

            try
            {
                Append(kind, data);
            }
            catch (Exception error) when (IsStorageError(error))
            {
                // Append already stopped recording and set the status for the UI.
            }
        }
    }

    public void AddBug(string? description, object snapshot)
    {
        description = description?.Trim() ?? string.Empty;
        if (description.Length > MaximumDescriptionLength)
        {
            int length = MaximumDescriptionLength;
            if (char.IsHighSurrogate(description[length - 1]))
                length--;
            description = description[..length];
        }

        lock (StorageLock)
            Append("bug_report", new { description, snapshot });
    }

    public string Export()
    {
        lock (StorageLock)
        {
            string exportDirectory = Path.Combine(directory, "Exports");
            Directory.CreateDirectory(exportDirectory);
            DateTime now = DateTime.UtcNow;
            string timestamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(exportDirectory, $"xiaoxing-test-{timestamp}-{Guid.NewGuid():N}.json");
            string temporaryPath = path + ".tmp";

            try
            {
                using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("schema", "terraria-ai-teammate-test-diagnostics");
                    writer.WriteNumber("schemaVersion", 1);
                    writer.WriteString("exportedAtUtc", now);
                    writer.WriteString("modVersion", modVersion);
                    writer.WriteString("sessionId", sessionId);
                    writer.WriteStartArray("events");
                    int skippedLines = WriteJournal(writer, previousJournal) + WriteJournal(writer, currentJournal);
                    writer.WriteEndArray();
                    writer.WriteNumber("skippedLines", skippedLines);
                    writer.WriteEndObject();
                }

                File.Move(temporaryPath, path);
                return path;
            }
            catch
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception error) when (IsStorageError(error))
                {
                    // Preserve the original export error.
                }
                throw;
            }
        }
    }

    private void Append(string kind, object? data)
    {
        RecordedEvent entry = new(DateTime.UtcNow, sessionId, kind, modVersion, data);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(entry, EventJson);
        if (payload.Length > MaximumEventBytes)
            payload = JsonSerializer.SerializeToUtf8Bytes(entry with { data = new { omitted = "event_too_large" } }, EventJson);
        if (payload.Length > MaximumEventBytes)
            throw new ArgumentException("Diagnostic event metadata exceeds the storage limit.");

        try
        {
            Directory.CreateDirectory(directory);
            FileInfo active = new(currentJournal);
            if (active.Exists && active.Length + payload.Length + 2 > MaximumJournalBytes)
                File.Move(currentJournal, previousJournal, overwrite: true);

            using (FileStream stream = new(currentJournal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n')
                        stream.WriteByte((byte)'\n');
                }

                stream.Write(payload);
                stream.WriteByte((byte)'\n');
            }

            sessionEventCount++;
            storageFailed = false;
        }
        catch (Exception error) when (IsStorageError(error))
        {
            isRecording = false;
            storageFailed = true;
            throw;
        }
    }

    private static int WriteJournal(Utf8JsonWriter writer, string path)
    {
        StreamReader input;
        try
        {
            input = File.OpenText(path);
        }
        catch (FileNotFoundException)
        {
            return 0;
        }

        using StreamReader journal = input;
        int skippedLines = 0;
        while (journal.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using JsonDocument entry = JsonDocument.Parse(line);
                JsonElement root = entry.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kind", out JsonElement kind) &&
                    kind.ValueKind == JsonValueKind.String && root.TryGetProperty("sessionId", out JsonElement session) &&
                    session.ValueKind == JsonValueKind.String && root.TryGetProperty("data", out _))
                    root.WriteTo(writer);
                else
                    skippedLines++;
            }
            catch (JsonException)
            {
                skippedLines++;
            }
        }
        return skippedLines;
    }

    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException;

    private sealed record RecordedEvent(DateTime utc, string sessionId, string kind, string modVersion, object? data);
}
