using System;
using Sim.Core.Entities;
using Sim.Core.Genetics;

namespace Sim.Core;

/// <summary>Sensor value computation (§4.2). Fills Creature.SensorInputs in genome.Sensors x slot order.</summary>
public sealed partial class World
{
    private void ComputeSensors(Creature c)
    {
        var genome = c.Genome!;
        int idx = 0;
        foreach (var s in genome.Sensors)
        {
            int slots = GeneSpec.SensorSlotCount(s.Kind);
            if (!s.Enabled)
            {
                for (int k = 0; k < slots; k++) c.SensorInputs[idx++] = 0f;
                continue;
            }

            switch (s.Kind)
            {
                case SensorKind.VisionCreature:
                    ComputeVisionCreature(c, s, c.SensorInputs, ref idx);
                    break;
                case SensorKind.VisionPlant:
                    c.SensorInputs[idx++] = ComputeVisionPlant(c, s);
                    break;
                case SensorKind.VisionMeat:
                    c.SensorInputs[idx++] = ComputeVisionMeat(c, s);
                    break;
                case SensorKind.Smell:
                    ComputeSmell(c, s, c.SensorInputs, ref idx);
                    break;
                case SensorKind.Contact:
                    c.SensorInputs[idx++] = ComputeContact(c);
                    break;
                case SensorKind.Energy:
                    c.SensorInputs[idx++] = c.Energy / c.MaxEnergy;
                    break;
                case SensorKind.Age:
                    c.SensorInputs[idx++] = c.Age / c.Lifespan;
                    break;
                case SensorKind.Health:
                    c.SensorInputs[idx++] = c.Health / c.MaxHealth;
                    break;
            }
        }
    }

    private void ComputeVisionCreature(Creature self, SensorGene gene, float[] output, ref int idx)
    {
        Hash.QueryRadius(self.X, self.Y, gene.Range, _queryScratch);

        Creature? best = null;
        float bestDist = float.MaxValue;
        float sensorDir = self.Heading + gene.Angle;
        float halfFov = gene.Fov * 0.5f;

        for (int k = 0; k < _queryScratch.Count; k++)
        {
            var other = Creatures[_queryScratch[k]];
            if (ReferenceEquals(other, self)) continue;

            float dx = other.X - self.X, dy = other.Y - self.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > gene.Range) continue;

            float bearing = MathF.Atan2(dy, dx);
            float delta = NormalizeAngle(bearing - sensorDir);
            if (MathF.Abs(delta) > halfFov) continue;

            if (dist < bestDist)
            {
                bestDist = dist;
                best = other;
            }
        }

        if (best is null)
        {
            output[idx++] = 0f; output[idx++] = 0f; output[idx++] = 0f; output[idx++] = 0f; output[idx++] = 0f;
            return;
        }

        output[idx++] = best.ColorR;
        output[idx++] = best.ColorG;
        output[idx++] = best.ColorB;
        output[idx++] = 1f - bestDist / gene.Range;
        output[idx++] = best.Size / (best.Size + self.Size);
    }

    private float ComputeVisionPlant(Creature self, SensorGene gene)
    {
        float baseDir = self.Heading + gene.Angle;
        float halfFov = gene.Fov * 0.5f;
        Span<float> rayOffsets = stackalloc float[] { -halfFov, 0f, halfFov };
        Span<float> distFracs = stackalloc float[] { 0.25f, 0.5f, 0.75f, 1.0f };

        float sum = 0f;
        int n = 0;
        float bMax = _cfg.World.BMax;
        foreach (float rayOffset in rayOffsets)
        {
            float dir = baseDir + rayOffset;
            float dcos = MathF.Cos(dir), dsin = MathF.Sin(dir);
            foreach (float frac in distFracs)
            {
                float dist = gene.Range * frac;
                float px = self.X + dcos * dist;
                float py = self.Y + dsin * dist;
                int cell = Plants.CellIndex(px, py);
                sum += Plants.Biomass[cell] / bMax;
                n++;
            }
        }
        return sum / n;
    }

    private float ComputeVisionMeat(Creature self, SensorGene gene)
    {
        float sensorDir = self.Heading + gene.Angle;
        float halfFov = gene.Fov * 0.5f;
        float sum = 0f;

        Food.QueryRadius(self.X, self.Y, gene.Range, _meatQueryScratch, _eggQueryScratch);

        for (int k = 0; k < _meatQueryScratch.Count; k++)
        {
            var m = Meat.Items[_meatQueryScratch[k]];
            float dx = m.X - self.X, dy = m.Y - self.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > gene.Range) continue;
            float bearing = MathF.Atan2(dy, dx);
            float delta = NormalizeAngle(bearing - sensorDir);
            if (MathF.Abs(delta) > halfFov) continue;
            sum += m.Energy;
        }
        // Eggs count as meat too (§4.2: "Includes eggs (as meat, energy = egg energy)").
        for (int k = 0; k < _eggQueryScratch.Count; k++)
        {
            var egg = Eggs[_eggQueryScratch[k]];
            float dx = egg.X - self.X, dy = egg.Y - self.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > gene.Range) continue;
            float bearing = MathF.Atan2(dy, dx);
            float delta = NormalizeAngle(bearing - sensorDir);
            if (MathF.Abs(delta) > halfFov) continue;
            sum += egg.Energy;
        }
        return MathF.Min(1f, sum / 50f);
    }

    private void ComputeSmell(Creature self, SensorGene gene, float[] output, ref int idx)
    {
        const float whiskerAngle = MathF.PI / 3f; // 60 degrees
        float leftDir = self.Heading + whiskerAngle;
        float rightDir = self.Heading - whiskerAngle;

        float lx = self.X + MathF.Cos(leftDir) * gene.Range;
        float ly = self.Y + MathF.Sin(leftDir) * gene.Range;
        float rx = self.X + MathF.Cos(rightDir) * gene.Range;
        float ry = self.Y + MathF.Sin(rightDir) * gene.Range;

        output[idx++] = MathF.Min(1f, Scent.Sample(gene.Channel, lx, ly) / 25f);
        output[idx++] = MathF.Min(1f, Scent.Sample(gene.Channel, rx, ry) / 25f);
    }

    private float ComputeContact(Creature self)
    {
        float searchRadius = self.Size + GeneSpec.SizeMax;
        Hash.QueryRadius(self.X, self.Y, searchRadius, _queryScratch);

        for (int k = 0; k < _queryScratch.Count; k++)
        {
            var other = Creatures[_queryScratch[k]];
            if (ReferenceEquals(other, self)) continue;
            float dx = other.X - self.X, dy = other.Y - self.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist <= self.Size + other.Size) return 1f;
        }
        return 0f;
    }

    private static float NormalizeAngle(float a)
    {
        a %= 2f * MathF.PI;
        if (a > MathF.PI) a -= 2f * MathF.PI;
        else if (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }
}
