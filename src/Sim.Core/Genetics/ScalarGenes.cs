namespace Sim.Core.Genetics;

/// <summary>§4.1.</summary>
public sealed class MetaGenes
{
    public float MutationRate;
    public float StructuralRate;

    public MetaGenes Clone() => new() { MutationRate = MutationRate, StructuralRate = StructuralRate };
}

public sealed class BodyGenes
{
    public float Size;
    public float Speed;
    public float Armor;
    public float ColorR;
    public float ColorG;
    public float ColorB;

    public BodyGenes Clone() => new()
    {
        Size = Size, Speed = Speed, Armor = Armor, ColorR = ColorR, ColorG = ColorG, ColorB = ColorB,
    };
}

public sealed class MetabolismGenes
{
    public float Diet;
    public float StorageCap;
    public float Lifespan;

    public MetabolismGenes Clone() => new() { Diet = Diet, StorageCap = StorageCap, Lifespan = Lifespan };
}

public sealed class ReproGenes
{
    public float EggThreshold;
    public float EggInvestment;

    public ReproGenes Clone() => new() { EggThreshold = EggThreshold, EggInvestment = EggInvestment };
}
