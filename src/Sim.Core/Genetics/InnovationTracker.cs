using System.Collections.Generic;

namespace Sim.Core.Genetics;

/// <summary>
/// Run-global monotonic id/innovation counters (§4.2-§4.4). Owned by World, consumed in a
/// fixed order (bootstrap gene creation, then mutation operators in their documented order).
/// The (from,to) -> innovation dictionary is looked up, never iterated, so it doesn't violate
/// the no-unordered-iteration rule.
/// </summary>
public sealed class InnovationTracker
{
    private long _nextSensorGeneId;
    private long _nextActuatorGeneId;
    private int _nextHiddenNodeId = 1;
    private int _nextLinkInnovation;
    private readonly Dictionary<(int from, int to), int> _linkInnovations = new();

    public long NextSensorGeneId() => _nextSensorGeneId++;

    public long NextActuatorGeneId() => _nextActuatorGeneId++;

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
}
