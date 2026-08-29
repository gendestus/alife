namespace Sim.Core.Genetics;

/// <summary>§4.2. `id` is run-global monotonic — it doubles as the innovation number for genome alignment.</summary>
public sealed class SensorGene
{
    public long Id;
    public SensorKind Kind;
    public int Channel;   // Smell only, [0,3]
    public float Range;
    public float Angle;
    public float Fov;
    public bool Enabled;

    public SensorGene Clone() => new()
    {
        Id = Id, Kind = Kind, Channel = Channel, Range = Range, Angle = Angle, Fov = Fov, Enabled = Enabled,
    };
}
