using System;

namespace Sim.Persistence;

// Plain row DTOs, one per db/schema.sql table (minus run, handled specially — see Writer.cs).
// PersistenceWriter buckets incoming objects by concrete type, so these are deliberately
// distinct classes rather than a shared base.

public enum EventKind
{
    EGG_LAID,
    HATCH,
    EGG_EATEN,
    BITE,
    RESEED,
}

public enum RunStatus
{
    RUNNING,
    COMPLETED,
    EXTINCT,
    ERROR,
}

public sealed class GenomeRow
{
    public long GenomeId;
    public long? ParentGenomeId;
    public long FirstSeenTick;
    public byte[] Hash = Array.Empty<byte>();
    public string DataJson = "{}";
    public float Size, Speed, Armor, ColorR, ColorG, ColorB;
    public float Diet, StorageCap, Lifespan;
    public float EggThreshold, EggInvestment;
    public float MutationRate, StructuralRate;
    public short NSensors, NActuators, NHidden, NLinks;
    public string SensorKindsJson = "{}";
    public string ActuatorKindsJson = "{}";
}

public sealed class SpeciesRow
{
    public int SpeciesId;
    public long FoundedTick;
    public long FounderGenomeId;
    public int? ParentSpeciesId;
}

public sealed class CreatureRow
{
    public ulong CreatureId;
    public long GenomeId;
    public int SpeciesId;
    public ulong? ParentCreatureId;
    public int Generation;
    public long BirthTick;
    public float BirthX, BirthY;
}

public sealed class CreatureDeathRow
{
    public ulong CreatureId;
    public long DeathTick;
    public Sim.Core.Entities.DeathCause Cause;
    public float X, Y;
    public int Age;
    public float EnergyAtDeath;
    public ulong? KillerCreatureId;
    public int OffspringCount;
    public int SpeciesId;
}

public sealed class EventRow
{
    public long Tick;
    public int Seq;
    public EventKind Kind;
    public long? ActorId;
    public long? TargetId;
    public float? X, Y;
    public float? Value;
    public string? DataJson;
}

public sealed class WorldStatsRow
{
    public long Tick;
    public int Population, Eggs, MeatItems;
    public float PlantBiomassTotal, MeatEnergyTotal, CreatureEnergyTotal;
    public int Births, EggsLaid, EggsEaten, DeathsStarvation, DeathsPredation, DeathsOldAge, Bites, CapHits;
    public float MeanEnergy, MeanAge, MeanGeneration;
    public int MaxGeneration;
    public float MeanSize, MeanSpeed, MeanArmor, MeanDiet, MeanStorageCap, MeanLifespan;
    public float MeanEggThreshold, MeanEggInvestment, MeanMutationRate, MeanStructuralRate;
    public float MeanSensors, MeanActuators, MeanHidden, MeanLinks;
    public int SpeciesCount, SpeciesCountMin5;
    public float Shannon, MeanPairwiseDistance;
    public float? TicksPerSecond;
}

public sealed class SpeciesStatsRow
{
    public long Tick;
    public int SpeciesId;
    public int Population;
    public float MeanSize, MeanSpeed, MeanArmor, MeanColorR, MeanColorG, MeanColorB;
    public float MeanDiet, MeanStorageCap, MeanLifespan, MeanEggThreshold, MeanEggInvestment;
    public float MeanMutationRate, MeanStructuralRate;
    public float MeanSensors, MeanActuators, MeanHidden, MeanLinks;
    public float MeanEnergy, MeanAge;
    public string SensorKindCountsJson = "{}";
    public string ActuatorKindCountsJson = "{}";
}

public sealed class PositionSampleRow
{
    public long Tick;
    public ulong CreatureId;
    public int SpeciesId;
    public float X, Y, Heading, Energy, Health;
}
