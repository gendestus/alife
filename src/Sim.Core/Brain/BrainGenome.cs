using System.Collections.Generic;

namespace Sim.Core.Brain;

/// <summary>A node in the genome's brain graph (§4.4). BindGeneId/BindSlot are set for Input/Output nodes.</summary>
public sealed class BrainNode
{
    public int Id;
    public NodeKind Kind;
    public long? BindGeneId;
    public int? BindSlot;

    public BrainNode Clone() => new() { Id = Id, Kind = Kind, BindGeneId = BindGeneId, BindSlot = BindSlot };
}

/// <summary>A directed, weighted connection between two brain nodes (§4.4).</summary>
public sealed class BrainLink
{
    public int Innovation;
    public int From;
    public int To;
    public float Weight;
    public bool Enabled;

    public BrainLink Clone() => new() { Innovation = Innovation, From = From, To = To, Weight = Weight, Enabled = Enabled };
}

/// <summary>The genome-side (undecoded) brain graph: nodes + links.</summary>
public sealed class BrainGenome
{
    public List<BrainNode> Nodes { get; set; } = new();
    public List<BrainLink> Links { get; set; } = new();

    public BrainGenome Clone()
    {
        var clone = new BrainGenome();
        foreach (var n in Nodes) clone.Nodes.Add(n.Clone());
        foreach (var l in Links) clone.Links.Add(l.Clone());
        return clone;
    }
}
