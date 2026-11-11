using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrariaAITeammate.Common;

public sealed record MiningYieldAmount(int ItemType, int Amount);

/// <summary>Task-owned per-item net gains, capped by ground pickups actually recorded during that action.</summary>
public sealed class MiningYieldLedger
{
    private readonly HashSet<int> accepted;
    private readonly Dictionary<int, int> baseline;
    private readonly Dictionary<int, int> groundPickups = new();
    public IReadOnlyCollection<int> AcceptedItemTypes { get; }

    public MiningYieldLedger(IEnumerable<int> acceptedItemTypes, IReadOnlyDictionary<int, int> initialInventory)
    {
        accepted = acceptedItemTypes.Where(id => id > 0).ToHashSet();
        AcceptedItemTypes = Array.AsReadOnly(accepted.OrderBy(id => id).ToArray());
        baseline = accepted.ToDictionary(id => id, id => Math.Max(0, initialInventory.GetValueOrDefault(id)));
    }

    public void RecordGroundPickup(int itemType, int amount)
    {
        if (amount <= 0 || !accepted.Contains(itemType)) return;
        groundPickups[itemType] = (int)Math.Min(int.MaxValue, (long)groundPickups.GetValueOrDefault(itemType) + amount);
    }

    public IReadOnlyList<MiningYieldAmount> Gains(IReadOnlyDictionary<int, int> inventory)
        => AcceptedItemTypes.Select(id => new MiningYieldAmount(id, (int)Math.Min(groundPickups.GetValueOrDefault(id),
                Math.Max(0L, (long)inventory.GetValueOrDefault(id) - baseline[id]))))
            .Where(row => row.Amount > 0).ToArray();

    public int TotalGained(IReadOnlyDictionary<int, int> inventory)
        => (int)Math.Min(int.MaxValue, Gains(inventory).Sum(row => (long)row.Amount));
}
