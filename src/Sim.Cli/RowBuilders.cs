using System.Collections.Generic;
using System.Text.Json;
using Sim.Core;
using Sim.Core.Brain;
using Sim.Core.Genetics;
using Sim.Persistence;

namespace Sim.Cli;

/// <summary>Translates World's plain event payloads into Sim.Persistence row objects (§8).</summary>
internal static class RowBuilders
{
    public static GenomeRow BuildGenomeRow(GenomeCreatedInfo info)
    {
        var g = info.Genome;
        int nSensors = 0, nActuators = 0, nHidden = 0, nLinks = 0;
        var sensorKinds = new Dictionary<string, int>();
        var actuatorKinds = new Dictionary<string, int>();

        foreach (var s in g.Sensors)
        {
            if (!s.Enabled) continue;
            nSensors++;
            string key = GeneSpec.SensorUsesChannel(s.Kind) ? $"{s.Kind}:{s.Channel}" : s.Kind.ToString();
            sensorKinds[key] = sensorKinds.GetValueOrDefault(key) + 1;
        }
        foreach (var a in g.Actuators)
        {
            if (!a.Enabled) continue;
            nActuators++;
            string key = GeneSpec.ActuatorUsesChannel(a.Kind) ? $"{a.Kind}:{a.Channel}" : a.Kind.ToString();
            actuatorKinds[key] = actuatorKinds.GetValueOrDefault(key) + 1;
        }
        foreach (var n in g.Brain.Nodes) if (n.Kind == NodeKind.Hidden) nHidden++;
        foreach (var l in g.Brain.Links) if (l.Enabled) nLinks++;

        return new GenomeRow
        {
            GenomeId = info.GenomeId,
            ParentGenomeId = info.ParentGenomeId,
            FirstSeenTick = info.FirstSeenTick,
            Hash = g.Hash(),
            DataJson = g.ToCanonicalJson(),
            Size = g.Body.Size,
            Speed = g.Body.Speed,
            Armor = g.Body.Armor,
            ColorR = g.Body.ColorR,
            ColorG = g.Body.ColorG,
            ColorB = g.Body.ColorB,
            Diet = g.Metabolism.Diet,
            StorageCap = g.Metabolism.StorageCap,
            Lifespan = g.Metabolism.Lifespan,
            EggThreshold = g.Repro.EggThreshold,
            EggInvestment = g.Repro.EggInvestment,
            MutationRate = g.Meta.MutationRate,
            StructuralRate = g.Meta.StructuralRate,
            NSensors = (short)nSensors,
            NActuators = (short)nActuators,
            NHidden = (short)nHidden,
            NLinks = (short)nLinks,
            SensorKindsJson = JsonSerializer.Serialize(sensorKinds),
            ActuatorKindsJson = JsonSerializer.Serialize(actuatorKinds),
        };
    }

    public static SpeciesRow BuildSpeciesRow(SpeciesCreatedInfo info) => new()
    {
        SpeciesId = info.SpeciesId,
        FoundedTick = info.FoundedTick,
        FounderGenomeId = info.FounderGenomeId,
        ParentSpeciesId = info.ParentSpeciesId,
    };

    public static CreatureRow BuildCreatureRow(Sim.Core.Entities.Creature c) => new()
    {
        CreatureId = c.Id,
        GenomeId = c.GenomeId,
        SpeciesId = c.SpeciesId,
        ParentCreatureId = c.ParentId,
        Generation = c.Generation,
        BirthTick = c.BirthTick,
        BirthX = c.X,
        BirthY = c.Y,
    };

    public static CreatureDeathRow BuildDeathRow(Sim.Core.Entities.Creature c, Sim.Core.Entities.DeathCause cause, long tick) => new()
    {
        CreatureId = c.Id,
        DeathTick = tick,
        Cause = cause,
        X = c.X,
        Y = c.Y,
        Age = c.Age,
        EnergyAtDeath = c.Energy,
        KillerCreatureId = cause == Sim.Core.Entities.DeathCause.PREDATION ? c.LastDamagedBy : null,
        OffspringCount = c.OffspringCount,
        SpeciesId = c.SpeciesId,
    };
}
