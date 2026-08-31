using System;
using System.Collections.Generic;
using Sim.Core.Brain;
using Sim.Core.Random;

namespace Sim.Core.Genetics;

/// <summary>Builds the bootstrap genome (§4.5).</summary>
public static class GenomeFactory
{
    /// <summary>
    /// One independent bootstrap genome: fresh gene ids and link innovations from `tracker`, so
    /// it reads as a brand-new founding lineage. Fine for a single genome (e.g. a test's starting
    /// point for chained mutation) — but a whole bootstrap POPULATION built by calling this once
    /// per individual mints distinct gene ids per call, so §7 GenomeDistance sees every pair as
    /// maximally novel (zero matching links) even though they're topologically identical. Use
    /// <see cref="CreateBootstrapPopulation"/> for that case instead.
    /// </summary>
    public static Genome CreateBootstrap(IRandom rng, InnovationTracker tracker)
    {
        var g = BuildTemplate(rng, tracker);
        PerturbBootstrapScalars(g, rng);
        return g;
    }

    /// <summary>
    /// A whole bootstrap population sharing one founding topology's gene ids and link
    /// innovations (reserved once), each individual then independently re-randomized (color,
    /// link weights, §4.1 scalar perturbation). This is what makes §7 GenomeDistance recognize
    /// the founding population as near-identical (as intended — "tune δ so the bootstrap
    /// population forms 1-3 species") instead of each individual looking like an unrelated
    /// lineage.
    /// </summary>
    public static List<Genome> CreateBootstrapPopulation(IRandom rng, InnovationTracker tracker, int count)
    {
        var template = BuildTemplate(rng, tracker);
        var result = new List<Genome>(count);
        for (int i = 0; i < count; i++)
        {
            var g = template.Clone();
            g.Body.ColorR = rng.NextFloat(0f, 1f);
            g.Body.ColorG = rng.NextFloat(0f, 1f);
            g.Body.ColorB = rng.NextFloat(0f, 1f);
            foreach (var link in g.Brain.Links) link.Weight = rng.NextGaussian(0f, 0.5f);
            PerturbBootstrapScalars(g, rng);
            result.Add(g);
        }
        return result;
    }

    /// <summary>Canonical bootstrap topology (sensors/actuators/nodes/links) with unperturbed default scalars — gene ids/link innovations reserved from tracker exactly once per call.</summary>
    private static Genome BuildTemplate(IRandom rng, InnovationTracker tracker)
    {
        var g = new Genome();

        g.Meta.MutationRate = 0.03f;
        g.Meta.StructuralRate = 0.02f;

        g.Body.Size = 1.0f;
        g.Body.Speed = 1.0f;
        g.Body.Armor = 0.0f;
        g.Body.ColorR = rng.NextFloat(0f, 1f);
        g.Body.ColorG = rng.NextFloat(0f, 1f);
        g.Body.ColorB = rng.NextFloat(0f, 1f);

        g.Metabolism.Diet = 0.05f;
        g.Metabolism.StorageCap = 1.0f;
        g.Metabolism.Lifespan = 2000f;

        g.Repro.EggThreshold = 80f;
        g.Repro.EggInvestment = 40f;

        var bias = new BrainNode { Id = NodeIds.Bias, Kind = NodeKind.Bias };
        g.Brain.Nodes.Add(bias);

        var visionPlant = new SensorGene { Id = tracker.NextSensorGeneId(), Kind = SensorKind.VisionPlant, Range = 12f, Angle = 0f, Fov = Deg(90f), Enabled = true };
        var visionCreature = new SensorGene { Id = tracker.NextSensorGeneId(), Kind = SensorKind.VisionCreature, Range = 10f, Angle = 0f, Fov = Deg(60f), Enabled = true };
        var energy = new SensorGene { Id = tracker.NextSensorGeneId(), Kind = SensorKind.Energy, Enabled = true };
        g.Sensors.Add(visionPlant);
        g.Sensors.Add(visionCreature);
        g.Sensors.Add(energy);

        var inputAndBiasNodes = new List<BrainNode> { bias };
        foreach (var s in g.Sensors)
        {
            int slots = GeneSpec.SensorSlotCount(s.Kind);
            for (int slot = 0; slot < slots; slot++)
            {
                var node = new BrainNode { Id = NodeIds.SensorInputNodeId(s.Id, slot), Kind = NodeKind.Input, BindGeneId = s.Id, BindSlot = slot };
                g.Brain.Nodes.Add(node);
                inputAndBiasNodes.Add(node);
            }
        }

        var thrust = new ActuatorGene { Id = tracker.NextActuatorGeneId(), Kind = ActuatorKind.Thrust, Strength = 1.0f, Enabled = true };
        var turn = new ActuatorGene { Id = tracker.NextActuatorGeneId(), Kind = ActuatorKind.Turn, Strength = 1.0f, Enabled = true };
        var eat = new ActuatorGene { Id = tracker.NextActuatorGeneId(), Kind = ActuatorKind.Eat, Enabled = true };
        var layEgg = new ActuatorGene { Id = tracker.NextActuatorGeneId(), Kind = ActuatorKind.LayEgg, Enabled = true };
        g.Actuators.Add(thrust);
        g.Actuators.Add(turn);
        g.Actuators.Add(eat);
        g.Actuators.Add(layEgg);

        var outputNodes = new List<BrainNode>();
        foreach (var a in g.Actuators)
        {
            var node = new BrainNode { Id = NodeIds.ActuatorOutputNodeId(a.Id), Kind = NodeKind.Output, BindGeneId = a.Id };
            g.Brain.Nodes.Add(node);
            outputNodes.Add(node);
        }

        // Every input (and bias) linked to every output, weight ~ N(0, 0.5).
        foreach (var from in inputAndBiasNodes)
        {
            foreach (var to in outputNodes)
            {
                float weight = rng.NextGaussian(0f, 0.5f);
                int innovation = tracker.LinkInnovation(from.Id, to.Id);
                g.Brain.Links.Add(new BrainLink { Innovation = innovation, From = from.Id, To = to.Id, Weight = weight, Enabled = true });
            }
        }

        return g;
    }

    private static void PerturbBootstrapScalars(Genome g, IRandom rng)
    {
        const float sigmaFrac = 0.02f;

        g.Meta.MutationRate = Clamped(g.Meta.MutationRate, GeneSpec.MutationRateMin, GeneSpec.MutationRateMax, rng, sigmaFrac);
        g.Meta.StructuralRate = Clamped(g.Meta.StructuralRate, GeneSpec.StructuralRateMin, GeneSpec.StructuralRateMax, rng, sigmaFrac);

        g.Body.Size = Clamped(g.Body.Size, GeneSpec.SizeMin, GeneSpec.SizeMax, rng, sigmaFrac);
        g.Body.Speed = Clamped(g.Body.Speed, GeneSpec.SpeedMin, GeneSpec.SpeedMax, rng, sigmaFrac);
        g.Body.Armor = Clamped(g.Body.Armor, GeneSpec.ArmorMin, GeneSpec.ArmorMax, rng, sigmaFrac);
        g.Body.ColorR = Clamped(g.Body.ColorR, GeneSpec.ColorMin, GeneSpec.ColorMax, rng, sigmaFrac);
        g.Body.ColorG = Clamped(g.Body.ColorG, GeneSpec.ColorMin, GeneSpec.ColorMax, rng, sigmaFrac);
        g.Body.ColorB = Clamped(g.Body.ColorB, GeneSpec.ColorMin, GeneSpec.ColorMax, rng, sigmaFrac);

        g.Metabolism.Diet = Clamped(g.Metabolism.Diet, GeneSpec.DietMin, GeneSpec.DietMax, rng, sigmaFrac);
        g.Metabolism.StorageCap = Clamped(g.Metabolism.StorageCap, GeneSpec.StorageCapMin, GeneSpec.StorageCapMax, rng, sigmaFrac);
        g.Metabolism.Lifespan = Clamped(g.Metabolism.Lifespan, GeneSpec.LifespanMin, GeneSpec.LifespanMax, rng, sigmaFrac);

        g.Repro.EggThreshold = Clamped(g.Repro.EggThreshold, GeneSpec.EggThresholdMin, GeneSpec.EggThresholdMax, rng, sigmaFrac);
        g.Repro.EggInvestment = Clamped(g.Repro.EggInvestment, GeneSpec.EggInvestmentMin, GeneSpec.EggInvestmentMax, rng, sigmaFrac);
    }

    private static float Clamped(float value, float min, float max, IRandom rng, float sigmaFrac)
    {
        value += rng.NextGaussian(0f, sigmaFrac * (max - min));
        return Math.Clamp(value, min, max);
    }

    private static float Deg(float degrees) => degrees * MathF.PI / 180f;
}
