using Sim.Core.Config;
using Xunit;

namespace Sim.Core.Tests;

/// <summary>DESIGN.md §12 test 1.</summary>
public class DeterminismTests
{
    [Fact]
    public void TwoWorlds_SameSeedAndConfig_IdenticalStateHash_After20kTicks()
    {
        // A small, tightly-capped population is enough to exercise the hash — this test is
        // about determinism, not ecology, and the C# scalar defaults (unlike the tuned
        // config/default.json) reproduce the M3 population-runaway dynamic given enough ticks.
        // A low popCap keeps per-tick cost bounded regardless of where that dynamic goes.
        var configA = new SimConfig();
        configA.Life.BootstrapCount = 40;
        configA.Life.PopCap = 100;
        var configB = new SimConfig();
        configB.Life.BootstrapCount = 40;
        configB.Life.PopCap = 100;

        var worldA = new World(configA, seed: 123);
        worldA.VerboseLogging = false;
        worldA.BootstrapSpawnFromGenome(configA.Life.BootstrapCount);

        var worldB = new World(configB, seed: 123);
        worldB.VerboseLogging = false;
        worldB.BootstrapSpawnFromGenome(configB.Life.BootstrapCount);

        for (int t = 0; t < 20_000; t++)
        {
            worldA.Tick();
            worldB.Tick();
        }

        byte[] hashA = worldA.ComputeStateHash();
        byte[] hashB = worldB.ComputeStateHash();

        Assert.Equal(System.Convert.ToHexString(hashA), System.Convert.ToHexString(hashB));
    }
}
