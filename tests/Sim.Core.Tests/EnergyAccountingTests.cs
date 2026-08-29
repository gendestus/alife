using Sim.Core.Config;
using Xunit;

namespace Sim.Core.Tests;

/// <summary>DESIGN.md §12 test 5.</summary>
public class EnergyAccountingTests
{
    [Fact]
    public void PoolEnergyConservedEachTick_WhenDeathDisabled()
    {
        var config = new SimConfig();
        config.Life.BootstrapCount = 50;

        var world = new World(config, seed: 123);
        world.SuppressDeath = true; // reproduction is not implemented until M3 — nothing to disable there
        world.BootstrapSpawn(config.Life.BootstrapCount);

        for (int t = 0; t < 2000; t++)
        {
            float before = world.PoolEnergy();
            world.Tick();
            float after = world.PoolEnergy();

            float expectedDelta = world.LastTickPlantRegrowth - world.LastTickCosts;
            float actualDelta = after - before;

            // Tolerance is float32 ULP-scale at pool magnitude (~1e5): PoolEnergy() rounds a
            // double-precision sum down to float, twice per tick, so ~0.02 is the real floor.
            Assert.True(
                System.MathF.Abs(actualDelta - expectedDelta) < 0.02f,
                $"tick {t}: expected delta={expectedDelta}, actual delta={actualDelta}, pool before={before}, after={after}");
        }

        Assert.Equal(config.Life.BootstrapCount, world.Population); // nobody died
    }
}
