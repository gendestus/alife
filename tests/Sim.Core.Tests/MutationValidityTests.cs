using Sim.Core.Brain;
using Sim.Core.Config;
using Sim.Core.Genetics;
using Sim.Core.Random;
using Xunit;

namespace Sim.Core.Tests;

/// <summary>DESIGN.md §12 test 3.</summary>
public class MutationValidityTests
{
    [Fact]
    public void ChainedRandomMutations_AlwaysValidateAndDecode()
    {
        var caps = new MutationConfig(); // defaults: 12 sensors, 10 actuators, 64 hidden, 512 links
        IRandom rng = new Xoshiro256StarStar(seed: 999);
        var tracker = new InnovationTracker();

        var genome = GenomeFactory.CreateBootstrap(rng, tracker);
        genome.Validate(caps);

        for (int i = 0; i < 100_000; i++)
        {
            genome = GenomeMutator.Mutate(genome, rng, tracker, caps);

            genome.Validate(caps);
            var brain = BrainDecoder.Decode(genome); // must decode without throwing
            brain.Step(new float[genome.Sensors.Count == 0 ? 0 : SensorInputCount(genome)]);

            Assert.True(genome.Sensors.Count <= caps.MaxSensors);
            Assert.True(genome.Actuators.Count <= caps.MaxActuators);
            Assert.True(genome.Brain.Links.Count <= caps.MaxLinks);
            int hiddenCount = 0;
            foreach (var n in genome.Brain.Nodes)
            {
                if (n.Kind == NodeKind.Hidden) hiddenCount++;
            }
            Assert.True(hiddenCount <= caps.MaxHidden);
        }
    }

    private static int SensorInputCount(Genome g)
    {
        int total = 0;
        foreach (var s in g.Sensors) total += GeneSpec.SensorSlotCount(s.Kind);
        return total;
    }
}
