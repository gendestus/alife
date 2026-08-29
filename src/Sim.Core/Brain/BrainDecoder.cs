using System.Collections.Generic;
using Sim.Core.Genetics;

namespace Sim.Core.Brain;

/// <summary>Decodes a genome's brain graph into a <see cref="BrainRuntime"/> (§4.4, once at hatch).</summary>
public static class BrainDecoder
{
    public static BrainRuntime Decode(Genome g)
    {
        // Fixed, deterministic slot order: nodes sorted by id (bias=0, hidden 1.., inputs 1e6+, outputs 2e6+).
        var nodesById = new List<BrainNode>(g.Brain.Nodes);
        nodesById.Sort((a, b) => a.Id.CompareTo(b.Id));

        var slotOf = new Dictionary<int, int>(nodesById.Count);
        var isComputed = new bool[nodesById.Count];
        int biasSlot = -1;
        for (int i = 0; i < nodesById.Count; i++)
        {
            var n = nodesById[i];
            slotOf[n.Id] = i;
            isComputed[i] = n.Kind is NodeKind.Hidden or NodeKind.Output;
            if (n.Kind == NodeKind.Bias) biasSlot = i;
        }

        var inputSlots = new int[SensorInputCount(g)];
        int inIdx = 0;
        foreach (var s in g.Sensors)
        {
            int slots = GeneSpec.SensorSlotCount(s.Kind);
            for (int slot = 0; slot < slots; slot++)
            {
                inputSlots[inIdx++] = slotOf[NodeIds.SensorInputNodeId(s.Id, slot)];
            }
        }

        var outputSlots = new int[g.Actuators.Count];
        for (int i = 0; i < g.Actuators.Count; i++)
        {
            outputSlots[i] = slotOf[NodeIds.ActuatorOutputNodeId(g.Actuators[i].Id)];
        }

        int enabledLinkCount = 0;
        foreach (var l in g.Brain.Links)
        {
            if (l.Enabled) enabledLinkCount++;
        }
        var linkFrom = new int[enabledLinkCount];
        var linkTo = new int[enabledLinkCount];
        var linkWeight = new float[enabledLinkCount];
        int li = 0;
        foreach (var l in g.Brain.Links)
        {
            if (!l.Enabled) continue;
            linkFrom[li] = slotOf[l.From];
            linkTo[li] = slotOf[l.To];
            linkWeight[li] = l.Weight;
            li++;
        }

        return new BrainRuntime(nodesById.Count, biasSlot, isComputed, inputSlots, outputSlots, linkFrom, linkTo, linkWeight);
    }

    private static int SensorInputCount(Genome g)
    {
        int total = 0;
        foreach (var s in g.Sensors) total += GeneSpec.SensorSlotCount(s.Kind);
        return total;
    }
}
