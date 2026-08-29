using Sim.Core.Config;

namespace Sim.Core;

/// <summary>
/// P×P grid of plant biomass (§2). Capacity is fixed at construction from 2-octave value
/// noise; biomass regrows toward capacity each tick via logistic growth + a small constant
/// seed term (so barren-looking cells can still recover from zero).
/// </summary>
public sealed class PlantGrid
{
    public int P { get; }
    public float CellSize { get; }
    public float[] Biomass { get; }
    public float[] Capacity { get; }

    private readonly float _r;
    private readonly float _seedRate;

    public PlantGrid(WorldConfig config)
    {
        P = config.PlantGrid;
        CellSize = config.Width / P;
        _r = config.PlantRate;
        _seedRate = config.PlantSeed;

        Biomass = new float[P * P];
        Capacity = new float[P * P];

        var noise = new ValueNoise2D((uint)config.NoiseSeedOffset);
        float kMin = config.CapacityMin * config.BMax;
        float kMax = config.BMax;

        for (int cy = 0; cy < P; cy++)
        {
            int row = cy * P;
            for (int cx = 0; cx < P; cx++)
            {
                float n = noise.Sample(cx, cy);
                float k = kMin + n * (kMax - kMin);
                Capacity[row + cx] = k;
                Biomass[row + cx] = k; // world starts fertile: cells begin at their capacity
            }
        }
    }

    /// <summary>Advance biomass one tick: logistic regrowth + constant seed influx, clamped [0, K]. Returns total biomass added.</summary>
    public float Regrow()
    {
        // Accumulate in double: summing ~16k float32 cells in float would drown the
        // energy-conservation invariant (§12 test 5) in its own rounding error.
        double added = 0.0;
        for (int i = 0; i < Biomass.Length; i++)
        {
            float b = Biomass[i];
            float k = Capacity[i];
            b += _r * b * (1f - b / k) + _seedRate;
            if (b < 0f) b = 0f;
            else if (b > k) b = k;
            added += (double)b - Biomass[i];
            Biomass[i] = b;
        }
        return (float)added;
    }

    public int CellIndex(float x, float y)
    {
        int cx = (int)(x / CellSize);
        int cy = (int)(y / CellSize);
        if (cx < 0) cx = 0; else if (cx >= P) cx = P - 1;
        if (cy < 0) cy = 0; else if (cy >= P) cy = P - 1;
        return cy * P + cx;
    }

    public float TotalBiomass()
    {
        double sum = 0.0;
        for (int i = 0; i < Biomass.Length; i++) sum += Biomass[i];
        return (float)sum;
    }
}
