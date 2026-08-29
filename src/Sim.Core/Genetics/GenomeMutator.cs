using System;
using System.Collections.Generic;
using Sim.Core.Brain;
using Sim.Core.Config;
using Sim.Core.Random;

namespace Sim.Core.Genetics;

/// <summary>
/// Mutation operators (§6). Operates on a deep copy of the parent using the parent's own
/// Meta genes (scaled by config), in the fixed order: scalar perturbation, weights, meta,
/// then each structural op in turn. RNG draws happen in that order so a run is reproducible.
/// </summary>
public static class GenomeMutator
{
    public static Genome Mutate(Genome parent, IRandom rng, InnovationTracker tracker, MutationConfig caps,
        float mutationScale = 1.0f, float structuralScale = 1.0f)
    {
        var child = parent.Clone();

        float mutationRate = Math.Clamp(parent.Meta.MutationRate * mutationScale, GeneSpec.MutationRateMin, GeneSpec.MutationRateMax);
        float structuralRate = Math.Clamp(parent.Meta.StructuralRate * structuralScale, GeneSpec.StructuralRateMin, GeneSpec.StructuralRateMax);

        PerturbScalars(child, rng, mutationRate);
        MutateWeights(child, rng, mutationRate);
        MutateMeta(child, rng);

        // Structural ops, each independently rolled — table order in §6.
        AddLink(child, rng, tracker, caps, structuralRate);
        AddNode(child, rng, tracker, caps, structuralRate);
        ToggleLink(child, rng, structuralRate);
        AddSensor(child, rng, tracker, caps, structuralRate);
        RemoveSensor(child, rng, structuralRate);
        DuplicateSensor(child, rng, tracker, caps, structuralRate);
        ToggleSensor(child, rng, structuralRate);
        AddActuator(child, rng, tracker, caps, structuralRate);
        RemoveActuator(child, rng, structuralRate);
        ToggleActuator(child, rng, structuralRate);

        return child;
    }

    private static bool Roll(IRandom rng, float p) => rng.NextFloat() < p;

    private static float Perturb(float value, float min, float max, IRandom rng, float p)
    {
        if (Roll(rng, p))
        {
            value += rng.NextGaussian(0f, 0.05f * (max - min));
            value = Math.Clamp(value, min, max);
        }
        return value;
    }

    private static void PerturbScalars(Genome g, IRandom rng, float p)
    {
        g.Body.Size = Perturb(g.Body.Size, GeneSpec.SizeMin, GeneSpec.SizeMax, rng, p);
        g.Body.Speed = Perturb(g.Body.Speed, GeneSpec.SpeedMin, GeneSpec.SpeedMax, rng, p);
        g.Body.Armor = Perturb(g.Body.Armor, GeneSpec.ArmorMin, GeneSpec.ArmorMax, rng, p);
        g.Body.ColorR = Perturb(g.Body.ColorR, GeneSpec.ColorMin, GeneSpec.ColorMax, rng, p);
        g.Body.ColorG = Perturb(g.Body.ColorG, GeneSpec.ColorMin, GeneSpec.ColorMax, rng, p);
        g.Body.ColorB = Perturb(g.Body.ColorB, GeneSpec.ColorMin, GeneSpec.ColorMax, rng, p);

        g.Metabolism.Diet = Perturb(g.Metabolism.Diet, GeneSpec.DietMin, GeneSpec.DietMax, rng, p);
        g.Metabolism.StorageCap = Perturb(g.Metabolism.StorageCap, GeneSpec.StorageCapMin, GeneSpec.StorageCapMax, rng, p);
        g.Metabolism.Lifespan = Perturb(g.Metabolism.Lifespan, GeneSpec.LifespanMin, GeneSpec.LifespanMax, rng, p);

        g.Repro.EggThreshold = Perturb(g.Repro.EggThreshold, GeneSpec.EggThresholdMin, GeneSpec.EggThresholdMax, rng, p);
        g.Repro.EggInvestment = Perturb(g.Repro.EggInvestment, GeneSpec.EggInvestmentMin, GeneSpec.EggInvestmentMax, rng, p);

        foreach (var s in g.Sensors)
        {
            if (GeneSpec.SensorUsesRangeAngleFov(s.Kind))
            {
                var (lo, hi) = s.Kind == SensorKind.Smell ? (GeneSpec.SmellRangeMin, GeneSpec.SmellRangeMax) : (GeneSpec.VisionRangeMin, GeneSpec.VisionRangeMax);
                s.Range = Perturb(s.Range, lo, hi, rng, p);
                s.Angle = Perturb(s.Angle, GeneSpec.AngleMin, GeneSpec.AngleMax, rng, p);
                s.Fov = Perturb(s.Fov, GeneSpec.FovMin, GeneSpec.FovMax, rng, p);
            }
            else if (GeneSpec.SensorUsesChannel(s.Kind))
            {
                s.Range = Perturb(s.Range, GeneSpec.SmellRangeMin, GeneSpec.SmellRangeMax, rng, p);
                float ch = Perturb(s.Channel, GeneSpec.ChannelMin, GeneSpec.ChannelMax, rng, p);
                s.Channel = (int)MathF.Round(ch);
            }
        }

        foreach (var a in g.Actuators)
        {
            if (GeneSpec.ActuatorUsesStrength(a.Kind))
            {
                a.Strength = Perturb(a.Strength, GeneSpec.StrengthMin, GeneSpec.StrengthMax, rng, p);
            }
            if (GeneSpec.ActuatorUsesChannel(a.Kind))
            {
                float ch = Perturb(a.Channel, GeneSpec.ChannelMin, GeneSpec.ChannelMax, rng, p);
                a.Channel = (int)MathF.Round(ch);
            }
        }
    }

    private static void MutateWeights(Genome g, IRandom rng, float mutationRate)
    {
        float p = 2f * mutationRate;
        foreach (var l in g.Brain.Links)
        {
            if (!Roll(rng, p)) continue;
            if (rng.NextFloat() < 0.9f)
            {
                l.Weight += rng.NextGaussian(0f, 0.5f);
            }
            else
            {
                l.Weight = rng.NextGaussian(0f, 1f);
            }
            l.Weight = Math.Clamp(l.Weight, -8f, 8f);
        }
    }

    private static void MutateMeta(Genome g, IRandom rng)
    {
        const float p = 0.05f;
        if (Roll(rng, p))
        {
            g.Meta.MutationRate = Math.Clamp(g.Meta.MutationRate * MathF.Exp(rng.NextGaussian(0f, 0.2f)), GeneSpec.MutationRateMin, GeneSpec.MutationRateMax);
        }
        if (Roll(rng, p))
        {
            g.Meta.StructuralRate = Math.Clamp(g.Meta.StructuralRate * MathF.Exp(rng.NextGaussian(0f, 0.2f)), GeneSpec.StructuralRateMin, GeneSpec.StructuralRateMax);
        }
    }

    private static void AddLink(Genome g, IRandom rng, InnovationTracker tracker, MutationConfig caps, float p)
    {
        if (!Roll(rng, p)) return;
        if (g.Brain.Links.Count >= caps.MaxLinks) return;

        var toCandidates = new List<BrainNode>();
        foreach (var n in g.Brain.Nodes)
        {
            if (n.Kind is NodeKind.Hidden or NodeKind.Output) toCandidates.Add(n);
        }
        if (toCandidates.Count == 0) return;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            var from = g.Brain.Nodes[rng.NextInt(0, g.Brain.Nodes.Count)];
            var to = toCandidates[rng.NextInt(0, toCandidates.Count)];
            if (LinkExists(g, from.Id, to.Id)) continue;

            float weight = rng.NextGaussian(0f, 1f);
            int innovation = tracker.LinkInnovation(from.Id, to.Id);
            g.Brain.Links.Add(new BrainLink { Innovation = innovation, From = from.Id, To = to.Id, Weight = weight, Enabled = true });
            return;
        }
    }

    private static bool LinkExists(Genome g, int from, int to)
    {
        foreach (var l in g.Brain.Links)
        {
            if (l.From == from && l.To == to) return true;
        }
        return false;
    }

    private static void AddNode(Genome g, IRandom rng, InnovationTracker tracker, MutationConfig caps, float p)
    {
        if (!Roll(rng, p)) return;

        int hiddenCount = 0;
        foreach (var n in g.Brain.Nodes)
        {
            if (n.Kind == NodeKind.Hidden) hiddenCount++;
        }
        if (hiddenCount >= caps.MaxHidden) return;
        if (g.Brain.Links.Count + 2 > caps.MaxLinks) return;

        var enabledLinks = new List<BrainLink>();
        foreach (var l in g.Brain.Links)
        {
            if (l.Enabled) enabledLinks.Add(l);
        }
        if (enabledLinks.Count == 0) return;

        var link = enabledLinks[rng.NextInt(0, enabledLinks.Count)];
        link.Enabled = false;

        int newId = tracker.NextHiddenNodeId();
        g.Brain.Nodes.Add(new BrainNode { Id = newId, Kind = NodeKind.Hidden });

        int inn1 = tracker.LinkInnovation(link.From, newId);
        int inn2 = tracker.LinkInnovation(newId, link.To);
        g.Brain.Links.Add(new BrainLink { Innovation = inn1, From = link.From, To = newId, Weight = 1f, Enabled = true });
        g.Brain.Links.Add(new BrainLink { Innovation = inn2, From = newId, To = link.To, Weight = link.Weight, Enabled = true });
    }

    private static void ToggleLink(Genome g, IRandom rng, float p)
    {
        if (!Roll(rng, p)) return;
        if (g.Brain.Links.Count == 0) return;
        var link = g.Brain.Links[rng.NextInt(0, g.Brain.Links.Count)];
        link.Enabled = !link.Enabled;
    }

    private static void AddSensor(Genome g, IRandom rng, InnovationTracker tracker, MutationConfig caps, float p)
    {
        if (!Roll(rng, p)) return;
        if (g.Sensors.Count >= caps.MaxSensors) return;

        var kind = (SensorKind)rng.NextInt(0, 8);
        long newId = tracker.NextSensorGeneId();
        var gene = new SensorGene { Id = newId, Kind = kind, Enabled = true };
        RandomizeSensorParams(gene, rng);
        g.Sensors.Add(gene);

        int slots = GeneSpec.SensorSlotCount(kind);
        var newInputIds = new int[slots];
        for (int slot = 0; slot < slots; slot++)
        {
            int nodeId = NodeIds.SensorInputNodeId(newId, slot);
            g.Brain.Nodes.Add(new BrainNode { Id = nodeId, Kind = NodeKind.Input, BindGeneId = newId, BindSlot = slot });
            newInputIds[slot] = nodeId;
        }

        if (g.Actuators.Count > 0 && g.Brain.Links.Count < caps.MaxLinks)
        {
            int fromNodeId = newInputIds[rng.NextInt(0, slots)];
            var toGene = g.Actuators[rng.NextInt(0, g.Actuators.Count)];
            int toNodeId = NodeIds.ActuatorOutputNodeId(toGene.Id);
            float w = rng.NextGaussian(0f, 0.5f);
            int inn = tracker.LinkInnovation(fromNodeId, toNodeId);
            g.Brain.Links.Add(new BrainLink { Innovation = inn, From = fromNodeId, To = toNodeId, Weight = w, Enabled = true });
        }
    }

    private static void RandomizeSensorParams(SensorGene gene, IRandom rng)
    {
        if (GeneSpec.SensorUsesRangeAngleFov(gene.Kind))
        {
            gene.Range = rng.NextFloat(GeneSpec.VisionRangeMin, GeneSpec.VisionRangeMax);
            gene.Angle = rng.NextFloat(GeneSpec.AngleMin, GeneSpec.AngleMax);
            gene.Fov = rng.NextFloat(GeneSpec.FovMin, GeneSpec.FovMax);
        }
        else if (GeneSpec.SensorUsesChannel(gene.Kind))
        {
            gene.Range = rng.NextFloat(GeneSpec.SmellRangeMin, GeneSpec.SmellRangeMax);
            gene.Channel = rng.NextInt(GeneSpec.ChannelMin, GeneSpec.ChannelMax + 1);
        }
    }

    private static void RemoveSensor(Genome g, IRandom rng, float p)
    {
        if (!Roll(rng, p)) return;
        if (g.Sensors.Count <= 1) return;

        int idx = rng.NextInt(0, g.Sensors.Count);
        var gene = g.Sensors[idx];
        g.Sensors.RemoveAt(idx);

        var idsToRemove = new HashSet<int>();
        int slots = GeneSpec.SensorSlotCount(gene.Kind);
        for (int slot = 0; slot < slots; slot++) idsToRemove.Add(NodeIds.SensorInputNodeId(gene.Id, slot));

        g.Brain.Nodes.RemoveAll(n => idsToRemove.Contains(n.Id));
        g.Brain.Links.RemoveAll(l => idsToRemove.Contains(l.From) || idsToRemove.Contains(l.To));
    }

    private static void DuplicateSensor(Genome g, IRandom rng, InnovationTracker tracker, MutationConfig caps, float p)
    {
        if (!Roll(rng, p)) return;
        if (g.Sensors.Count == 0 || g.Sensors.Count >= caps.MaxSensors) return;

        var original = g.Sensors[rng.NextInt(0, g.Sensors.Count)];
        long newId = tracker.NextSensorGeneId();
        var dup = original.Clone();
        dup.Id = newId;

        if (GeneSpec.SensorUsesRangeAngleFov(dup.Kind))
        {
            var (lo, hi) = (GeneSpec.VisionRangeMin, GeneSpec.VisionRangeMax);
            dup.Range = Math.Clamp(dup.Range + rng.NextGaussian(0f, 0.1f * (hi - lo)), lo, hi);
            dup.Angle = Math.Clamp(dup.Angle + rng.NextGaussian(0f, 0.1f * (GeneSpec.AngleMax - GeneSpec.AngleMin)), GeneSpec.AngleMin, GeneSpec.AngleMax);
            dup.Fov = Math.Clamp(dup.Fov + rng.NextGaussian(0f, 0.1f * (GeneSpec.FovMax - GeneSpec.FovMin)), GeneSpec.FovMin, GeneSpec.FovMax);
        }
        else if (GeneSpec.SensorUsesChannel(dup.Kind))
        {
            dup.Range = Math.Clamp(dup.Range + rng.NextGaussian(0f, 0.1f * (GeneSpec.SmellRangeMax - GeneSpec.SmellRangeMin)), GeneSpec.SmellRangeMin, GeneSpec.SmellRangeMax);
        }

        g.Sensors.Add(dup);

        int slots = GeneSpec.SensorSlotCount(dup.Kind);
        var dupInputIds = new int[slots];
        for (int slot = 0; slot < slots; slot++)
        {
            int nodeId = NodeIds.SensorInputNodeId(newId, slot);
            g.Brain.Nodes.Add(new BrainNode { Id = nodeId, Kind = NodeKind.Input, BindGeneId = newId, BindSlot = slot });
            dupInputIds[slot] = nodeId;
        }

        var linksToAdd = new List<BrainLink>();
        foreach (var l in g.Brain.Links)
        {
            for (int slot = 0; slot < slots; slot++)
            {
                int origNodeId = NodeIds.SensorInputNodeId(original.Id, slot);
                if (l.From != origNodeId) continue;
                int inn = tracker.LinkInnovation(dupInputIds[slot], l.To);
                linksToAdd.Add(new BrainLink { Innovation = inn, From = dupInputIds[slot], To = l.To, Weight = l.Weight, Enabled = l.Enabled });
            }
        }
        foreach (var l in linksToAdd)
        {
            if (g.Brain.Links.Count >= caps.MaxLinks) break;
            g.Brain.Links.Add(l);
        }
    }

    private static void ToggleSensor(Genome g, IRandom rng, float p)
    {
        if (!Roll(rng, p)) return;
        if (g.Sensors.Count == 0) return;
        var gene = g.Sensors[rng.NextInt(0, g.Sensors.Count)];
        gene.Enabled = !gene.Enabled;
    }

    private static void AddActuator(Genome g, IRandom rng, InnovationTracker tracker, MutationConfig caps, float p)
    {
        if (!Roll(rng, p)) return;
        if (g.Actuators.Count >= caps.MaxActuators) return;

        var kind = (ActuatorKind)rng.NextInt(0, 6);
        long newId = tracker.NextActuatorGeneId();
        var gene = new ActuatorGene { Id = newId, Kind = kind, Enabled = true };
        if (GeneSpec.ActuatorUsesStrength(kind)) gene.Strength = rng.NextFloat(GeneSpec.StrengthMin, GeneSpec.StrengthMax);
        if (GeneSpec.ActuatorUsesChannel(kind)) gene.Channel = rng.NextInt(GeneSpec.ChannelMin, GeneSpec.ChannelMax + 1);
        g.Actuators.Add(gene);

        int outNodeId = NodeIds.ActuatorOutputNodeId(newId);
        g.Brain.Nodes.Add(new BrainNode { Id = outNodeId, Kind = NodeKind.Output, BindGeneId = newId });

        if (g.Brain.Links.Count < caps.MaxLinks)
        {
            var fromCandidates = new List<BrainNode>();
            foreach (var n in g.Brain.Nodes)
            {
                if (n.Kind is NodeKind.Input or NodeKind.Bias) fromCandidates.Add(n);
            }
            var from = fromCandidates[rng.NextInt(0, fromCandidates.Count)];
            float w = rng.NextGaussian(0f, 0.5f);
            int inn = tracker.LinkInnovation(from.Id, outNodeId);
            g.Brain.Links.Add(new BrainLink { Innovation = inn, From = from.Id, To = outNodeId, Weight = w, Enabled = true });
        }
    }

    private static void RemoveActuator(Genome g, IRandom rng, float p)
    {
        if (!Roll(rng, p)) return;
        if (g.Actuators.Count <= 1) return;

        int idx = rng.NextInt(0, g.Actuators.Count);
        var gene = g.Actuators[idx];
        g.Actuators.RemoveAt(idx);

        int outId = NodeIds.ActuatorOutputNodeId(gene.Id);
        g.Brain.Nodes.RemoveAll(n => n.Id == outId);
        g.Brain.Links.RemoveAll(l => l.From == outId || l.To == outId);
    }

    private static void ToggleActuator(Genome g, IRandom rng, float p)
    {
        if (!Roll(rng, p)) return;
        if (g.Actuators.Count == 0) return;
        var gene = g.Actuators[rng.NextInt(0, g.Actuators.Count)];
        gene.Enabled = !gene.Enabled;
    }
}
