using Sim.Core.Brain;
using Sim.Core.Config;
using Sim.Core.Genetics;
using Sim.Core.Random;
using Xunit;

namespace Sim.Core.Tests;

/// <summary>DESIGN.md §12 test 7.</summary>
public class SpeciationTests
{
    [Fact]
    public void IdenticalGenomes_AreOneSpecies()
    {
        var rng = new Xoshiro256StarStar(seed: 1);
        var tracker = new InnovationTracker();
        var g = GenomeFactory.CreateBootstrap(rng, tracker);
        var cfg = new SpeciesConfig(); // defaults: c1=1, c2=0.4, c3=2, c4=1, delta=3.0

        float d = GenomeDistance.Compute(g, g.Clone(), cfg);

        Assert.Equal(0f, d);
        Assert.True(d < cfg.Delta);
    }

    /// <summary>
    /// Isolates the (E+D)/N link-topology term with a reduced δ: under the production weights
    /// (c1=1), that term is mathematically bounded by 2·min(|A|,|B|)/max(|A|,|B|) ≤ 2, so no
    /// amount of pure link disagreement crosses the production δ=3.0 on its own — by design,
    /// small structural mutations shouldn't fork a new species. This asserts the term's
    /// direction and magnitude are correct, not the tuned production threshold (that's an
    /// empirical M5 acceptance check, not a unit test).
    /// </summary>
    [Fact]
    public void DisjointLinkSets_ExceedReducedDelta()
    {
        var a = MinimalGenomeWithLinks(linkCount: 20, innovationOffset: 0);   // innovations 0..19
        var b = MinimalGenomeWithLinks(linkCount: 20, innovationOffset: 20);  // innovations 20..39, fully disjoint from a
        var cfg = new SpeciesConfig { Delta = 1.5f };

        float d = GenomeDistance.Compute(a, b, cfg);

        Assert.True(d > cfg.Delta, $"expected distance > {cfg.Delta}, got {d}");
    }

    private static Genome MinimalGenomeWithLinks(int linkCount, int innovationOffset)
    {
        var g = new Genome();
        for (int i = 0; i < linkCount; i++)
        {
            g.Brain.Links.Add(new BrainLink { Innovation = innovationOffset + i, From = 0, To = 1, Weight = 0f, Enabled = true });
        }
        return g;
    }

    /// <summary>M5 accept criterion (§13): the bootstrap population, run through the real speciation pass, should form 1-3 species under production δ.</summary>
    [Fact]
    public void Bootstrap_FormsFewSpecies()
    {
        var config = new SimConfig();
        config.Life.BootstrapCount = 200;
        config.Life.PopCap = 300;

        var world = new World(config, seed: 123);
        world.VerboseLogging = false;
        world.BootstrapSpawnFromGenome(config.Life.BootstrapCount);

        world.Tick(); // speciateEvery defaults to 100, and the pass fires at pre-increment CurrentTick==0

        var speciesIds = new System.Collections.Generic.HashSet<int>();
        foreach (var c in world.Creatures) speciesIds.Add(c.SpeciesId);

        Assert.InRange(speciesIds.Count, 1, 3);
    }
}
