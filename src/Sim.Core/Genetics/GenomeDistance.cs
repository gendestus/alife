using System;
using System.Collections.Generic;
using Sim.Core.Config;

namespace Sim.Core.Genetics;

/// <summary>
/// §7 genome distance: d = c1·(E+D)/N + c2·W̄ + c3·B + c4·(1−J).
///
/// <see cref="Profile"/> precomputes the per-genome data (sorted links, normalized scalars,
/// sorted enabled-gene-kind multiset) once, so callers doing many pairwise comparisons against
/// the same genomes (the §7 mean_pairwise_distance sample: 200 creatures, 19,900 pairs, every
/// statsEvery ticks) build each profile once instead of re-deriving it per pair.
/// </summary>
public static class GenomeDistance
{
    private const int ScalarCount = 11; // Size,Speed,Armor,ColorR,ColorG,ColorB,Diet,StorageCap,Lifespan,EggThreshold,EggInvestment

    public readonly struct Profile
    {
        // Links are appended in increasing-innovation order by construction (mutation only
        // appends, RemoveActuator's RemoveAll preserves relative order) — sorted ascending here
        // defensively so the two-pointer merge in Distance() is correct even if that ever changes.
        public readonly (int Innovation, float Weight)[] Links;
        public readonly float[] Scalars; // normalized to [0,1], fixed order (ScalarCount)
        public readonly string[] EnabledKinds; // sorted ordinal, "S:kind[:channel]" / "A:kind[:channel]"

        internal Profile((int, float)[] links, float[] scalars, string[] enabledKinds)
        {
            Links = links;
            Scalars = scalars;
            EnabledKinds = enabledKinds;
        }

        public static Profile Build(Genome g)
        {
            var links = new (int, float)[g.Brain.Links.Count];
            for (int i = 0; i < links.Length; i++)
            {
                var l = g.Brain.Links[i];
                links[i] = (l.Innovation, l.Weight);
            }
            Array.Sort(links, (a, b) => a.Item1.CompareTo(b.Item1));

            var scalars = new float[ScalarCount];
            scalars[0] = Norm(g.Body.Size, GeneSpec.SizeMin, GeneSpec.SizeMax);
            scalars[1] = Norm(g.Body.Speed, GeneSpec.SpeedMin, GeneSpec.SpeedMax);
            scalars[2] = Norm(g.Body.Armor, GeneSpec.ArmorMin, GeneSpec.ArmorMax);
            scalars[3] = Norm(g.Body.ColorR, GeneSpec.ColorMin, GeneSpec.ColorMax);
            scalars[4] = Norm(g.Body.ColorG, GeneSpec.ColorMin, GeneSpec.ColorMax);
            scalars[5] = Norm(g.Body.ColorB, GeneSpec.ColorMin, GeneSpec.ColorMax);
            scalars[6] = Norm(g.Metabolism.Diet, GeneSpec.DietMin, GeneSpec.DietMax);
            scalars[7] = Norm(g.Metabolism.StorageCap, GeneSpec.StorageCapMin, GeneSpec.StorageCapMax);
            scalars[8] = Norm(g.Metabolism.Lifespan, GeneSpec.LifespanMin, GeneSpec.LifespanMax);
            scalars[9] = Norm(g.Repro.EggThreshold, GeneSpec.EggThresholdMin, GeneSpec.EggThresholdMax);
            scalars[10] = Norm(g.Repro.EggInvestment, GeneSpec.EggInvestmentMin, GeneSpec.EggInvestmentMax);

            var kinds = new List<string>();
            foreach (var s in g.Sensors)
            {
                if (!s.Enabled) continue;
                kinds.Add(GeneSpec.SensorUsesChannel(s.Kind) ? $"S:{s.Kind}:{s.Channel}" : $"S:{s.Kind}");
            }
            foreach (var a in g.Actuators)
            {
                if (!a.Enabled) continue;
                kinds.Add(GeneSpec.ActuatorUsesChannel(a.Kind) ? $"A:{a.Kind}:{a.Channel}" : $"A:{a.Kind}");
            }
            kinds.Sort(StringComparer.Ordinal);

            return new Profile(links, scalars, kinds.ToArray());
        }

        private static float Norm(float v, float lo, float hi) =>
            hi > lo ? Math.Clamp((v - lo) / (hi - lo), 0f, 1f) : 0f;
    }

    public static float Compute(Genome a, Genome b, SpeciesConfig cfg) =>
        Distance(Profile.Build(a), Profile.Build(b), cfg);

    public static float Distance(in Profile a, in Profile b, SpeciesConfig cfg)
    {
        // --- E, D: excess/disjoint links by innovation number, W̄: mean |Δweight| over matching. ---
        int i = 0, j = 0, excess = 0, disjoint = 0, matching = 0;
        double weightDiffSum = 0;
        int maxInnoA = a.Links.Length > 0 ? a.Links[^1].Innovation : 0;
        int maxInnoB = b.Links.Length > 0 ? b.Links[^1].Innovation : 0;

        while (i < a.Links.Length && j < b.Links.Length)
        {
            int ia = a.Links[i].Innovation, ib = b.Links[j].Innovation;
            if (ia == ib)
            {
                matching++;
                weightDiffSum += Math.Abs(a.Links[i].Weight - b.Links[j].Weight);
                i++; j++;
            }
            else if (ia < ib)
            {
                if (ia > maxInnoB) excess++; else disjoint++;
                i++;
            }
            else
            {
                if (ib > maxInnoA) excess++; else disjoint++;
                j++;
            }
        }
        while (i < a.Links.Length) { if (a.Links[i].Innovation > maxInnoB) excess++; else disjoint++; i++; }
        while (j < b.Links.Length) { if (b.Links[j].Innovation > maxInnoA) excess++; else disjoint++; j++; }

        int n = Math.Max(a.Links.Length, b.Links.Length);
        if (n < 1) n = 1;
        float wbar = matching > 0 ? (float)(weightDiffSum / matching) : 0f;

        // --- B: Euclidean distance over normalized scalar genes. ---
        double sumSq = 0;
        for (int k = 0; k < ScalarCount; k++)
        {
            float diff = a.Scalars[k] - b.Scalars[k];
            sumSq += diff * diff;
        }
        float bDist = (float)Math.Sqrt(sumSq);

        // --- J: Jaccard similarity over the enabled (kind,channel) multiset (sorted-merge counting). ---
        int ki = 0, kj = 0, intersection = 0, union_ = 0;
        while (ki < a.EnabledKinds.Length && kj < b.EnabledKinds.Length)
        {
            int cmp = string.CompareOrdinal(a.EnabledKinds[ki], b.EnabledKinds[kj]);
            if (cmp == 0) { intersection++; union_++; ki++; kj++; }
            else if (cmp < 0) { union_++; ki++; }
            else { union_++; kj++; }
        }
        union_ += (a.EnabledKinds.Length - ki) + (b.EnabledKinds.Length - kj);
        float jaccard = union_ > 0 ? (float)intersection / union_ : 1f;

        return cfg.C1 * (excess + disjoint) / n + cfg.C2 * wbar + cfg.C3 * bDist + cfg.C4 * (1f - jaccard);
    }
}
