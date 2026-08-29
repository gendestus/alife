# ALIFE-2D — Design Document

Headless 2D artificial-life evolution simulation. C# / .NET 8. Observability to Postgres.

**Read this whole document before writing code.** Build in the milestone order in §13; each milestone has acceptance criteria. Do not add gameplay, rendering, threading, or crossover — they are explicitly deferred (§15). When the spec is silent, apply the design principles in §0.

---

## 0. Purpose and scope

This is a research sim, not a game. The goal is to observe selection and, ideally, emergent behavior in a population of creatures whose bodies, senses, and brains are encoded in a variable-length genome, living in a resource-limited 2D world, reproducing by egg with mutation.

Everything observable is written to Postgres so a later Unity/VR viewer can replay a run (or attach live), and so runs can be analyzed with SQL. The viewer is out of scope; it only consumes the tables in §8.

### Design principles

1. **Typed genes, never bytes.** A genome is a list of structured genes. Mutation operates on fields, so every mutation yields a decodable genome by construction.
2. **Variable length.** Sensors, actuators, and brain topology can grow. Open-endedness needs room to grow.
3. **Every trait has an energy cost.** Nothing is free. Complexity must earn its keep.
4. **Sensors and actuators are genes; brain I/O follows them.** Adding a sensor gene adds brain input nodes. This is the main emergence enabler.
5. **World channels carry no designed meaning.** Color is three numbers others can see. Scent is four channels anyone can emit into or smell. Meaning, if any, emerges.
6. **Deterministic.** Same seed + config + binary → identical run. No wall clock and no unordered iteration anywhere in `Sim.Core`.
7. **Append-only persistence.** No `UPDATE` in the hot path.

### Non-goals for v1

Gameplay, rendering, sexual reproduction/crossover, overseer AIs, program-style (VM) genomes, terrain hazards, multithreading.

---

## 1. Solution layout

```
alife/
├── Alife.sln
├── DESIGN.md                      # this file
├── db/
│   └── schema.sql                 # tables, indexes, views, functions (§8)
├── config/
│   └── default.json               # all tunables (§9)
├── src/
│   ├── Sim.Core/                  # world, entities, genome, brain, mutation, energy, speciation.
│   │                              #   ZERO external dependencies. Pure, deterministic C#.
│   ├── Sim.Persistence/           # Npgsql binary COPY writer, batching, background flush.
│   └── Sim.Cli/                   # entry point, config loading, run loop, stats scheduling.
└── tests/
    └── Sim.Core.Tests/            # xUnit (§12)
```

Conventions:
- .NET 8, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- `Sim.Core` references nothing. `Sim.Persistence` references `Sim.Core` + Npgsql. `Sim.Cli` references both.
- Hot loops: no LINQ, no per-tick allocations, `readonly struct` where sensible, flat arrays for brain state.
- Never iterate a `Dictionary`/`HashSet` in a way that affects simulation state. Use ordered lists or sorted keys.
- One `IRandom` (xoshiro256** or `System.Random` with seed — pick one and never mix) owned by `World`, consumed in a fixed order.

---

## 2. World

**Coordinates.** Continuous 2D, `x, y ∈ [0, W)`, `W = 512` default. Bounded: positions are clamped at walls, no damage. (`toroidal: true` config option wraps instead.) Headings in radians.

**Time.** 1 tick = 1 sim step. All durations in ticks. No wall clock in `Sim.Core`.

### Layers

| Layer | Storage | Behavior |
|---|---|---|
| **Plants** | `P×P` grid, `P=128` (cell = 4 units). `float biomass[]`, `float capacity[]` | `capacity` from 2-octave value noise scaled to `[0.2·bMax, bMax]`, `bMax=10` — fertile and barren regions. Each tick: `b += r·b·(1 − b/K) + seed`, `r=0.01`, `seed=0.002`, clamp `[0,K]`. |
| **Meat** | `List<Meat>` (x, y, energy) | Created on creature death with `energy = corpseEnergy·size` (`corpseEnergy=30`). Decays `×0.995`/tick. Removed when `< 0.5`. |
| **Scent** | 4 channels × `S×S` grid, `S=64` (cell = 8 units) | Every `scentStep=4` ticks: decay `×0.97`, then 4-neighbor diffusion `c' = (1−α)·c + α·mean(neighbors)`, `α=0.2`. Emit actuator deposits. Clamp `[0,100]`. |
| **Spatial hash** | Uniform grid, cell = 8 units, over creatures, eggs, meat | Rebuilt each tick from scratch (simple, deterministic). Bucket contents ordered by entity id. |

### Tick order (fixed)

1. Plants regrow. Scent step if `tick % scentStep == 0`.
2. Rebuild spatial hash.
3. For each living creature in **ascending id order**: sense → brain step → act. Actions that affect others (bite, eat egg/meat) apply immediately. The order asymmetry is accepted.
4. For each creature: apply upkeep costs, age += 1, health regen, death checks. Dead creatures → meat.
5. Eggs: hatch where `tick ≥ hatchTick`. Assign new creature ids in egg-id order.
6. Speciation pass if `tick % speciateEvery == 0` (§7).
7. Stats + persistence hooks (§8).

---

## 3. Entities

```csharp
sealed class Creature {
  ulong Id;                 // run-global, monotonic, assigned at hatch
  long GenomeId;
  int SpeciesId;
  float X, Y, Heading;
  float Energy, MaxEnergy, Health, MaxHealth;
  int Age;                  // ticks since hatch
  long BirthTick;
  ulong ParentId;
  int Generation;
  int OffspringCount;       // eggs laid
  Brain Brain;              // decoded from genome once at hatch; holds activation buffers
  float[] SensorInputs;     // length = brain input count
  float[] ActuatorOutputs;  // length = brain output count
  ulong LastDamagedBy; long LastDamagedTick;
}

sealed class Egg   { ulong Id; Genome Genome; long GenomeId; float X, Y, Energy; long LaidTick, HatchTick; ulong ParentId; int SpeciesId; int Generation; }
struct Meat        { float X, Y, Energy; }
```

The egg holds the **already-mutated** genome; mutation happens at lay time (§6). Genome id is assigned (and the genome row emitted) at lay time so an egg eaten before hatching still has a persisted genome.

---

## 4. Genome

A `Genome` is a plain record of typed sections. Serialize to canonical JSON (sorted keys, fixed float formatting `R`) for hashing and storage.

```
Genome
├─ Meta         mutationRate, structuralRate
├─ Body         size, speed, armor, colorR, colorG, colorB
├─ Metabolism   diet, storageCap, lifespan
├─ Sensors[]    { id, kind, channel, range, angle, fov, enabled }
├─ Actuators[]  { id, kind, channel, strength, enabled }
├─ Brain        nodes[] { id, kind, bindGeneId, bindSlot }
│               links[] { innovation, from, to, weight, enabled }
└─ Repro        eggThreshold, eggInvestment
```

### 4.1 Scalar genes

Every scalar gene has a benefit and a cost. Ranges are hard clamps.

| Section | Gene | Range | Effect | Per-tick cost |
|---|---|---|---|---|
| Meta | `mutationRate` | `[0.002, 0.2]` | P(perturb) per scalar field at egg lay (§6) | none |
| Meta | `structuralRate` | `[0.001, 0.1]` | P(each structural mutation) at egg lay | none |
| Body | `size` | `[0.5, 3.0]` | `MaxEnergy = 100·size·storageCap`; `MaxHealth = 50·size`; bite damage ∝ size; bite reach; movement cost ∝ size; corpse energy ∝ size | `cBasal·size^1.5`, `cBasal=0.03` |
| Body | `speed` | `[0.0, 2.0]` | Max forward units/tick at full thrust | folded into movement cost |
| Body | `armor` | `[0.0, 1.0]` | Damage taken `× (1 − 0.8·armor)` | `cArmor·armor·size`, `cArmor=0.03` |
| Body | `colorR/G/B` | `[0,1]` each | Visible to `VisionCreature` sensors. No other effect. | none |
| Metabolism | `diet` | `[0,1]` | `plantEff = (1−diet)^dietExp`, `meatEff = diet^dietExp`, `dietExp=1.5` (generalists penalized) | none |
| Metabolism | `storageCap` | `[0.5, 2.0]` | Multiplies `MaxEnergy` | `cStore·storageCap·size`, `cStore=0.01` |
| Metabolism | `lifespan` | `[500, 5000]` | Death by `OLD_AGE` at `age ≥ lifespan` | `cLife·lifespan/1000`, `cLife=0.01` (somatic maintenance) |
| Repro | `eggThreshold` | `[30, 200]` | `LayEgg` only fires when `energy ≥ eggThreshold` | none |
| Repro | `eggInvestment` | `[10, 100]` | Energy transferred to egg (child's starting energy). Must be `< eggThreshold`; clamp at lay to `min(eggInvestment, eggThreshold − 5)` | none |

### 4.2 Sensor genes

Each sensor gene exposes a fixed number of brain input slots. `id` is a run-global monotonic integer assigned when the gene is created (bootstrap or mutation) — it doubles as the innovation number for genome alignment (§7). Cap: **12 sensor genes** per genome.

| `kind` | Params used | Slots | Output semantics | Per-tick cost |
|---|---|---|---|---|
| `VisionCreature` | `range [2,40]`, `angle [−π,π]`, `fov [15°,120°]` | 5 | Nearest other creature whose center is within `range` and within `±fov/2` of `heading+angle`: `[colorR, colorG, colorB, 1−dist/range, otherSize/(otherSize+selfSize)]`. All zeros if none. | `cVis·(range/10)·(fov/60°)`, `cVis=0.01` |
| `VisionPlant` | same | 1 | Mean `biomass/bMax` over 12 sample points: 3 rays (`angle−fov/2`, `angle`, `angle+fov/2`) × distances `range·{0.25,0.5,0.75,1.0}`. | same formula, `×0.5` |
| `VisionMeat` | same | 1 | `min(1, Σ meat.energy in cone / 50)`. Includes eggs (as meat, energy = egg energy). | same formula, `×0.5` |
| `Smell` | `channel [0..3]`, `range [1,6]` | 2 | Scent channel value at two whisker points `pos + rot(heading ± 60°)·range`, each `min(1, v/25)`. `[left, right]`. | `0.005` |
| `Contact` | — | 1 | `1` if any other creature within `size + otherSize`, else `0`. | `0.002` |
| `Energy` | — | 1 | `energy/MaxEnergy` | `0.001` |
| `Age` | — | 1 | `age/lifespan` | `0.001` |
| `Health` | — | 1 | `health/MaxHealth` | `0.001` |

Disabled sensor genes cost nothing and output nothing; their input nodes are still present in the brain (outputs held at 0) so links survive dormancy.

### 4.3 Actuator genes

Each actuator gene binds one brain output node `o ∈ [−1,1]` (tanh). Cap: **10 actuator genes**. Multiple genes of the same kind: outputs are summed then clamped, and the summed value drives the action once. `id` assigned like sensors.

| `kind` | Params | Effect of output `o` | Cost |
|---|---|---|---|
| `Thrust` | `strength [0.5,2]` | `v = clamp(o,0,1)·speed·strength`, capped at `speed·2`; `pos += (cos h, sin h)·v` | `cMove·v·size`, `cMove=0.05` |
| `Turn` | `strength` | `heading += o·maxTurn·strength`, `maxTurn=0.3` rad | `cTurn·|o|`, `cTurn=0.005` |
| `Eat` | — | If `o>0.5`: pick the source with higher expected gain among plant cell under creature (`min(eatRate, b)·energyPerBiomass·plantEff`) and nearest meat/egg within `size+1` (`min(eatRate, m.energy)·meatEff`). Consume from that one source. `eatRate=2`, `energyPerBiomass=1`. Eating an egg destroys it (event `EGG_EATEN`). | `0.01` when active |
| `Bite` | `strength` | If `o>0.5`: nearest other creature within reach `size·1.5` and within `±45°` of heading takes `dmg = 10·size·strength·(1−0.8·targetArmor)`. Sets target `LastDamagedBy`. | `0.5` per attempt |
| `LayEgg` | — | If `o>0.5` and `age ≥ maturityTicks` and `energy ≥ eggThreshold` and `population < popCap`: lay egg (§6). | `eggInvestment + cEggOverhead`, `cEggOverhead=5` |
| `Emit` | `channel [0..3]`, `strength` | If `o>0`: deposit `10·strength·o` into scent channel at creature's cell. | `cEmit·strength·o`, `cEmit=0.01` |

Passive cost: `0.002` per enabled actuator gene per tick.

A creature with no `Thrust` cannot move; with no `Eat` it starves; with no `LayEgg` its lineage ends. No protection — selection handles it.

### 4.4 Brain genes

NEAT-style. Own implementation (SharpNEAT's fixed-I/O genome model fights principle 4; the needed subset is ~500 lines).

**Nodes** — `{ id, kind ∈ {Input, Output, Hidden, Bias}, bindGeneId, bindSlot }`
- `Input`: one per sensor slot. `id = SENSOR_BASE + sensorGeneId·8 + slot` (`SENSOR_BASE = 1_000_000`). Deterministic from the sensor gene, so alignment across genomes is by id.
- `Output`: one per actuator gene. `id = ACTUATOR_BASE + actuatorGeneId` (`ACTUATOR_BASE = 2_000_000`).
- `Bias`: exactly one, `id = 0`, activation always `1`.
- `Hidden`: `id` from the run-global node-innovation counter (starts at 1).
- Cap: **64 hidden nodes**.

**Links** — `{ innovation, from, to, weight ∈ [−8,8], enabled }`
- `from ∈ {Input, Bias, Hidden, Output}`, `to ∈ {Hidden, Output}`. Recurrent and self links allowed.
- `innovation` from run-global `Dictionary<(from,to), int>` — same `(from,to)` pair always gets the same number within a run. Node-split innovations keyed by the split link's innovation.
- Cap: **512 links**.

**Evaluation** — synchronous one-step recurrent update, once per tick:
```
inputs[i] ← sensor values (0 for disabled sensors); bias ← 1
for each non-input node n (any order — reads only prev):
    s = Σ_{enabled links l with l.to == n} weight · prev[l.from]
    next[n] = tanh(s)
swap(prev, next); actuator outputs ← next[outputNode]
```
Deep paths incur a few ticks of latency; accepted for determinism and simplicity. Decode to flat arrays at hatch: `float[] act, prevAct; int[] linkFrom, linkTo; float[] linkW` (enabled links only).

**Cost**: `cNode·hiddenCount + cLink·enabledLinks`, `cNode=0.002`, `cLink=0.0005`.

### 4.5 Bootstrap genome

```
Meta:       mutationRate 0.03, structuralRate 0.02
Body:       size 1.0, speed 1.0, armor 0.0, color random per individual
Metabolism: diet 0.05, storageCap 1.0, lifespan 2000
Sensors:    VisionPlant(range 12, angle 0, fov 90°), VisionCreature(range 10, angle 0, fov 60°), Energy
Actuators:  Thrust(1.0), Turn(1.0), Eat, LayEgg
Brain:      every input and bias linked to every output, weight ~ N(0, 0.5)
Repro:      eggThreshold 80, eggInvestment 40
```
Bootstrap population: `bootstrapCount=600` at uniform random positions and headings. Per individual: fresh random weights, random color, each scalar gene perturbed by `N(0, 2%·range)`.

**Energy sanity check for this genome** (size 1): passive ≈ basal 0.03 + lifespan 0.02 + store 0.01 + vision 0.012 + 0.005 + energy 0.001 + actuators 0.008 + brain (32 links) 0.016 ≈ **0.10/tick**; moving full-time adds 0.05. Eating a plant cell yields up to `2·0.93 ≈ 1.9/tick`, so ~8% of ticks on food breaks even. World plant regrowth at half capacity ≈ 16384 cells × 0.025 ≈ 400 e/tick, supporting roughly 2,000–2,700 creatures before egg costs. Target steady-state population: **500–3000**. If outside this band after M3, tune §14 before proceeding.

---

## 5. Energy, health, death

Per creature per tick, in step 4 of the tick order:

```
energy -= passiveCost(genome) + activeCosts(this tick)
if energy > 0.2·MaxEnergy: health = min(MaxHealth, health + 0.05)
age += 1
```

Death checks, in this order; first match is the cause:

| Cause | Condition |
|---|---|
| `STARVATION` | `energy ≤ 0` |
| `PREDATION` | `health ≤ 0` (killer = `LastDamagedBy`) |
| `OLD_AGE` | `age ≥ lifespan` |

On death: emit `creature_death` row, spawn `Meat(x, y, corpseEnergy·size)`, remove from world. Energy is not conserved across death by design (corpse energy is fixed by mass, not remaining energy).

**Population cap.** `popCap=6000`. When `creatures + eggs ≥ popCap`, `LayEgg` is a no-op and `world_stats.cap_hits` increments. Hitting the cap is a tuning failure, not a feature.

**Extinction.** If `creatures == 0 && eggs == 0`: if `reseedOnExtinction`, spawn the bootstrap population and emit event `RESEED`; else end the run with status `EXTINCT`.

---

## 6. Reproduction and mutation

Asexual. When `LayEgg` fires:

1. `child = Mutate(parent.Genome, rng)` (below).
2. `genomeId = parent.GenomeId` if `child` hashes identical to parent's genome, else a new id; emit `genome` row on new id.
3. Deduct `eggInvestment + cEggOverhead` from parent. `parent.OffspringCount++`.
4. Egg at parent position, `Energy = eggInvestment`, `HatchTick = tick + incubationTicks` (`incubationTicks=50`, config), inherits `SpeciesId`, `Generation = parent.Generation + 1`. Emit event `EGG_LAID`.
5. At hatch: new creature with `Energy = egg.Energy`, `Health = MaxHealth`, heading random. Emit `creature` row and event `HATCH`.

### Mutation operators

Applied to a deep copy, using the **parent's** `Meta` genes (times config multipliers `mutationScale`, `structuralScale`, default 1.0). RNG draws in the fixed order listed.

**Scalar perturbation** — for every scalar field in Body, Metabolism, Repro, and every sensor/actuator param: with `P = mutationRate`, `value += N(0, 0.05·(max−min))`, clamp. Color fields use `σ = 0.05`.

**Weights** — for every link: with `P = 2·mutationRate`: 90% `weight += N(0, 0.5)`, 10% `weight = N(0, 1)`; clamp `[−8,8]`.

**Meta** — with fixed `P = 0.05` each: `mutationRate *= exp(N(0, 0.2))`, same for `structuralRate`; clamp to ranges. (Floors prevent lineages from switching mutation off.)

**Structural** — each drawn independently with `P = structuralRate`; a mutation that would exceed a cap is a no-op:

| Op | Action |
|---|---|
| `AddLink` | Random `from ∈ {Input,Bias,Hidden,Output}`, `to ∈ {Hidden,Output}`, pair not already present. `weight = N(0,1)`, enabled. Up to 10 attempts to find a free pair. |
| `AddNode` | Random enabled link `L`: disable it; new hidden node `H`; add `from→H (w=1)`, `H→to (w=L.weight)`. |
| `ToggleLink` | Flip `enabled` on a random link. |
| `AddSensor` | Random kind, random params in range, new id. Add its input nodes; add one link from a random new input slot to a random output, `w = N(0, 0.5)`. |
| `RemoveSensor` | If `count > 1`: remove random sensor gene, its input nodes, and links touching them. |
| `DuplicateSensor` | Copy a random sensor gene with new id, params perturbed `N(0, 0.1·range)`. Input nodes added; links from the original's inputs are **copied** to the duplicate's corresponding slots (so the copy starts functional and can diverge). |
| `ToggleSensor` | Flip `enabled` on a random sensor gene. |
| `AddActuator` | Random kind, random params, new id, output node, one link from random input/bias, `w = N(0, 0.5)`. |
| `RemoveActuator` | If `count > 1`: remove random actuator gene, its output node, links touching it. |
| `ToggleActuator` | Flip `enabled` on a random actuator gene. |

Every mutated genome must pass `Genome.Validate()` (ranges, caps, dangling link refs, exactly one bias, node/gene binding consistency). Validation failure is a bug, not a sim event — throw.

---

## 7. Speciation, lineage, diversity

### Genome distance

```
d = c1·(E + D)/N  +  c2·W̄  +  c3·B  +  c4·(1 − J)
```
- `E, D`: excess and disjoint links by innovation number; `N = max(linkCount)`, min 1. `W̄`: mean |Δweight| over matching links.
- `B`: Euclidean distance over all scalar genes in Body/Metabolism/Repro, each normalized to `[0,1]` by its range (color included).
- `J`: Jaccard similarity over the multiset of `(kind, channel)` for enabled sensors + actuators.
- `c1=1.0, c2=0.4, c3=2.0, c4=1.0`. Threshold `δ=3.0`. Tune `δ` so the bootstrap population forms 1–3 species.

### Speciation pass (every `speciateEvery=100` ticks)

NEAT-style with persistent species ids:
1. Species ordered by id; each has a `representative` genome.
2. For each living creature in id order: assign to the first species with `d(genome, rep) < δ`; else create a new species (`founderGenomeId`, `foundedTick`, `parentSpeciesId` = creature's previous species) with this genome as representative.
3. After assignment: each species with members picks a new representative uniformly at random from members (RNG, deterministic). Species with zero members keep their last representative for `speciesRetainTicks=2000` then are dropped from matching.
4. Newborns inherit the parent's species id between passes.

### Diversity metrics (every `statsEvery=100` ticks)

- `species_count`: species with `population ≥ 1`; `species_count_min5`: with `≥ 5`.
- `shannon`: `−Σ pᵢ ln pᵢ` over species population fractions.
- `mean_pairwise_distance`: mean `d` over all pairs in a uniform random sample of 200 living creatures (19,900 pairs).

These are the scoreboard any future overseer optimizes (§15).

---

## 8. Observability — Postgres

Schema in `db/schema.sql`. Postgres ≥ 15. Every table is keyed by `run_id`; ids within a run are run-local.

### Writer (`Sim.Persistence`)

- Sim thread pushes plain record structs into a `System.Threading.Channels.Channel<T>` (bounded, `100_000`). One background task drains it, buckets by table, and flushes with **Npgsql binary COPY** when any bucket reaches `5_000` rows or `2 s` has elapsed, one transaction per flush. The sim never waits on the DB except: at shutdown (drain + flush), and if the channel is full (log a warning with a counter; this means the DB is the bottleneck).
- `run` row inserted at start (config JSON, seed, git SHA). Single `UPDATE run SET ended_at, last_tick, status` at end — the only update in the system.
- `--no-db` runs the sim with a null writer (for speed tests and tuning).

### Tables (summary — see `schema.sql` for exact DDL)

| Table | Written when | Volume |
|---|---|---|
| `run` | start / end | 1 |
| `genome` | new genome id (lay time) | ~#mutated eggs |
| `species` | new species | small |
| `creature` | hatch | #hatches |
| `creature_death` | death | #deaths |
| `event` | `EGG_LAID`, `HATCH`, `EGG_EATEN`, `BITE`, `RESEED` | high; `BITE` can be throttled via `logBites` |
| `world_stats` | every `statsEvery` ticks | ticks/100 |
| `species_stats` | every `statsEvery` ticks × living species | ticks/100 × species |
| `position_sample` | every `positionsEvery=100` ticks, all living creatures with `id % positionModulo == 0` (`positionModulo=1`) | **the big one** |

**Volume math** (1M-tick run, 2,000 creatures): `position_sample` at every 100 ticks = 20M rows ≈ 1 GB. At every 25 ticks, 4 GB. Start at 100. For fine replay later, run live (§15) or use `positionModulo`.

**Extracted columns.** `genome.data` is the canonical JSONB, but frequently-queried scalars (`size`, `speed`, `diet`, `n_sensors`, `n_links`, …) are also stored as real columns so analysis doesn't need JSONB path queries.

### Views and functions (in `schema.sql`)

- `v_population` — population, eggs, births, deaths by cause per stats tick.
- `v_trait_trends` — population-mean traits per stats tick (selection visible as drift in these).
- `v_species_timeline` — per species: founded, last seen, peak population, lineage parent.
- `v_dominant_species` — top 5 species by population per stats tick.
- `v_sensor_prevalence` — fraction of population carrying each sensor kind, per stats tick.
- `v_death_causes` — deaths by cause per 10k-tick bucket.
- `f_ancestry(run_id, creature_id)` — recursive walk up `parent_creature_id`; returns the lineage with genome ids.
- `f_genome(run_id, creature_id)` — the creature's genome JSONB.

### What "evolution is happening" looks like in SQL

- `v_trait_trends.mean_speed` and `mean_size` move monotonically early, then plateau or oscillate.
- `species_stats` shows `mean_diet` bimodal across species (plant vs meat lineages) — check with `SELECT tick, species_id, mean_diet, population FROM species_stats WHERE population > 20`.
- `v_sensor_prevalence` shows `Smell` on channel *k* rising in one species while `Emit` on channel *k* is present in another (or the same) — communication or eavesdropping.
- `v_death_causes` shifts from `STARVATION`-dominated to `PREDATION`-present.

---

## 9. Configuration

`config/default.json` — every tunable, with these defaults. The CLI loads it, applies `--set key=value` overrides, and stores the resolved JSON in `run.config`.

```jsonc
{
  "world":   { "width": 512, "toroidal": false, "plantGrid": 128, "bMax": 10, "plantRate": 0.01, "plantSeed": 0.002,
               "capacityMin": 0.2, "noiseSeedOffset": 0, "scentGrid": 64, "scentStep": 4, "scentDecay": 0.97, "scentDiffuse": 0.2,
               "hashCell": 8, "corpseEnergy": 30, "meatDecay": 0.995 },
  "energy":  { "cBasal": 0.03, "cArmor": 0.03, "cStore": 0.01, "cLife": 0.01, "cVis": 0.01, "cMove": 0.05, "cTurn": 0.005,
               "cEmit": 0.01, "cEggOverhead": 5, "cNode": 0.002, "cLink": 0.0005, "actuatorPassive": 0.002,
               "eatRate": 2, "energyPerBiomass": 1, "dietExp": 1.5, "maxTurn": 0.3, "healthRegen": 0.05 },
  "life":    { "maturityTicks": 150, "incubationTicks": 50, "popCap": 6000, "reseedOnExtinction": false, "bootstrapCount": 600 },
  "mutation":{ "mutationScale": 1.0, "structuralScale": 1.0, "maxSensors": 12, "maxActuators": 10, "maxHidden": 64, "maxLinks": 512 },
  "species": { "c1": 1.0, "c2": 0.4, "c3": 2.0, "c4": 1.0, "delta": 3.0, "speciateEvery": 100, "retainTicks": 2000, "sampleSize": 200 },
  "logging": { "statsEvery": 100, "positionsEvery": 100, "positionModulo": 1, "logBites": true }
}
```

---

## 10. CLI

```
sim run  --config config/default.json --seed 42 --ticks 1000000 --db "Host=localhost;Database=alife;Username=..;Password=.."
         [--no-db] [--set logging.positionsEvery=25] [--notes "text"] [--checkpoint-every 100000]
sim resume --checkpoint runs/<run_id>/tick_0400000.bin --ticks 600000 --db "..."
sim bench --config ... --ticks 20000            # no-db, prints ticks/sec, population, GC counts
```

- Progress line to stderr every 1,000 ticks: `tick pop eggs species tps`.
- Checkpoint = binary serialization of full `World` state + RNG state + innovation tables. `resume` must reproduce the identical trajectory as an uninterrupted run (test in §12).
- Exit codes: 0 completed, 2 extinct, 1 error.

---

## 11. Determinism and performance

- **Determinism**: single-threaded `Sim.Core`; fixed tick order (§2); all entity iteration in ascending id order; one RNG stream consumed in documented order; no `Dictionary` iteration affecting state; `float` throughout (bitwise reproducibility is only guaranteed on the same platform/binary — accepted).
- **Targets** (laptop core, `--no-db`): 2,000 creatures at **≥ 1,000 ticks/s**; zero GC allocations per tick in steady state (verify with `bench`).
- **Hot spots**, in expected order: vision sensors (bound by spatial-hash query + 12 plant samples each), brain eval (`O(links)`), spatial-hash rebuild. Cap sensor `range` at 40 and per-genome sensors at 12 for this reason.
- Persistence must cost `< 10%` of tick rate at default logging.

---

## 12. Tests (`Sim.Core.Tests`, xUnit)

1. **Determinism**: two `World`s with same seed/config, 20k ticks → identical state hash (positions, energies, RNG state, innovation counters).
2. **Checkpoint fidelity**: run 20k ticks; checkpoint at 10k; resume to 20k → identical hash to (1).
3. **Mutation validity**: 100k random mutations from bootstrap genome, chained → every genome passes `Validate()`, every genome decodes to a runnable brain, caps never exceeded.
4. **Brain eval**: hand-built 3-node networks produce expected outputs; recurrent self-link accumulates as expected across ticks.
5. **Energy accounting**: with reproduction and death disabled, `Σ creature energy + Σ plant biomass·energyPerBiomass + Σ meat` changes each tick by exactly `(plant regrowth) − (Σ costs)`, within float tolerance.
6. **Sensor geometry**: `VisionCreature` sees a creature placed at known bearing/distance and not one outside the cone; `Smell` left/right respond to a gradient.
7. **Speciation**: identical genomes → one species; a genome with 20 extra links → distance `> δ`.
8. **Selection smoke test** (slow, `[Trait("Category","Slow")]`): bootstrap population, 200k ticks → `mean_speed` or `mean VisionPlant range` at tick 200k differs from tick 0 by more than 3σ of the bootstrap perturbation.

---

## 13. Milestones

Build strictly in order. Do not start M(n+1) until M(n) acceptance passes.

**M1 — World and metabolism (no brain).** World, plant layer, meat, spatial hash, `Creature` with a fixed hardcoded genome and a random-walk controller (random thrust/turn, eat when on food), energy costs, death causes, `bench` command.
*Accept*: 100k ticks at 600 creatures, `≥ 2,000 ticks/s`, no per-tick allocations; test 5 passes; population declines to zero by starvation/old age (no reproduction yet) — deaths logged to stderr with causes.

**M2 — Genome, brain, sensors, actuators.** `Genome`, `Validate`, canonical JSON + hash, brain decode/eval, all sensor and actuator kinds, bootstrap genome.
*Accept*: tests 3, 4, 6 pass. A hand-written genome (VisionPlant → Thrust/Turn wiring that steers toward food) outlives the random-walk controller by `> 2×` mean lifespan over 10 seeds.

**M3 — Reproduction and mutation.** Eggs, hatch, all mutation operators, `Meta` evolution, lineage fields, extinction/reseed.
*Accept*: from bootstrap, population stays in `[500, 3000]` for 500k ticks over 3 seeds with no intervention. Test 8 passes. If not: tune per §14, record what changed in `config/default.json` commit message.

**M4 — Persistence.** `schema.sql`, writer, all tables, `run`/`resume`/checkpoint.
*Accept*: 1M-tick run at default logging writes without channel-full warnings and with `< 10%` tick-rate loss vs `--no-db`. Every view returns rows. Test 2 passes.

**M5 — Speciation and stats.** Distance metric, speciation pass, `species`/`species_stats`/`world_stats` diversity columns.
*Accept*: test 7 passes; bootstrap forms 1–3 species; `species_count_min5 > 3` by 500k ticks on at least 1 of 3 seeds (if not, this is a finding — record it, don't force it).

**M6 — Validation run.** 3 seeds × 2M ticks, default config. Produce `docs/run-notes.md`: population curve, trait trends, species timeline, sensor prevalence, death causes. Explicitly answer: did `diet` bimodalize? did any `Smell`/`Emit` channel pairing appear? did `PREDATION` become a meaningful death cause? Negative results are results.

---

## 14. Known failure modes and the knob for each

| Symptom | Likely cause | First knob |
|---|---|---|
| Extinction < 50k ticks | upkeep too high or food too sparse | `plantRate` ↑, `cBasal` ↓, `bootstrapCount` ↑ |
| Population pinned at `popCap` | food too abundant, costs too low | `plantRate` ↓, `cMove` ↑ |
| Everything converges to one species, distance metric shows collapse | single niche | lower `capacityMin` (more barren regions), raise `dietExp`; consider seasonal `plantRate` modulation (deferred) |
| Genome bloat (links/sensors at caps, no fitness gain) | structural costs too low | `cLink`, `cVis` ↑ |
| No structural innovation ever fixes | structural costs too high, or `structuralRate` drifting to floor | `cLink` ↓; check `mean_structural_rate` in `species_stats` |
| `mutationRate` drifts to floor in all lineages | normal (mutation is mostly deleterious). Not a bug. Floor exists for this reason. | — |
| No predation ever | bite cost too high vs meat payoff, or `Bite` never added | `corpseEnergy` ↑, bite cost ↓ |
| Boom–bust oscillation | classic predator–prey / overgrazing dynamics. Often fine. | if it causes extinction: `plantSeed` ↑ (faster recovery from zero) |
| tps far below target | vision sampling | profile first; then `maxSensors` ↓, hash cell size tuning |

---

## 15. Deferred (with the hook already in place)

- **Crossover / mate choice.** Distance metric (§7) is the mate-compatibility function. Add `MateColorPref` gene and a `Mate` actuator; align genomes by innovation/gene id like NEAT.
- **Terrain hazards** (water, cold). Second grid layer with per-cell damage; a `VisionTerrain` sensor kind.
- **Seasons.** Multiply `plantRate` by a slow sinusoid; non-stationary environment is a known driver of diversity.
- **Overseer AIs.** API surface is `SpawnGenome(genome, count, x, y)` + read access to `world_stats`/`species_stats`. The diversity metrics in §7 are their objective. Everything else about them is a separate design.
- **Live viewer.** `Sim.Cli --stream tcp://:9000` emitting per-tick creature snapshots (id, x, y, heading, species, size, color) as a binary frame; Unity/VR client renders. Replay viewer reads `position_sample` + `genome` directly.
- **Program-style (VM) brains.** Not planned. The `Brain` section is the only thing that would change.
