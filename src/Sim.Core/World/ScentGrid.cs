using Sim.Core.Config;

namespace Sim.Core;

/// <summary>
/// 4-channel S×S scent grid (§2). Advanced only every scentStep ticks: decay, then
/// 4-neighbor diffusion. Emit actuators deposit; Smell sensors sample.
/// </summary>
public sealed class ScentGrid
{
    private const int Channels = 4;
    private const float MaxValue = 100f;

    public const int ChannelCount = Channels;
    public int S { get; }
    public float CellSize { get; }

    private readonly float[][] _values; // [channel][cell]
    private readonly float[][] _scratch;
    private readonly float _decay;
    private readonly float _alpha;
    private readonly bool _toroidal;

    public ScentGrid(WorldConfig config)
    {
        S = config.ScentGrid;
        CellSize = config.Width / S;
        _decay = config.ScentDecay;
        _alpha = config.ScentDiffuse;
        _toroidal = config.Toroidal;

        _values = new float[Channels][];
        _scratch = new float[Channels][];
        for (int c = 0; c < Channels; c++)
        {
            _values[c] = new float[S * S];
            _scratch[c] = new float[S * S];
        }
    }

    private int CellIndex(float x, float y)
    {
        int cx = (int)(x / CellSize);
        int cy = (int)(y / CellSize);
        if (cx < 0) cx = 0; else if (cx >= S) cx = S - 1;
        if (cy < 0) cy = 0; else if (cy >= S) cy = S - 1;
        return cy * S + cx;
    }

    public float Sample(int channel, float x, float y) => _values[channel][CellIndex(x, y)];

    /// <summary>Live reference to a channel's cell array — for checkpoint save only; don't mutate.</summary>
    public float[] GetChannelValues(int channel) => _values[channel];

    /// <summary>For checkpoint load: overwrite a channel's cells from a saved array of the same length.</summary>
    public void SetChannelValues(int channel, float[] values) => System.Array.Copy(values, _values[channel], _values[channel].Length);

    public void Deposit(int channel, float x, float y, float amount)
    {
        int i = CellIndex(x, y);
        float v = _values[channel][i] + amount;
        if (v < 0f) v = 0f; else if (v > MaxValue) v = MaxValue;
        _values[channel][i] = v;
    }

    /// <summary>Decay + 4-neighbor diffusion, run every scentStep ticks.</summary>
    public void Step()
    {
        for (int c = 0; c < Channels; c++)
        {
            var src = _values[c];
            var dst = _scratch[c];

            for (int cy = 0; cy < S; cy++)
            {
                int row = cy * S;
                for (int cx = 0; cx < S; cx++)
                {
                    float decayed = src[row + cx] * _decay;
                    dst[row + cx] = decayed;
                }
            }

            for (int cy = 0; cy < S; cy++)
            {
                int row = cy * S;
                for (int cx = 0; cx < S; cx++)
                {
                    float center = dst[row + cx];
                    float sum = 0f;
                    int count = 0;
                    AccumulateNeighbor(dst, cx - 1, cy, ref sum, ref count);
                    AccumulateNeighbor(dst, cx + 1, cy, ref sum, ref count);
                    AccumulateNeighbor(dst, cx, cy - 1, ref sum, ref count);
                    AccumulateNeighbor(dst, cx, cy + 1, ref sum, ref count);
                    float mean = count > 0 ? sum / count : center;
                    src[row + cx] = (1f - _alpha) * center + _alpha * mean;
                }
            }
        }
    }

    private void AccumulateNeighbor(float[] grid, int cx, int cy, ref float sum, ref int count)
    {
        if (_toroidal)
        {
            cx = ((cx % S) + S) % S;
            cy = ((cy % S) + S) % S;
        }
        else
        {
            if (cx < 0 || cx >= S || cy < 0 || cy >= S) return;
        }
        sum += grid[cy * S + cx];
        count++;
    }
}
