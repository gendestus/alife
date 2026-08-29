using System;

namespace Sim.Core.Brain;

/// <summary>
/// Decoded, flat-array brain state for one creature (§4.4). Synchronous one-step recurrent
/// evaluation: links read the previous tick's activations, so recurrent paths get one tick
/// of latency. No allocation in <see cref="Step"/>.
/// </summary>
public sealed class BrainRuntime
{
    private float[] _prev;
    private float[] _next;
    private readonly bool[] _isComputed; // true for Hidden/Output slots (tanh'd each step); false for Input/Bias (externally driven)
    private readonly int _biasSlot;
    private readonly int[] _inputSlots;  // slot per sensor input, ordered to match the SensorInputs array
    private readonly int[] _outputSlots; // slot per actuator output, ordered to match the genome's Actuators list
    private readonly int[] _linkFrom;
    private readonly int[] _linkTo;
    private readonly float[] _linkWeight;

    internal BrainRuntime(int nodeCount, int biasSlot, bool[] isComputed, int[] inputSlots, int[] outputSlots,
        int[] linkFrom, int[] linkTo, float[] linkWeight)
    {
        _prev = new float[nodeCount];
        _next = new float[nodeCount];
        _isComputed = isComputed;
        _biasSlot = biasSlot;
        _inputSlots = inputSlots;
        _outputSlots = outputSlots;
        _linkFrom = linkFrom;
        _linkTo = linkTo;
        _linkWeight = linkWeight;
        _prev[_biasSlot] = 1f;
    }

    /// <summary>One synchronous update. sensorValues.Length must equal the input slot count.</summary>
    public void Step(ReadOnlySpan<float> sensorValues)
    {
        for (int i = 0; i < _inputSlots.Length; i++) _prev[_inputSlots[i]] = sensorValues[i];
        _prev[_biasSlot] = 1f;

        for (int i = 0; i < _next.Length; i++)
        {
            if (_isComputed[i]) _next[i] = 0f;
        }

        for (int i = 0; i < _linkFrom.Length; i++)
        {
            _next[_linkTo[i]] += _linkWeight[i] * _prev[_linkFrom[i]];
        }

        for (int i = 0; i < _next.Length; i++)
        {
            if (_isComputed[i]) _next[i] = MathF.Tanh(_next[i]);
        }

        (_prev, _next) = (_next, _prev);
    }

    public float GetOutput(int actuatorIndex) => _prev[_outputSlots[actuatorIndex]];

    /// <summary>Test/debug hook: raw activation of a slot after the most recent Step().</summary>
    public float GetSlot(int slot) => _prev[slot];
}
