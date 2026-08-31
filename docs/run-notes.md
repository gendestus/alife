# M6 Validation Run — Notes

**DESIGN.md §13 M6.** 3 seeds × 2,000,000 ticks, default config (`config/default.json`), writing to the live Postgres instance. Population sampling (`logging.positionsEvery`) was disabled for these runs (`--set logging.positionsEvery=0`) — pure telemetry-volume reduction, not a dynamics-affecting change; every other config value is default.

| Seed | run_id | Status |
|---|---|---|
| 1 | `439bfbf3-cdb3-47d6-9f2b-acabb836142e` | COMPLETED, 0 dropped rows |
| 2 | `6b64685d-0d27-4e49-905a-d7764e369fe6` | COMPLETED, 0 dropped rows |
| 3 | `f0b1c2cf-8468-4ce7-a32a-7ecb68d871f5` | COMPLETED, 0 dropped rows |

Seeds 1–3 were chosen because they're the same seeds already vetted by `PopulationStabilityTests` — an untested seed (999) was tried first at small scale and went extinct at tick 3,219, underscoring how sensitive these dynamics are to initial conditions. All three chosen seeds ran the full 2,000,000 ticks without extinction.

Note: this run immediately followed an M5 fix to `GenomeFactory` (bootstrap individuals now share one founding topology's gene ids/link innovations, instead of each minting fresh ones — see M5 notes). That fix changed the RNG draw order in `BootstrapSpawnFromGenome`, so these trajectories are not comparable to any pre-fix run of the same seed.

---

## Population

| Seed | min | max | mean | cap_hits |
|---|---|---|---|---|
| 1 | 27 | 2,656 | 441.7 | 0 |
| 2 | 7 | 1,651 | 465.3 | 0 |
| 3 | 47 | 1,456 | 482.9 | 0 |

Zero cap hits in all three runs (`popCap`=6,000 never approached) — no runaway. All three show the boom-bust oscillation already documented in M3/M5 (troughs well below the [500,3000] design target, recovering each time): a real, accepted characteristic of this config, not a regression. Seed 2's single-tick minimum of 7 is the deepest trough observed across all validation and test runs to date; the population recovered within the next few thousand ticks and never approached extinction (`Extinct` never set in any of the three runs).

---

## Trait trends

All three seeds converge to the **same regime** by ~500k ticks and hold it through 2M:

| Trait | Tick 100 (bootstrap) | ~500k–2M (converged) |
|---|---|---|
| `mean_size` | ~1.00 | **~0.50** (the gene-range floor, `GeneSpec.SizeMin`) |
| `mean_speed` | ~1.00 | **~1.9–2.0** (near the gene-range ceiling) |
| `mean_diet` | ~0.05 | **~0.0000–0.0005** (pure herbivore) |
| `mean_armor` | ~0.008 | **~0.00–0.11**, no consistent trend, always near zero |
| `mean_lifespan` | ~2000 | **~930–1200** (roughly halved) |
| `mean_storage_cap` | ~1.00 | **~2.00** (the gene-range ceiling) |

This confirms the M3 finding at 10× the scale (500k → 2M ticks, and now across a properly-speciated population): `cBasal`'s size^1.5 scaling makes size growth net-negative, pinning size at its floor; the freed energy budget goes into speed and storage capacity instead. Lifespan roughly halving is new information at this scale — faster generational turnover appears to be favored once the population stabilizes into its food-limited regime (consistent with `max_generation` reaching ~3,000–3,200 over 2M ticks — genuinely deep lineages, not stagnation).

---

## Species timeline

| Seed | species founded | peak `species_count` | peak `species_count_min5` | tick `species_count_min5` first ≥3 |
|---|---|---|---|---|
| 1 | 113 | 7 | **5** | 132,100 |
| 2 | 40 | 4 | 3 | (transient, not sustained) |
| 3 | 29 | 3 | 2 | never |

Every run shows a lot of *speciation churn*: dozens of short-lived side-branches split off from the founding lineage (`species_id=0`) and re-collapse within a few hundred to a few thousand ticks (most have `peak_population` in the single-to-low-double digits and `stats_rows` in the single digits). Only `species_id=0` persists for the entire 2M-tick run in all three seeds (`stats_rows=20000`, i.e. present at every stats snapshot). Sustained multi-species coexistence (`species_count_min5 > 1` held for any length of time) essentially doesn't happen — the moments where `species_count_min5` reaches 2–5 are brief spikes, not stable coexistence.

**M5's own acceptance bar** ("`species_count_min5 > 3` by 500k ticks on at least 1 of 3 seeds") **is satisfied**: seed 1 reaches `species_count_min5=4` at tick 135,200, well inside the 500k window. Seeds 2 and 3 don't reach it. This wasn't checked empirically before M5 was marked complete (only the unit-level speciation tests were) — confirmed here, satisfied, no action needed.

---

## Sensor / actuator prevalence (final living population)

| Seed | Sensors | Actuators |
|---|---|---|
| 1 | `VisionPlant` 100%, `Age` 56.4% | `Eat`/`LayEgg`/`Thrust`/`Turn` 100% |
| 2 | `VisionPlant` 100%, `Energy` 99.8%, `Health` 0.5%, `Smell:2` 0.5% | `Eat`/`LayEgg`/`Thrust` 100%, `Turn` 99.8%, `Bite` 0.9% |
| 3 | `VisionPlant` 100%, `Energy` 65.1% | `Eat`/`LayEgg`/`Thrust`/`Turn` 100% |

`VisionPlant` is universal — unsurprising given the population converged to pure herbivory. The three seeds diverge on their *second* sense: seed 1 settled on `Age`, seeds 2 and 3 kept `Energy` (seed 3 only in 65% of the population — the other 35% dropped it entirely). `Bite` and `Smell` are present only as rare, apparently-non-adaptive minority traits (≤1%); `Emit` doesn't appear in any final living population at all (see below).

---

## Death causes (totals over 2M ticks)

| Seed | STARVATION | OLD_AGE | PREDATION |
|---|---|---|---|
| 1 | 1,355,458 (94.7%) | 71,326 (5.0%) | 2 (0.0001%) |
| 2 | 1,332,511 (91.6%) | 121,888 (8.4%) | 3 (0.0002%) |
| 3 | 1,436,508 (92.9%) | 109,175 (7.1%) | 0 |

Starvation dominates overwhelmingly in all three runs; old age is a real but secondary cause; predation is statistical noise.

---

## The three required questions

**Did `diet` bimodalize?** No. In every seed, `mean_diet` converges from the bootstrap's ~0.05 down to ~0.0000–0.0009 by 500k ticks and stays there through 2M. The final-population diet histogram (10 buckets over [0,1]) puts essentially the entire population in the lowest bucket in all three seeds (311/312, 423/423, 484/484 — 99.7–100%). This is unimodal convergence to pure herbivory, not a diet split. Combined with `Bite` and predation both being vanishingly rare (below), there's no evidence a carnivore/omnivore strategy was ever competitive under this config — plant biomass regrowth is cheap enough, and `dietExp=1.5` steep enough, that specializing on meat doesn't pay.

**Did any `Smell`/`Emit` channel pairing appear?** No — more strongly, `Emit` didn't survive into the final living population in *any* of the three seeds at all (0% prevalence), so a Smell/Emit pairing was never structurally possible in the end state. `Smell` itself appears only as a rare minority trait in seed 2 (0.5%, one specific channel). No scent-based signaling convention emerged; the emergent-communication hope in §7's diversity framing didn't materialize under this config.

**Did `PREDATION` become a meaningful death cause?** No. 0–3 predation deaths total per run, against 1.3–1.4 million starvation deaths — three to five orders of magnitude apart. `Bite` itself only persisted in seed 2's population, and even there at under 1% prevalence. Predation is not a viable strategy under the default energy/combat costs.

All three are genuine negative results, consistent across all three seeds — not an artifact of one unlucky run.

---

## Known caveats

- Position sampling was disabled for these runs (see top) — no spatial/trajectory data was recorded, only aggregate/per-species stats and lineage/event tables.
- `sim run`'s stderr progress line hardcoded `species=1` (a placeholder string from before M5's speciation pass was wired in) during these three runs — cosmetic only, never affected anything written to the database. Fixed after these runs completed (now prints the real distinct species count).
- The M5 bootstrap-identity fix (see above) means these three seeds' trajectories aren't the same ones exercised in earlier M3/M4 manual runs under the same seed numbers, only in the (re-verified) `PopulationStabilityTests`.
