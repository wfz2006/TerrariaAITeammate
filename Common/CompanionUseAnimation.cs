using System;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

public enum CompanionUseKind
{
    None,
    Swing,
    Shoot,
    Drink,
    Mine,
    Place,
    Thrust,
    Yoyo
}

public sealed class CompanionUseAnimation
{
    public Item? Item { get; private set; }
    public Item? LastItem { get; private set; }
    public CompanionUseKind Kind { get; private set; }
    public Vector2 Aim { get; private set; } = Vector2.UnitX;
    public int Duration { get; private set; }
    public int Ticks { get; private set; }
    public uint UseSequence { get; private set; }

    public bool Active => Ticks > 0;
    public float Progress => Duration <= 0 ? 1f : 1f - Ticks / (float)Duration;

    public void Start(Item item, CompanionUseKind kind, int duration, Vector2 aim)
    {
        if (item.IsAir)
            return;

        Item = item;
        LastItem = item;
        Kind = kind;
        UseSequence++;
        Duration = Math.Max(4, duration);
        Ticks = Duration;
        Aim = aim.LengthSquared() > 0.001f ? Vector2.Normalize(aim) : Vector2.UnitX;
    }

    public void Tick()
    {
        if (Ticks > 0)
            Ticks--;
    }

    public void Release() => Ticks = 0;

    public void TrackYoyo(Vector2 aim)
    {
        if (Active && Kind == CompanionUseKind.Yoyo && aim.LengthSquared() > 0.001f)
            Aim = Vector2.Normalize(aim);
    }

    public float Rotation(int direction)
    {
        if (!Active)
            return direction >= 0 ? 0.85f : MathHelper.Pi - 0.85f;

        float swing = MathHelper.Lerp(-1.15f, 1.25f, Progress);

        return Kind switch
        {
            CompanionUseKind.Swing or CompanionUseKind.Mine =>
                direction >= 0 ? swing : MathHelper.Pi - swing,
            CompanionUseKind.Shoot or CompanionUseKind.Thrust or CompanionUseKind.Yoyo => Aim.ToRotation(),
            CompanionUseKind.Drink =>
                direction >= 0 ? MathHelper.Lerp(0.7f, -0.15f, Progress) : MathHelper.Pi - MathHelper.Lerp(0.7f, -0.15f, Progress),
            CompanionUseKind.Place => direction >= 0 ? 0.35f : MathHelper.Pi - 0.35f,
            _ => direction >= 0 ? 0.85f : MathHelper.Pi - 0.85f
        };
    }

    public bool FlipVertically(int direction) =>
        Active && Kind is CompanionUseKind.Shoot or CompanionUseKind.Thrust or CompanionUseKind.Yoyo ? Aim.X < 0f : direction < 0;
}
