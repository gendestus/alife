using Sim.Core.Genetics;

namespace Sim.Core;

// Plain event payloads World raises as things happen (§8 event table semantics). No DB/Npgsql
// knowledge here — Sim.Persistence (or any other subscriber) translates these into rows.

public struct GenomeCreatedInfo
{
    public long GenomeId;
    public long? ParentGenomeId;
    public Genome Genome;
    public long FirstSeenTick;
}

public struct EggLaidInfo
{
    public ulong ParentId;
    public ulong EggId;
    public long GenomeId;
    public float X, Y;
    public float EggEnergy;
    public long Tick;
}

public struct EggHatchedInfo
{
    public ulong EggId;
    public ulong CreatureId;
    public float X, Y;
    public long Tick;
}

public struct EggEatenInfo
{
    public ulong EaterId;
    public ulong EggId;
    public float X, Y;
    public float ValueGained;
    public long Tick;
}

public struct BiteInfo
{
    public ulong BiterId;
    public ulong TargetId;
    public float X, Y;
    public float Damage;
    public long Tick;
}
