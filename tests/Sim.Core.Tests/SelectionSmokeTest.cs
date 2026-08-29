using System;
using System.IO;
using System.Text.Json;
using Sim.Core.Config;
using Sim.Core.Genetics;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Core.Tests;

/// <summary>DESIGN.md §12 test 8 (slow).</summary>
public class SelectionSmokeTest
{
    private readonly ITestOutputHelper _output;

    public SelectionSmokeTest(ITestOutputHelper output)
    {
        _output = output;
    }

    private static SimConfig LoadDefaultConfig()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config", "default.json");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SimConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static float MeanSpeed(World world)
    {
        if (world.Population == 0) return 0f;
        double sum = 0;
        foreach (var c in world.Creatures) sum += c.Speed;
        return (float)(sum / world.Population);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Bootstrap_MeanSpeedDriftsBeyond3Sigma_Over200kTicks()
    {
        var config = LoadDefaultConfig();
        var world = new World(config, seed: 7);
        world.VerboseLogging = false;
        world.BootstrapSpawnFromGenome(config.Life.BootstrapCount);

        float meanSpeedAt0 = MeanSpeed(world);

        for (long t = 0; t < 200_000; t++)
        {
            world.Tick();
        }

        float meanSpeedAt200k = MeanSpeed(world);
        float diff = MathF.Abs(meanSpeedAt200k - meanSpeedAt0);

        // Bootstrap perturbation (§4.5): each scalar gene, including Body.Speed, is perturbed
        // by N(0, 2%*range) at spawn. 3 sigma of that is the "more than noise" bar test 8 sets.
        float sigma = 0.02f * (GeneSpec.SpeedMax - GeneSpec.SpeedMin);
        float threshold = 3f * sigma;

        _output.WriteLine($"meanSpeed(0)={meanSpeedAt0:F3} meanSpeed(200k)={meanSpeedAt200k:F3} diff={diff:F3} threshold={threshold:F3} finalPop={world.Population}");

        Assert.False(world.Extinct, "population went extinct before tick 200k");
        Assert.True(diff > threshold, $"expected |delta mean_speed| > {threshold:F3}, got {diff:F3}");
    }
}
