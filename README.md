# ALIFE

An artificial-life evolution simulation. Creatures with variable-length genomes — bodies, senses, and neural brains — live in a resource-limited world, reproduce by egg with mutation, and are subject to nothing but the world's rules. The headless simulation runs fast and writes everything to Postgres. A Unity/VR viewer lets you load a snapshot, walk into the world at conversational speed, and be perceived by the things living in it.

There is no fitness function, no score, no goal state. The point is to find out what happens.

---

## Contents

| File | What it is |
|---|---|
| `DESIGN.md` | The headless 2D simulation. Genome, world rules, energy, mutation, speciation, persistence, milestones. The authoritative spec. |
| `TRANSITION-3D.md` | The Unity/VR viewer: sim↔viewer contract, terrain, instanced-parts rendering, gaits, Blender art spec, in-process hosting, forks, presence. Appendix A is a patch against `DESIGN.md`. Not yet in this repo. |
| `db/schema.sql` | Tables, indexes, analysis views, lineage functions. Implemented. |
| `db/002_forks_presence.sql` | Run tree (forks), player actions, cross-run ancestry, run comparison. Not yet in this repo — see Unity roadmap. |
| `db/003_admin.sql` | Reset and deletion utilities. Not yet in this repo. |
| `config/default.json` | Every tunable constant. |

---

## Background: the 2014 version

The first attempt at this was a self-replicating C# program. It drew "energy" as tokens from an API every few minutes, and when it had enough, it copied itself into a new folder and triggered a compile. The copy mechanism was the interesting part: it read its own source file, converted it to a string of hexadecimal values against a token dictionary of C# syntax fragments, then wrote the values back out one at a time with a small chance that any value would increment or decrement. Reinterpreting the mutated hex produced different source, and in principle the mutation space covered any valid C# program.

It ran. It produced odd behavior, and it locked the machine up more than once. It was abandoned for three reasons, and each one is a design constraint on this project:

| 2014 problem | What it actually was | How this project answers it |
|---|---|---|
| Random edits usually broke the build | Mutation over a grammar where almost every change is a syntax error — a fitness landscape made of cliffs | **Typed genes, never bytes.** A genome is a list of structured genes; mutation operates on fields, so every mutation is decodable by construction. (Tierra and Avida solved this in the early 90s the same way.) |
| No way to steer evolution | No selection pressure beyond "compiles and doesn't crash" | **A world with rules.** Finite plant biomass, energy costs on every trait, predation, spatially varying fertility. Fitness is not defined; it is whatever survives. |
| No insight without killing the system and reading source | The simulation and the analysis were the same process | **Everything goes to Postgres.** Lineage, genomes, deaths by cause, per-species trait means, positions. Plus a VR viewer to watch it happen. |
| System lockups | Unsandboxed random code doing what unsandboxed random code does | The sim executes no generated code. Genomes are data interpreted by a fixed evaluator. |

Worth being clear about what is and isn't new here: the evolutionary machinery existed in 1994 (Tierra, Polyworld, Karl Sims, later Avida and Framsticks; Bibites more recently). What's changed is cheap compute for long runs, a database and query layer for observability, VR for presence, and — later — LLMs as overseers. The hard problems are the same ones they were: representation, fitness landscape, and avoiding collapse to a single genotype.

**Prior art worth reading:** Tierra, Avida, Polyworld, Karl Sims' evolved virtual creatures, Framsticks, The Bibites, NEAT (Stanley & Miikkulainen), novelty search (Lehman & Stanley), MAP-Elites, POET (Wang, Lehman, Clune & Stanley), *Endless Forms* (Clune & Lipson).

---

## What the simulation is

**World.** Continuous 2D, 512 units square, bounded. Plant biomass on a 128² grid regrows logistically toward a capacity map derived from noise — fertile valleys and barren ridges, so there is more than one niche. Corpses become meat that decays. Four scent channels diffuse and decay. No terrain hazards in v1.

**Creatures.** Each has a genome of typed genes:

```
Meta         mutationRate, structuralRate          — evolvable evolvability
Body         size, speed, armor, color(r,g,b)
Metabolism   diet (plant↔meat), storageCap, lifespan
Sensors[]    variable length: vision cones, smell, contact, internal state
Actuators[]  variable length: thrust, turn, eat, bite, lay egg, emit scent
Brain        NEAT-style nodes and links; recurrent allowed
Repro        eggThreshold, eggInvestment
```

(`Morph` — neutral body-plan markers for rendering, the seed for procedural 3D body generation in the viewer — is planned but not yet added; see Track 1, A.1 below.)

**The five principles everything follows:**

1. Typed genes, never bytes — every mutation is valid by construction.
2. Variable length — sensors, actuators, and brain topology can grow.
3. Every trait costs energy — complexity has to earn its keep.
4. Sensors and actuators are genes, and brain I/O grows with them — a lineage can evolve a new sense and the brain grows inputs to use it. This is the main emergence enabler.
5. World channels carry no designed meaning — color is three numbers others can see; scent is four channels anyone can emit into or smell. Nothing defines "red = danger." Signaling, mimicry, and warning coloration either emerge or they don't.

**Reproduction** is asexual, by egg, with mutation applied at lay time: scalar perturbation, weight perturbation, and structural operators (add/remove link, add node, add/remove/duplicate/toggle a sensor or actuator). Gene duplication is in there deliberately — duplication then divergence is the primary source of novelty in biology, and it works in silico for the same reason.

**Determinism** is a hard requirement. Same seed, config, and binary produce the same run, tick for tick. That is what makes checkpointing, replay, and controlled comparison possible.

---

## Architecture

```
        headless, ~2000 tick/s                    in Unity, ~2 tick/s
   ┌──────────────────────────────┐         ┌──────────────────────────────┐
   │ Sim.Cli run                  │         │ Unity viewer (PC VR / Quest) │
   │   Sim.Core  (pure C#, no deps)│  fork  │   Sim.Core.dll hosted        │
   │   Sim.Persistence → Postgres │ ──────▶ │   in-process (SimHost)       │
   │   checkpoints → runs/<id>/   │         │   FileSink → persist_*.bin   │
   └──────────────────────────────┘         └──────────────────────────────┘
                  ▲                                        │
                  └────── sim run --resume ────────────────┘
                          sim ingest
```

`Sim.Core` has zero dependencies and is pure, deterministic C#. That's what lets Unity host the same simulation in-process rather than reimplementing it — the viewer is a renderer, never a second simulator.

```
alife/
├── DESIGN.md  TRANSITION-3D.md  README.md
├── db/          schema.sql, 002_forks_presence.sql, 003_admin.sql
├── config/      default.json
├── src/
│   ├── Sim.Core/         world, genome, brain, mutation, speciation, checkpoints.
│   │                     No dependencies. (Track 1 below will move persistence
│   │                     record types and frame structs in here too.)
│   ├── Sim.Persistence/  Npgsql binary COPY writer
│   └── Sim.Cli/          run, resume, bench, migrate, query
│                         (serve, fork, ingest, db come with the viewer/forks work)
├── tests/       Sim.Core.Tests (xUnit)
├── tools/       build-core-dll.sh, uv_from_axis.py            [not yet in this repo]
├── art/         creatures.blend                                [not yet in this repo]
├── runs/        <run_id>/ checkpoint files (tick_<n>.bin) today;
│                persist_*.bin/input_*.log/run.json come with the viewer work
└── unity/       the viewer project                              [not yet in this repo]
```

---

## Quickstart

```bash
# database — createdb, then apply the schema (either works)
createdb alife
psql -d alife -f db/schema.sql
# or: dotnet run --project src/Sim.Cli -- migrate --db "Host=...;Database=alife;Username=...;Password=..."

# db/002_forks_presence.sql and db/003_admin.sql (fork tree, admin utilities) come with the
# forks/presence work — not yet in this repo.

# build and sanity-check
dotnet build -c Release
dotnet test
dotnet run --project src/Sim.Cli -- bench --config config/default.json --ticks 20000

# a real run
dotnet run -c Release --project src/Sim.Cli -- run \
  --config config/default.json --seed 42 --ticks 2000000 \
  --checkpoint-every 20000 \
  --db "Host=localhost;Database=alife;Username=alife;Password=..."

# resuming from a checkpoint is a separate command today:
dotnet run -c Release --project src/Sim.Cli -- resume \
  --checkpoint runs/<run_id>/tick_1000000.bin --ticks 1000000 \
  --db "Host=localhost;Database=alife;Username=alife;Password=..."
```

Then watch it:

```sql
SELECT tick, population, species_count_min5, mean_speed, mean_diet, mean_sensors
FROM v_trait_trends JOIN v_population USING (run_id, tick)
WHERE run_id = '<uuid>' ORDER BY tick;
```

---

## CLI

Implemented today:

```
sim run     --config F --seed N --ticks N --db S [--no-db] [--set k=v] [--checkpoint-every N] [--notes S]
sim resume  --checkpoint runs/<id>/tick_<n>.bin --ticks N --db S [--no-db] [--checkpoint-every N]
sim bench   --config F --ticks N                      # no DB; ticks/sec, population, GC
sim migrate --db S [--schema path]                     # applies db/schema.sql
sim query   --db S --sql "..."                         # ad-hoc query, tab-separated output
```

Exit codes (`sim run`/`sim resume`): `0` completed, `1` error, `2` extinct.

Planned, come with the viewer/forks work (not runnable yet):

```
sim serve   --live   --config F --seed N --db S [--rate 2] [--port 9077]
sim serve   --replay <run_id> [--rate 2] [--port 9077]
sim fork    <parent_run_id> --tick T
sim ingest  runs/<run_id> --db S
sim db      reset [--yes] | drop-run <id> [--include-forks] [--files] | apply
```

---

## Working with the data

```sql
-- did diet split into herbivore and carnivore lineages?
SELECT tick, species_id, population, mean_diet FROM species_stats
WHERE run_id = :run AND population > 20 ORDER BY tick, species_id;

-- did anyone evolve to smell a channel someone else emits into?
SELECT * FROM v_sensor_prevalence WHERE run_id = :run AND kind LIKE 'Smell%';
SELECT * FROM v_actuator_prevalence WHERE run_id = :run AND kind LIKE 'Emit%';

-- lineage of a particular creature
SELECT * FROM f_ancestry(:run, :creature_id);
SELECT f_genome(:run, :creature_id);
SELECT * FROM f_genome_lineage(:run, :genome_id);
```

The following need `db/002_forks_presence.sql` / `db/003_admin.sql` (not yet in this repo — see Contents):

```sql
-- what a session changed, against its control fork
SELECT * FROM f_compare_runs(:session_run, :control_run);

-- housekeeping
SELECT * FROM v_run_sizes;
CALL p_delete_run('<uuid>');        -- run plus its fork subtree
CALL p_reset_all();                 -- everything; then clear runs/*/ingested.json
```

**Signs the thing is working:** trait means drift and then plateau rather than staying flat; `mean_diet` goes bimodal across species; `v_death_causes` shifts from starvation-dominated to predation-present; `v_sensor_prevalence` shows a sensor kind rising in one lineage. **Negative results are results** — record them in `docs/run-notes.md` rather than tuning until you see what you wanted.

---

## Roadmap

**Sim** (`DESIGN.md` §13). M1 world and metabolism → M2 genome, brain, sensors → M3 reproduction and mutation → M4 persistence → M5 speciation and stats → M6 validation run (3 seeds × 2M ticks, written up).

Acceptance criteria are per-milestone and are not negotiable — M3's in particular ("population holds in 500–3000 for 500k ticks across 3 seeds with no intervention") is where the energy constants get their real test. `DESIGN.md` §14 maps every failure symptom to the knob that addresses it.

**Viewer** (`TRANSITION-3D.md` §11). V0a/V0b sim deltas → V1 protocol + capsules → V1b in-process hosting → V2 terrain → V3 instanced parts, static pose → V4a/V4b gaits → V5 VR → V5b presence → V6 overlays and audio → V7 Blender assets → V8 Quest port.

---

## Unity: next steps

Three tracks run in parallel. Only the first is blocking.

### Track 1 — Sim deltas (do this now, in the sim repo)

`TRANSITION-3D.md` **Appendix A.6 and A.7 constrain code being written today.** Retrofitting them later is a week of grinding; adding them now is an afternoon:

- `Sim.Core` multi-targets `net8.0;netstandard2.1`, `LangVersion 9.0` — no `record struct`, no file-scoped namespaces, no `required`, no global usings.
- No `System.Threading.Channels`, no `System.Text.Json`, no reflection anywhere in Core. Canonical genome JSON is hand-rolled.
- A `FastMath` layer (`Tanh`, `Sin`, `Cos`, `Exp`, `Sqrt`) that every call site in Core goes through, replacing `MathF`. CoreCLR, Mono, and IL2CPP route `MathF` to different libm implementations; this removes the largest source of cross-runtime divergence.
- Explicit `BinaryWriter` checkpoint format. No `BinaryFormatter`, no reflection-based serializer.
- Persistence record types, frame structs, and checkpoint code move into `Sim.Core`; `Sim.Persistence` becomes Npgsql-only.

Then A.1 (`Morph` genes — neutral body-plan markers, rendering-only, no effect on sim behavior: the seed for procedural 3D body generation in the viewer, e.g. driving instanced-part selection/proportions rather than every creature rendering identically), A.8 (fork tree), A.9 (observer entity), A.10 (input log).

**Also fix the inconsistency between the docs:** `DESIGN.md` §10 says `sim resume --checkpoint`, `TRANSITION-3D.md` A.8 says `sim run --resume`. `sim resume --checkpoint` is what's implemented — a separate command that derives the checkpoint's `run_id` from its path and continues writing into that same run row, so the `run_id` already carries through despite the separate verb. `TRANSITION-3D.md` A.8 should be updated to match rather than the other way around.

### Track 2 — Blender (start now, independent of everything)

`TRANSITION-3D.md` §8 is a complete spec sheet: 19 primitives with pivots, axes, unit dimensions, triangle budgets for PC/Quest/LOD1, and the `param` UV map carrying `t ∈ [0,1]` along each part's primary axis (the shader depends on it for taper, belly, and undulation).

Model the unit creature only — everything is scaled per instance. Nine parts unblock the most: `cr_torso`, `cr_head`, `cr_limb_upper`, `cr_limb_lower`, `cr_foot`, `cr_eye`, `cr_antenna`, `cr_tail`, `cr_neck`. Placeholders (§8.3) mean code never waits on art, and V7 is a name-matched swap requiring no code changes.

### Track 3 — Unity project (after V0a)

Create the project with the §4 package list and assembly layout, get a desktop-mode camera working before enabling any XR package, and build V1 in `ProtocolTest.unity`: 2,000 capsules driven by real frames, correct interpolation, rate control. Then V1b drops `Sim.Core.dll` into `Assets/Plugins/Sim/` and the same test runs in-process from a checkpoint.

### How the two halves fit together

Evolve headless overnight at ~2000 tick/s. Pick a checkpoint. Fork it, and enter that moment in VR at 2 tick/s — an hour in the headset is roughly 7,000 ticks, three or four lifespans; the same hour headless is millions. Time moves fast when you're not there.

Inside, you are a sim entity. Creatures with a `VisionCreature` sensor see your color, size, and distance; `Contact` fires when you're close; if you enable a scent channel they can smell you. You can drop food, emit scent, and strike. Nothing about you is special-cased in their brains — every input is one they already had a sensor for, which is what makes an evolved response to you a real result rather than a scripted one.

Every session is a **fork**: new `run_id`, `parent_run_id`, `fork_tick`, ids and tick numbers continuing from the checkpoint. So the honest experiment is two forks from the same checkpoint — one you walk into, one left alone — resumed headless for the same number of ticks and compared with `f_compare_runs`. Whatever your presence did to that world, the untouched branch is still there to measure it against.

---

## Deferred

Crossover and mate choice (the species distance metric is already the compatibility function); seasons and terrain hazards; the two overseer AIs — a creator maximizing diversity against an assimilator minimizing it, both acting only by designing and spawning creatures, with the §7 diversity metrics as their scoreboard; god-view/tabletop scale; merged-mesh creature bodies if instanced-part seams prove distracting. Program-style VM genomes are not planned — the `Brain` section is the only thing that would change, but the 2014 lesson stands.
