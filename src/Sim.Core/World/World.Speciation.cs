using Sim.Core.Genetics;

namespace Sim.Core;

/// <summary>NEAT-style speciation pass (§7), run every speciateEvery ticks from Tick().</summary>
public sealed partial class World
{
    private void RunSpeciationPass()
    {
        var cfg = _cfg.Species;

        // Active candidates for matching: species seen within the retain window, ordered by id
        // (§7 step 1 — "species ordered by id"). Species beyond the window are left in
        // _speciesById (permanent historical record) but excluded from matching.
        _activeSpecies.Clear();
        foreach (var s in _speciesById.Values)
        {
            if (CurrentTick - s.LastSeenTick <= cfg.RetainTicks) _activeSpecies.Add(s);
        }
        _activeSpecies.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var s in _activeSpecies) s.MembersScratch.Clear();

        foreach (var c in Creatures)
        {
            if (c.Genome is null) continue; // M1 legacy random-walk creatures carry no genome

            int previousSpeciesId = c.SpeciesId;
            SpeciesRecord? matched = null;
            foreach (var s in _activeSpecies)
            {
                float d = GenomeDistance.Compute(c.Genome, s.Representative, cfg);
                if (d < cfg.Delta) { matched = s; break; }
            }

            if (matched is null)
            {
                // Only a genuinely-registered prior species counts as a parent — a creature's
                // placeholder SpeciesId=0 before the very first pass isn't one (§7 step 2).
                int? parentSpeciesId = _speciesById.ContainsKey(previousSpeciesId) ? previousSpeciesId : null;
                matched = new SpeciesRecord
                {
                    Id = _nextSpeciesId++,
                    FoundedTick = CurrentTick,
                    ParentSpeciesId = parentSpeciesId,
                    FounderGenomeId = c.GenomeId,
                    Representative = c.Genome,
                    LastSeenTick = CurrentTick,
                };
                _speciesById[matched.Id] = matched;
                _activeSpecies.Add(matched); // monotonic id => stays appended in ascending order
                SpeciesCreated?.Invoke(new SpeciesCreatedInfo
                {
                    SpeciesId = matched.Id,
                    FoundedTick = CurrentTick,
                    FounderGenomeId = c.GenomeId,
                    ParentSpeciesId = parentSpeciesId,
                });
            }

            c.SpeciesId = matched.Id;
            matched.LastSeenTick = CurrentTick;
            matched.MembersScratch.Add(c.Genome);
        }

        // §7 step 3: each species with members picks a new representative uniformly at random
        // from members (RNG, deterministic — consumed in ascending species-id order so a
        // checkpoint/resume that restores _speciesById identically reproduces this draw sequence).
        foreach (var s in _activeSpecies)
        {
            if (s.MembersScratch.Count == 0) continue;
            s.Representative = s.MembersScratch[_rng.NextInt(0, s.MembersScratch.Count)];
        }
    }
}
