using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    private const string GoalCheckpointTag = "agentGoalCheckpoint";
    private const string GoalProtectionCheckpointTag = "agentGoalOriginalProtection";
    private sealed record GoalSaveState(GoalResumeCheckpoint Goal, GoalPreservationSaveSource? OriginalProtection);
    private GoalSaveState? goalCheckpoint;
    private SavedGoalHistory? restoredGoalHistory;
    private bool restoredGoalAwaitingContinue;

    private static Guid ActiveWorldFileId => Main.ActiveWorldFileData?.UniqueId ?? Guid.Empty;

    private void RefreshGoalCheckpoint()
    {
        // Publish an immutable copy on the game thread. World saving may read it
        // on another thread and must not enumerate the live plan or journal.
        var checkpoint = goalPlanningState.HasGoal && goalStatus is "active" or "paused" or "waiting_for_player"
            ? GoalResumeCheckpoint.Capture(Main.worldID, ActiveWorldFileId, activeGoal, goalCriteria, goalCompletionAction,
                goalCompletionTarget, goalStatus, goalPlanningState, restoredGoalHistory)
            : null;
        GoalPreservationSaveSource? protection = checkpoint != null &&
            (RequiresStructurePreservation || checkpoint.Conditions.Any(condition => condition.Type == "new_housing_valid"))
            ? PreservationEvidence.SavedOriginal(Main.worldID, WorldKnowledgeObserver.LoadEpoch, goalStartSequence, activeGoal)
            : null;
        Volatile.Write(ref goalCheckpoint, checkpoint == null ? null : new GoalSaveState(checkpoint, protection));
    }

    private void ClearGoalCheckpoint()
    {
        restoredGoalHistory = null;
        restoredGoalAwaitingContinue = false;
        Volatile.Write(ref goalCheckpoint, null);
    }

    private void SaveAgentGoal(TagCompound tag)
    {
        GoalSaveState? checkpoint = Volatile.Read(ref goalCheckpoint);
        if (checkpoint == null || checkpoint.Goal.WorldId != Main.worldID || checkpoint.Goal.WorldFileId != ActiveWorldFileId) return;
        string? data = checkpoint.Goal.Encode();
        if (data == null)
        {
            Mod.Logger.Warn("Unfinished goal did not fit the world checkpoint format.");
            return;
        }
        byte[] goal = Encoding.UTF8.GetBytes(data);
        tag[GoalCheckpointTag] = goal;
        if (checkpoint.OriginalProtection is not { } original) return;
        try
        {
            byte[]? audit = original.EncodeOriginal();
            if (audit == null) return;
            byte[] identity = JsonSerializer.SerializeToUtf8Bytes(original.Identity);
            byte[] content = GoalProtectionContentHash();
            tag[GoalProtectionCheckpointTag] = new TagCompound
            {
                ["version"] = 1,
                ["identity"] = identity,
                ["content"] = content,
                ["audit"] = audit,
                ["checksum"] = GoalProtectionChecksum(goal, identity, content, audit)
            };
        }
        catch (Exception exception)
        {
            Mod.Logger.Warn("Could not save original goal protection: " + exception.GetType().Name);
        }
    }

    private void LoadAgentGoal(TagCompound tag)
    {
        if (!worldReady || !tag.TryGet(GoalCheckpointTag, out byte[] data)) return;
        GoalResumeCheckpoint? checkpoint = GoalResumeCheckpoint.Decode(Encoding.UTF8.GetString(data), Main.worldID, ActiveWorldFileId);
        if (checkpoint == null) return;
        SetActiveGoal(checkpoint.Objective, checkpoint.PlayerRequest, capturePreservation: false);
        checkpoint.RestorePlanning(goalPlanningState, goalStartSequence);
        RestoreAgentGoalProtection(tag, data, checkpoint);
        goalCriteria = checkpoint.Criteria;
        goalCompletionAction = checkpoint.CompletionAction;
        goalCompletionTarget = checkpoint.CompletionTarget;
        restoredGoalHistory = checkpoint.History;
        restoredGoalAwaitingContinue = true;
        goalStatus = "paused";
        Executor.Clear(true);
        lastGoalResult = "从存档恢复了未完成目标。历史仅供参考，继续前须重新核对当前世界、背包和任务条件。";
        RefreshGoalCheckpoint();
        TestLog("goal checkpoint restored paused; awaiting explicit continue");
    }

    private void RestoreAgentGoalProtection(TagCompound tag, byte[] goal, GoalResumeCheckpoint checkpoint)
    {
        try
        {
            if (!tag.TryGet(GoalProtectionCheckpointTag, out TagCompound saved) || saved.GetInt("version") != 1 ||
                !saved.TryGet("identity", out byte[] identity) || identity.Length is 0 or > 131072 ||
                !saved.TryGet("audit", out byte[] audit) || audit.Length > ShelterAuditCheckpoint.MaxBytes ||
                !saved.TryGet("content", out byte[] content) || !content.AsSpan().SequenceEqual(GoalProtectionContentHash()) ||
                !saved.TryGet("checksum", out byte[] checksum) ||
                !checksum.AsSpan().SequenceEqual(GoalProtectionChecksum(goal, identity, content, audit)))
                return;
            if (JsonSerializer.Deserialize<GoalPreservationIdentity>(identity) is { } original &&
                GoalWorldObserver.RestorePreservation(this, original, audit, checkpoint.Objective, checkpoint.PlayerRequest))
                TestLog("original goal protection restored; live world comparison required");
        }
        catch (Exception exception)
        {
            Mod.Logger.Warn("Saved original goal protection unavailable: " + exception.GetType().Name);
        }
    }

    private static byte[] GoalProtectionContentHash() =>
        SHA256.HashData(Encoding.UTF8.GetBytes(ShelterWorldAudit.CheckpointContentIdentity()));

    private static byte[] GoalProtectionChecksum(byte[] goal, byte[] identity, byte[] content, byte[] audit)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(SHA256.HashData(goal));
        hash.AppendData(SHA256.HashData(identity));
        hash.AppendData(content);
        hash.AppendData(audit);
        return hash.GetHashAndReset();
    }

    private bool TrySpawnRestoredCompanion(Player player)
    {
        const int radius = 8;
        const int width = CompanionMovement.Width, height = CompanionMovement.Height;
        Vector2 preferred = player.Center + new Vector2(-80f * player.direction, 0f);
        int centerX = (int)(player.Center.X / 16f), standRow = CompanionMovement.StandRow(player.Bottom.Y);
        float bestDistance = float.PositiveInfinity;
        TileStandPose? chosen = null;
        for (int x = centerX - radius; x <= centerX + radius; x++)
        for (int y = standRow - radius; y <= standRow + radius; y++)
        {
            var tile = new Point(x, y);
            if (!TilePathfinder.TryGetStandPose(tile, out TileStandPose pose) ||
                Vector2.DistanceSquared(pose.Center, player.Center) > radius * radius * 16 * 16)
                continue;
            float distance = Vector2.DistanceSquared(pose.Center, preferred);
            if (distance >= bestDistance || !TilePathfinder.IsStableStand(x, y)) continue;
            Vector2 corner = pose.Center - new Vector2(width / 2f, height / 2f);
            var body = new Rectangle((int)corner.X, (int)corner.Y, width, height);
            if (body.Intersects(player.Hitbox) || Main.npc.Any(npc => npc.active && body.Intersects(npc.Hitbox)) ||
                Collision.SolidCollision(corner, width, height) ||
                !Collision.CanHitLine(corner, width, height, player.position, player.width, player.height))
                continue;
            chosen = pose;
            bestDistance = distance;
        }
        if (chosen is not { } stand) return false;
        if (Loadout.EnsureStarterTools())
            TestLog("starter tools granted on spawn: " + Loadout.Summary());
        return SpawnNewCompanion(new Vector2(stand.Center.X, stand.Bottom), stand.Center,
            ModContent.NPCType<Content.NPCs.AICompanion>()) is { active: true };
    }

    private bool TryContinueRestoredGoal(Player player, string message, bool fromChat)
    {
        if (!restoredGoalAwaitingContinue || !GoalResumeCheckpoint.IsExplicitContinue(message)) return false;
        if (!fromChat) Main.NewText($"你：{message}", Color.White);
        Memory.Add($"玩家说：{message}");

        if (GetLlmSettings() == null)
            LastReply = "原任务已恢复并暂停。启用 LLM 后说“继续”，我会先核对现场。";
        else if (!player.active || player.dead)
            LastReply = "原任务仍暂停，等你回到世界后再继续。";
        else if (CompanionNpc == null && !TrySpawnRestoredCompanion(player))
            LastReply = "附近没有合适的落脚点，我先保持暂停。请到能站稳的地方再说“继续”。";
        else
        {
            InvalidateAgentIntent();
            manualControlActive = false;
            restoredGoalAwaitingContinue = false;
            Executor.SetHold(false);
            Executor.ClearForGoalReview();
            goalPlanningState.MarkPaused(false);
            goalWaitingForPlayer = false;
            goalReviewBlocked = false;
            goalReviewPending = true;
            goalStatus = "active";
            lastGoalResult = "玩家已要求继续存档目标。请依据本次实际背包、双方位置和当前世界复盘；存档历史不证明当前库存、保护条件或实体状态。";
            LastReply = "我先核对现场，再继续原任务。";
            RefreshGoalCheckpoint();
        }
        Main.NewText("小星：" + LastReply, Color.LightBlue);
        Memory.Add("小星：" + LastReply);
        return true;
    }
}
