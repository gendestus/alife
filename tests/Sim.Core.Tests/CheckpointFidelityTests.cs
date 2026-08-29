using System;
using System.IO;
using Sim.Core.Config;
using Xunit;

namespace Sim.Core.Tests;

/// <summary>DESIGN.md §12 test 2.</summary>
public class CheckpointFidelityTests
{
    private static SimConfig MakeConfig()
    {
        var config = new SimConfig();
        config.Life.BootstrapCount = 40;
        config.Life.PopCap = 100; // keep this test fast — see DeterminismTests for why
        return config;
    }

    [Fact]
    public void Run20k_CheckpointAt10k_Resume_MatchesUninterruptedRun()
    {
        const ulong seed = 55;

        var reference = new World(MakeConfig(), seed);
        reference.VerboseLogging = false;
        reference.BootstrapSpawnFromGenome(40);
        for (int t = 0; t < 20_000; t++) reference.Tick();
        byte[] referenceHash = reference.ComputeStateHash();

        var worldA = new World(MakeConfig(), seed);
        worldA.VerboseLogging = false;
        worldA.BootstrapSpawnFromGenome(40);
        for (int t = 0; t < 10_000; t++) worldA.Tick();

        using var stream = new MemoryStream();
        worldA.WriteCheckpoint(stream, configJson: "{}");

        stream.Position = 0;
        var (tick, _, resumedSeed) = World.ReadCheckpointHeader(stream);
        var worldB = new World(MakeConfig(), resumedSeed);
        worldB.VerboseLogging = false;
        worldB.ReadCheckpointBody(stream, tick);

        for (long t = tick; t < 20_000; t++) worldB.Tick();
        byte[] resumedHash = worldB.ComputeStateHash();

        Assert.Equal(Convert.ToHexString(referenceHash), Convert.ToHexString(resumedHash));
    }
}
