using System.Collections.Generic;

namespace Sim.Core.Genetics;

/// <summary>Snapshot of InnovationTracker's state — used for the world state hash (§12 test 1) and checkpointing. LinkInnovations is sorted by (from,to) so it's deterministic regardless of the live dictionary's internal order.</summary>
public sealed class InnovationState
{
    public long NextSensorGeneId;
    public long NextActuatorGeneId;
    public long NextGenomeId;
    public int NextHiddenNodeId;
    public int NextLinkInnovation;
    public (int From, int To, int Innovation)[] LinkInnovations = System.Array.Empty<(int, int, int)>();
}

/// <summary>
/// Run-global monotonic id/innovation counters (§4.2-§4.4). Owned by World, consumed in a
/// fixed order (bootstrap gene creation, then mutation operators in their documented order).
/// The (from,to) -> innovation dictionary is looked up, never iterated in a way that affects
/// simulation state — GetState() sorts before exposing it, for the one place (hashing/
/// checkpointing) that does need a deterministic full view.
/// </summary>
public sealed class InnovationTracker
{
    private long _nextSensorGeneId;
    private long _nextActuatorGeneId;
    private long _nextGenomeId;
    private int _nextHiddenNodeId = 1;
    private int _nextLinkInnovation;
    private readonly Dictionary<(int from, int to), int> _linkInnovations = new();

    public long NextSensorGeneId() => _nextSensorGeneId++;

    public long NextActuatorGeneId() => _nextActuatorGeneId++;

    public long NextGenomeId() => _nextGenomeId++;

    public int NextHiddenNodeId() => _nextHiddenNodeId++;

    /// <summary>Same (from,to) pair always gets the same innovation number within a run.</summary>
    public int LinkInnovation(int from, int to)
    {
        var key = (from, to);
        if (_linkInnovations.TryGetValue(key, out int existing)) return existing;
        int assigned = _nextLinkInnovation++;
        _linkInnovations[key] = assigned;
        return assigned;
    }

    public InnovationState GetState()
    {
        var links = new (int, int, int)[_linkInnovations.Count];
        int i = 0;
        foreach (var kv in _linkInnovations)
        {
            links[i++] = (kv.Key.from, kv.Key.to, kv.Value);
        }
        System.Array.Sort(links, (a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));

        return new InnovationState
        {
            NextSensorGeneId = _nextSensorGeneId,
            NextActuatorGeneId = _nextActuatorGeneId,
            NextGenomeId = _nextGenomeId,
            NextHiddenNodeId = _nextHiddenNodeId,
            NextLinkInnovation = _nextLinkInnovation,
            LinkInnovations = links,
        };
    }

    public void SetState(InnovationState state)
    {
        _nextSensorGeneId = state.NextSensorGeneId;
        _nextActuatorGeneId = state.NextActuatorGeneId;
        _nextGenomeId = state.NextGenomeId;
        _nextHiddenNodeId = state.NextHiddenNodeId;
        _nextLinkInnovation = state.NextLinkInnovation;
        _linkInnovations.Clear();
        foreach (var (from, to, innovation) in state.LinkInnovations)
        {
            _linkInnovations[(from, to)] = innovation;
        }
    }
}
