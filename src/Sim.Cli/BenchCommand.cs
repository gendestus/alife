using System;
using System.Diagnostics;
using Sim.Core;

namespace Sim.Cli;

/// <summary>`sim bench` — no-db run that prints ticks/sec, population, and GC counts (§10, §13 M1).</summary>
internal static class BenchCommand
{
    public static int Run(string[] args)
    {
        string configPath = "config/default.json";
        long ticks = 20_000;
        ulong seed = 42;
        var overrides = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": configPath = args[++i]; break;
                case "--ticks": ticks = long.Parse(args[++i]); break;
                case "--seed": seed = ulong.Parse(args[++i]); break;
                case "--set": overrides.Add(args[++i]); break;
                default: throw new ArgumentException($"Unknown bench argument '{args[i]}'.");
            }
        }

        var config = ConfigLoader.Load(configPath);
        foreach (var o in overrides) ConfigLoader.ApplyOverride(config, o);

        var world = new World(config, seed);
        world.BootstrapSpawn(config.Life.BootstrapCount);

        GC.Collect();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);

        var sw = Stopwatch.StartNew();
        for (long t = 0; t < ticks; t++)
        {
            world.Tick();

            if (t % 1000 == 0)
            {
                double tps = t == 0 ? 0 : t / sw.Elapsed.TotalSeconds;
                Console.Error.WriteLine($"tick={world.CurrentTick} pop={world.Population} eggs=0 species=1 tps={tps:F0}");
            }

        }
        sw.Stop();

        int dGen0 = GC.CollectionCount(0) - gen0;
        int dGen1 = GC.CollectionCount(1) - gen1;
        int dGen2 = GC.CollectionCount(2) - gen2;

        Console.WriteLine($"ticks={world.CurrentTick}");
        Console.WriteLine($"population={world.Population}");
        Console.WriteLine($"elapsed_s={sw.Elapsed.TotalSeconds:F3}");
        Console.WriteLine($"ticks_per_sec={world.CurrentTick / sw.Elapsed.TotalSeconds:F1}");
        Console.WriteLine($"gc_gen0={dGen0} gc_gen1={dGen1} gc_gen2={dGen2}");
        Console.WriteLine($"deaths_starvation={world.DeathsStarvation} deaths_old_age={world.DeathsOldAge} deaths_predation={world.DeathsPredation}");

        return 0;
    }
}
