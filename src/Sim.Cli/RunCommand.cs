using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Npgsql;
using Sim.Core;
using Sim.Persistence;

namespace Sim.Cli;

/// <summary>`sim run` — full simulation run, optionally writing to Postgres (§8, §10).</summary>
internal static class RunCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string configPath = "config/default.json";
        long ticks = 1_000_000;
        ulong seed = 42;
        string? db = null;
        bool noDb = false;
        string? notes = null;
        long? checkpointEvery = null;
        var overrides = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": configPath = args[++i]; break;
                case "--seed": seed = ulong.Parse(args[++i]); break;
                case "--ticks": ticks = long.Parse(args[++i]); break;
                case "--db": db = args[++i]; break;
                case "--no-db": noDb = true; break;
                case "--set": overrides.Add(args[++i]); break;
                case "--notes": notes = args[++i]; break;
                case "--checkpoint-every": checkpointEvery = long.Parse(args[++i]); break;
                default: throw new ArgumentException($"Unknown run argument '{args[i]}'.");
            }
        }

        if (db is null && !noDb) throw new ArgumentException("run requires --db \"Host=...;Database=...;Username=...;Password=...\" or --no-db.");

        var config = ConfigLoader.Load(configPath);
        foreach (var o in overrides) ConfigLoader.ApplyOverride(config, o);
        string configJson = ConfigLoader.ToJson(config);
        string? gitSha = TryGetGitSha();

        var world = new World(config, seed);
        world.VerboseLogging = false; // DB rows (or the progress line, --no-db) are the record now
        world.BootstrapSpawnFromGenome(config.Life.BootstrapCount);

        Guid runId = Guid.NewGuid();
        PersistenceWriter? writer = null;
        NpgsqlDataSource? dataSource = null;
        var stats = new StatsComputer();
        int eventSeq = 0;
        long eventSeqTick = -1;

        int NextSeq()
        {
            if (world.CurrentTick != eventSeqTick) { eventSeqTick = world.CurrentTick; eventSeq = 0; }
            return eventSeq++;
        }

        if (!noDb)
        {
            dataSource = PersistenceWriter.BuildDataSource(db!);
            var w = await PersistenceWriter.OpenAsync(dataSource, runId, (long)seed, configJson, gitSha, notes);
            writer = w;

            world.SpeciesCreated += info => w.Enqueue(RowBuilders.BuildSpeciesRow(info));
            world.GenomeCreated += info => w.Enqueue(RowBuilders.BuildGenomeRow(info));
            world.CreatureCreated += c => w.Enqueue(RowBuilders.BuildCreatureRow(c));
            world.CreatureDied += (c, cause) => w.Enqueue(RowBuilders.BuildDeathRow(c, cause, world.CurrentTick));

            world.EggLaid += info => w.Enqueue(new EventRow
            {
                Tick = info.Tick, Seq = NextSeq(), Kind = EventKind.EGG_LAID,
                ActorId = (long)info.ParentId, TargetId = (long)info.EggId, X = info.X, Y = info.Y, Value = info.EggEnergy,
                DataJson = $"{{\"genome_id\":{info.GenomeId}}}",
            });
            world.EggHatched += info => w.Enqueue(new EventRow
            {
                Tick = info.Tick, Seq = NextSeq(), Kind = EventKind.HATCH,
                ActorId = (long)info.EggId, TargetId = (long)info.CreatureId, X = info.X, Y = info.Y,
            });
            world.EggEaten += info => w.Enqueue(new EventRow
            {
                Tick = info.Tick, Seq = NextSeq(), Kind = EventKind.EGG_EATEN,
                ActorId = (long)info.EaterId, TargetId = (long)info.EggId, X = info.X, Y = info.Y, Value = info.ValueGained,
            });
            if (config.Logging.LogBites)
            {
                world.Bitten += info => w.Enqueue(new EventRow
                {
                    Tick = info.Tick, Seq = NextSeq(), Kind = EventKind.BITE,
                    ActorId = (long)info.BiterId, TargetId = (long)info.TargetId, X = info.X, Y = info.Y, Value = info.Damage,
                });
            }
            world.Reseeded += count => w.Enqueue(new EventRow
            {
                Tick = world.CurrentTick, Seq = NextSeq(), Kind = EventKind.RESEED, Value = count,
            });
        }

        var sw = Stopwatch.StartNew();
        RunStatus finalStatus = RunStatus.RUNNING;
        long lastTick = 0;

        try
        {
            for (long t = 0; t < ticks; t++)
            {
                world.Tick();
                lastTick = world.CurrentTick;

                if (writer is not null)
                {
                    if (config.Logging.StatsEvery > 0 && world.CurrentTick % config.Logging.StatsEvery == 0)
                    {
                        double tps = t == 0 ? 0 : (t + 1) / sw.Elapsed.TotalSeconds;
                        var (worldRow, speciesRows) = stats.Compute(world, config.Species, (float)tps);
                        writer.Enqueue(worldRow);
                        foreach (var sr in speciesRows) writer.Enqueue(sr);
                    }
                    if (config.Logging.PositionsEvery > 0 && world.CurrentTick % config.Logging.PositionsEvery == 0)
                    {
                        foreach (var c in world.Creatures)
                        {
                            if (config.Logging.PositionModulo > 1 && c.Id % (ulong)config.Logging.PositionModulo != 0) continue;
                            writer.Enqueue(new PositionSampleRow
                            {
                                Tick = world.CurrentTick, CreatureId = c.Id, SpeciesId = c.SpeciesId,
                                X = c.X, Y = c.Y, Heading = c.Heading, Energy = c.Energy, Health = c.Health,
                            });
                        }
                    }
                }

                if (checkpointEvery is > 0 && world.CurrentTick % checkpointEvery.Value == 0)
                {
                    WriteCheckpointFile(world, runId, configJson);
                }

                if (t % 1000 == 0)
                {
                    double tps = t == 0 ? 0 : t / sw.Elapsed.TotalSeconds;
                    int speciesCount = CountDistinctSpecies(world);
                    Console.Error.WriteLine($"tick={world.CurrentTick} pop={world.Population} eggs={world.Eggs.Count} species={speciesCount} tps={tps:F0}");
                }

                if (world.Extinct)
                {
                    finalStatus = RunStatus.EXTINCT;
                    Console.Error.WriteLine($"tick={world.CurrentTick} EXTINCT");
                    break;
                }
            }

            if (finalStatus == RunStatus.RUNNING) finalStatus = RunStatus.COMPLETED;
        }
        catch
        {
            finalStatus = RunStatus.ERROR;
            throw;
        }
        finally
        {
            if (writer is not null)
            {
                await writer.WriteRunEndAsync(lastTick, finalStatus);
                await writer.DisposeAsync(); // drains + flushes remaining rows, then disposes the data source
            }
        }

        Console.WriteLine($"run_id={runId}");
        Console.WriteLine($"ticks={lastTick}");
        Console.WriteLine($"status={finalStatus}");
        if (writer is not null) Console.WriteLine($"dropped_rows={writer.DroppedCount}");

        return finalStatus switch
        {
            RunStatus.EXTINCT => 2,
            RunStatus.ERROR => 1,
            _ => 0,
        };
    }

    /// <summary>Distinct species ids among living creatures — cheap enough for a once-per-1000-tick progress line.</summary>
    internal static int CountDistinctSpecies(World world)
    {
        var seen = new HashSet<int>();
        foreach (var c in world.Creatures) seen.Add(c.SpeciesId);
        return seen.Count;
    }

    internal static void WriteCheckpointFile(World world, Guid runId, string configJson)
    {
        string dir = Path.Combine("runs", runId.ToString());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"tick_{world.CurrentTick:D7}.bin");
        using var fs = File.Create(path);
        world.WriteCheckpoint(fs, configJson);
    }

    private static string? TryGetGitSha()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
