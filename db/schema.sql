-- ALIFE-2D observability schema. Postgres >= 15.
-- Idempotent: safe to re-run. All tables keyed by run_id; ids within a run are run-local.
-- Append-only except run.ended_at / run.last_tick / run.status (one UPDATE at run end).

-- ---------------------------------------------------------------------------
-- Types
-- ---------------------------------------------------------------------------
DO $$ BEGIN
  CREATE TYPE death_cause AS ENUM ('STARVATION', 'PREDATION', 'OLD_AGE');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
  CREATE TYPE event_kind AS ENUM ('EGG_LAID', 'HATCH', 'EGG_EATEN', 'BITE', 'RESEED');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
  CREATE TYPE run_status AS ENUM ('RUNNING', 'COMPLETED', 'EXTINCT', 'ERROR');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- ---------------------------------------------------------------------------
-- run
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS run (
  run_id       uuid        PRIMARY KEY,
  started_at   timestamptz NOT NULL DEFAULT now(),
  ended_at     timestamptz,
  status       run_status  NOT NULL DEFAULT 'RUNNING',
  seed         bigint      NOT NULL,
  config       jsonb       NOT NULL,          -- fully resolved config (defaults + overrides)
  git_sha      text,
  notes        text,
  last_tick    bigint
);

-- ---------------------------------------------------------------------------
-- genome — one row per distinct genome id. data is the canonical JSON.
-- Extracted columns duplicate common scalars so analysis avoids jsonb paths.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS genome (
  run_id            uuid     NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,
  genome_id         bigint   NOT NULL,
  parent_genome_id  bigint,                     -- NULL for bootstrap genomes
  first_seen_tick   bigint   NOT NULL,
  hash              bytea    NOT NULL,          -- sha256 of canonical JSON
  data              jsonb    NOT NULL,
  -- extracted scalars
  size              real     NOT NULL,
  speed             real     NOT NULL,
  armor             real     NOT NULL,
  color_r           real     NOT NULL,
  color_g           real     NOT NULL,
  color_b           real     NOT NULL,
  diet              real     NOT NULL,
  storage_cap       real     NOT NULL,
  lifespan          real     NOT NULL,
  egg_threshold     real     NOT NULL,
  egg_investment    real     NOT NULL,
  mutation_rate     real     NOT NULL,
  structural_rate   real     NOT NULL,
  n_sensors         smallint NOT NULL,          -- enabled sensor genes
  n_actuators       smallint NOT NULL,          -- enabled actuator genes
  n_hidden          smallint NOT NULL,
  n_links           smallint NOT NULL,          -- enabled links
  sensor_kinds      jsonb    NOT NULL,          -- e.g. {"VisionPlant":1,"Smell:2":1,"Energy":1}
  actuator_kinds    jsonb    NOT NULL,          -- e.g. {"Thrust":1,"Turn":1,"Eat":1,"LayEgg":1,"Emit:2":1}
  PRIMARY KEY (run_id, genome_id)
);
CREATE INDEX IF NOT EXISTS genome_parent_idx ON genome (run_id, parent_genome_id);
CREATE INDEX IF NOT EXISTS genome_hash_idx   ON genome (run_id, hash);

-- ---------------------------------------------------------------------------
-- species
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS species (
  run_id             uuid    NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,
  species_id         integer NOT NULL,
  founded_tick       bigint  NOT NULL,
  founder_genome_id  bigint  NOT NULL,
  parent_species_id  integer,                   -- species the founder belonged to before the split; NULL for bootstrap
  PRIMARY KEY (run_id, species_id)
);

-- ---------------------------------------------------------------------------
-- creature — written at hatch (or bootstrap spawn)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS creature (
  run_id              uuid    NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,
  creature_id         bigint  NOT NULL,
  genome_id           bigint  NOT NULL,
  species_id          integer NOT NULL,         -- species at birth (inherited from parent)
  parent_creature_id  bigint,                   -- NULL for bootstrap
  generation          integer NOT NULL,
  birth_tick          bigint  NOT NULL,
  birth_x             real    NOT NULL,
  birth_y             real    NOT NULL,
  PRIMARY KEY (run_id, creature_id)
);
CREATE INDEX IF NOT EXISTS creature_genome_idx  ON creature (run_id, genome_id);
CREATE INDEX IF NOT EXISTS creature_parent_idx  ON creature (run_id, parent_creature_id);
CREATE INDEX IF NOT EXISTS creature_birth_brin  ON creature USING brin (run_id, birth_tick);

-- ---------------------------------------------------------------------------
-- creature_death — written at death
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS creature_death (
  run_id              uuid        NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,
  creature_id         bigint      NOT NULL,
  death_tick          bigint      NOT NULL,
  cause               death_cause NOT NULL,
  x                   real        NOT NULL,
  y                   real        NOT NULL,
  age                 integer     NOT NULL,
  energy_at_death     real        NOT NULL,
  killer_creature_id  bigint,                   -- set when cause = PREDATION
  offspring_count     integer     NOT NULL,     -- eggs laid over lifetime
  species_id          integer     NOT NULL,     -- species at time of death
  PRIMARY KEY (run_id, creature_id)
);
CREATE INDEX IF NOT EXISTS creature_death_tick_brin ON creature_death USING brin (run_id, death_tick);
CREATE INDEX IF NOT EXISTS creature_death_cause_idx ON creature_death (run_id, cause);

-- ---------------------------------------------------------------------------
-- event — interaction log. Semantics by kind:
--   EGG_LAID : actor = parent creature, target = egg id, value = egg energy, data = {"genome_id": n}
--   HATCH    : actor = egg id,          target = new creature id
--   EGG_EATEN: actor = eater creature,  target = egg id,          value = energy gained
--   BITE     : actor = biter,           target = bitten creature, value = damage dealt
--   RESEED   : actor/target NULL,       value = count spawned
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS event (
  run_id     uuid       NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,
  tick       bigint     NOT NULL,
  seq        integer    NOT NULL,                -- ordering within a tick
  kind       event_kind NOT NULL,
  actor_id   bigint,
  target_id  bigint,
  x          real,
  y          real,
  value      real,
  data       jsonb,
  PRIMARY KEY (run_id, tick, seq)
);
CREATE INDEX IF NOT EXISTS event_kind_idx ON event (run_id, kind, tick);

-- ---------------------------------------------------------------------------
-- world_stats — every statsEvery ticks
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS world_stats (
  run_id                  uuid    NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,
  tick                    bigint  NOT NULL,
  population              integer NOT NULL,
  eggs                    integer NOT NULL,
  meat_items              integer NOT NULL,
  plant_biomass_total     real    NOT NULL,
  meat_energy_total       real    NOT NULL,
  creature_energy_total   real    NOT NULL,
  -- flows since previous stats row
  births                  integer NOT NULL,
  eggs_laid               integer NOT NULL,
  eggs_eaten              integer NOT NULL,
  deaths_starvation       integer NOT NULL,
  deaths_predation        integer NOT NULL,
  deaths_old_age          integer NOT NULL,
  bites                   integer NOT NULL,
  cap_hits                integer NOT NULL,
  -- population means
  mean_energy             real    NOT NULL,
  mean_age                real    NOT NULL,
  mean_generation         real    NOT NULL,
  max_generation          integer NOT NULL,
  mean_size               real    NOT NULL,
  mean_speed              real    NOT NULL,
  mean_armor              real    NOT NULL,
  mean_diet               real    NOT NULL,
  mean_storage_cap        real    NOT NULL,
  mean_lifespan           real    NOT NULL,
  mean_egg_threshold      real    NOT NULL,
  mean_egg_investment     real    NOT NULL,
  mean_mutation_rate      real    NOT NULL,
  mean_structural_rate    real    NOT NULL,
  mean_sensors            real    NOT NULL,
  mean_actuators          real    NOT NULL,
  mean_hidden             real    NOT NULL,
  mean_links              real    NOT NULL,
  -- diversity
  species_count           integer NOT NULL,
  species_count_min5      integer NOT NULL,
  shannon                 real    NOT NULL,
  mean_pairwise_distance  real    NOT NULL,
  -- wall clock (measured by Sim.Cli, not part of sim state)
  ticks_per_second        real,
  PRIMARY KEY (run_id, tick)
);

-- ---------------------------------------------------------------------------
-- species_stats — every statsEvery ticks, one row per species with population > 0
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS species_stats (
  run_id                uuid    NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,
  tick                  bigint  NOT NULL,
  species_id            integer NOT NULL,
  population            integer NOT NULL,
  mean_size             real    NOT NULL,
  mean_speed            real    NOT NULL,
  mean_armor            real    NOT NULL,
  mean_color_r          real    NOT NULL,
  mean_color_g          real    NOT NULL,
  mean_color_b          real    NOT NULL,
  mean_diet             real    NOT NULL,
  mean_storage_cap      real    NOT NULL,
  mean_lifespan         real    NOT NULL,
  mean_egg_threshold    real    NOT NULL,
  mean_egg_investment   real    NOT NULL,
  mean_mutation_rate    real    NOT NULL,
  mean_structural_rate  real    NOT NULL,
  mean_sensors          real    NOT NULL,
  mean_actuators        real    NOT NULL,
  mean_hidden           real    NOT NULL,
  mean_links            real    NOT NULL,
  mean_energy           real    NOT NULL,
  mean_age              real    NOT NULL,
  sensor_kind_counts    jsonb   NOT NULL,       -- {"VisionPlant": 120, "Smell:2": 40, ...} = creatures carrying it
  actuator_kind_counts  jsonb   NOT NULL,
  PRIMARY KEY (run_id, tick, species_id)
);
CREATE INDEX IF NOT EXISTS species_stats_species_idx ON species_stats (run_id, species_id, tick);

-- ---------------------------------------------------------------------------
-- position_sample — every positionsEvery ticks. The big table.
-- Consider: CREATE UNLOGGED TABLE (faster, lost on crash) or partition by run_id for large studies.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS position_sample (
  run_id       uuid     NOT NULL,   -- no FK on purpose: keeps COPY fast; runs are never deleted mid-write
  tick         bigint   NOT NULL,
  creature_id  bigint   NOT NULL,
  species_id   integer  NOT NULL,   -- current species at sample time
  x            real     NOT NULL,
  y            real     NOT NULL,
  heading      real     NOT NULL,
  energy       real     NOT NULL,
  health       real     NOT NULL
);
CREATE INDEX IF NOT EXISTS position_sample_tick_brin ON position_sample USING brin (run_id, tick);
-- Optional, large; enable if you query single-creature trajectories often:
-- CREATE INDEX IF NOT EXISTS position_sample_creature_idx ON position_sample (run_id, creature_id, tick);

-- ===========================================================================
-- Views
-- ===========================================================================

CREATE OR REPLACE VIEW v_population AS
SELECT run_id, tick, population, eggs, births, eggs_laid, eggs_eaten,
       deaths_starvation, deaths_predation, deaths_old_age,
       deaths_starvation + deaths_predation + deaths_old_age AS deaths_total,
       cap_hits, species_count, species_count_min5, shannon, mean_pairwise_distance
FROM world_stats;

CREATE OR REPLACE VIEW v_trait_trends AS
SELECT run_id, tick, population,
       mean_size, mean_speed, mean_armor, mean_diet, mean_storage_cap, mean_lifespan,
       mean_egg_threshold, mean_egg_investment, mean_mutation_rate, mean_structural_rate,
       mean_sensors, mean_actuators, mean_hidden, mean_links, mean_generation, max_generation
FROM world_stats;

CREATE OR REPLACE VIEW v_species_timeline AS
SELECT s.run_id, s.species_id, s.founded_tick, s.parent_species_id, s.founder_genome_id,
       MAX(ss.tick)        AS last_seen_tick,
       MAX(ss.population)  AS peak_population,
       COUNT(*)            AS stats_rows
FROM species s
LEFT JOIN species_stats ss ON ss.run_id = s.run_id AND ss.species_id = s.species_id
GROUP BY s.run_id, s.species_id, s.founded_tick, s.parent_species_id, s.founder_genome_id;

CREATE OR REPLACE VIEW v_dominant_species AS
SELECT run_id, tick, species_id, population, mean_diet, mean_size, mean_speed, rnk
FROM (
  SELECT run_id, tick, species_id, population, mean_diet, mean_size, mean_speed,
         RANK() OVER (PARTITION BY run_id, tick ORDER BY population DESC, species_id) AS rnk
  FROM species_stats
) t
WHERE rnk <= 5;

-- Fraction of the population carrying each sensor kind, per stats tick.
CREATE OR REPLACE VIEW v_sensor_prevalence AS
SELECT ss.run_id, ss.tick, k.kind,
       SUM(k.cnt_text::integer)::real / NULLIF(ws.population, 0) AS fraction
FROM species_stats ss
JOIN world_stats ws ON ws.run_id = ss.run_id AND ws.tick = ss.tick
CROSS JOIN LATERAL jsonb_each_text(ss.sensor_kind_counts) AS k(kind, cnt_text)
GROUP BY ss.run_id, ss.tick, k.kind, ws.population;

CREATE OR REPLACE VIEW v_actuator_prevalence AS
SELECT ss.run_id, ss.tick, k.kind,
       SUM(k.cnt_text::integer)::real / NULLIF(ws.population, 0) AS fraction
FROM species_stats ss
JOIN world_stats ws ON ws.run_id = ss.run_id AND ws.tick = ss.tick
CROSS JOIN LATERAL jsonb_each_text(ss.actuator_kind_counts) AS k(kind, cnt_text)
GROUP BY ss.run_id, ss.tick, k.kind, ws.population;

-- Deaths by cause per 10k-tick bucket.
CREATE OR REPLACE VIEW v_death_causes AS
SELECT run_id, (death_tick / 10000) * 10000 AS bucket_tick, cause,
       COUNT(*) AS deaths, AVG(age)::real AS mean_age
FROM creature_death
GROUP BY run_id, bucket_tick, cause;

-- Lifetime reproductive success distribution per genome (fitness proxy).
CREATE OR REPLACE VIEW v_genome_fitness AS
SELECT c.run_id, c.genome_id,
       COUNT(*)                          AS individuals,
       AVG(d.offspring_count)::real      AS mean_offspring,
       AVG(d.age)::real                  AS mean_lifespan,
       MIN(c.birth_tick)                 AS first_birth_tick,
       MAX(d.death_tick)                 AS last_death_tick
FROM creature c
JOIN creature_death d ON d.run_id = c.run_id AND d.creature_id = c.creature_id
GROUP BY c.run_id, c.genome_id;

-- ===========================================================================
-- Functions
-- ===========================================================================

-- Walk up the parent chain. depth 0 = the creature itself.
CREATE OR REPLACE FUNCTION f_ancestry(p_run_id uuid, p_creature_id bigint)
RETURNS TABLE (depth integer, creature_id bigint, parent_creature_id bigint, genome_id bigint,
               species_id integer, generation integer, birth_tick bigint)
LANGUAGE sql STABLE AS $$
  WITH RECURSIVE up AS (
    SELECT 0 AS depth, c.creature_id, c.parent_creature_id, c.genome_id, c.species_id, c.generation, c.birth_tick
    FROM creature c
    WHERE c.run_id = p_run_id AND c.creature_id = p_creature_id
    UNION ALL
    SELECT up.depth + 1, c.creature_id, c.parent_creature_id, c.genome_id, c.species_id, c.generation, c.birth_tick
    FROM up
    JOIN creature c ON c.run_id = p_run_id AND c.creature_id = up.parent_creature_id
  )
  SELECT * FROM up ORDER BY depth;
$$;

-- Genome JSON for a creature.
CREATE OR REPLACE FUNCTION f_genome(p_run_id uuid, p_creature_id bigint)
RETURNS jsonb
LANGUAGE sql STABLE AS $$
  SELECT g.data
  FROM creature c
  JOIN genome g ON g.run_id = c.run_id AND g.genome_id = c.genome_id
  WHERE c.run_id = p_run_id AND c.creature_id = p_creature_id;
$$;

-- Genome lineage: walk parent_genome_id upward.
CREATE OR REPLACE FUNCTION f_genome_lineage(p_run_id uuid, p_genome_id bigint)
RETURNS TABLE (depth integer, genome_id bigint, parent_genome_id bigint, first_seen_tick bigint,
               size real, speed real, diet real, n_sensors smallint, n_links smallint)
LANGUAGE sql STABLE AS $$
  WITH RECURSIVE up AS (
    SELECT 0 AS depth, g.genome_id, g.parent_genome_id, g.first_seen_tick, g.size, g.speed, g.diet, g.n_sensors, g.n_links
    FROM genome g WHERE g.run_id = p_run_id AND g.genome_id = p_genome_id
    UNION ALL
    SELECT up.depth + 1, g.genome_id, g.parent_genome_id, g.first_seen_tick, g.size, g.speed, g.diet, g.n_sensors, g.n_links
    FROM up JOIN genome g ON g.run_id = p_run_id AND g.genome_id = up.parent_genome_id
  )
  SELECT * FROM up ORDER BY depth;
$$;

-- Snapshot of the world at (or just before) a tick, for a replay viewer.
CREATE OR REPLACE FUNCTION f_snapshot(p_run_id uuid, p_tick bigint)
RETURNS TABLE (tick bigint, creature_id bigint, species_id integer, x real, y real, heading real,
               energy real, health real, size real, color_r real, color_g real, color_b real)
LANGUAGE sql STABLE AS $$
  WITH t AS (
    SELECT MAX(ps.tick) AS tick FROM position_sample ps WHERE ps.run_id = p_run_id AND ps.tick <= p_tick
  )
  SELECT ps.tick, ps.creature_id, ps.species_id, ps.x, ps.y, ps.heading, ps.energy, ps.health,
         g.size, g.color_r, g.color_g, g.color_b
  FROM position_sample ps
  JOIN t ON ps.tick = t.tick
  JOIN creature c ON c.run_id = ps.run_id AND c.creature_id = ps.creature_id
  JOIN genome g   ON g.run_id = c.run_id AND g.genome_id = c.genome_id
  WHERE ps.run_id = p_run_id;
$$;
