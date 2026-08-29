using System;
using System.Diagnostics;
using Sim.Core;

namespace Sim.Cli;

/// <summary>`sim bench` — no-db run that prints ticks/sec, population, and GC counts (§10, §13).</summary>
internal static class BenchCommand
{
    public static int Run(string[] args)
    {
        string configPath = "config/default.json";
        long ticks = 20_000;
        ulong seed = 42;
        bool legacyRandomWalk = false;
        var overrides = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": configPath = args[++i]; break;
                case "--ticks": ticks = long.Parse(args[++i]); break;
                case "--seed": seed = ulong.Parse(args[++i]); break;
                case "--set": overrides.Add(args[++i]); break;
                case "--legacy-random-walk": legacyRandomWalk = true; break;
                default: throw new ArgumentException($"Unknown bench argument '{args[i]}'.");
            }
        }

        var config = ConfigLoader.Load(configPath);
        foreach (var o in overrides) ConfigLoader.ApplyOverride(config, o);

        var world = new World(config, seed);
        world.VerboseLogging = false;
        if (legacyRandomWalk) world.BootstrapSpawn(config.Life.BootstrapCount);
        else world.BootstrapSpawnFromGenome(config.Life.BootstrapCount);

        GC.Collect();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);

        int minPop = int.MaxValue, maxPop = int.MinValue;

        var sw = Stopwatch.StartNew();
        for (long t = 0; t < ticks; t++)
        {
            world.Tick();

            if (world.Population < minPop) minPop = world.Population;
            if (world.Population > maxPop) maxPop = world.Population;

            if (t % 500 == 0)
            {
                double tps = t == 0 ? 0 : t / sw.Elapsed.TotalSeconds;
                double sumSize = 0, sumSpeed = 0, sumDiet = 0, sumMaxEnergy = 0, sumEggThreshold = 0, sumEggInvestment = 0;
                double sumSensors = 0, sumActuators = 0, sumHidden = 0, sumLinks = 0, sumMutRate = 0;
                int n = world.Creatures.Count;
                for (int i = 0; i < n; i++)
                {
                    var c = world.Creatures[i];
                    sumSize += c.Size; sumSpeed += c.Speed; sumDiet += c.Diet; sumMaxEnergy += c.MaxEnergy;
                    sumEggThreshold += c.EggThreshold; sumEggInvestment += c.EggInvestment;
                    var g = c.Genome;
                    if (g != null)
                    {
                        sumSensors += g.Sensors.Count;
                        sumActuators += g.Actuators.Count;
                        sumMutRate += g.Meta.MutationRate;
                        int hidden = 0, links = 0;
                        foreach (var node in g.Brain.Nodes) if (node.Kind == Sim.Core.Brain.NodeKind.Hidden) hidden++;
                        foreach (var link in g.Brain.Links) if (link.Enabled) links++;
                        sumHidden += hidden; sumLinks += links;
                    }
                }
                string traits = n > 0
                    ? $" size={sumSize / n:F2} speed={sumSpeed / n:F2} diet={sumDiet / n:F2} maxE={sumMaxEnergy / n:F1}" +
                      $" eggThr={sumEggThreshold / n:F1} eggInv={sumEggInvestment / n:F1} mutR={sumMutRate / n:F3}" +
                      $" sensors={sumSensors / n:F1} actuators={sumActuators / n:F1} hidden={sumHidden / n:F1} links={sumLinks / n:F1}"
                    : "";
                Console.Error.WriteLine($"tick={world.CurrentTick} pop={world.Population} eggs={world.Eggs.Count} tps={tps:F0}{traits}");
            }

            if (world.Extinct)
            {
                Console.Error.WriteLine($"tick={world.CurrentTick} EXTINCT");
                break;
            }
        }
        sw.Stop();

        int dGen0 = GC.CollectionCount(0) - gen0;
        int dGen1 = GC.CollectionCount(1) - gen1;
        int dGen2 = GC.CollectionCount(2) - gen2;

        Console.WriteLine($"ticks={world.CurrentTick}");
        Console.WriteLine($"population={world.Population}");
        Console.WriteLine($"min_population={minPop} max_population={maxPop}");
        Console.WriteLine($"eggs={world.Eggs.Count}");
        Console.WriteLine($"elapsed_s={sw.Elapsed.TotalSeconds:F3}");
        Console.WriteLine($"ticks_per_sec={world.CurrentTick / sw.Elapsed.TotalSeconds:F1}");
        Console.WriteLine($"gc_gen0={dGen0} gc_gen1={dGen1} gc_gen2={dGen2}");
        Console.WriteLine($"deaths_starvation={world.DeathsStarvation} deaths_old_age={world.DeathsOldAge} deaths_predation={world.DeathsPredation}");
        Console.WriteLine($"eggs_laid={world.EggsLaid} eggs_hatched={world.EggsHatched} eggs_eaten={world.EggsEaten} cap_hits={world.CapHits}");

        return 0;
    }
}
