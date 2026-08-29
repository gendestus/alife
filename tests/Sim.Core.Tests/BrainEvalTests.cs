using System;
using System.Collections.Generic;
using Sim.Core.Brain;
using Sim.Core.Genetics;
using Xunit;

namespace Sim.Core.Tests;

/// <summary>DESIGN.md §12 test 4.</summary>
public class BrainEvalTests
{
    // Every genome needs exactly one sensor + one actuator gene so the decoder has real
    // input/output nodes to bind to; the test genomes just choose which links use them.
    private static (Genome genome, int inputNodeId, int outputNodeId) MakeBaseGenome()
    {
        var sensor = new SensorGene { Id = 0, Kind = SensorKind.Energy, Enabled = true };
        var actuator = new ActuatorGene { Id = 0, Kind = ActuatorKind.Thrust, Strength = 1f, Enabled = true };
        int inputNodeId = NodeIds.SensorInputNodeId(sensor.Id, 0);
        int outputNodeId = NodeIds.ActuatorOutputNodeId(actuator.Id);

        var genome = new Genome();
        genome.Sensors.Add(sensor);
        genome.Actuators.Add(actuator);
        genome.Brain.Nodes.Add(new BrainNode { Id = NodeIds.Bias, Kind = NodeKind.Bias });
        genome.Brain.Nodes.Add(new BrainNode { Id = inputNodeId, Kind = NodeKind.Input, BindGeneId = sensor.Id, BindSlot = 0 });
        genome.Brain.Nodes.Add(new BrainNode { Id = outputNodeId, Kind = NodeKind.Output, BindGeneId = actuator.Id });

        return (genome, inputNodeId, outputNodeId);
    }

    [Fact]
    public void BiasToOutput_ProducesTanhOfWeight()
    {
        var (genome, _, outputNodeId) = MakeBaseGenome();
        genome.Brain.Links.Add(new BrainLink { Innovation = 0, From = NodeIds.Bias, To = outputNodeId, Weight = 2f, Enabled = true });

        var brain = BrainDecoder.Decode(genome);
        brain.Step(stackalloc float[] { 0f });

        Assert.Equal(MathF.Tanh(2f), brain.GetOutput(0), precision: 5);
    }

    [Fact]
    public void InputToOutput_ProducesTanhOfWeightedInput()
    {
        var (genome, _, outputNodeId) = MakeBaseGenome();
        genome.Brain.Links.Add(new BrainLink { Innovation = 0, From = NodeIds.SensorInputNodeId(0, 0), To = outputNodeId, Weight = 3f, Enabled = true });

        var brain = BrainDecoder.Decode(genome);
        brain.Step(stackalloc float[] { 0.5f });

        Assert.Equal(MathF.Tanh(1.5f), brain.GetOutput(0), precision: 5);
    }

    [Fact]
    public void DisabledLink_IsIgnored()
    {
        var (genome, _, outputNodeId) = MakeBaseGenome();
        genome.Brain.Links.Add(new BrainLink { Innovation = 0, From = NodeIds.Bias, To = outputNodeId, Weight = 5f, Enabled = false });

        var brain = BrainDecoder.Decode(genome);
        brain.Step(stackalloc float[] { 0f });

        Assert.Equal(0f, brain.GetOutput(0), precision: 5);
    }

    [Fact]
    public void RecurrentSelfLink_AccumulatesAcrossTicks()
    {
        var (genome, inputNodeId, outputNodeId) = MakeBaseGenome();
        int hiddenId = 1;
        genome.Brain.Nodes.Add(new BrainNode { Id = hiddenId, Kind = NodeKind.Hidden });

        const float wIn = 1f, wSelf = 0.5f, wOut = 1f;
        genome.Brain.Links.Add(new BrainLink { Innovation = 0, From = inputNodeId, To = hiddenId, Weight = wIn, Enabled = true });
        genome.Brain.Links.Add(new BrainLink { Innovation = 1, From = hiddenId, To = hiddenId, Weight = wSelf, Enabled = true });
        genome.Brain.Links.Add(new BrainLink { Innovation = 2, From = hiddenId, To = outputNodeId, Weight = wOut, Enabled = true });

        var brain = BrainDecoder.Decode(genome);

        // Independent reference recurrence, per §4.4's synchronous update rule, to check
        // the implementation against — not just re-deriving the same code.
        float refHiddenPrev = 0f;
        const float x = 1f;
        var sensorValues = new float[] { x };

        for (int t = 0; t < 5; t++)
        {
            float refHiddenNext = MathF.Tanh(wSelf * refHiddenPrev + wIn * x);
            float refOutput = MathF.Tanh(wOut * refHiddenPrev); // reads hidden's *previous* value, same tick

            brain.Step(sensorValues);

            Assert.Equal(refOutput, brain.GetOutput(0), precision: 5);

            refHiddenPrev = refHiddenNext;
        }
    }
}
