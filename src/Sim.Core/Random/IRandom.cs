namespace Sim.Core.Random;

/// <summary>
/// The single deterministic random stream for a run. Owned by <c>World</c>, consumed in a
/// fixed order by <c>Sim.Core</c>. No other source of randomness (wall clock, GUIDs, etc.)
/// may appear in <c>Sim.Core</c>.
/// </summary>
public interface IRandom
{
    ulong NextUInt64();

    /// <summary>Uniform double in [0, 1).</summary>
    double NextDouble();

    /// <summary>Uniform float in [0, 1).</summary>
    float NextFloat();

    /// <summary>Uniform float in [min, max).</summary>
    float NextFloat(float min, float max);

    /// <summary>Uniform int in [minInclusive, maxExclusive).</summary>
    int NextInt(int minInclusive, int maxExclusive);

    /// <summary>Standard normal sample scaled/shifted to N(mean, std).</summary>
    float NextGaussian(float mean, float std);

    /// <summary>Full internal state, for hashing (§12 test 1) and checkpointing.</summary>
    (ulong S0, ulong S1, ulong S2, ulong S3) GetState();

    void SetState(ulong s0, ulong s1, ulong s2, ulong s3);
}
