using System.Collections.Generic;
using System.Linq;

namespace TerrariaAITeammate.Common;

public sealed class BossEncounter
{
    public long SpawnId { get; init; }
    public int NpcType { get; init; }
    public int Slot { get; init; }
    public int CompanionDamage { get; set; }
    public long DeathSequence { get; set; }
    public int LootPickedUp { get; set; }
}

public sealed class BossEvidenceLedger
{
    private long nextSpawn;
    private readonly Dictionary<long, BossEncounter> encounters = new();
    public long DeathSequence { get; private set; }
    public long Spawn(int npcType, int slot)
    {
        long id = ++nextSpawn;
        encounters[id] = new BossEncounter { SpawnId = id, NpcType = npcType, Slot = slot };
        return id;
    }
    public void Hit(long id, int damage)
    {
        if (damage > 0 && encounters.TryGetValue(id, out BossEncounter? encounter)) encounter.CompanionDamage += damage;
    }
    public void Kill(long id)
    {
        if (encounters.TryGetValue(id, out BossEncounter? encounter) && encounter.DeathSequence == 0)
            encounter.DeathSequence = ++DeathSequence;
    }
    public int Defeats(int npcType, long afterDeathSequence = 0)
        => encounters.Values.Count(e => e.NpcType == npcType && e.DeathSequence > afterDeathSequence && e.CompanionDamage > 0);
    public int Damage(int npcType) => encounters.Values.Where(e => e.NpcType == npcType).Sum(e => e.CompanionDamage);
    public int SpawnCount(int npcType) => encounters.Values.Count(e => e.NpcType == npcType);
    public BossEncounter? ReadEncounter(long spawnId) => encounters.TryGetValue(spawnId, out BossEncounter? encounter)
        ? new BossEncounter { SpawnId = encounter.SpawnId, NpcType = encounter.NpcType, Slot = encounter.Slot,
            CompanionDamage = encounter.CompanionDamage, DeathSequence = encounter.DeathSequence, LootPickedUp = encounter.LootPickedUp }
        : null;
    public void Loot(long id, int count)
    {
        if (count > 0 && encounters.TryGetValue(id, out BossEncounter? encounter)) encounter.LootPickedUp += count;
    }
    public int LootCount(int npcType, long afterDeathSequence = 0) => encounters.Values
        .Where(e => e.NpcType == npcType && e.DeathSequence > afterDeathSequence && e.CompanionDamage > 0)
        .Sum(e => e.LootPickedUp);
    public BossEncounter[] Snapshot() => encounters.Values.OrderBy(e => e.SpawnId).TakeLast(8).ToArray();
    public void Clear() { encounters.Clear(); nextSpawn = DeathSequence = 0; }
}
