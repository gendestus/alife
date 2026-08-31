using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Npgsql;
using Sim.Core;
using Sim.Persistence;

namespace Sim.Cli;

/// <summary>`sim resume` — continue a run from a checkpoint file (§8, §10, §12 test 2).</summary>
internal static class ResumeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? checkpointPath = null;
        long ticks = 1_000_000;
        string? db = null;
        bool noDb = false;
        long? checkpointEvery = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--checkpoint": checkpointPath = args[++i]; break;
                case "--ticks": ticks = long.Parse(args[++i]); break;
                case "--db": db = args[++i]; break;
                case "--no-db": noDb = true; break;
                case "--checkpoint-every": checkpointEvery = long.Parse(args[++i]); break;
                default: throw new ArgumentException($"Unknown resume argument '{args[i]}'.");
            }
        }

        if (checkpointPath is null) throw new ArgumentException("resume requires --checkpoint <path>.");
        if (db is null && !noDb) throw new ArgumentException("resume requires --db \"Host=...;Database=...;Username=...;Password=...\" or --no-db.");

        // The checkpoint lives at runs/<run_id>/tick_NNNNNNN.bin — resuming continues writing
        // into that same run row rather than starting a new one.
        Guid runId = Guid.Parse(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(checkpointPath))!));

        long startTick;
        string configJson;
        ulong seed;
        World world;
        Sim.Core.Config.SimConfig config;
        using (var fs = File.OpenRead(checkpointPath))
        {
            (startTick, configJson, seed) = World.ReadCheckpointHeader(fs);
            config = ConfigLoader.LoadFromJson(configJson);
            world = new World(config, seed);
            world.VerboseLogging = false;
            world.ReadCheckpointBody(fs, startTick);
        }

        PersistenceWriter? writer = null;
        NpgsqlDataSource? dataSource = null;
        var stats = new StatsComputer();
        stats.Prime(world); // world.EggsHatched etc. already reflect everything before the checkpoint
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
            var w = PersistenceWriter.Resume(dataSource, runId);
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
        long lastTick = startTick;

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
                    RunCommand.WriteCheckpointFile(world, runId, configJson);
                }

                if (t % 1000 == 0)
                {
                    double tps = t == 0 ? 0 : t / sw.Elapsed.TotalSeconds;
                    int speciesCount = RunCommand.CountDistinctSpecies(world);
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
                await writer.DisposeAsync();
            }
        }

        Console.WriteLine($"run_id={runId}");
        Console.WriteLine($"resumed_from_tick={startTick}");
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
}
