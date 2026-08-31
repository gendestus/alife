using System.Collections.Generic;
using System.IO;
using System.Text;
using Sim.Core.Brain;
using Sim.Core.Entities;
using Sim.Core.Genetics;
using Sim.Core.Random;

namespace Sim.Core;

/// <summary>
/// Checkpoint format v1 (§10): magic "ALCK", u16 version, then tick/config/seed/RNG/counters/
/// plant+scent grids/meat/genome table/creatures/eggs, all via explicit BinaryWriter calls —
/// no BinaryFormatter, so the format is stable across .NET versions and portable to any
/// consumer that wants to read it (e.g. a non-.NET tool) without needing this assembly.
///
/// Sim.Core doesn't parse JSON, so the resolved config travels as an opaque string: the
/// caller (Sim.Cli) supplies it when writing and is responsible for parsing it back into a
/// SimConfig — via ReadCheckpointHeader — before constructing the World that ReadCheckpointBody
/// then populates.
/// </summary>
public sealed partial class World
{
    private const string CheckpointMagic = "ALCK";
    private const ushort CheckpointVersion = 1;

    public void WriteCheckpoint(Stream stream, string configJson)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        w.Write(Encoding.ASCII.GetBytes(CheckpointMagic));
        w.Write(CheckpointVersion);
        w.Write(CurrentTick);
        w.Write(configJson);
        w.Write(Seed);

        var (s0, s1, s2, s3) = _rng.GetState();
        w.Write(s0); w.Write(s1); w.Write(s2); w.Write(s3);
        bool hasSpare = false;
        float spare = 0f;
        if (_rng is Xoshiro256StarStar xo) (hasSpare, spare) = xo.GetGaussianCache();
        w.Write(hasSpare);
        w.Write(spare);

        w.Write(NextCreatureId);
        w.Write(NextEggId);
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

        // §7 speciation registry — must round-trip exactly: representative reselection during
        // RunSpeciationPass consumes RNG draws whose sequence depends on this state.
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
            GenomeBinary.Write(w, s.Representative);
        }

        // Reporting counters only — nothing in Sim.Core reads these to decide behavior, but a
        // resumed run's totals should still make sense as a continuation of the original.
        w.Write(DeathsStarvation); w.Write(DeathsPredation); w.Write(DeathsOldAge);
        w.Write(EggsLaid); w.Write(EggsHatched); w.Write(EggsEaten); w.Write(CapHits); w.Write(Bites);

        w.Write(Plants.P);
        foreach (var b in Plants.Biomass) w.Write(b);
        foreach (var k in Plants.Capacity) w.Write(k);

        w.Write(Scent.S);
        for (int c = 0; c < ScentGrid.ChannelCount; c++)
        {
            foreach (var v in Scent.GetChannelValues(c)) w.Write(v);
        }

        w.Write(Meat.Count);
        foreach (var m in Meat.Items) { w.Write(m.X); w.Write(m.Y); w.Write(m.Energy); }

        // Genome table: distinct genomes referenced by living creatures/eggs, keyed by genome id.
        var genomesById = new SortedDictionary<long, Genome>();
        foreach (var c in Creatures) if (c.Genome is not null) genomesById[c.GenomeId] = c.Genome;
        foreach (var e in Eggs) genomesById[e.GenomeId] = e.Genome;
        w.Write(genomesById.Count);
        foreach (var kv in genomesById)
        {
            w.Write(kv.Key);
            GenomeBinary.Write(w, kv.Value);
        }

        w.Write(Creatures.Count);
        foreach (var c in Creatures) WriteCreature(w, c);

        w.Write(Eggs.Count);
        foreach (var e in Eggs)
        {
            w.Write(e.Id);
            w.Write(e.GenomeId);
            w.Write(e.X); w.Write(e.Y); w.Write(e.Energy);
            w.Write(e.LaidTick); w.Write(e.HatchTick);
            w.Write(e.ParentId);
            w.Write(e.SpeciesId);
            w.Write(e.Generation);
        }
    }

    private static void WriteCreature(BinaryWriter w, Creature c)
    {
        w.Write(c.Id);
        w.Write(c.X); w.Write(c.Y); w.Write(c.Heading);
        w.Write(c.Energy); w.Write(c.MaxEnergy); w.Write(c.Health); w.Write(c.MaxHealth);
        w.Write(c.Age);
        w.Write(c.BirthTick);
        w.Write(c.Size); w.Write(c.Speed); w.Write(c.Armor); w.Write(c.Diet);
        w.Write(c.StorageCap); w.Write(c.Lifespan); w.Write(c.EggThreshold); w.Write(c.EggInvestment);
        w.Write(c.ColorR); w.Write(c.ColorG); w.Write(c.ColorB);
        w.Write(c.LastDamagedBy); w.Write(c.LastDamagedTick);
        w.Write(c.PassiveCostPerTick);
        w.Write(c.GenomeId);
        w.Write(c.SpeciesId);
        w.Write(c.ParentId.HasValue);
        if (c.ParentId.HasValue) w.Write(c.ParentId.Value);
        w.Write(c.Generation);
        w.Write(c.OffspringCount);

        // The one piece of per-creature state a genome re-decode can't reconstruct.
        var activations = c.Brain!.GetActivationState();
        w.Write(activations.Length);
        foreach (var a in activations) w.Write(a);
    }

    /// <summary>Reads just enough (tick/configJson/seed) for the caller to build a matching SimConfig and construct a World, before calling ReadCheckpointBody on it.</summary>
    public static (long Tick, string ConfigJson, ulong Seed) ReadCheckpointHeader(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        string magic = Encoding.ASCII.GetString(r.ReadBytes(4));
        if (magic != CheckpointMagic) throw new InvalidDataException($"not an ALCK checkpoint (magic was '{magic}')");
        ushort version = r.ReadUInt16();
        if (version != CheckpointVersion) throw new InvalidDataException($"unsupported checkpoint version {version}");
        long tick = r.ReadInt64();
        string configJson = r.ReadString();
        ulong seed = r.ReadUInt64();
        return (tick, configJson, seed);
    }

    /// <summary>Populates this (freshly-constructed, matching-config) World from the checkpoint body following the header read by ReadCheckpointHeader on the same stream.</summary>
    public void ReadCheckpointBody(Stream stream, long tick)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        CurrentTick = tick;

        ulong s0 = r.ReadUInt64(), s1 = r.ReadUInt64(), s2 = r.ReadUInt64(), s3 = r.ReadUInt64();
        _rng.SetState(s0, s1, s2, s3);
        bool hasSpare = r.ReadBoolean();
        float spare = r.ReadSingle();
        if (_rng is Xoshiro256StarStar xo) xo.SetGaussianCache(hasSpare, spare);

        NextCreatureId = r.ReadUInt64();
        NextEggId = r.ReadUInt64();
        var inno = new InnovationState
        {
            NextSensorGeneId = r.ReadInt64(),
            NextActuatorGeneId = r.ReadInt64(),
            NextGenomeId = r.ReadInt64(),
            NextHiddenNodeId = r.ReadInt32(),
            NextLinkInnovation = r.ReadInt32(),
        };
        int linkInnovationCount = r.ReadInt32();
        var linkInnovations = new (int, int, int)[linkInnovationCount];
        for (int i = 0; i < linkInnovationCount; i++)
        {
            linkInnovations[i] = (r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        }
        inno.LinkInnovations = linkInnovations;
        Innovations.SetState(inno);

        _nextSpeciesId = r.ReadInt32();
        int speciesRecordCount = r.ReadInt32();
        _speciesById.Clear();
        for (int i = 0; i < speciesRecordCount; i++)
        {
            int id = r.ReadInt32();
            long foundedTick = r.ReadInt64();
            bool hasParent = r.ReadBoolean();
            int? parentSpeciesId = hasParent ? r.ReadInt32() : null;
            long founderGenomeId = r.ReadInt64();
            long lastSeenTick = r.ReadInt64();
            var representative = GenomeBinary.Read(r);
            _speciesById[id] = new SpeciesRecord
            {
                Id = id,
                FoundedTick = foundedTick,
                ParentSpeciesId = parentSpeciesId,
                FounderGenomeId = founderGenomeId,
                LastSeenTick = lastSeenTick,
                Representative = representative,
            };
        }

        DeathsStarvation = r.ReadInt64(); DeathsPredation = r.ReadInt64(); DeathsOldAge = r.ReadInt64();
        EggsLaid = r.ReadInt64(); EggsHatched = r.ReadInt64(); EggsEaten = r.ReadInt64(); CapHits = r.ReadInt64(); Bites = r.ReadInt64();

        int plantP = r.ReadInt32();
        if (plantP != Plants.P) throw new InvalidDataException($"checkpoint plant grid is {plantP}x{plantP}, world is {Plants.P}x{Plants.P} — config mismatch?");
        for (int i = 0; i < Plants.Biomass.Length; i++) Plants.Biomass[i] = r.ReadSingle();
        for (int i = 0; i < Plants.Capacity.Length; i++) Plants.Capacity[i] = r.ReadSingle();

        int scentS = r.ReadInt32();
        if (scentS != Scent.S) throw new InvalidDataException($"checkpoint scent grid is {scentS}x{scentS}, world is {Scent.S}x{Scent.S} — config mismatch?");
        for (int c = 0; c < ScentGrid.ChannelCount; c++)
        {
            var channel = new float[scentS * scentS];
            for (int i = 0; i < channel.Length; i++) channel[i] = r.ReadSingle();
            Scent.SetChannelValues(c, channel);
        }

        int meatCount = r.ReadInt32();
        for (int i = 0; i < meatCount; i++)
        {
            Meat.Spawn(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        int genomeCount = r.ReadInt32();
        var genomesById = new Dictionary<long, Genome>(genomeCount);
        for (int i = 0; i < genomeCount; i++)
        {
            long id = r.ReadInt64();
            genomesById[id] = GenomeBinary.Read(r);
        }

        Creatures.Clear();
        int creatureCount = r.ReadInt32();
        for (int i = 0; i < creatureCount; i++)
        {
            Creatures.Add(ReadCreature(r, genomesById));
        }

        Eggs.Clear();
        int eggCount = r.ReadInt32();
        for (int i = 0; i < eggCount; i++)
        {
            long genomeId = default;
            ulong id = r.ReadUInt64();
            genomeId = r.ReadInt64();
            var egg = new Egg
            {
                Id = id,
                GenomeId = genomeId,
                Genome = genomesById[genomeId],
                X = r.ReadSingle(),
                Y = r.ReadSingle(),
                Energy = r.ReadSingle(),
                LaidTick = r.ReadInt64(),
                HatchTick = r.ReadInt64(),
                ParentId = r.ReadUInt64(),
                SpeciesId = r.ReadInt32(),
                Generation = r.ReadInt32(),
            };
            Eggs.Add(egg);
        }
    }

    private static Creature ReadCreature(BinaryReader r, Dictionary<long, Genome> genomesById)
    {
        var c = new Creature
        {
            Id = r.ReadUInt64(),
            X = r.ReadSingle(),
            Y = r.ReadSingle(),
            Heading = r.ReadSingle(),
            Energy = r.ReadSingle(),
            MaxEnergy = r.ReadSingle(),
            Health = r.ReadSingle(),
            MaxHealth = r.ReadSingle(),
            Age = r.ReadInt32(),
            BirthTick = r.ReadInt64(),
            Size = r.ReadSingle(),
            Speed = r.ReadSingle(),
            Armor = r.ReadSingle(),
            Diet = r.ReadSingle(),
            StorageCap = r.ReadSingle(),
            Lifespan = r.ReadSingle(),
            EggThreshold = r.ReadSingle(),
            EggInvestment = r.ReadSingle(),
            ColorR = r.ReadSingle(),
            ColorG = r.ReadSingle(),
            ColorB = r.ReadSingle(),
            LastDamagedBy = r.ReadUInt64(),
            LastDamagedTick = r.ReadInt64(),
            PassiveCostPerTick = r.ReadSingle(),
            Alive = true,
        };
        long genomeId = r.ReadInt64();
        c.GenomeId = genomeId;
        c.SpeciesId = r.ReadInt32();
        c.ParentId = r.ReadBoolean() ? r.ReadUInt64() : null;
        c.Generation = r.ReadInt32();
        c.OffspringCount = r.ReadInt32();

        var genome = genomesById[genomeId];
        c.Genome = genome;
        c.Brain = BrainDecoder.Decode(genome);
        int inputCount = 0;
        foreach (var s in genome.Sensors) inputCount += GeneSpec.SensorSlotCount(s.Kind);
        c.SensorInputs = new float[inputCount];
        c.ActuatorOutputs = new float[genome.Actuators.Count];

        int activationCount = r.ReadInt32();
        var activations = new float[activationCount];
        for (int i = 0; i < activationCount; i++) activations[i] = r.ReadSingle();
        c.Brain.SetActivationState(activations);

        return c;
    }
}
