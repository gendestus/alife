using System;
using Sim.Core.Entities;
using Sim.Core.Genetics;

namespace Sim.Core;

/// <summary>Brain step + actuator execution (§4.3, §4.4) for genome-driven creatures.</summary>
public sealed partial class World
{
    private const int ActuatorKindCount = 6; // Thrust, Turn, Eat, Bite, LayEgg, Emit

    private void GenomeAct(Creature c)
    {
        var genome = c.Genome!;
        var brain = c.Brain!;

        ComputeSensors(c);
        brain.Step(c.SensorInputs);

        // Multiple genes of the same kind: outputs are summed then clamped, and the summed
        // value drives the action once (§4.3). The common case is exactly one gene per kind.
        Span<float> sumByKind = stackalloc float[ActuatorKindCount];
        Span<int> firstGeneIndexByKind = stackalloc int[ActuatorKindCount];
        for (int k = 0; k < ActuatorKindCount; k++) firstGeneIndexByKind[k] = -1;

        for (int i = 0; i < genome.Actuators.Count; i++)
        {
            var gene = genome.Actuators[i];
            float o = brain.GetOutput(i);
            c.ActuatorOutputs[i] = o;
            if (!gene.Enabled) continue;

            int k = (int)gene.Kind;
            sumByKind[k] += o;
            if (firstGeneIndexByKind[k] < 0) firstGeneIndexByKind[k] = i;
        }

        for (int k = 0; k < ActuatorKindCount; k++)
        {
            if (firstGeneIndexByKind[k] < 0) continue;
            var gene = genome.Actuators[firstGeneIndexByKind[k]];
            float o = Math.Clamp(sumByKind[k], -1f, 1f);
            ExecuteActuator(c, gene, o);
        }

        ClampOrWrapPosition(c);
    }

    private void ExecuteActuator(Creature c, ActuatorGene gene, float o)
    {
        switch (gene.Kind)
        {
            case ActuatorKind.Thrust: ExecuteThrust(c, gene, o); break;
            case ActuatorKind.Turn: ExecuteTurn(c, gene, o); break;
            case ActuatorKind.Eat: ExecuteEat(c, o); break;
            case ActuatorKind.Bite: ExecuteBite(c, gene, o); break;
            case ActuatorKind.Emit: ExecuteEmit(c, gene, o); break;
            case ActuatorKind.LayEgg: ExecuteLayEgg(c, o); break;
        }
    }

    private void ExecuteThrust(Creature c, ActuatorGene gene, float o)
    {
        float v = Math.Clamp(o, 0f, 1f) * c.Speed * gene.Strength;
        v = MathF.Min(v, c.Speed * 2f);
        c.X += MathF.Cos(c.Heading) * v;
        c.Y += MathF.Sin(c.Heading) * v;
        float cost = _cfg.Energy.CMove * v * c.Size;
        c.Energy -= cost;
        _lastTickCostsAccum += cost;
    }

    private void ExecuteTurn(Creature c, ActuatorGene gene, float o)
    {
        c.Heading += o * _cfg.Energy.MaxTurn * gene.Strength;
        float cost = _cfg.Energy.CTurn * MathF.Abs(o);
        c.Energy -= cost;
        _lastTickCostsAccum += cost;
    }

    private void ExecuteEat(Creature c, float o)
    {
        if (o <= 0.5f) return;

        int cell = Plants.CellIndex(c.X, c.Y);
        float b = Plants.Biomass[cell];
        float plantEff = MathF.Pow(1f - c.Diet, _cfg.Energy.DietExp);
        float plantDesiredAmount = MathF.Min(_cfg.Energy.EatRate, b);
        float plantDesiredGain = plantDesiredAmount * _cfg.Energy.EnergyPerBiomass * plantEff;

        float reach = c.Size + 1f;
        float meatEff = MathF.Pow(c.Diet, _cfg.Energy.DietExp);

        Food.QueryRadius(c.X, c.Y, reach, _meatQueryScratch, _eggQueryScratch);

        int meatIdx = -1;
        float meatDist = float.MaxValue;
        for (int k = 0; k < _meatQueryScratch.Count; k++)
        {
            int i = _meatQueryScratch[k];
            var m = Meat.Items[i];
            float dx = m.X - c.X, dy = m.Y - c.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist <= reach && dist < meatDist)
            {
                meatDist = dist;
                meatIdx = i;
            }
        }

        // Eggs count as meat for foraging purposes too (§4.2/§4.3), nearest source wins.
        int eggIdx = -1;
        float eggDist = float.MaxValue;
        for (int k = 0; k < _eggQueryScratch.Count; k++)
        {
            int i = _eggQueryScratch[k];
            var egg = Eggs[i];
            float dx = egg.X - c.X, dy = egg.Y - c.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist <= reach && dist < eggDist)
            {
                eggDist = dist;
                eggIdx = i;
            }
        }

        bool eggIsNearer = eggIdx >= 0 && (meatIdx < 0 || eggDist < meatDist);
        float animalDesiredGain;
        if (eggIsNearer) animalDesiredGain = Eggs[eggIdx].Energy * meatEff; // full amount: eating an egg destroys it
        else if (meatIdx >= 0) animalDesiredGain = MathF.Min(_cfg.Energy.EatRate, Meat.Items[meatIdx].Energy) * meatEff;
        else animalDesiredGain = 0f;

        float headroom = MathF.Max(0f, c.MaxEnergy - c.Energy);
        float gained;
        if (animalDesiredGain > plantDesiredGain && (eggIsNearer || meatIdx >= 0))
        {
            float actualGain = MathF.Min(animalDesiredGain, headroom);
            if (eggIsNearer)
            {
                // Tombstoned (Energy = 0), not removed: FoodIndex bucket indices for this tick
                // must stay valid for creatures acting later in the same tick's loop. Actual
                // removal happens in HatchEggs' end-of-tick compaction pass.
                Eggs[eggIdx].Energy = 0f;
                EggsEaten++;
            }
            else
            {
                float actualAmount = animalDesiredGain > 0f ? actualGain / meatEff : 0f;
                Meat.Reduce(meatIdx, actualAmount);
            }
            gained = actualGain;
        }
        else
        {
            float actualGain = MathF.Min(plantDesiredGain, headroom);
            float actualAmount = plantDesiredGain > 0f ? actualGain / (_cfg.Energy.EnergyPerBiomass * plantEff) : 0f;
            Plants.Biomass[cell] = b - actualAmount;
            gained = actualGain;
        }

        c.Energy += gained;
        c.Energy -= EatActiveCost;
        _lastTickCostsAccum += EatActiveCost;
    }

    private void ExecuteLayEgg(Creature c, float o)
    {
        if (o <= 0.5f) return;
        if (c.Age < _cfg.Life.MaturityTicks) return;
        if (c.Energy < c.EggThreshold) return;
        if (Creatures.Count + Eggs.Count >= _cfg.Life.PopCap)
        {
            CapHits++;
            return;
        }

        var childGenome = GenomeMutator.Mutate(c.Genome!, _rng, Innovations, _cfg.Mutation, _cfg.Mutation.MutationScale, _cfg.Mutation.StructuralScale);
        long genomeId = GenomesEqual(childGenome, c.Genome!) ? c.GenomeId : Innovations.NextGenomeId();

        // Clamp at lay time (§4.1): eggInvestment must stay below eggThreshold.
        float eggInvestment = MathF.Min(c.EggInvestment, c.EggThreshold - 5f);
        float cost = eggInvestment + _cfg.Energy.CEggOverhead;
        c.Energy -= cost;
        c.OffspringCount++;
        _lastTickCostsAccum += _cfg.Energy.CEggOverhead; // eggInvestment transfers to the egg, not a loss

        Eggs.Add(new Entities.Egg
        {
            Id = NextEggId++,
            Genome = childGenome,
            GenomeId = genomeId,
            X = c.X,
            Y = c.Y,
            Energy = eggInvestment,
            LaidTick = CurrentTick,
            HatchTick = CurrentTick + _cfg.Life.IncubationTicks,
            ParentId = c.Id,
            SpeciesId = c.SpeciesId,
            Generation = c.Generation + 1,
        });
        EggsLaid++;
    }

    private static bool GenomesEqual(Genome a, Genome b) => a.Hash().AsSpan().SequenceEqual(b.Hash());

    private void ExecuteBite(Creature c, ActuatorGene gene, float o)
    {
        if (o <= 0.5f) return;

        c.Energy -= 0.5f;
        _lastTickCostsAccum += 0.5f;

        float reach = c.Size * 1.5f;
        Hash.QueryRadius(c.X, c.Y, reach, _queryScratch);

        Creature? target = null;
        float bestDist = float.MaxValue;
        const float halfCone = MathF.PI / 4f; // ±45 degrees
        for (int k = 0; k < _queryScratch.Count; k++)
        {
            var other = Creatures[_queryScratch[k]];
            if (ReferenceEquals(other, c)) continue;
            float dx = other.X - c.X, dy = other.Y - c.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > reach) continue;
            float bearing = MathF.Atan2(dy, dx);
            float delta = NormalizeAngle(bearing - c.Heading);
            if (MathF.Abs(delta) > halfCone) continue;
            if (dist < bestDist)
            {
                bestDist = dist;
                target = other;
            }
        }

        if (target is null) return;

        float dmg = 10f * c.Size * gene.Strength * (1f - 0.8f * target.Armor);
        target.Health -= dmg;
        target.LastDamagedBy = c.Id;
        target.LastDamagedTick = CurrentTick;
    }

    private void ExecuteEmit(Creature c, ActuatorGene gene, float o)
    {
        if (o <= 0f) return;
        Scent.Deposit(gene.Channel, c.X, c.Y, 10f * gene.Strength * o);
        float cost = _cfg.Energy.CEmit * gene.Strength * o;
        c.Energy -= cost;
        _lastTickCostsAccum += cost;
    }
}
