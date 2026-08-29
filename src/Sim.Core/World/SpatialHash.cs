using System.Collections.Generic;
using Sim.Core.Entities;

namespace Sim.Core;

/// <summary>
/// Uniform grid over creature positions, rebuilt from scratch every tick (§2). Deterministic:
/// creatures must be inserted in ascending id order so each bucket ends up id-ordered too.
/// Bucket lists are cleared (not reallocated) each rebuild, so steady-state rebuilds are
/// allocation-free.
/// </summary>
public sealed class SpatialHash
{
    private readonly float _cellSize;
    private readonly float _worldWidth;
    private readonly int _gridDim;
    private readonly List<int>[] _buckets; // holds indices into the creature list

    public SpatialHash(float worldWidth, float cellSize)
    {
        _worldWidth = worldWidth;
        _cellSize = cellSize;
        _gridDim = System.Math.Max(1, (int)System.MathF.Ceiling(worldWidth / cellSize));
        _buckets = new List<int>[_gridDim * _gridDim];
        for (int i = 0; i < _buckets.Length; i++) _buckets[i] = new List<int>();
    }

    private int CellOf(float x, float y)
    {
        int cx = (int)(x / _cellSize);
        int cy = (int)(y / _cellSize);
        if (cx < 0) cx = 0; else if (cx >= _gridDim) cx = _gridDim - 1;
        if (cy < 0) cy = 0; else if (cy >= _gridDim) cy = _gridDim - 1;
        return cy * _gridDim + cx;
    }

    /// <summary>Rebuild from the given creature list, which must already be in ascending-id order.</summary>
    public void Rebuild(List<Creature> creaturesInIdOrder)
    {
        for (int i = 0; i < _buckets.Length; i++) _buckets[i].Clear();

        for (int i = 0; i < creaturesInIdOrder.Count; i++)
        {
            var c = creaturesInIdOrder[i];
            if (!c.Alive) continue;
            _buckets[CellOf(c.X, c.Y)].Add(i);
        }
    }

    /// <summary>Indices (into the list passed to Rebuild) of creatures in the cell containing (x, y).</summary>
    public IReadOnlyList<int> CreaturesInCellAt(float x, float y) => _buckets[CellOf(x, y)];

    public int GridDim => _gridDim;
    public float CellSize => _cellSize;

    /// <summary>Indices of creatures in the 3x3 block of cells centered on (x, y) — the usual query for range-limited sensors.</summary>
    public void QueryNeighborhood(float x, float y, List<int> result)
    {
        result.Clear();
        int cx = (int)(x / _cellSize);
        int cy = (int)(y / _cellSize);
        for (int dy = -1; dy <= 1; dy++)
        {
            int gy = cy + dy;
            if (gy < 0 || gy >= _gridDim) continue;
            for (int dx = -1; dx <= 1; dx++)
            {
                int gx = cx + dx;
                if (gx < 0 || gx >= _gridDim) continue;
                var bucket = _buckets[gy * _gridDim + gx];
                for (int k = 0; k < bucket.Count; k++) result.Add(bucket[k]);
            }
        }
    }
}
