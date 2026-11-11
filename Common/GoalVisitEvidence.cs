using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrariaAITeammate.Common;

/// <summary>Goal-scoped native arrivals. Never reconstructed from model text or query success.</summary>
public sealed class GoalVisitEvidence
{
    private const int Capacity = 64;
    private readonly List<GoalVisitRecord> visits = new();
    private string epoch = "";
    private long boundary;

    public void Begin(string worldEpoch, long goalStartSequence)
    {
        visits.Clear(); epoch = worldEpoch; boundary = goalStartSequence;
    }

    public void Clear() => Begin("", 0);

    public bool Record(string worldEpoch, long goalStartSequence, long sequence, AIPlan? task,
        bool succeeded, GoalVisitPose pose)
    {
        if (epoch.Length == 0 || epoch != worldEpoch || boundary != goalStartSequence || sequence <= boundary ||
            !succeeded || task?.Action != AIActionType.Explore || task.X is not int x || task.Y is not int y ||
            !float.IsFinite(pose.CenterX) || !float.IsFinite(pose.Bottom) || !float.IsFinite(pose.VelocityY) ||
            !float.IsFinite(pose.StandBottom) || !float.IsFinite(pose.StandCenterX ?? (pose.StandX * 16f + 8)) ||
            !CompanionMovement.ReachedStand(pose.CenterX, pose.Bottom, pose.VelocityY, pose.Grounded,
                pose.StandX, pose.StandRow, pose.StandBottom, pose.StandCenterX)) return false;
        visits.RemoveAll(v => v.X == x && v.Y == y);
        visits.Add(new(sequence, x, y, pose));
        if (visits.Count > Capacity) visits.RemoveAt(0);
        return true;
    }

    public GoalNativeConditionResult Read(GoalCondition condition, string worldEpoch, long goalStartSequence)
    {
        if (!condition.X.HasValue || !condition.Y.HasValue)
            return new(null, "到访地点未绑定。");
        if (epoch.Length == 0 || epoch != worldEpoch || boundary != goalStartSequence)
            return new(null, "当前世界或目标没有有效的到访记录，旧世界/旧任务不能复用。");
        GoalVisitRecord? visit = visits.LastOrDefault(v => v.X == condition.X && v.Y == condition.Y);
        return visit == null
            ? new(false, $"本目标尚未真实到访请求坐标({condition.X},{condition.Y})；位置查询和路线图不算到访。")
            : new(true, $"本目标动作#{visit.Sequence}实际到访({visit.X},{visit.Y})对应站位({visit.Pose.StandX},{visit.Pose.StandRow})；" +
                $"当时脚底{visit.Pose.Bottom / 16f:0.###}格、接地，距冻结站位水平{Math.Abs(visit.Pose.CenterX - (visit.Pose.StandCenterX ?? (visit.Pose.StandX * 16f + 8))):0.###}像素。" +
                "这是本目标历史到访证据，当前返程仍须独立核对near_player。");
    }

    private sealed record GoalVisitRecord(long Sequence, int X, int Y, GoalVisitPose Pose);
}

public sealed record GoalVisitPose(int StandX, int StandRow, float StandBottom,
    float CenterX, float Bottom, float VelocityY, bool Grounded, float? StandCenterX = null);
