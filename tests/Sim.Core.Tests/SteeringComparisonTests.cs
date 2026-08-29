using System;
using Sim.Core.Brain;
using Sim.Core.Config;
using Sim.Core.Genetics;
using Sim.Core.Random;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Core.Tests;

/// <summary>
/// DESIGN.md §13 M2 acceptance: "A hand-written genome (VisionPlant -> Thrust/Turn wiring
/// that steers toward food) outlives the random-walk controller by > 2x mean lifespan over
/// 10 seeds." One of each per seed, sharing a world/food field so both face identical
/// conditions.
/// </summary>
public class SteeringComparisonTests
{
    private readonly ITestOutputHelper _output;

    public SteeringComparisonTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Random-walk (M1 baseline) always uses a hardcoded lifespan of 2000 — moot here, since it
    // starves well before that in this scenario regardless. The steering genome's own lifespan
    // is raised well past 2000 so its ceiling doesn't artificially compress the gap; BMax is
    // raised to compensate for the resulting higher cLife maintenance draw.
    private const float SteeringLifespan = 4000f;
    private const long MaxTicks = 4500;

    private static Genome BuildSteeringGenome()
    {
        var g = new Genome();
        g.Meta.MutationRate = 0.03f;
        g.Meta.StructuralRate = 0.02f;
        g.Body.Size = 1f; g.Body.Speed = 1f; g.Body.Armor = 0f;
        g.Body.ColorR = 0.4f; g.Body.ColorG = 0.8f; g.Body.ColorB = 0.2f;
        g.Metabolism.Diet = 0f; g.Metabolism.StorageCap = 1f; g.Metabolism.Lifespan = SteeringLifespan;
        g.Repro.EggThreshold = 80f; g.Repro.EggInvestment = 40f;

        var bias = new BrainNode { Id = NodeIds.Bias, Kind = NodeKind.Bias };
        g.Brain.Nodes.Add(bias);

        const float coneAngle = 0.7854f; // 45 degrees
        const float fov = 1.047f;        // 60 degrees
        var left = new SensorGene { Id = 0, Kind = SensorKind.VisionPlant, Range = 15f, Angle = coneAngle, Fov = fov, Enabled = true };
        var right = new SensorGene { Id = 1, Kind = SensorKind.VisionPlant, Range = 15f, Angle = -coneAngle, Fov = fov, Enabled = true };
        g.Sensors.Add(left);
        g.Sensors.Add(right);
        var leftNode = new BrainNode { Id = NodeIds.SensorInputNodeId(left.Id, 0), Kind = NodeKind.Input, BindGeneId = left.Id, BindSlot = 0 };
        var rightNode = new BrainNode { Id = NodeIds.SensorInputNodeId(right.Id, 0), Kind = NodeKind.Input, BindGeneId = right.Id, BindSlot = 0 };
        g.Brain.Nodes.Add(leftNode);
        g.Brain.Nodes.Add(rightNode);

        var thrust = new ActuatorGene { Id = 0, Kind = ActuatorKind.Thrust, Strength = 1f, Enabled = true };
        var turn = new ActuatorGene { Id = 1, Kind = ActuatorKind.Turn, Strength = 1f, Enabled = true };
        var eat = new ActuatorGene { Id = 2, Kind = ActuatorKind.Eat, Enabled = true };
        g.Actuators.Add(thrust);
        g.Actuators.Add(turn);
        g.Actuators.Add(eat);
        var thrustNode = new BrainNode { Id = NodeIds.ActuatorOutputNodeId(thrust.Id), Kind = NodeKind.Output, BindGeneId = thrust.Id };
        var turnNode = new BrainNode { Id = NodeIds.ActuatorOutputNodeId(turn.Id), Kind = NodeKind.Output, BindGeneId = turn.Id };
        var eatNode = new BrainNode { Id = NodeIds.ActuatorOutputNodeId(eat.Id), Kind = NodeKind.Output, BindGeneId = eat.Id };
        g.Brain.Nodes.Add(thrustNode);
        g.Brain.Nodes.Add(turnNode);
        g.Brain.Nodes.Add(eatNode);

        int nextInnovation = 0;
        void Link(int from, int to, float w) =>
            g.Brain.Links.Add(new BrainLink { Innovation = nextInnovation++, From = from, To = to, Weight = w, Enabled = true });

        // Small positive baseline: keep slowly exploring once the local patch is exhausted,
        // rather than going idle and waiting at a depleted spot.
        Link(bias.Id, thrustNode.Id, 0.15f);
        Link(leftNode.Id, thrustNode.Id, 3.0f);  // visible food -> move, harder the more there is
        Link(rightNode.Id, thrustNode.Id, 3.0f);
        Link(leftNode.Id, turnNode.Id, 12.0f);   // more food on the left -> turn hard left
        Link(rightNode.Id, turnNode.Id, -12.0f); // more food on the right -> turn hard right
        Link(bias.Id, eatNode.Id, 3.0f);         // always attempt to eat (tanh(3) > 0.5)

        return g;
    }

    private static SimConfig BuildScenarioConfig()
    {
        // Scarce, patchy world: plenty for a genome that actively seeks food, not enough for
        // one that eats only whatever happens to be underfoot.
        var config = new SimConfig();
        config.World.Width = 100f;
        config.World.PlantGrid = 25;   // cell size 4, same ratio as default.json
        config.World.BMax = 0.9f;
        config.World.PlantRate = 0.01f;
        config.World.PlantSeed = 0.002f;
        config.World.CapacityMin = 0.02f;
        return config;
    }

    private (float randomWalkAge, float steeringAge) RunOneSeed(ulong seed)
    {
        var config = BuildScenarioConfig();
        var world = new World(config, seed);
        var placer = new Xoshiro256StarStar(seed ^ 0xA5A5A5A5UL);
        float w = config.World.Width;

        world.BootstrapSpawn(1); // M1 baseline: random-walk controller
        var randomWalk = world.Creatures[^1];

        float x = placer.NextFloat(0f, w), y = placer.NextFloat(0f, w), h = placer.NextFloat(0f, MathF.PI * 2f);
        var steering = world.SpawnFromGenome(BuildSteeringGenome(), x, y, h);

        for (long t = 0; t < MaxTicks && world.Population > 0; t++)
        {
            world.Tick();
        }

        return (randomWalk.Age, steering.Age);
    }

    [Fact]
    public void SteeringGenome_OutlivesRandomWalk_ByMoreThan2x_Over10Seeds()
    {
        double rwTotal = 0, stTotal = 0;
        const int seedCount = 10;

        for (ulong seed = 0; seed < seedCount; seed++)
        {
            var (rw, st) = RunOneSeed(seed * 7919UL + 1UL);
            rwTotal += rw;
            stTotal += st;
            _output.WriteLine($"seed {seed}: random-walk age={rw}, steering age={st}, ratio={st / MathF.Max(rw, 1e-6f):F2}x");
        }

        double rwMean = rwTotal / seedCount;
        double stMean = stTotal / seedCount;
        _output.WriteLine($"overall: random-walk={rwMean:F1}, steering={stMean:F1}, ratio={stMean / rwMean:F2}x");

        Assert.True(stMean > 2.0 * rwMean, $"expected steering mean age ({stMean:F1}) > 2x random-walk mean age ({rwMean:F1})");
    }
}
