using System;
using System.Collections.Generic;
using Sim.Core.Brain;
using Sim.Core.Config;
using Sim.Core.Genetics;
using Xunit;

namespace Sim.Core.Tests;

/// <summary>DESIGN.md §12 test 6.</summary>
public class SensorGeometryTests
{
    // A creature with zero actuators and one dummy sensor: decodes fine, never acts, never moves.
    private static Genome MakeStationaryGenome()
    {
        var g = new Genome();
        var sensor = new SensorGene { Id = 0, Kind = SensorKind.Energy, Enabled = true };
        g.Sensors.Add(sensor);
        g.Brain.Nodes.Add(new BrainNode { Id = NodeIds.Bias, Kind = NodeKind.Bias });
        g.Brain.Nodes.Add(new BrainNode { Id = NodeIds.SensorInputNodeId(sensor.Id, 0), Kind = NodeKind.Input, BindGeneId = sensor.Id, BindSlot = 0 });
        g.Body.Size = 1f; g.Body.Speed = 0f; g.Metabolism.StorageCap = 1f; g.Metabolism.Lifespan = 2000f;
        return g;
    }

    private static Genome MakeObserverGenome(SensorGene sensor)
    {
        var g = new Genome();
        g.Sensors.Add(sensor);
        g.Brain.Nodes.Add(new BrainNode { Id = NodeIds.Bias, Kind = NodeKind.Bias });
        for (int slot = 0; slot < GeneSpec.SensorSlotCount(sensor.Kind); slot++)
        {
            g.Brain.Nodes.Add(new BrainNode { Id = NodeIds.SensorInputNodeId(sensor.Id, slot), Kind = NodeKind.Input, BindGeneId = sensor.Id, BindSlot = slot });
        }
        g.Body.Size = 1f; g.Body.Speed = 0f; g.Metabolism.StorageCap = 1f; g.Metabolism.Lifespan = 2000f;
        return g;
    }

    private static World NewWorld()
    {
        var config = new SimConfig();
        config.World.Width = 512;
        return new World(config, seed: 1);
    }

    [Fact]
    public void VisionCreature_SeesTargetInCone_AtExpectedBearingAndDistance()
    {
        var world = NewWorld();
        var sensorGene = new SensorGene { Id = 0, Kind = SensorKind.VisionCreature, Range = 20f, Angle = 0f, Fov = MathF.PI / 2f, Enabled = true };
        var observer = world.SpawnFromGenome(MakeObserverGenome(sensorGene), x: 100, y: 100, heading: 0f);

        var targetGenome = MakeStationaryGenome();
        targetGenome.Body.ColorR = 0.8f; targetGenome.Body.ColorG = 0.1f; targetGenome.Body.ColorB = 0.3f;
        targetGenome.Body.Size = 1.5f;
        // Directly ahead (bearing 0 == observer heading), distance 10, well inside a 20-range/90-degree-fov cone.
        world.SpawnFromGenome(targetGenome, x: 110, y: 100, heading: 0f);

        world.Tick();

        Assert.Equal(0.8f, observer.SensorInputs[0], precision: 3);
        Assert.Equal(0.1f, observer.SensorInputs[1], precision: 3);
        Assert.Equal(0.3f, observer.SensorInputs[2], precision: 3);
        Assert.Equal(1f - 10f / 20f, observer.SensorInputs[3], precision: 3);
        Assert.Equal(1.5f / (1.5f + 1f), observer.SensorInputs[4], precision: 3);
    }

    [Fact]
    public void VisionCreature_IgnoresTargetOutsideCone()
    {
        var world = NewWorld();
        var sensorGene = new SensorGene { Id = 0, Kind = SensorKind.VisionCreature, Range = 20f, Angle = 0f, Fov = MathF.PI / 2f, Enabled = true };
        var observer = world.SpawnFromGenome(MakeObserverGenome(sensorGene), x: 100, y: 100, heading: 0f);

        var targetGenome = MakeStationaryGenome();
        // Directly behind the observer (bearing == heading + pi) — outside a 90-degree cone.
        world.SpawnFromGenome(targetGenome, x: 90, y: 100, heading: 0f);

        world.Tick();

        for (int i = 0; i < 5; i++) Assert.Equal(0f, observer.SensorInputs[i], precision: 5);
    }

    [Fact]
    public void VisionCreature_IgnoresTargetBeyondRange()
    {
        var world = NewWorld();
        var sensorGene = new SensorGene { Id = 0, Kind = SensorKind.VisionCreature, Range = 5f, Angle = 0f, Fov = MathF.PI / 2f, Enabled = true };
        var observer = world.SpawnFromGenome(MakeObserverGenome(sensorGene), x: 100, y: 100, heading: 0f);

        var targetGenome = MakeStationaryGenome();
        world.SpawnFromGenome(targetGenome, x: 120, y: 100, heading: 0f); // 20 units away, range is only 5

        world.Tick();

        for (int i = 0; i < 5; i++) Assert.Equal(0f, observer.SensorInputs[i], precision: 5);
    }

    [Fact]
    public void Smell_LeftRight_RespondToGradient()
    {
        var world = NewWorld();
        var sensorGene = new SensorGene { Id = 0, Kind = SensorKind.Smell, Channel = 0, Range = 4f, Enabled = true };
        var observer = world.SpawnFromGenome(MakeObserverGenome(sensorGene), x: 200, y: 200, heading: 0f);

        // Same whisker geometry as World.Sensing's ComputeSmell, so the deposit lands exactly
        // on the sampled cell for the left whisker and nowhere near the right one.
        const float whiskerAngle = MathF.PI / 3f;
        float leftDir = observer.Heading + whiskerAngle;
        float lx = observer.X + MathF.Cos(leftDir) * sensorGene.Range;
        float ly = observer.Y + MathF.Sin(leftDir) * sensorGene.Range;
        world.Scent.Deposit(sensorGene.Channel, lx, ly, 80f);

        world.Tick();

        float left = observer.SensorInputs[0];
        float right = observer.SensorInputs[1];
        Assert.True(left > right, $"expected left ({left}) > right ({right}) toward the deposited gradient");
        Assert.True(left > 0f, "left whisker should detect the deposited scent");
    }
}
