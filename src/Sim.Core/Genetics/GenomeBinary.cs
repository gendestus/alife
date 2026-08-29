using System.Collections.Generic;
using System.IO;
using Sim.Core.Brain;

namespace Sim.Core.Genetics;

/// <summary>
/// Binary (de)serialization of a Genome, for checkpointing — deliberately not JSON-based, so
/// Sim.Core never needs a JSON parser just to round-trip a checkpoint.
/// </summary>
public static class GenomeBinary
{
    public static void Write(BinaryWriter w, Genome g)
    {
        w.Write(g.Meta.MutationRate);
        w.Write(g.Meta.StructuralRate);

        w.Write(g.Body.Size);
        w.Write(g.Body.Speed);
        w.Write(g.Body.Armor);
        w.Write(g.Body.ColorR);
        w.Write(g.Body.ColorG);
        w.Write(g.Body.ColorB);

        w.Write(g.Metabolism.Diet);
        w.Write(g.Metabolism.StorageCap);
        w.Write(g.Metabolism.Lifespan);

        w.Write(g.Repro.EggThreshold);
        w.Write(g.Repro.EggInvestment);

        w.Write(g.Sensors.Count);
        foreach (var s in g.Sensors)
        {
            w.Write(s.Id);
            w.Write((int)s.Kind);
            w.Write(s.Channel);
            w.Write(s.Range);
            w.Write(s.Angle);
            w.Write(s.Fov);
            w.Write(s.Enabled);
        }

        w.Write(g.Actuators.Count);
        foreach (var a in g.Actuators)
        {
            w.Write(a.Id);
            w.Write((int)a.Kind);
            w.Write(a.Channel);
            w.Write(a.Strength);
            w.Write(a.Enabled);
        }

        w.Write(g.Brain.Nodes.Count);
        foreach (var n in g.Brain.Nodes)
        {
            w.Write(n.Id);
            w.Write((int)n.Kind);
            w.Write(n.BindGeneId.HasValue);
            if (n.BindGeneId.HasValue) w.Write(n.BindGeneId.Value);
            w.Write(n.BindSlot.HasValue);
            if (n.BindSlot.HasValue) w.Write(n.BindSlot.Value);
        }

        w.Write(g.Brain.Links.Count);
        foreach (var l in g.Brain.Links)
        {
            w.Write(l.Innovation);
            w.Write(l.From);
            w.Write(l.To);
            w.Write(l.Weight);
            w.Write(l.Enabled);
        }
    }

    public static Genome Read(BinaryReader r)
    {
        var g = new Genome
        {
            Meta = new MetaGenes { MutationRate = r.ReadSingle(), StructuralRate = r.ReadSingle() },
            Body = new BodyGenes
            {
                Size = r.ReadSingle(),
                Speed = r.ReadSingle(),
                Armor = r.ReadSingle(),
                ColorR = r.ReadSingle(),
                ColorG = r.ReadSingle(),
                ColorB = r.ReadSingle(),
            },
            Metabolism = new MetabolismGenes
            {
                Diet = r.ReadSingle(),
                StorageCap = r.ReadSingle(),
                Lifespan = r.ReadSingle(),
            },
            Repro = new ReproGenes { EggThreshold = r.ReadSingle(), EggInvestment = r.ReadSingle() },
        };

        int sensorCount = r.ReadInt32();
        for (int i = 0; i < sensorCount; i++)
        {
            g.Sensors.Add(new SensorGene
            {
                Id = r.ReadInt64(),
                Kind = (SensorKind)r.ReadInt32(),
                Channel = r.ReadInt32(),
                Range = r.ReadSingle(),
                Angle = r.ReadSingle(),
                Fov = r.ReadSingle(),
                Enabled = r.ReadBoolean(),
            });
        }

        int actuatorCount = r.ReadInt32();
        for (int i = 0; i < actuatorCount; i++)
        {
            g.Actuators.Add(new ActuatorGene
            {
                Id = r.ReadInt64(),
                Kind = (ActuatorKind)r.ReadInt32(),
                Channel = r.ReadInt32(),
                Strength = r.ReadSingle(),
                Enabled = r.ReadBoolean(),
            });
        }

        int nodeCount = r.ReadInt32();
        for (int i = 0; i < nodeCount; i++)
        {
            var node = new BrainNode { Id = r.ReadInt32(), Kind = (NodeKind)r.ReadInt32() };
            if (r.ReadBoolean()) node.BindGeneId = r.ReadInt64();
            if (r.ReadBoolean()) node.BindSlot = r.ReadInt32();
            g.Brain.Nodes.Add(node);
        }

        int linkCount = r.ReadInt32();
        for (int i = 0; i < linkCount; i++)
        {
            g.Brain.Links.Add(new BrainLink
            {
                Innovation = r.ReadInt32(),
                From = r.ReadInt32(),
                To = r.ReadInt32(),
                Weight = r.ReadSingle(),
                Enabled = r.ReadBoolean(),
            });
        }

        return g;
    }
}
