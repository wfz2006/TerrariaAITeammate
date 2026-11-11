using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrariaAITeammate.Common;

public sealed record MaterialRequirement(int Id, int Count, int[] AcceptedItems);

public static class MaterialAllocator
{
    // A shared inventory is a capacity constraint, including when recipe groups overlap.
    public static bool TryAllocate(IReadOnlyList<MaterialRequirement> requirements,
        IReadOnlyDictionary<int, int> inventory, out Dictionary<int, int> consumption)
    {
        consumption = new Dictionary<int, int>();
        if (requirements.Any(requirement => requirement.Count < 0))
            return false;

        int[] types = inventory.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToArray();
        int firstRequirement = 1 + types.Length;
        int sink = firstRequirement + requirements.Count;
        var residual = new int[sink + 1, sink + 1];
        long wanted = 0;
        for (int i = 0; i < types.Length; i++)
            residual[0, i + 1] = inventory[types[i]];
        for (int j = 0; j < requirements.Count; j++)
        {
            MaterialRequirement requirement = requirements[j];
            residual[firstRequirement + j, sink] = requirement.Count;
            wanted += requirement.Count;
            for (int i = 0; i < types.Length; i++)
                if (Array.IndexOf(requirement.AcceptedItems, types[i]) >= 0)
                    residual[i + 1, firstRequirement + j] = requirement.Count;
        }

        long sent = 0;
        var parent = new int[sink + 1];
        while (sent < wanted)
        {
            Array.Fill(parent, -1);
            parent[0] = 0;
            var queue = new Queue<int>();
            queue.Enqueue(0);
            while (queue.Count > 0 && parent[sink] < 0)
            {
                int from = queue.Dequeue();
                for (int to = 1; to <= sink; to++)
                {
                    if (parent[to] >= 0 || residual[from, to] <= 0)
                        continue;
                    parent[to] = from;
                    queue.Enqueue(to);
                }
            }
            if (parent[sink] < 0)
                return false;

            int amount = int.MaxValue;
            for (int to = sink; to != 0; to = parent[to])
                amount = Math.Min(amount, residual[parent[to], to]);
            for (int to = sink; to != 0; to = parent[to])
            {
                int from = parent[to];
                residual[from, to] -= amount;
                residual[to, from] += amount;
            }
            sent += amount;
        }

        for (int i = 0; i < types.Length; i++)
        {
            int used = inventory[types[i]] - residual[0, i + 1];
            if (used > 0)
                consumption[types[i]] = used;
        }
        return true;
    }
}
