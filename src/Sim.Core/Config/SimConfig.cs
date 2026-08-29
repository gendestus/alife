namespace Sim.Core.Config;

/// <summary>
/// Fully resolved configuration, mirroring config/default.json (see DESIGN.md §9).
/// Plain data only — Sim.Core has zero external dependencies, so JSON (de)serialization
/// happens in Sim.Cli, which populates these POCOs.
/// </summary>
public sealed class SimConfig
{
    public WorldConfig World { get; set; } = new();
    public EnergyConfig Energy { get; set; } = new();
    public LifeConfig Life { get; set; } = new();
    public MutationConfig Mutation { get; set; } = new();
    public SpeciesConfig Species { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

public sealed class WorldConfig
{
    public float Width { get; set; } = 512;
    public bool Toroidal { get; set; } = false;
    public int PlantGrid { get; set; } = 128;
    public float BMax { get; set; } = 10;
    public float PlantRate { get; set; } = 0.01f;
    public float PlantSeed { get; set; } = 0.002f;
    public float CapacityMin { get; set; } = 0.2f;
    public int NoiseSeedOffset { get; set; } = 0;
    public int ScentGrid { get; set; } = 64;
    public int ScentStep { get; set; } = 4;
    public float ScentDecay { get; set; } = 0.97f;
    public float ScentDiffuse { get; set; } = 0.2f;
    public float HashCell { get; set; } = 8;
    public float CorpseEnergy { get; set; } = 30;
    public float MeatDecay { get; set; } = 0.995f;
}

public sealed class EnergyConfig
{
    public float CBasal { get; set; } = 0.03f;
    public float CArmor { get; set; } = 0.03f;
    public float CStore { get; set; } = 0.01f;
    public float CLife { get; set; } = 0.01f;
    public float CVis { get; set; } = 0.01f;
    public float CMove { get; set; } = 0.05f;
    public float CTurn { get; set; } = 0.005f;
    public float CEmit { get; set; } = 0.01f;
    public float CEggOverhead { get; set; } = 5;
    public float CNode { get; set; } = 0.002f;
    public float CLink { get; set; } = 0.0005f;
    public float ActuatorPassive { get; set; } = 0.002f;
    public float EatRate { get; set; } = 2;
    public float EnergyPerBiomass { get; set; } = 1;
    public float DietExp { get; set; } = 1.5f;
    public float MaxTurn { get; set; } = 0.3f;
    public float HealthRegen { get; set; } = 0.05f;
}

public sealed class LifeConfig
{
    public int MaturityTicks { get; set; } = 150;
    public int IncubationTicks { get; set; } = 50;
    public int PopCap { get; set; } = 6000;
    public bool ReseedOnExtinction { get; set; } = false;
    public int BootstrapCount { get; set; } = 600;
}

public sealed class MutationConfig
{
    public float MutationScale { get; set; } = 1.0f;
    public float StructuralScale { get; set; } = 1.0f;
    public int MaxSensors { get; set; } = 12;
    public int MaxActuators { get; set; } = 10;
    public int MaxHidden { get; set; } = 64;
    public int MaxLinks { get; set; } = 512;
}

public sealed class SpeciesConfig
{
    public float C1 { get; set; } = 1.0f;
    public float C2 { get; set; } = 0.4f;
    public float C3 { get; set; } = 2.0f;
    public float C4 { get; set; } = 1.0f;
    public float Delta { get; set; } = 3.0f;
    public int SpeciateEvery { get; set; } = 100;
    public int RetainTicks { get; set; } = 2000;
    public int SampleSize { get; set; } = 200;
}

public sealed class LoggingConfig
{
    public int StatsEvery { get; set; } = 100;
    public int PositionsEvery { get; set; } = 100;
    public int PositionModulo { get; set; } = 1;
    public bool LogBites { get; set; } = true;
}
