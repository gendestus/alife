namespace Sim.Core;

/// <summary>Corpse energy dropped on creature death (§2, §5). Decays and is removed when small.</summary>
public struct Meat
{
    public float X;
    public float Y;
    public float Energy;

    public Meat(float x, float y, float energy)
    {
        X = x;
        Y = y;
        Energy = energy;
    }
}
