using System;
using System.Collections.Generic;
using System.Text.Json;
using Sim.Core;
using Sim.Core.Brain;
using Sim.Core.Config;
using Sim.Core.Entities;
using Sim.Core.Genetics;
using Sim.Persistence;

namespace Sim.Cli;

/// <summary>world_stats / species_stats snapshots (§7, §8).</summary>
internal sealed class StatsComputer
{
    // Cumulative counters as of the previous stats snapshot, to compute this snapshot's deltas.
    private long _prevEggsHatched, _prevEggsLaid, _prevEggsEaten;
    private long _prevDeathsStarvation, _prevDeathsPredation, _prevDeathsOldAge, _prevBites, _prevCapHits;

    // Reused across calls to sample-without-replacement for mean_pairwise_distance; seeded
    // arbitrarily since this is a reporting metric, not simulation state (§11 determinism only
    // governs Sim.Core's own RNG stream).
    private readonly Random _sampleRng = new();

    /// <summary>Seeds the delta baseline from a resumed World's cumulative counters, so the first post-resume snapshot reports only what happened since resume — not since tick 0.</summary>
    public void Prime(World world)
    {
        _prevEggsHatched = world.EggsHatched; _prevEggsLaid = world.EggsLaid; _prevEggsEaten = world.EggsEaten;
        _prevDeathsStarvation = world.DeathsStarvation; _prevDeathsPredation = world.DeathsPredation; _prevDeathsOldAge = world.DeathsOldAge;
        _prevBites = world.Bites; _prevCapHits = world.CapHits;
    }

    public (WorldStatsRow World, List<SpeciesStatsRow> SpeciesRows) Compute(World world, SpeciesConfig speciesCfg, float? ticksPerSecond)
    {
        int n = world.Population;

        var bySpecies = new Dictionary<int, List<Creature>>();
        foreach (var c in world.Creatures)
        {
            if (!bySpecies.TryGetValue(c.SpeciesId, out var list)) bySpecies[c.SpeciesId] = list = new List<Creature>();
            list.Add(c);
        }

        var whole = Aggregate(world.Creatures);

        int speciesCountMin5 = 0;
        double shannon = 0.0;
        foreach (var kv in bySpecies)
        {
            int pop = kv.Value.Count;
            if (pop >= 5) speciesCountMin5++;
            if (n > 0 && pop > 0)
            {
                double p = (double)pop / n;
                shannon -= p * Math.Log(p);
            }
        }

        var worldRow = new WorldStatsRow
        {
            Tick = world.CurrentTick,
            Population = n,
            Eggs = world.Eggs.Count,
            MeatItems = world.Meat.Count,
            PlantBiomassTotal = world.Plants.TotalBiomass(),
            MeatEnergyTotal = world.Meat.TotalEnergy(),
            CreatureEnergyTotal = world.TotalCreatureEnergy(),
            Births = (int)(world.EggsHatched - _prevEggsHatched),
            EggsLaid = (int)(world.EggsLaid - _prevEggsLaid),
            EggsEaten = (int)(world.EggsEaten - _prevEggsEaten),
            DeathsStarvation = (int)(world.DeathsStarvation - _prevDeathsStarvation),
            DeathsPredation = (int)(world.DeathsPredation - _prevDeathsPredation),
            DeathsOldAge = (int)(world.DeathsOldAge - _prevDeathsOldAge),
            Bites = (int)(world.Bites - _prevBites),
            CapHits = (int)(world.CapHits - _prevCapHits),
            MeanEnergy = whole.MeanEnergy,
            MeanAge = whole.MeanAge,
            MeanGeneration = whole.MeanGeneration,
            MaxGeneration = whole.MaxGeneration,
            MeanSize = whole.MeanSize,
            MeanSpeed = whole.MeanSpeed,
            MeanArmor = whole.MeanArmor,
            MeanDiet = whole.MeanDiet,
            MeanStorageCap = whole.MeanStorageCap,
            MeanLifespan = whole.MeanLifespan,
            MeanEggThreshold = whole.MeanEggThreshold,
            MeanEggInvestment = whole.MeanEggInvestment,
            MeanMutationRate = whole.MeanMutationRate,
            MeanStructuralRate = whole.MeanStructuralRate,
            MeanSensors = whole.MeanSensors,
            MeanActuators = whole.MeanActuators,
            MeanHidden = whole.MeanHidden,
            MeanLinks = whole.MeanLinks,
            SpeciesCount = bySpecies.Count,
            SpeciesCountMin5 = speciesCountMin5,
            Shannon = (float)shannon,
            MeanPairwiseDistance = ComputePairwiseSample(world.Creatures, speciesCfg),
            TicksPerSecond = ticksPerSecond,
        };

        var speciesRows = new List<SpeciesStatsRow>(bySpecies.Count);
        foreach (var kv in bySpecies)
        {
            var agg = Aggregate(kv.Value);
            speciesRows.Add(new SpeciesStatsRow
            {
                Tick = world.CurrentTick,
                SpeciesId = kv.Key,
                Population = kv.Value.Count,
                MeanSize = agg.MeanSize,
                MeanSpeed = agg.MeanSpeed,
                MeanArmor = agg.MeanArmor,
                MeanColorR = agg.MeanColorR,
                MeanColorG = agg.MeanColorG,
                MeanColorB = agg.MeanColorB,
                MeanDiet = agg.MeanDiet,
                MeanStorageCap = agg.MeanStorageCap,
                MeanLifespan = agg.MeanLifespan,
                MeanEggThreshold = agg.MeanEggThreshold,
                MeanEggInvestment = agg.MeanEggInvestment,
                MeanMutationRate = agg.MeanMutationRate,
                MeanStructuralRate = agg.MeanStructuralRate,
                MeanSensors = agg.MeanSensors,
                MeanActuators = agg.MeanActuators,
                MeanHidden = agg.MeanHidden,
                MeanLinks = agg.MeanLinks,
                MeanEnergy = agg.MeanEnergy,
                MeanAge = agg.MeanAge,
                SensorKindCountsJson = JsonSerializer.Serialize(agg.SensorKindCounts),
                ActuatorKindCountsJson = JsonSerializer.Serialize(agg.ActuatorKindCounts),
            });
        }

        _prevEggsHatched = world.EggsHatched; _prevEggsLaid = world.EggsLaid; _prevEggsEaten = world.EggsEaten;
        _prevDeathsStarvation = world.DeathsStarvation; _prevDeathsPredation = world.DeathsPredation; _prevDeathsOldAge = world.DeathsOldAge;
        _prevBites = world.Bites; _prevCapHits = world.CapHits;

        return (worldRow, speciesRows);
    }

    private readonly struct TraitAggregate
    {
        public readonly float MeanEnergy, MeanAge, MeanGeneration;
        public readonly int MaxGeneration;
        public readonly float MeanSize, MeanSpeed, MeanArmor, MeanColorR, MeanColorG, MeanColorB;
        public readonly float MeanDiet, MeanStorageCap, MeanLifespan, MeanEggThreshold, MeanEggInvestment;
        public readonly float MeanMutationRate, MeanStructuralRate;
        public readonly float MeanSensors, MeanActuators, MeanHidden, MeanLinks;
        public readonly Dictionary<string, int> SensorKindCounts;
        public readonly Dictionary<string, int> ActuatorKindCounts;

        public TraitAggregate(float meanEnergy, float meanAge, float meanGeneration, int maxGeneration,
            float meanSize, float meanSpeed, float meanArmor, float meanColorR, float meanColorG, float meanColorB,
            float meanDiet, float meanStorageCap, float meanLifespan, float meanEggThreshold, float meanEggInvestment,
            float meanMutationRate, float meanStructuralRate,
            float meanSensors, float meanActuators, float meanHidden, float meanLinks,
            Dictionary<string, int> sensorKindCounts, Dictionary<string, int> actuatorKindCounts)
        {
            MeanEnergy = meanEnergy; MeanAge = meanAge; MeanGeneration = meanGeneration; MaxGeneration = maxGeneration;
            MeanSize = meanSize; MeanSpeed = meanSpeed; MeanArmor = meanArmor;
            MeanColorR = meanColorR; MeanColorG = meanColorG; MeanColorB = meanColorB;
            MeanDiet = meanDiet; MeanStorageCap = meanStorageCap; MeanLifespan = meanLifespan;
            MeanEggThreshold = meanEggThreshold; MeanEggInvestment = meanEggInvestment;
            MeanMutationRate = meanMutationRate; MeanStructuralRate = meanStructuralRate;
            MeanSensors = meanSensors; MeanActuators = meanActuators; MeanHidden = meanHidden; MeanLinks = meanLinks;
            SensorKindCounts = sensorKindCounts; ActuatorKindCounts = actuatorKindCounts;
        }
    }

    /// <summary>Population-wide or per-species trait means (§8 world_stats/species_stats columns).</summary>
    private static TraitAggregate Aggregate(List<Creature> creatures)
    {
        int n = creatures.Count;
        double sumEnergy = 0, sumAge = 0, sumGeneration = 0;
        double sumSize = 0, sumSpeed = 0, sumArmor = 0, sumColorR = 0, sumColorG = 0, sumColorB = 0;
        double sumDiet = 0, sumStorageCap = 0, sumLifespan = 0, sumEggThreshold = 0, sumEggInvestment = 0;
        double sumMutationRate = 0, sumStructuralRate = 0;
        double sumSensors = 0, sumActuators = 0, sumHidden = 0, sumLinks = 0;
        int maxGeneration = 0;
        var sensorKindCounts = new Dictionary<string, int>();
        var actuatorKindCounts = new Dictionary<string, int>();
        var seenKinds = new HashSet<string>();

        foreach (var c in creatures)
        {
            sumEnergy += c.Energy; sumAge += c.Age; sumGeneration += c.Generation;
            if (c.Generation > maxGeneration) maxGeneration = c.Generation;
            sumSize += c.Size; sumSpeed += c.Speed; sumArmor += c.Armor;
            sumColorR += c.ColorR; sumColorG += c.ColorG; sumColorB += c.ColorB;
            sumDiet += c.Diet; sumStorageCap += c.StorageCap; sumLifespan += c.Lifespan;
            sumEggThreshold += c.EggThreshold; sumEggInvestment += c.EggInvestment;

            var genome = c.Genome;
            if (genome is null) continue;

            sumMutationRate += genome.Meta.MutationRate;
            sumStructuralRate += genome.Meta.StructuralRate;

            int hidden = 0, links = 0, sensors = 0, actuators = 0;
            foreach (var node in genome.Brain.Nodes) if (node.Kind == NodeKind.Hidden) hidden++;
            foreach (var link in genome.Brain.Links) if (link.Enabled) links++;
            sumHidden += hidden; sumLinks += links;

            seenKinds.Clear();
            foreach (var s in genome.Sensors)
            {
                if (!s.Enabled) continue;
                sensors++;
                string key = GeneSpec.SensorUsesChannel(s.Kind) ? $"{s.Kind}:{s.Channel}" : s.Kind.ToString();
                if (seenKinds.Add(key)) sensorKindCounts[key] = sensorKindCounts.GetValueOrDefault(key) + 1;
            }
            sumSensors += sensors;

            seenKinds.Clear();
            foreach (var a in genome.Actuators)
            {
                if (!a.Enabled) continue;
                actuators++;
                string key = GeneSpec.ActuatorUsesChannel(a.Kind) ? $"{a.Kind}:{a.Channel}" : a.Kind.ToString();
                if (seenKinds.Add(key)) actuatorKindCounts[key] = actuatorKindCounts.GetValueOrDefault(key) + 1;
            }
            sumActuators += actuators;
        }

        float Mean(double sum) => n > 0 ? (float)(sum / n) : 0f;

        return new TraitAggregate(
            Mean(sumEnergy), Mean(sumAge), Mean(sumGeneration), maxGeneration,
            Mean(sumSize), Mean(sumSpeed), Mean(sumArmor), Mean(sumColorR), Mean(sumColorG), Mean(sumColorB),
            Mean(sumDiet), Mean(sumStorageCap), Mean(sumLifespan), Mean(sumEggThreshold), Mean(sumEggInvestment),
            Mean(sumMutationRate), Mean(sumStructuralRate),
            Mean(sumSensors), Mean(sumActuators), Mean(sumHidden), Mean(sumLinks),
            sensorKindCounts, actuatorKindCounts);
    }

    /// <summary>§7: mean d over all pairs in a uniform random sample of up to sampleSize living creatures.</summary>
    private float ComputePairwiseSample(List<Creature> creatures, SpeciesConfig cfg)
    {
        var withGenome = new List<Creature>(creatures.Count);
        foreach (var c in creatures) if (c.Genome is not null) withGenome.Add(c);

        int total = withGenome.Count;
        int sampleSize = Math.Min(cfg.SampleSize, total);
        if (sampleSize < 2) return 0f;

        // Reservoir sampling for `sampleSize` distinct indices from [0, total).
        var sampled = new int[sampleSize];
        for (int i = 0; i < sampleSize; i++) sampled[i] = i;
        for (int i = sampleSize; i < total; i++)
        {
            int j = _sampleRng.Next(0, i + 1);
            if (j < sampleSize) sampled[j] = i;
        }

        var profiles = new GenomeDistance.Profile[sampleSize];
        for (int i = 0; i < sampleSize; i++) profiles[i] = GenomeDistance.Profile.Build(withGenome[sampled[i]].Genome!);

        double sum = 0.0;
        int pairs = 0;
        for (int i = 0; i < sampleSize; i++)
        {
            for (int j = i + 1; j < sampleSize; j++)
            {
                sum += GenomeDistance.Distance(profiles[i], profiles[j], cfg);
                pairs++;
            }
        }
        return pairs > 0 ? (float)(sum / pairs) : 0f;
    }
}
