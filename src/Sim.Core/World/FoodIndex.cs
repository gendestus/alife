using System.Collections.Generic;
using Sim.Core.Entities;

namespace Sim.Core;

/// <summary>
/// Uniform grid over meat and egg positions, rebuilt from scratch every tick — the meat/egg
/// analogue of <see cref="SpatialHash"/>. Added in M3 after linear scans over Meat/Eggs (fine
/// at M2's handful-of-creatures scale) turned out to dominate tick cost once populations
/// reached thousands with thousands of lingering corpses. Cell size reuses config.World.HashCell,
/// the same knob the creature hash already uses — no new hardcoded constant.
///
/// Eggs are tombstoned (Energy set to 0) rather than removed on predation, so bucket indices
/// stay valid for the rest of the tick even though eggs can be "eaten" mid-act-loop; actual
/// removal happens during the end-of-tick hatch/compaction pass.
/// </summary>
public sealed class FoodIndex
{
    private readonly float _cellSize;
    private readonly int _gridDim;
    private readonly List<int>[] _meatBuckets;
    private readonly List<int>[] _eggBuckets;

    public FoodIndex(float worldWidth, float cellSize)
    {
        _cellSize = cellSize;
        _gridDim = System.Math.Max(1, (int)System.MathF.Ceiling(worldWidth / cellSize));
        _meatBuckets = new List<int>[_gridDim * _gridDim];
        _eggBuckets = new List<int>[_gridDim * _gridDim];
        for (int i = 0; i < _meatBuckets.Length; i++)
        {
            _meatBuckets[i] = new List<int>();
            _eggBuckets[i] = new List<int>();
        }
    }

    private int CellOf(float x, float y)
    {
        int cx = (int)(x / _cellSize);
        int cy = (int)(y / _cellSize);
        if (cx < 0) cx = 0; else if (cx >= _gridDim) cx = _gridDim - 1;
        if (cy < 0) cy = 0; else if (cy >= _gridDim) cy = _gridDim - 1;
        return cy * _gridDim + cx;
    }

    public void Rebuild(MeatField meat, List<Egg> eggs)
    {
        for (int i = 0; i < _meatBuckets.Length; i++)
        {
            _meatBuckets[i].Clear();
            _eggBuckets[i].Clear();
        }

        for (int i = 0; i < meat.Count; i++)
        {
            var m = meat.Items[i];
            _meatBuckets[CellOf(m.X, m.Y)].Add(i);
        }
        for (int i = 0; i < eggs.Count; i++)
        {
            var egg = eggs[i];
            if (egg.Energy <= 0f) continue; // tombstoned (eaten earlier this tick — shouldn't happen at rebuild, but cheap to guard)
            _eggBuckets[CellOf(egg.X, egg.Y)].Add(i);
        }
    }

    /// <summary>Meat/egg indices (into MeatField.Items / the Eggs list) within cells overlapping a circle of the given radius around (x, y).</summary>
    public void QueryRadius(float x, float y, float radius, List<int> meatResult, List<int> eggResult)
    {
        meatResult.Clear();
        eggResult.Clear();
        int cx = (int)(x / _cellSize);
        int cy = (int)(y / _cellSize);
        int cellRadius = System.Math.Max(1, (int)System.MathF.Ceiling(radius / _cellSize));
        for (int dy = -cellRadius; dy <= cellRadius; dy++)
        {
            int gy = cy + dy;
            if (gy < 0 || gy >= _gridDim) continue;
            for (int dx = -cellRadius; dx <= cellRadius; dx++)
            {
                int gx = cx + dx;
                if (gx < 0 || gx >= _gridDim) continue;
                int idx = gy * _gridDim + gx;
                var meatBucket = _meatBuckets[idx];
                for (int k = 0; k < meatBucket.Count; k++) meatResult.Add(meatBucket[k]);
                var eggBucket = _eggBuckets[idx];
                for (int k = 0; k < eggBucket.Count; k++) eggResult.Add(eggBucket[k]);
            }
        }
    }
}
