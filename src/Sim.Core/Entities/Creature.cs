using Sim.Core.Brain;
using Sim.Core.Genetics;

namespace Sim.Core.Entities;

/// <summary>
/// A living creature. Traits (Size/Speed/.../Lifespan) are copied from the genome at hatch
/// for fast per-tick access. If Genome/Brain are null, the creature runs the M1 random-walk
/// controller instead — kept around as the baseline for the M2 steering comparison (§13 M2).
/// </summary>
public sealed class Creature
{
    public ulong Id;
    public float X, Y, Heading;
    public float Energy, MaxEnergy, Health, MaxHealth;
    public int Age;
    public long BirthTick;
    public bool Alive;

    // Lineage (§3). SpeciesId is a placeholder (0) until speciation lands in M5.
    public long GenomeId;
    public int SpeciesId;
    public ulong? ParentId;
    public int Generation;
    public int OffspringCount;

    // Scalar traits (§4.1), copied from the genome at hatch (or hardcoded for the M1 baseline).
    public float Size;
    public float Speed;
    public float Armor;
    public float Diet;
    public float StorageCap;
    public float Lifespan;
    public float EggThreshold;
    public float EggInvestment;
    public float ColorR, ColorG, ColorB;

    public ulong LastDamagedBy;
    public long LastDamagedTick;

    /// <summary>Cached at spawn: sum of all static per-tick costs (basal/armor/store/life/sensors/actuators/brain).</summary>
    public float PassiveCostPerTick;

    public Genome? Genome;
    public BrainRuntime? Brain;
    public float[] SensorInputs = System.Array.Empty<float>();
    public float[] ActuatorOutputs = System.Array.Empty<float>();
}
