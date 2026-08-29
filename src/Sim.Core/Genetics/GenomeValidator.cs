using System;
using System.Collections.Generic;
using Sim.Core.Brain;
using Sim.Core.Config;

namespace Sim.Core.Genetics;

/// <summary>
/// §6: "Every mutated genome must pass Genome.Validate() ... Validation failure is a bug,
/// not a sim event — throw."
/// </summary>
public static class GenomeValidator
{
    public static void Validate(this Genome g, MutationConfig caps)
    {
        Require(InRange(g.Meta.MutationRate, GeneSpec.MutationRateMin, GeneSpec.MutationRateMax), "Meta.MutationRate out of range");
        Require(InRange(g.Meta.StructuralRate, GeneSpec.StructuralRateMin, GeneSpec.StructuralRateMax), "Meta.StructuralRate out of range");

        Require(InRange(g.Body.Size, GeneSpec.SizeMin, GeneSpec.SizeMax), "Body.Size out of range");
        Require(InRange(g.Body.Speed, GeneSpec.SpeedMin, GeneSpec.SpeedMax), "Body.Speed out of range");
        Require(InRange(g.Body.Armor, GeneSpec.ArmorMin, GeneSpec.ArmorMax), "Body.Armor out of range");
        Require(InRange(g.Body.ColorR, GeneSpec.ColorMin, GeneSpec.ColorMax), "Body.ColorR out of range");
        Require(InRange(g.Body.ColorG, GeneSpec.ColorMin, GeneSpec.ColorMax), "Body.ColorG out of range");
        Require(InRange(g.Body.ColorB, GeneSpec.ColorMin, GeneSpec.ColorMax), "Body.ColorB out of range");

        Require(InRange(g.Metabolism.Diet, GeneSpec.DietMin, GeneSpec.DietMax), "Metabolism.Diet out of range");
        Require(InRange(g.Metabolism.StorageCap, GeneSpec.StorageCapMin, GeneSpec.StorageCapMax), "Metabolism.StorageCap out of range");
        Require(InRange(g.Metabolism.Lifespan, GeneSpec.LifespanMin, GeneSpec.LifespanMax), "Metabolism.Lifespan out of range");

        Require(InRange(g.Repro.EggThreshold, GeneSpec.EggThresholdMin, GeneSpec.EggThresholdMax), "Repro.EggThreshold out of range");
        Require(InRange(g.Repro.EggInvestment, GeneSpec.EggInvestmentMin, GeneSpec.EggInvestmentMax), "Repro.EggInvestment out of range");

        Require(g.Sensors.Count <= caps.MaxSensors, $"sensor cap exceeded: {g.Sensors.Count} > {caps.MaxSensors}");
        Require(g.Actuators.Count <= caps.MaxActuators, $"actuator cap exceeded: {g.Actuators.Count} > {caps.MaxActuators}");
        Require(g.Brain.Links.Count <= caps.MaxLinks, $"link cap exceeded: {g.Brain.Links.Count} > {caps.MaxLinks}");

        var seenSensorIds = new HashSet<long>();
        foreach (var s in g.Sensors)
        {
            Require(seenSensorIds.Add(s.Id), $"duplicate sensor gene id {s.Id}");
            if (GeneSpec.SensorUsesRangeAngleFov(s.Kind))
            {
                var (lo, hi) = s.Kind == SensorKind.Smell ? (GeneSpec.SmellRangeMin, GeneSpec.SmellRangeMax) : (GeneSpec.VisionRangeMin, GeneSpec.VisionRangeMax);
                Require(InRange(s.Range, lo, hi), $"sensor {s.Id} Range out of range");
                Require(InRange(s.Angle, GeneSpec.AngleMin, GeneSpec.AngleMax), $"sensor {s.Id} Angle out of range");
                Require(InRange(s.Fov, GeneSpec.FovMin, GeneSpec.FovMax), $"sensor {s.Id} Fov out of range");
            }
            if (GeneSpec.SensorUsesChannel(s.Kind))
            {
                Require(InRange(s.Range, GeneSpec.SmellRangeMin, GeneSpec.SmellRangeMax), $"sensor {s.Id} Range out of range");
                Require(s.Channel >= GeneSpec.ChannelMin && s.Channel <= GeneSpec.ChannelMax, $"sensor {s.Id} Channel out of range");
            }
        }

        var seenActuatorIds = new HashSet<long>();
        foreach (var a in g.Actuators)
        {
            Require(seenActuatorIds.Add(a.Id), $"duplicate actuator gene id {a.Id}");
            if (GeneSpec.ActuatorUsesStrength(a.Kind))
            {
                Require(InRange(a.Strength, GeneSpec.StrengthMin, GeneSpec.StrengthMax), $"actuator {a.Id} Strength out of range");
            }
            if (GeneSpec.ActuatorUsesChannel(a.Kind))
            {
                Require(a.Channel >= GeneSpec.ChannelMin && a.Channel <= GeneSpec.ChannelMax, $"actuator {a.Id} Channel out of range");
            }
        }

        ValidateBrainStructure(g, caps);
    }

    private static void ValidateBrainStructure(Genome g, MutationConfig caps)
    {
        var nodeIds = new HashSet<int>();
        int biasCount = 0;
        int hiddenCount = 0;
        foreach (var n in g.Brain.Nodes)
        {
            Require(nodeIds.Add(n.Id), $"duplicate node id {n.Id}");
            if (n.Kind == NodeKind.Bias)
            {
                biasCount++;
                Require(n.Id == NodeIds.Bias, "bias node must have id 0");
            }
            else if (n.Kind == NodeKind.Hidden)
            {
                hiddenCount++;
                Require(n.BindGeneId is null && n.BindSlot is null, $"hidden node {n.Id} must not be bound to a gene");
            }
        }
        Require(biasCount == 1, $"expected exactly one bias node, found {biasCount}");
        Require(hiddenCount <= caps.MaxHidden, $"hidden node cap exceeded: {hiddenCount} > {caps.MaxHidden}");

        // Every sensor/actuator gene must have all its I/O nodes present, even when disabled
        // (§4.2: "their input nodes are still present ... so links survive dormancy").
        var inputNodesByGene = new Dictionary<long, HashSet<int>>();
        var outputNodeByGene = new Dictionary<long, int>();
        foreach (var n in g.Brain.Nodes)
        {
            if (n.Kind == NodeKind.Input)
            {
                Require(n.BindGeneId is not null && n.BindSlot is not null, $"input node {n.Id} missing gene binding");
                Require(n.Id == NodeIds.SensorInputNodeId(n.BindGeneId!.Value, n.BindSlot!.Value), $"input node {n.Id} id/binding mismatch");
                if (!inputNodesByGene.TryGetValue(n.BindGeneId!.Value, out var set))
                    inputNodesByGene[n.BindGeneId!.Value] = set = new HashSet<int>();
                Require(set.Add(n.BindSlot!.Value), $"duplicate slot binding on sensor gene {n.BindGeneId}");
            }
            else if (n.Kind == NodeKind.Output)
            {
                Require(n.BindGeneId is not null, $"output node {n.Id} missing gene binding");
                Require(n.Id == NodeIds.ActuatorOutputNodeId(n.BindGeneId!.Value), $"output node {n.Id} id/binding mismatch");
                Require(outputNodeByGene.TryAdd(n.BindGeneId!.Value, n.Id), $"duplicate output node for actuator gene {n.BindGeneId}");
            }
        }

        foreach (var s in g.Sensors)
        {
            int slots = GeneSpec.SensorSlotCount(s.Kind);
            Require(inputNodesByGene.TryGetValue(s.Id, out var set) && set.Count == slots,
                $"sensor gene {s.Id} ({s.Kind}) expects {slots} input node(s)");
        }
        foreach (var a in g.Actuators)
        {
            Require(outputNodeByGene.ContainsKey(a.Id), $"actuator gene {a.Id} ({a.Kind}) missing output node");
        }

        foreach (var l in g.Brain.Links)
        {
            Require(nodeIds.Contains(l.From), $"link {l.Innovation} has dangling From={l.From}");
            Require(nodeIds.Contains(l.To), $"link {l.Innovation} has dangling To={l.To}");
            Require(InRange(l.Weight, -8f, 8f), $"link {l.Innovation} weight out of range");

            var toNode = FindNode(g, l.To);
            Require(toNode.Kind is NodeKind.Hidden or NodeKind.Output, $"link {l.Innovation} targets a non-Hidden/Output node");
        }
    }

    private static BrainNode FindNode(Genome g, int id)
    {
        foreach (var n in g.Brain.Nodes)
        {
            if (n.Id == id) return n;
        }
        throw new InvalidOperationException($"node {id} not found");
    }

    private static bool InRange(float v, float lo, float hi) => v >= lo && v <= hi;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Genome.Validate: {message}");
    }
}
