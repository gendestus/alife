namespace Sim.Core.Genetics;

/// <summary>§4.3. `id` is run-global monotonic, assigned like sensor gene ids.</summary>
public sealed class ActuatorGene
{
    public long Id;
    public ActuatorKind Kind;
    public int Channel;   // Emit only, [0,3]
    public float Strength;
    public bool Enabled;

    public ActuatorGene Clone() => new()
    {
        Id = Id, Kind = Kind, Channel = Channel, Strength = Strength, Enabled = Enabled,
    };
}
