using System.Collections.Generic;
using Sim.Core.Genetics;

namespace Sim.Core;

/// <summary>
/// Persistent NEAT-style species bookkeeping (§7). Lives for the run's duration once founded —
/// "dropped from matching" (after speciesRetainTicks with zero members) means excluded from the
/// speciation pass's active candidate list, not deleted; the `species` DB table row (written
/// once, at founding) is a permanent historical record.
/// MembersScratch is rebuilt every speciation pass and is not checkpointed.
/// </summary>
public sealed class SpeciesRecord
{
    public int Id;
    public long FoundedTick;
    public int? ParentSpeciesId;
    public long FounderGenomeId;
    public long LastSeenTick;
    public Genome Representative = null!;

    public readonly List<Genome> MembersScratch = new();
}
