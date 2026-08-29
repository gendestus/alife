using System.Collections.Generic;
using Sim.Core.Brain;

namespace Sim.Core.Genetics;

/// <summary>A plain record of typed sections (§4). Mutation operates on fields, so every mutation yields a decodable genome by construction.</summary>
public sealed class Genome
{
    public MetaGenes Meta { get; set; } = new();
    public BodyGenes Body { get; set; } = new();
    public MetabolismGenes Metabolism { get; set; } = new();
    public List<SensorGene> Sensors { get; set; } = new();
    public List<ActuatorGene> Actuators { get; set; } = new();
    public BrainGenome Brain { get; set; } = new();
    public ReproGenes Repro { get; set; } = new();

    public Genome Clone()
    {
        var clone = new Genome
        {
            Meta = Meta.Clone(),
            Body = Body.Clone(),
            Metabolism = Metabolism.Clone(),
            Repro = Repro.Clone(),
            Brain = Brain.Clone(),
        };
        foreach (var s in Sensors) clone.Sensors.Add(s.Clone());
        foreach (var a in Actuators) clone.Actuators.Add(a.Clone());
        return clone;
    }
}
