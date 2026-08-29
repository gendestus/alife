namespace Sim.Core.Random;

/// <summary>
/// xoshiro256** (Blackman &amp; Vigna). Deterministic, fast, non-cryptographic PRNG.
/// The sole RNG stream for a run — see <see cref="IRandom"/>.
/// </summary>
public sealed class Xoshiro256StarStar : IRandom
{
    private ulong _s0, _s1, _s2, _s3;
    private bool _hasSpareGaussian;
    private float _spareGaussian;

    public Xoshiro256StarStar(ulong seed)
    {
        // Seed the state via SplitMix64, as recommended by the algorithm's authors,
        // so a single 64-bit seed still yields well-distributed initial state.
        ulong z = seed;
        _s0 = SplitMix64(ref z);
        _s1 = SplitMix64(ref z);
        _s2 = SplitMix64(ref z);
        _s3 = SplitMix64(ref z);
        if ((_s0 | _s1 | _s2 | _s3) == 0)
        {
            _s0 = 1; // all-zero state is invalid for xoshiro
        }
    }

    private static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong RotL(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextUInt64()
    {
        ulong result = RotL(_s1 * 5, 7) * 9;

        ulong t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;

        _s2 ^= t;

        _s3 = RotL(_s3, 45);

        return result;
    }

    /// <summary>Explicit engine state — used by checkpoint save/restore.</summary>
    public (ulong s0, ulong s1, ulong s2, ulong s3) GetState() => (_s0, _s1, _s2, _s3);

    public void SetState(ulong s0, ulong s1, ulong s2, ulong s3)
    {
        _s0 = s0; _s1 = s1; _s2 = s2; _s3 = s3;
        _hasSpareGaussian = false;
    }

    public double NextDouble()
    {
        // Top 53 bits -> [0, 1).
        return (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    }

    public float NextFloat() => (float)NextDouble();

    public float NextFloat(float min, float max) => min + NextFloat() * (max - min);

    public int NextInt(int minInclusive, int maxExclusive)
    {
        uint range = (uint)(maxExclusive - minInclusive);
        // Lemire's method: a 32x32->64 multiply, so this needs a 32-bit draw, not the full
        // 64-bit output — multiplying by the full 64 bits silently overflows ulong and can
        // return a value >= range.
        uint r32 = (uint)NextUInt64();
        ulong m = (ulong)r32 * range;
        return minInclusive + (int)(m >> 32);
    }

    public float NextGaussian(float mean, float std)
    {
        if (_hasSpareGaussian)
        {
            _hasSpareGaussian = false;
            return mean + std * _spareGaussian;
        }

        // Marsaglia polar method.
        double u, v, s;
        do
        {
            u = NextDouble() * 2.0 - 1.0;
            v = NextDouble() * 2.0 - 1.0;
            s = u * u + v * v;
        } while (s >= 1.0 || s == 0.0);

        double mul = System.Math.Sqrt(-2.0 * System.Math.Log(s) / s);
        _spareGaussian = (float)(v * mul);
        _hasSpareGaussian = true;

        return mean + std * (float)(u * mul);
    }
}
