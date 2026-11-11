using System;

namespace TerrariaAITeammate.Common;

/// <summary>Evidence for one locally dispatched attack, independent of any LLM goal.</summary>
public sealed class BossAttackEvidence
{
    public long CapturedDeathSequence { get; }
    public long? BoundSpawnId { get; private set; }
    public int? BoundNpcType { get; private set; }
    public int DamageAtBinding { get; private set; }

    public BossAttackEvidence(long capturedDeathSequence)
    {
        if (capturedDeathSequence < 0) throw new ArgumentOutOfRangeException(nameof(capturedDeathSequence));
        CapturedDeathSequence = capturedDeathSequence;
    }

    public bool TryBind(BossEvidenceLedger ledger, long spawnId, int npcType)
    {
        if (BoundSpawnId.HasValue) return BoundSpawnId == spawnId && BoundNpcType == npcType;
        BossEncounter? encounter = ledger.ReadEncounter(spawnId);
        if (spawnId <= 0 || npcType <= 0 || encounter == null || encounter.NpcType != npcType || encounter.DeathSequence != 0) return false;
        BoundSpawnId = spawnId;
        BoundNpcType = npcType;
        DamageAtBinding = encounter.CompanionDamage;
        return true;
    }

    public bool IsProven(BossEvidenceLedger ledger, int npcType)
    {
        if (!BoundSpawnId.HasValue || BoundNpcType != npcType) return false;
        BossEncounter? encounter = ledger.ReadEncounter(BoundSpawnId.Value);
        return encounter != null && encounter.NpcType == npcType && encounter.DeathSequence > CapturedDeathSequence &&
            encounter.CompanionDamage > DamageAtBinding;
    }
}
