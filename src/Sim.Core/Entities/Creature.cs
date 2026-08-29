namespace Sim.Core.Entities;

/// <summary>
/// M1 creature: fixed scalar traits (a hardcoded stand-in for the genome, which arrives in M2)
/// and a random-walk controller. No brain, no sensors/actuators genes, no reproduction yet.
/// </summary>
public sealed class Creature
{
    public ulong Id;
    public float X, Y, Heading;
    public float Energy, MaxEnergy, Health, MaxHealth;
    public int Age;
    public long BirthTick;
    public bool Alive;

    // Fixed traits (§4.1 scalar genes, hardcoded for M1).
    public float Size;
    public float Speed;
    public float Armor;
    public float Diet;
    public float StorageCap;
    public float Lifespan;
}
