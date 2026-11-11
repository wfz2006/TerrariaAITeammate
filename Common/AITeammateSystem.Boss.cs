using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaAITeammate.Common;

public partial class AITeammateSystem
{
    public BossEvidenceLedger BossEvidence { get; } = new();
    public int PrimaryDecisionRequestCount { get; private set; }
    public int CombatDecisionRequestCount { get; private set; }
    private string goalCompletionTarget = "";
    private long goalBossDeathBoundary;
    public int CompanionBossDefeats(int type) => BossEvidence.Defeats(type);
    public int BossSpawnCount(int type) => BossEvidence.SpawnCount(type);
    public int BossCompanionDamage(int type) => BossEvidence.Damage(type);
    public int BossLootPickups(int type, long afterDeathSequence = 0) => BossEvidence.LootCount(type, afterDeathSequence);
    public bool BossDefeatedForCurrentGoal(int type) => !string.IsNullOrEmpty(activeGoal) && BossEvidence.Defeats(type, goalBossDeathBoundary) > 0;

    public object BossWorldState => new
    {
        progress = BossKnowledge.Progress(), supportedSummoners = new[] { "king_slime:560", "eye_of_cthulhu:43" },
        activeBosses = Main.npc.Where(n => n.active && n.boss).Select(n =>
        {
            bool observed = CompanionNpc != null && Vector2.Distance(n.Center, CompanionNpc.Center) <= 160 * 16f;
            return new { npcType = n.type, name = n.FullName, observed,
                x = observed ? (int?)(n.Center.X / 16f) : null, y = observed ? (int?)(n.Center.Y / 16f) : null,
                hp = observed ? (int?)n.life : null };
        }).ToArray(),
        realDefeats = BossEvidence.Snapshot().Where(e => e.DeathSequence > 0).Select(e =>
            new { e.SpawnId, e.NpcType, e.DeathSequence, e.CompanionDamage }).ToArray()
    };
}
