namespace Sim.Core;

/// <summary>
/// Deterministic 2-octave value noise on an integer lattice, hashed from (x, y, seed) —
/// no external noise library, no floating drift across platforms beyond normal float math.
/// Output is in [0, 1].
/// </summary>
public sealed class ValueNoise2D
{
    private readonly uint _seed;

    public ValueNoise2D(uint seed)
    {
        _seed = seed;
    }

    private float Lattice(int x, int y)
    {
        unchecked
        {
            uint h = (uint)x * 0x27D4EB2Fu ^ (uint)y * 0x165667B1u ^ _seed * 0x85EBCA6Bu;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    /// <summary>Single-octave value noise, lattice spaced at 1/frequency cells.</summary>
    private float Octave(float x, float y, float frequency)
    {
        float fx = x * frequency;
        float fy = y * frequency;
        int x0 = (int)System.MathF.Floor(fx);
        int y0 = (int)System.MathF.Floor(fy);
        int x1 = x0 + 1;
        int y1 = y0 + 1;

        float tx = Smooth(fx - x0);
        float ty = Smooth(fy - y0);

        float v00 = Lattice(x0, y0);
        float v10 = Lattice(x1, y0);
        float v01 = Lattice(x0, y1);
        float v11 = Lattice(x1, y1);

        float a = v00 + (v10 - v00) * tx;
        float b = v01 + (v11 - v01) * tx;
        return a + (b - a) * ty;
    }

    /// <summary>2-octave value noise at (x, y), normalized to [0, 1].</summary>
    public float Sample(float x, float y)
    {
        const float freq1 = 1f / 24f;
        const float freq2 = freq1 * 2f;
        const float amp1 = 1f;
        const float amp2 = 0.5f;

        float sum = Octave(x, y, freq1) * amp1 + Octave(x, y, freq2) * amp2;
        return sum / (amp1 + amp2);
    }
}
