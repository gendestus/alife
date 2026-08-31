using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Npgsql;
using Npgsql.NameTranslation;
using NpgsqlTypes;
using Sim.Core.Entities;

namespace Sim.Persistence;

/// <summary>
/// Npgsql binary-COPY writer (§8). The sim pushes plain row objects into a bounded Channel;
/// one background task drains it, buckets by table, and flushes with binary COPY when any
/// bucket reaches 5,000 rows or 2 seconds have elapsed, one transaction per flush. The sim
/// never waits on the DB except at shutdown (drain + flush) — TryWrite drops and counts
/// instead of blocking if the channel is ever full.
/// </summary>
public sealed class PersistenceWriter : IAsyncDisposable
{
    private const int ChannelCapacity = 100_000;
    private const int FlushRowThreshold = 5_000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly Channel<object> _channel;
    private readonly NpgsqlDataSource _dataSource;
    private readonly Guid _runId;
    private readonly Task _drainTask;
    private readonly CancellationTokenSource _stopSignal = new();

    private long _droppedCount;
    private long _lastDroppedLogAt;
    private DateTime _lastFlush = DateTime.UtcNow;

    private readonly List<GenomeRow> _genomes = new();
    private readonly List<SpeciesRow> _species = new();
    private readonly List<CreatureRow> _creatures = new();
    private readonly List<CreatureDeathRow> _deaths = new();
    private readonly List<EventRow> _events = new();
    private readonly List<WorldStatsRow> _worldStats = new();
    private readonly List<SpeciesStatsRow> _speciesStats = new();
    private readonly List<PositionSampleRow> _positionSamples = new();

    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    public Guid RunId => _runId;

    private PersistenceWriter(NpgsqlDataSource dataSource, Guid runId)
    {
        _dataSource = dataSource;
        _runId = runId;
        _channel = Channel.CreateBounded<object>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _drainTask = Task.Run(() => DrainLoopAsync(_stopSignal.Token));
    }

    public static NpgsqlDataSource BuildDataSource(string connectionString)
    {
        var dsBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dsBuilder.MapEnum<DeathCause>("death_cause", new NpgsqlNullNameTranslator());
        dsBuilder.MapEnum<EventKind>("event_kind", new NpgsqlNullNameTranslator());
        dsBuilder.MapEnum<RunStatus>("run_status", new NpgsqlNullNameTranslator());
        return dsBuilder.Build();
    }

    public static async Task<PersistenceWriter> OpenAsync(NpgsqlDataSource dataSource, Guid runId, long seed, string configJson, string? gitSha, string? notes)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO run (run_id, seed, config, git_sha, notes, status) VALUES (@run_id, @seed, @config::jsonb, @git_sha, @notes, 'RUNNING')", conn);
        cmd.Parameters.AddWithValue("run_id", runId);
        cmd.Parameters.AddWithValue("seed", seed);
        cmd.Parameters.AddWithValue("config", configJson);
        cmd.Parameters.AddWithValue("git_sha", (object?)gitSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("notes", (object?)notes ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();

        return new PersistenceWriter(dataSource, runId);
    }

    /// <summary>Attaches to an existing run row (written by the original `sim run`) instead of inserting a new one — used by `sim resume`.</summary>
    public static PersistenceWriter Resume(NpgsqlDataSource dataSource, Guid runId) => new(dataSource, runId);

    public async Task WriteRunEndAsync(long lastTick, RunStatus status)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE run SET ended_at = now(), last_tick = @last_tick, status = @status WHERE run_id = @run_id", conn);
        cmd.Parameters.AddWithValue("last_tick", lastTick);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("run_id", _runId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Non-blocking. Drops and counts (with a throttled warning) if the channel is full.</summary>
    public void Enqueue(object row)
    {
        if (_channel.Writer.TryWrite(row)) return;

        long dropped = Interlocked.Increment(ref _droppedCount);
        if (dropped - Interlocked.Read(ref _lastDroppedLogAt) >= 1000)
        {
            Interlocked.Exchange(ref _lastDroppedLogAt, dropped);
            Console.Error.WriteLine($"WARNING: persistence channel full, {dropped} rows dropped so far — the DB is the bottleneck");
        }
    }

    private async Task DrainLoopAsync(CancellationToken token)
    {
        // A single always-awaited WaitToReadAsync per iteration — racing it against a Task.Delay
        // and re-issuing a fresh call on timeout (the previous version) leaves the loser
        // orphaned-but-still-pending, which stacks up overlapping reads against a channel
        // declared SingleReader — undefined behavior, observed as a hang.
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(token))
            {
                while (reader.TryRead(out var item))
                {
                    Bucket(item);
                    if (AnyBucketAtThreshold()) await FlushAllAsync();
                }
                if (DateTime.UtcNow - _lastFlush >= FlushInterval) await FlushAllAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }

        // Final drain: take whatever's left in the channel, then flush.
        while (reader.TryRead(out var leftover)) Bucket(leftover);
        await FlushAllAsync();
    }

    private void Bucket(object row)
    {
        switch (row)
        {
            case GenomeRow g: _genomes.Add(g); break;
            case SpeciesRow s: _species.Add(s); break;
            case CreatureRow c: _creatures.Add(c); break;
            case CreatureDeathRow d: _deaths.Add(d); break;
            case EventRow e: _events.Add(e); break;
            case WorldStatsRow ws: _worldStats.Add(ws); break;
            case SpeciesStatsRow ss: _speciesStats.Add(ss); break;
            case PositionSampleRow ps: _positionSamples.Add(ps); break;
        }
    }

    private bool AnyBucketAtThreshold() =>
        _genomes.Count >= FlushRowThreshold || _species.Count >= FlushRowThreshold ||
        _creatures.Count >= FlushRowThreshold || _deaths.Count >= FlushRowThreshold ||
        _events.Count >= FlushRowThreshold || _worldStats.Count >= FlushRowThreshold ||
        _speciesStats.Count >= FlushRowThreshold || _positionSamples.Count >= FlushRowThreshold;

    private async Task FlushAllAsync()
    {
        _lastFlush = DateTime.UtcNow;

        bool anyRows = _genomes.Count > 0 || _species.Count > 0 || _creatures.Count > 0 || _deaths.Count > 0 ||
                       _events.Count > 0 || _worldStats.Count > 0 || _speciesStats.Count > 0 || _positionSamples.Count > 0;
        if (!anyRows) return;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await CopyGenomesAsync(conn);
        await CopySpeciesAsync(conn);
        await CopyCreaturesAsync(conn);
        await CopyDeathsAsync(conn);
        await CopyEventsAsync(conn);
        await CopyWorldStatsAsync(conn);
        await CopySpeciesStatsAsync(conn);
        await CopyPositionSamplesAsync(conn);

        await tx.CommitAsync();

        _genomes.Clear(); _species.Clear(); _creatures.Clear(); _deaths.Clear();
        _events.Clear(); _worldStats.Clear(); _speciesStats.Clear(); _positionSamples.Clear();
    }

    private async Task CopyGenomesAsync(NpgsqlConnection conn)
    {
        if (_genomes.Count == 0) return;
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY genome (run_id, genome_id, parent_genome_id, first_seen_tick, hash, data, size, speed, armor, color_r, color_g, color_b, diet, storage_cap, lifespan, egg_threshold, egg_investment, mutation_rate, structural_rate, n_sensors, n_actuators, n_hidden, n_links, sensor_kinds, actuator_kinds) FROM STDIN (FORMAT BINARY)");
        foreach (var g in _genomes)
        {
            await w.StartRowAsync();
            await w.WriteAsync(_runId);
            await w.WriteAsync(g.GenomeId);
            if (g.ParentGenomeId.HasValue) await w.WriteAsync(g.ParentGenomeId.Value); else await w.WriteNullAsync();
            await w.WriteAsync(g.FirstSeenTick);
            await w.WriteAsync(g.Hash);
            await w.WriteAsync(g.DataJson, NpgsqlDbType.Jsonb);
            await w.WriteAsync(g.Size); await w.WriteAsync(g.Speed); await w.WriteAsync(g.Armor);
            await w.WriteAsync(g.ColorR); await w.WriteAsync(g.ColorG); await w.WriteAsync(g.ColorB);
            await w.WriteAsync(g.Diet); await w.WriteAsync(g.StorageCap); await w.WriteAsync(g.Lifespan);
            await w.WriteAsync(g.EggThreshold); await w.WriteAsync(g.EggInvestment);
            await w.WriteAsync(g.MutationRate); await w.WriteAsync(g.StructuralRate);
            await w.WriteAsync(g.NSensors); await w.WriteAsync(g.NActuators); await w.WriteAsync(g.NHidden); await w.WriteAsync(g.NLinks);
            await w.WriteAsync(g.SensorKindsJson, NpgsqlDbType.Jsonb);
            await w.WriteAsync(g.ActuatorKindsJson, NpgsqlDbType.Jsonb);
        }
        await w.CompleteAsync();
    }

    private async Task CopySpeciesAsync(NpgsqlConnection conn)
    {
        if (_species.Count == 0) return;
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY species (run_id, species_id, founded_tick, founder_genome_id, parent_species_id) FROM STDIN (FORMAT BINARY)");
        foreach (var s in _species)
        {
            await w.StartRowAsync();
            await w.WriteAsync(_runId);
            await w.WriteAsync(s.SpeciesId);
            await w.WriteAsync(s.FoundedTick);
            await w.WriteAsync(s.FounderGenomeId);
            if (s.ParentSpeciesId.HasValue) await w.WriteAsync(s.ParentSpeciesId.Value); else await w.WriteNullAsync();
        }
        await w.CompleteAsync();
    }

    private async Task CopyCreaturesAsync(NpgsqlConnection conn)
    {
        if (_creatures.Count == 0) return;
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY creature (run_id, creature_id, genome_id, species_id, parent_creature_id, generation, birth_tick, birth_x, birth_y) FROM STDIN (FORMAT BINARY)");
        foreach (var c in _creatures)
        {
            await w.StartRowAsync();
            await w.WriteAsync(_runId);
            await w.WriteAsync((long)c.CreatureId);
            await w.WriteAsync(c.GenomeId);
            await w.WriteAsync(c.SpeciesId);
            if (c.ParentCreatureId.HasValue) await w.WriteAsync((long)c.ParentCreatureId.Value); else await w.WriteNullAsync();
            await w.WriteAsync(c.Generation);
            await w.WriteAsync(c.BirthTick);
            await w.WriteAsync(c.BirthX);
            await w.WriteAsync(c.BirthY);
        }
        await w.CompleteAsync();
    }

    private async Task CopyDeathsAsync(NpgsqlConnection conn)
    {
        if (_deaths.Count == 0) return;
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY creature_death (run_id, creature_id, death_tick, cause, x, y, age, energy_at_death, killer_creature_id, offspring_count, species_id) FROM STDIN (FORMAT BINARY)");
        foreach (var d in _deaths)
        {
            await w.StartRowAsync();
            await w.WriteAsync(_runId);
            await w.WriteAsync((long)d.CreatureId);
            await w.WriteAsync(d.DeathTick);
            await w.WriteAsync(d.Cause);
            await w.WriteAsync(d.X);
            await w.WriteAsync(d.Y);
            await w.WriteAsync(d.Age);
            await w.WriteAsync(d.EnergyAtDeath);
            if (d.KillerCreatureId.HasValue) await w.WriteAsync((long)d.KillerCreatureId.Value); else await w.WriteNullAsync();
            await w.WriteAsync(d.OffspringCount);
            await w.WriteAsync(d.SpeciesId);
        }
        await w.CompleteAsync();
    }

    private async Task CopyEventsAsync(NpgsqlConnection conn)
    {
        if (_events.Count == 0) return;
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY event (run_id, tick, seq, kind, actor_id, target_id, x, y, value, data) FROM STDIN (FORMAT BINARY)");
        foreach (var e in _events)
        {
            await w.StartRowAsync();
            await w.WriteAsync(_runId);
            await w.WriteAsync(e.Tick);
            await w.WriteAsync(e.Seq);
            await w.WriteAsync(e.Kind);
            if (e.ActorId.HasValue) await w.WriteAsync(e.ActorId.Value); else await w.WriteNullAsync();
            if (e.TargetId.HasValue) await w.WriteAsync(e.TargetId.Value); else await w.WriteNullAsync();
            if (e.X.HasValue) await w.WriteAsync(e.X.Value); else await w.WriteNullAsync();
            if (e.Y.HasValue) await w.WriteAsync(e.Y.Value); else await w.WriteNullAsync();
            if (e.Value.HasValue) await w.WriteAsync(e.Value.Value); else await w.WriteNullAsync();
            if (e.DataJson is not null) await w.WriteAsync(e.DataJson, NpgsqlDbType.Jsonb); else await w.WriteNullAsync();
        }
        await w.CompleteAsync();
    }

    private async Task CopyWorldStatsAsync(NpgsqlConnection conn)
    {
        if (_worldStats.Count == 0) return;
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY world_stats (run_id, tick, population, eggs, meat_items, plant_biomass_total, meat_energy_total, creature_energy_total, births, eggs_laid, eggs_eaten, deaths_starvation, deaths_predation, deaths_old_age, bites, cap_hits, mean_energy, mean_age, mean_generation, max_generation, mean_size, mean_speed, mean_armor, mean_diet, mean_storage_cap, mean_lifespan, mean_egg_threshold, mean_egg_investment, mean_mutation_rate, mean_structural_rate, mean_sensors, mean_actuators, mean_hidden, mean_links, species_count, species_count_min5, shannon, mean_pairwise_distance, ticks_per_second) FROM STDIN (FORMAT BINARY)");
        foreach (var s in _worldStats)
        {
            await w.StartRowAsync();
            await w.WriteAsync(_runId);
            await w.WriteAsync(s.Tick);
            await w.WriteAsync(s.Population);
            await w.WriteAsync(s.Eggs);
            await w.WriteAsync(s.MeatItems);
            await w.WriteAsync(s.PlantBiomassTotal);
            await w.WriteAsync(s.MeatEnergyTotal);
            await w.WriteAsync(s.CreatureEnergyTotal);
            await w.WriteAsync(s.Births);
            await w.WriteAsync(s.EggsLaid);
            await w.WriteAsync(s.EggsEaten);
            await w.WriteAsync(s.DeathsStarvation);
            await w.WriteAsync(s.DeathsPredation);
            await w.WriteAsync(s.DeathsOldAge);
            await w.WriteAsync(s.Bites);
            await w.WriteAsync(s.CapHits);
            await w.WriteAsync(s.MeanEnergy);
            await w.WriteAsync(s.MeanAge);
            await w.WriteAsync(s.MeanGeneration);
            await w.WriteAsync(s.MaxGeneration);
            await w.WriteAsync(s.MeanSize);
            await w.WriteAsync(s.MeanSpeed);
            await w.WriteAsync(s.MeanArmor);
            await w.WriteAsync(s.MeanDiet);
            await w.WriteAsync(s.MeanStorageCap);
            await w.WriteAsync(s.MeanLifespan);
            await w.WriteAsync(s.MeanEggThreshold);
            await w.WriteAsync(s.MeanEggInvestment);
            await w.WriteAsync(s.MeanMutationRate);
            await w.WriteAsync(s.MeanStructuralRate);
            await w.WriteAsync(s.MeanSensors);
            await w.WriteAsync(s.MeanActuators);
            await w.WriteAsync(s.MeanHidden);
            await w.WriteAsync(s.MeanLinks);
            await w.WriteAsync(s.SpeciesCount);
            await w.WriteAsync(s.SpeciesCountMin5);
            await w.WriteAsync(s.Shannon);
            await w.WriteAsync(s.MeanPairwiseDistance);
            if (s.TicksPerSecond.HasValue) await w.WriteAsync(s.TicksPerSecond.Value); else await w.WriteNullAsync();
        }
        await w.CompleteAsync();
    }

    private async Task CopySpeciesStatsAsync(NpgsqlConnection conn)
    {
        if (_speciesStats.Count == 0) return;
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY species_stats (run_id, tick, species_id, population, mean_size, mean_speed, mean_armor, mean_color_r, mean_color_g, mean_color_b, mean_diet, mean_storage_cap, mean_lifespan, mean_egg_threshold, mean_egg_investment, mean_mutation_rate, mean_structural_rate, mean_sensors, mean_actuators, mean_hidden, mean_links, mean_energy, mean_age, sensor_kind_counts, actuator_kind_counts) FROM STDIN (FORMAT BINARY)");
        foreach (var s in _speciesStats)
        {
            await w.StartRowAsync();
            await w.WriteAsync(_runId);
            await w.WriteAsync(s.Tick);
            await w.WriteAsync(s.SpeciesId);
            await w.WriteAsync(s.Population);
            await w.WriteAsync(s.MeanSize);
            await w.WriteAsync(s.MeanSpeed);
            await w.WriteAsync(s.MeanArmor);
            await w.WriteAsync(s.MeanColorR);
            await w.WriteAsync(s.MeanColorG);
            await w.WriteAsync(s.MeanColorB);
            await w.WriteAsync(s.MeanDiet);
            await w.WriteAsync(s.MeanStorageCap);
            await w.WriteAsync(s.MeanLifespan);
            await w.WriteAsync(s.MeanEggThreshold);
            await w.WriteAsync(s.MeanEggInvestment);
            await w.WriteAsync(s.MeanMutationRate);
            await w.WriteAsync(s.MeanStructuralRate);
            await w.WriteAsync(s.MeanSensors);
            await w.WriteAsync(s.MeanActuators);
            await w.WriteAsync(s.MeanHidden);
            await w.WriteAsync(s.MeanLinks);
            await w.WriteAsync(s.MeanEnergy);
            await w.WriteAsync(s.MeanAge);
            await w.WriteAsync(s.SensorKindCountsJson, NpgsqlDbType.Jsonb);
            await w.WriteAsync(s.ActuatorKindCountsJson, NpgsqlDbType.Jsonb);
        }
        await w.CompleteAsync();
    }

    private async Task CopyPositionSamplesAsync(NpgsqlConnection conn)
    {
        if (_positionSamples.Count == 0) return;
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY position_sample (run_id, tick, creature_id, species_id, x, y, heading, energy, health) FROM STDIN (FORMAT BINARY)");
        foreach (var p in _positionSamples)
        {
            await w.StartRowAsync();
            await w.WriteAsync(_runId);
            await w.WriteAsync(p.Tick);
            await w.WriteAsync((long)p.CreatureId);
            await w.WriteAsync(p.SpeciesId);
            await w.WriteAsync(p.X);
            await w.WriteAsync(p.Y);
            await w.WriteAsync(p.Heading);
            await w.WriteAsync(p.Energy);
            await w.WriteAsync(p.Health);
        }
        await w.CompleteAsync();
    }

    /// <summary>Signals the drain loop to stop after one final drain+flush pass, then waits for it.</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _stopSignal.Cancel();
        try { await _drainTask; } catch (OperationCanceledException) { }
        await _dataSource.DisposeAsync();
    }
}
