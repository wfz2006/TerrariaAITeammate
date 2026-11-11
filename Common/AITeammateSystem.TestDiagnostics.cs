using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private TestDiagnosticsRecorder? testDiagnostics;
    private int testRecordingGeneration;

    private TestDiagnosticsRecorder DiagnosticsRecorder => testDiagnostics ??= new TestDiagnosticsRecorder(
        Path.Combine(Main.SavePath, "AITeammate", "Diagnostics"), Mod.Version.ToString());

    internal bool IsTestRecording => testDiagnostics?.IsRecording == true;
    internal bool TestRecordingFailed => testDiagnostics?.StorageFailed == true;
    internal int TestEventCount => testDiagnostics?.SessionEventCount ?? 0;

    internal void ToggleTestRecording()
    {
        if (!CanUseCompanionControls) return;
        try
        {
            if (IsTestRecording)
            {
                DiagnosticsRecorder.Stop();
                return;
            }

            DiagnosticsRecorder.Start();
            testRecordingGeneration++;
            DiagnosticsRecorder.Record("session_context", new
            {
                terrariaVersion = Main.versionNumber,
                tmodLoaderVersion = typeof(Mod).Assembly.GetName().Version?.ToString(),
                modelEnabled = ModContent.GetInstance<AIConfig>().UseLlm,
                combatModelEnabled = ModContent.GetInstance<AIConfig>().UseCombatLlm,
                state = CaptureTestState()
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Main.NewText(CompanionControlsUI.Text("DiagnosticsWriteError"), Color.OrangeRed);
        }
    }

    private void StopTestRecording()
    {
        if (!IsTestRecording) return;
        try { DiagnosticsRecorder.Stop(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Closing the world must succeed even when diagnostics storage is unavailable.
            Mod.Logger.Warn($"Could not stop test recording ({error.GetType().Name}).");
        }
    }

    internal void AddTestBug(string? description = null)
    {
        if (!CanUseCompanionControls) return;
        try
        {
            DiagnosticsRecorder.AddBug(RedactTestNote(description), CaptureTestState());
            Main.NewText(CompanionControlsUI.Text("DiagnosticsBugSaved"), Color.LightGreen);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Main.NewText(CompanionControlsUI.Text("DiagnosticsWriteError"), Color.OrangeRed);
        }
    }

    internal void ExportTestDiagnostics()
    {
        if (!CanUseCompanionControls) return;
        try
        {
            string path = DiagnosticsRecorder.Export();
            Main.NewText(CompanionControlsUI.Text("DiagnosticsExported", path), Color.LightGreen);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Main.NewText(CompanionControlsUI.Text("DiagnosticsExportError"), Color.OrangeRed);
        }
    }

    private static string? RedactTestNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var secrets = new HashSet<string?>(StringComparer.Ordinal)
        {
            ModContent.GetInstance<AIConfig>().ApiKey?.Trim(),
            Environment.GetEnvironmentVariable("AITEAMMATE_API_KEY")?.Trim(),
            GetLlmSettings()?.ApiKey
        };
        foreach (string? secret in secrets)
            if (!string.IsNullOrEmpty(secret))
                note = note.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return note;
    }

    private object CaptureTestState()
    {
        Player player = Main.LocalPlayer;
        NPC? companion = CompanionNpc;
        return new
        {
            action = Executor.CurrentAction.ToString(),
            queuedTasks = Executor.SnapshotTasks().Count,
            paused = Executor.HoldPosition,
            activeGoal = !string.IsNullOrWhiteSpace(activeGoal),
            autoMode,
            playerLife = player.statLife,
            playerMaxLife = player.statLifeMax2,
            companionLife = companion?.life,
            companionMaxLife = companion?.lifeMax,
            offsetXTiles = companion == null ? (int?)null : (int)MathF.Round((companion.Center.X - player.Center.X) / 16f),
            offsetYTiles = companion == null ? (int?)null : (int)MathF.Round((companion.Center.Y - player.Center.Y) / 16f),
            primaryRequests = PrimaryDecisionRequestCount,
            combatRequests = CombatDecisionRequestCount
        };
    }

    private TestModelRequest? BeginTestModelRequest(string phase, long requestId)
    {
        if (!IsTestRecording) return null;
        DiagnosticsRecorder.Record("model_request", new { phase, requestId });
        return new TestModelRequest(testRecordingGeneration, phase, requestId);
    }

    private void FinishTestModelRequest(TestModelRequest? request, Exception? error = null, bool cancelled = false)
    {
        if (request == null || !IsTestRecording || request.Generation != testRecordingGeneration) return;
        string? errorCode = error switch
        {
            LlmOutputLimitException => "output_limit",
            JsonException or KeyNotFoundException or InvalidOperationException => "invalid_response",
            HttpRequestException { StatusCode: not null } => "http_error",
            HttpRequestException => "network_error",
            TimeoutException => "timeout",
            null => null,
            _ => "request_error"
        };
        LlmUsage? usage = request.Usage;
        DiagnosticsRecorder.Record("model_result", new
        {
            phase = request.Phase,
            requestId = request.Id,
            status = cancelled ? "cancelled" : errorCode == null ? "succeeded" : "failed",
            errorCode,
            httpStatus = error is HttpRequestException { StatusCode: { } code } ? (int?)code : null,
            elapsedMs = (long)Stopwatch.GetElapsedTime(request.StartedAt).TotalMilliseconds,
            promptTokens = usage?.PromptTokens,
            completionTokens = usage?.CompletionTokens,
            cacheHitTokens = usage?.CacheHitTokens,
            cacheMissTokens = usage?.CacheMissTokens,
            reasoningTokens = usage?.ReasoningTokens
        });
    }

    private void RecordTestDecision(string source, string status, string code, DecisionTicket? ticket)
    {
        if (IsTestRecording)
            DiagnosticsRecorder.Record("decision", new { source, status, code, requestId = ticket?.Request });
    }

    internal void RecordTestNavigationFailure(AIActionType action, string code)
    {
        if (IsTestRecording)
            DiagnosticsRecorder.Record("navigation_blocked", new { action = action.ToString(), code });
    }

    internal void RecordTestChatFailure()
    {
        if (IsTestRecording)
            DiagnosticsRecorder.Record("chat_error", new { code = "chat_dispatch_failed" });
    }

    private sealed class TestModelRequest(int generation, string phase, long id)
    {
        public int Generation { get; } = generation;
        public string Phase { get; } = phase;
        public long Id { get; } = id;
        public long StartedAt { get; } = Stopwatch.GetTimestamp();
        public LlmUsage? Usage { get; private set; }

        public void CaptureUsage(LlmUsage usage) => Usage = usage;
    }
}
