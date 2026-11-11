using System;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

internal static class CompanionAim
{
    public static bool TryFindVisiblePoint(Vector2 origin, Vector2 targetCenter, int targetWidth, out Vector2 point)
    {
        point = targetCenter;
        if (Collision.CanHitLine(origin, 1, 1, targetCenter, 1, 1)) return true;

        // A wall beside the target can block its center ray. For melee, try one
        // point inside the side of the hitbox closest to the attacker.
        float offset = Math.Sign(origin.X - targetCenter.X) * (targetWidth / 4f);
        if (offset == 0f) return false;
        var nearSide = targetCenter + new Vector2(offset, 0f);
        if (!Collision.CanHitLine(origin, 1, 1, nearSide, 1, 1)) return false;
        point = nearSide;
        return true;
    }
}
