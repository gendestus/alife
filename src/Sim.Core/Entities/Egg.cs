using Sim.Core.Genetics;

namespace Sim.Core.Entities;

/// <summary>
/// Holds the already-mutated genome from lay time (§6): mutation happens once, at lay, not
/// at hatch, so an egg eaten before hatching still carries a persisted, decodable genome.
/// </summary>
public sealed class Egg
{
    public ulong Id;
    public Genome Genome = null!;
    public long GenomeId;
    public float X, Y;
    public float Energy;
    public long LaidTick;
    public long HatchTick;
    public ulong ParentId;
    public int SpeciesId;
    public int Generation;
}
