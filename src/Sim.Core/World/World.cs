using System;
using System.Collections.Generic;
using Sim.Core.Brain;
using Sim.Core.Config;
using Sim.Core.Entities;
using Sim.Core.Genetics;
using Sim.Core.Random;

namespace Sim.Core;

/// <summary>
/// World: plants, meat, scent, spatial hash, creature population, energy upkeep, and death.
/// Reproduction/speciation arrive in later milestones (§13). Split across World.cs (core loop),
/// World.Sensing.cs (sensor evaluation) and World.Acting.cs (actuator execution).
/// </summary>
public sealed partial class World
{
    private const float EatActiveCost = 0.01f; // §4.3 Eat: fixed, not config-driven.
    private const int LegacyActuatorCount = 3;  // Thrust, Turn, Eat — the M1 random-walk baseline's fixed set.

    private readonly SimConfig _cfg;
    private readonly IRandom _rng;

    public PlantGrid Plants { get; }
    public MeatField Meat { get; }
    public ScentGrid Scent { get; }
    public SpatialHash Hash { get; }
    public FoodIndex Food { get; }
    public InnovationTracker Innovations { get; } = new();
    public List<Creature> Creatures { get; } = new();
    public List<Egg> Eggs { get; } = new();

    // §7 speciation bookkeeping. _speciesById is permanent (species rows are never deleted);
    // _activeSpecies is the current pass's matching candidate list, rebuilt each pass.
    private readonly Dictionary<int, SpeciesRecord> _speciesById = new();
    private readonly List<SpeciesRecord> _activeSpecies = new();
    private int _nextSpeciesId;

    public long CurrentTick { get; private set; }
    public ulong NextCreatureId { get; private set; }
    public ulong NextEggId { get; private set; }

    /// <summary>Test-only: skip death checks/removal so the energy pool stays closed.</summary>
    public bool SuppressDeath { get; set; }

    /// <summary>Per-death stderr logging (§10, M1's "deaths logged to stderr with causes"). On by
    /// default to match the CLI's established behavior; long-running tests turn it off.</summary>
    public bool VerboseLogging { get; set; } = true;

    /// <summary>True once creatures and eggs both hit zero with reseedOnExtinction off (§5) — the run is over.</summary>
    public bool Extinct { get; private set; }

    // Fired as things happen; subscribers (persistence) translate these into DB rows. Handlers
    // must be fast/non-blocking — the sim never waits on the DB (§8).
    public event Action<GenomeCreatedInfo>? GenomeCreated;
    public event Action<Creature>? CreatureCreated;
    public event Action<Creature, DeathCause>? CreatureDied;
    public event Action<EggLaidInfo>? EggLaid;
    public event Action<EggHatchedInfo>? EggHatched;
    public event Action<EggEatenInfo>? EggEaten;
    public event Action<BiteInfo>? Bitten;
    public event Action<int>? Reseeded;
    public event Action<SpeciesCreatedInfo>? SpeciesCreated;

    public long DeathsStarvation { get; private set; }
    public long DeathsPredation { get; private set; }
    public long DeathsOldAge { get; private set; }
    public long EggsLaid { get; private set; }
    public long EggsHatched { get; private set; }
    public long EggsEaten { get; private set; }
    public long CapHits { get; private set; }
    public long Bites { get; private set; }

    public float LastTickPlantRegrowth { get; private set; }
    public float LastTickCosts => (float)_lastTickCostsAccum;
    private double _lastTickCostsAccum; // double: summed over up to popCap creatures, kept precise for §12 test 5

    // Reused per-tick scratch buffers (avoids per-creature allocation in sensing/acting).
    private readonly List<int> _queryScratch = new();
    private readonly List<int> _meatQueryScratch = new();
    private readonly List<int> _eggQueryScratch = new();

    /// <summary>The seed this run started from — kept for provenance (e.g. in checkpoints); restoring RNG state uses GetState/SetState, not re-seeding.</summary>
    public ulong Seed { get; }

    public World(SimConfig config, ulong seed)
    {
        _cfg = config;
        Seed = seed;
        _rng = new Xoshiro256StarStar(seed);
        Plants = new PlantGrid(config.World);
        Meat = new MeatField(config.World.MeatDecay);
        Scent = new ScentGrid(config.World);
        Hash = new SpatialHash(config.World.Width, config.World.HashCell);
        Food = new FoodIndex(config.World.Width, config.World.HashCell);
    }

    public int Population => Creatures.Count;

    /// <summary>M1 baseline: fixed traits, no genome/brain, random-walk controller.</summary>
    public void BootstrapSpawn(int count)
    {
        float w = _cfg.World.Width;
        for (int i = 0; i < count; i++)
        {
            float x = _rng.NextFloat(0f, w);
            float y = _rng.NextFloat(0f, w);
            float heading = _rng.NextFloat(0f, MathF.PI * 2f);

            var c = new Creature
            {
                Id = NextCreatureId++,
                X = x,
                Y = y,
                Heading = heading,
                Size = 1f,
                Speed = 1f,
                Armor = 0f,
                Diet = 0f,
                StorageCap = 1f,
                Lifespan = 2000f,
                Age = 0,
                BirthTick = CurrentTick,
                Alive = true,
            };
            c.MaxEnergy = 100f * c.Size * c.StorageCap;
            c.MaxHealth = 50f * c.Size;
            c.Energy = c.MaxEnergy;
            c.Health = c.MaxHealth;
            c.PassiveCostPerTick = _cfg.Energy.CBasal * MathF.Pow(c.Size, 1.5f)
                                  + _cfg.Energy.CArmor * c.Armor * c.Size
                                  + _cfg.Energy.CStore * c.StorageCap * c.Size
                                  + _cfg.Energy.CLife * c.Lifespan / 1000f
                                  + _cfg.Energy.ActuatorPassive * LegacyActuatorCount;

            Creatures.Add(c);
        }
    }

    /// <summary>M2: a real, genome+brain-driven creature. genomeId/parentId/generation/speciesId default to a fresh bootstrap-style lineage.</summary>
    public Creature SpawnFromGenome(Genome genome, float x, float y, float heading,
        long genomeId = -1, ulong? parentId = null, int generation = 0, int speciesId = 0)
    {
        bool mintedGenomeId = genomeId < 0;
        long resolvedGenomeId = mintedGenomeId ? Innovations.NextGenomeId() : genomeId;

        var c = new Creature
        {
            Id = NextCreatureId++,
            X = x,
            Y = y,
            Heading = heading,
            Size = genome.Body.Size,
            Speed = genome.Body.Speed,
            Armor = genome.Body.Armor,
            Diet = genome.Metabolism.Diet,
            StorageCap = genome.Metabolism.StorageCap,
            Lifespan = genome.Metabolism.Lifespan,
            EggThreshold = genome.Repro.EggThreshold,
            EggInvestment = genome.Repro.EggInvestment,
            ColorR = genome.Body.ColorR,
            ColorG = genome.Body.ColorG,
            ColorB = genome.Body.ColorB,
            Age = 0,
            BirthTick = CurrentTick,
            Alive = true,
            Genome = genome,
            GenomeId = resolvedGenomeId,
            ParentId = parentId,
            Generation = generation,
            SpeciesId = speciesId,
        };
        c.MaxEnergy = 100f * c.Size * c.StorageCap;
        c.MaxHealth = 50f * c.Size;
        c.Energy = c.MaxEnergy;
        c.Health = c.MaxHealth;
        c.PassiveCostPerTick = GeneSpec.TotalPassiveCost(genome, _cfg.Energy);

        c.Brain = BrainDecoder.Decode(genome);
        int inputCount = 0;
        foreach (var s in genome.Sensors) inputCount += GeneSpec.SensorSlotCount(s.Kind);
        c.SensorInputs = new float[inputCount];
        c.ActuatorOutputs = new float[genome.Actuators.Count];

        Creatures.Add(c);

        if (mintedGenomeId)
        {
            GenomeCreated?.Invoke(new GenomeCreatedInfo
            {
                GenomeId = resolvedGenomeId,
                ParentGenomeId = null,
                Genome = genome,
                FirstSeenTick = CurrentTick,
            });
        }
        CreatureCreated?.Invoke(c);

        return c;
    }

    /// <summary>
    /// M2+: a bootstrap population of real genome-driven creatures (§4.5). Individuals share one
    /// founding topology's gene ids/link innovations (see CreateBootstrapPopulation) so §7
    /// GenomeDistance recognizes them as the same founding lineage rather than as unrelated,
    /// maximally-novel genomes.
    /// </summary>
    public void BootstrapSpawnFromGenome(int count)
    {
        float w = _cfg.World.Width;
        var genomes = GenomeFactory.CreateBootstrapPopulation(_rng, Innovations, count);
        foreach (var genome in genomes)
        {
            float x = _rng.NextFloat(0f, w);
            float y = _rng.NextFloat(0f, w);
            float heading = _rng.NextFloat(0f, MathF.PI * 2f);
            SpawnFromGenome(genome, x, y, heading);
        }
    }

    public float TotalCreatureEnergy()
    {
        double sum = 0.0;
        for (int i = 0; i < Creatures.Count; i++)
        {
            if (Creatures[i].Alive) sum += Creatures[i].Energy;
        }
        return (float)sum;
    }

    /// <summary>Σ creature energy + Σ egg energy + Σ plant biomass·energyPerBiomass + Σ meat energy — the closed pool (§12 test 5).</summary>
    public float PoolEnergy()
    {
        double eggSum = 0.0;
        for (int i = 0; i < Eggs.Count; i++) eggSum += Eggs[i].Energy;

        return TotalCreatureEnergy()
             + (float)eggSum
             + Plants.TotalBiomass() * _cfg.Energy.EnergyPerBiomass
             + Meat.TotalEnergy();
    }

    /// <summary>
    /// SHA-256 over positions/energies/RNG state/innovation counters, in a fixed deterministic
    /// order (§12 test 1: two Worlds, same seed/config, N ticks -&gt; identical hash).
    /// </summary>
    public byte[] ComputeStateHash()
    {
        using var stream = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(CurrentTick);
            w.Write(NextCreatureId);
            w.Write(NextEggId);

            var (s0, s1, s2, s3) = _rng.GetState();
            w.Write(s0); w.Write(s1); w.Write(s2); w.Write(s3);
            if (_rng is Xoshiro256StarStar xo)
            {
                var (hasSpare, spare) = xo.GetGaussianCache();
                w.Write(hasSpare);
                w.Write(spare);
            }

            var inno = Innovations.GetState();
            w.Write(inno.NextSensorGeneId);
            w.Write(inno.NextActuatorGeneId);
            w.Write(inno.NextGenomeId);
            w.Write(inno.NextHiddenNodeId);
            w.Write(inno.NextLinkInnovation);
            w.Write(inno.LinkInnovations.Length);
            foreach (var (from, to, innovation) in inno.LinkInnovations)
            {
                w.Write(from); w.Write(to); w.Write(innovation);
            }

            w.Write(Creatures.Count); // already ascending-id order
            foreach (var c in Creatures)
            {
                w.Write(c.Id);
                w.Write(c.X); w.Write(c.Y); w.Write(c.Heading);
                w.Write(c.Energy); w.Write(c.Health);
                w.Write(c.Age);
                w.Write(c.GenomeId);
                w.Write(c.SpeciesId);
            }

            w.Write(Eggs.Count); // append-only order
            foreach (var e in Eggs)
            {
                w.Write(e.Id); w.Write(e.X); w.Write(e.Y); w.Write(e.Energy); w.Write(e.HatchTick);
            }

            // §7 speciation state: representative reselection consumes RNG draws whose sequence
            // depends on this bookkeeping, so it must be hashed (and checkpointed — see
            // World.Checkpoint.cs) for test 1/2 to catch any divergence.
            w.Write(_nextSpeciesId);
            var speciesIdsSorted = new List<int>(_speciesById.Keys);
            speciesIdsSorted.Sort();
            w.Write(speciesIdsSorted.Count);
            foreach (var id in speciesIdsSorted)
            {
                var s = _speciesById[id];
                w.Write(s.Id);
                w.Write(s.FoundedTick);
                w.Write(s.ParentSpeciesId.HasValue);
                if (s.ParentSpeciesId.HasValue) w.Write(s.ParentSpeciesId.Value);
                w.Write(s.FounderGenomeId);
                w.Write(s.LastSeenTick);
                w.Write(s.Representative.Hash());
            }
        }
        stream.Position = 0;
        return System.Security.Cryptography.SHA256.HashData(stream);
    }

    public void Tick()
    {
        _lastTickCostsAccum = 0.0;

        LastTickPlantRegrowth = Plants.Regrow();
        if (_cfg.World.ScentStep > 0 && CurrentTick % _cfg.World.ScentStep == 0) Scent.Step();
        Hash.Rebuild(Creatures);
        Food.Rebuild(Meat, Eggs);

        // Step 3: sense -> brain -> act. Actions apply immediately.
        for (int i = 0; i < Creatures.Count; i++)
        {
            var c = Creatures[i];
            if (!c.Alive) continue;
            if (c.Genome is null) LegacyRandomWalkAct(c); else GenomeAct(c);
        }

        // Step 4: upkeep, age, health regen, death checks — a separate full pass so every
        // creature's death check sees all of this tick's actions, regardless of id order.
        ApplyUpkeepAndDeath();

        CompactDead();
        Meat.Decay();

        // Step 5: hatch eggs whose incubation is done, in egg-id order (§2).
        HatchEggs();

        // Step 6: speciation pass (§7), independent cadence from stats/checkpoint logging.
        if (_cfg.Species.SpeciateEvery > 0 && CurrentTick % _cfg.Species.SpeciateEvery == 0) RunSpeciationPass();

        CheckExtinction();

        CurrentTick++;
    }

    /// <summary>Hatch eggs where tick &gt;= hatchTick, in egg-id order (list order, since Eggs is append-only until hatch/predation removal).</summary>
    private void HatchEggs()
    {
        int write = 0;
        for (int read = 0; read < Eggs.Count; read++)
        {
            var egg = Eggs[read];
            if (egg.Energy <= 0f) continue; // tombstoned by predation this tick (World.Acting's ExecuteEat) — drop it

            if (CurrentTick < egg.HatchTick)
            {
                Eggs[write++] = egg;
                continue;
            }

            float heading = _rng.NextFloat(0f, MathF.PI * 2f);
            var c = SpawnFromGenome(egg.Genome, egg.X, egg.Y, heading, egg.GenomeId, egg.ParentId, egg.Generation, egg.SpeciesId);
            EggsHatched++;
            EggHatched?.Invoke(new EggHatchedInfo { EggId = egg.Id, CreatureId = c.Id, X = egg.X, Y = egg.Y, Tick = CurrentTick });
            if (VerboseLogging)
            {
                Console.Error.WriteLine($"tick={CurrentTick} hatch id={c.Id} parent={egg.ParentId} generation={egg.Generation}");
            }
        }
        if (write < Eggs.Count)
        {
            Eggs.RemoveRange(write, Eggs.Count - write);
        }
    }

    private void CheckExtinction()
    {
        if (Creatures.Count > 0 || Eggs.Count > 0) return;

        if (_cfg.Life.ReseedOnExtinction)
        {
            if (VerboseLogging) Console.Error.WriteLine($"tick={CurrentTick} RESEED count={_cfg.Life.BootstrapCount}");
            BootstrapSpawnFromGenome(_cfg.Life.BootstrapCount);
            Reseeded?.Invoke(_cfg.Life.BootstrapCount);
        }
        else
        {
            Extinct = true;
        }
    }

    private void LegacyRandomWalkAct(Creature c)
    {
        float turnO = _rng.NextFloat(-1f, 1f);
        float thrustO = _rng.NextFloat(-1f, 1f);

        c.Heading += turnO * _cfg.Energy.MaxTurn;
        float turnCost = _cfg.Energy.CTurn * MathF.Abs(turnO);

        float v = Math.Clamp(thrustO, 0f, 1f) * c.Speed;
        v = MathF.Min(v, c.Speed * 2f);
        c.X += MathF.Cos(c.Heading) * v;
        c.Y += MathF.Sin(c.Heading) * v;
        ClampOrWrapPosition(c);
        float moveCost = _cfg.Energy.CMove * v * c.Size;

        // "Eat when on food": always attempt; only consume what's there and what fits in storage.
        int cell = Plants.CellIndex(c.X, c.Y);
        float b = Plants.Biomass[cell];
        float plantEff = MathF.Pow(1f - c.Diet, _cfg.Energy.DietExp);
        float desiredAmount = MathF.Min(_cfg.Energy.EatRate, b);
        float desiredGain = desiredAmount * _cfg.Energy.EnergyPerBiomass * plantEff;
        float headroom = MathF.Max(0f, c.MaxEnergy - c.Energy);
        float actualGain = MathF.Min(desiredGain, headroom);
        // Only pull from the plant what the creature can actually absorb, so eating never wastes energy.
        float actualAmount = desiredGain > 0f ? actualGain / (_cfg.Energy.EnergyPerBiomass * plantEff) : 0f;
        Plants.Biomass[cell] = b - actualAmount;
        c.Energy += actualGain;

        c.Energy -= moveCost + turnCost + EatActiveCost;
        _lastTickCostsAccum += moveCost + turnCost + EatActiveCost;
    }

    private void ApplyUpkeepAndDeath()
    {
        for (int i = 0; i < Creatures.Count; i++)
        {
            var c = Creatures[i];
            if (!c.Alive) continue;

            c.Energy -= c.PassiveCostPerTick;
            _lastTickCostsAccum += c.PassiveCostPerTick;

            if (c.Energy > 0.2f * c.MaxEnergy)
            {
                c.Health = MathF.Min(c.MaxHealth, c.Health + _cfg.Energy.HealthRegen);
            }
            c.Age += 1;

            if (SuppressDeath) continue;

            DeathCause? cause = null;
            if (c.Energy <= 0f) cause = DeathCause.STARVATION;
            else if (c.Health <= 0f) cause = DeathCause.PREDATION;
            else if (c.Age >= c.Lifespan) cause = DeathCause.OLD_AGE;

            if (cause.HasValue)
            {
                Kill(c, cause.Value);
            }
        }
    }

    private void Kill(Creature c, DeathCause cause)
    {
        c.Alive = false;
        switch (cause)
        {
            case DeathCause.STARVATION: DeathsStarvation++; break;
            case DeathCause.PREDATION: DeathsPredation++; break;
            case DeathCause.OLD_AGE: DeathsOldAge++; break;
        }
        Meat.Spawn(c.X, c.Y, _cfg.World.CorpseEnergy * c.Size);
        CreatureDied?.Invoke(c, cause);
        if (VerboseLogging)
        {
            string killer = cause == DeathCause.PREDATION ? $" killer={c.LastDamagedBy}" : "";
            Console.Error.WriteLine($"tick={CurrentTick} death id={c.Id} cause={cause} age={c.Age} energy={c.Energy:F2}{killer}");
        }
    }

    private void CompactDead()
    {
        int write = 0;
        for (int read = 0; read < Creatures.Count; read++)
        {
            if (!Creatures[read].Alive) continue;
            Creatures[write++] = Creatures[read];
        }
        if (write < Creatures.Count)
        {
            Creatures.RemoveRange(write, Creatures.Count - write);
        }
    }

    private void ClampOrWrapPosition(Creature c)
    {
        float w = _cfg.World.Width;
        if (_cfg.World.Toroidal)
        {
            c.X = Wrap(c.X, w);
            c.Y = Wrap(c.Y, w);
        }
        else
        {
            if (c.X < 0f) c.X = 0f; else if (c.X >= w) c.X = w - 1e-4f;
            if (c.Y < 0f) c.Y = 0f; else if (c.Y >= w) c.Y = w - 1e-4f;
        }
    }

    private static float Wrap(float a, float m)
    {
        float r = a % m;
        return r < 0f ? r + m : r;
    }
}
