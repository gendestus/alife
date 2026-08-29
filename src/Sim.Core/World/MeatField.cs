using System.Collections.Generic;

namespace Sim.Core;

/// <summary>Holds all live meat items and advances their decay in place, no per-tick allocation.</summary>
public sealed class MeatField
{
    private readonly List<Meat> _items = new();
    private readonly float _decay;

    public MeatField(float decay)
    {
        _decay = decay;
    }

    public IReadOnlyList<Meat> Items => _items;
    public int Count => _items.Count;

    public void Spawn(float x, float y, float energy)
    {
        _items.Add(new Meat(x, y, energy));
    }

    /// <summary>Decay every item by one tick; compact out anything below the removal threshold.</summary>
    public void Decay()
    {
        int write = 0;
        for (int read = 0; read < _items.Count; read++)
        {
            var m = _items[read];
            m.Energy *= _decay;
            if (m.Energy < 0.5f) continue;
            _items[write++] = m;
        }
        if (write < _items.Count)
        {
            _items.RemoveRange(write, _items.Count - write);
        }
    }

    public float TotalEnergy()
    {
        double sum = 0.0;
        for (int i = 0; i < _items.Count; i++) sum += _items[i].Energy;
        return (float)sum;
    }
}
