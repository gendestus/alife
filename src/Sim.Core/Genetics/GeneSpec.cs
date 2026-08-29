using Sim.Core.Config;

namespace Sim.Core.Genetics;

/// <summary>Ranges, slot counts and cost formulas from §4.1-§4.3, in one place so Validate/Mutation/bootstrap agree.</summary>
public static class GeneSpec
{
    public const float MutationRateMin = 0.002f, MutationRateMax = 0.2f;
    public const float StructuralRateMin = 0.001f, StructuralRateMax = 0.1f;
    public const float SizeMin = 0.5f, SizeMax = 3.0f;
    public const float SpeedMin = 0.0f, SpeedMax = 2.0f;
    public const float ArmorMin = 0.0f, ArmorMax = 1.0f;
    public const float ColorMin = 0.0f, ColorMax = 1.0f;
    public const float DietMin = 0.0f, DietMax = 1.0f;
    public const float StorageCapMin = 0.5f, StorageCapMax = 2.0f;
    public const float LifespanMin = 500f, LifespanMax = 5000f;
    public const float EggThresholdMin = 30f, EggThresholdMax = 200f;
    public const float EggInvestmentMin = 10f, EggInvestmentMax = 100f;

    // Vision* range; Smell has its own narrower range (below).
    public const float VisionRangeMin = 2f, VisionRangeMax = 40f;
    public const float SmellRangeMin = 1f, SmellRangeMax = 6f;
    public const float AngleMin = -System.MathF.PI, AngleMax = System.MathF.PI;
    public const float FovMin = 15f * System.MathF.PI / 180f;
    public const float FovMax = 120f * System.MathF.PI / 180f;

    // §4.3 only states Thrust's strength range explicitly; reused for Turn/Bite/Emit for consistency.
    public const float StrengthMin = 0.5f, StrengthMax = 2.0f;

    public const int ChannelMin = 0, ChannelMax = 3;

    public static int SensorSlotCount(SensorKind kind) => kind switch
    {
        SensorKind.VisionCreature => 5,
        SensorKind.VisionPlant => 1,
        SensorKind.VisionMeat => 1,
        SensorKind.Smell => 2,
        SensorKind.Contact => 1,
        SensorKind.Energy => 1,
        SensorKind.Age => 1,
        SensorKind.Health => 1,
        _ => 0,
    };

    public static bool SensorUsesRangeAngleFov(SensorKind kind) =>
        kind is SensorKind.VisionCreature or SensorKind.VisionPlant or SensorKind.VisionMeat;

    public static bool SensorUsesChannel(SensorKind kind) => kind == SensorKind.Smell;

    public static bool ActuatorUsesChannel(ActuatorKind kind) => kind == ActuatorKind.Emit;

    public static bool ActuatorUsesStrength(ActuatorKind kind) =>
        kind is ActuatorKind.Thrust or ActuatorKind.Turn or ActuatorKind.Bite or ActuatorKind.Emit;

    /// <summary>Per-tick cost of an enabled sensor gene (§4.2). Disabled sensors cost nothing.</summary>
    public static float SensorCost(SensorGene gene, EnergyConfig cfg)
    {
        if (!gene.Enabled) return 0f;
        return gene.Kind switch
        {
            SensorKind.VisionCreature => cfg.CVis * (gene.Range / 10f) * (gene.Fov / (60f * System.MathF.PI / 180f)),
            SensorKind.VisionPlant => cfg.CVis * (gene.Range / 10f) * (gene.Fov / (60f * System.MathF.PI / 180f)) * 0.5f,
            SensorKind.VisionMeat => cfg.CVis * (gene.Range / 10f) * (gene.Fov / (60f * System.MathF.PI / 180f)) * 0.5f,
            SensorKind.Smell => 0.005f,
            SensorKind.Contact => 0.002f,
            SensorKind.Energy => 0.001f,
            SensorKind.Age => 0.001f,
            SensorKind.Health => 0.001f,
            _ => 0f,
        };
    }

    public static float ClampSensorRange(SensorKind kind, float value)
    {
        var (lo, hi) = kind == SensorKind.Smell ? (SmellRangeMin, SmellRangeMax) : (VisionRangeMin, VisionRangeMax);
        return System.Math.Clamp(value, lo, hi);
    }

    /// <summary>
    /// Sum of every static (non-output-dependent) per-tick cost: basal/armor/store/life,
    /// enabled sensors, enabled-actuator-gene passive cost, and brain node/link cost (§4.1-§4.4).
    /// Constant for a given genome, so callers cache it once at hatch rather than recomputing.
    /// </summary>
    public static float TotalPassiveCost(Genome g, EnergyConfig cfg)
    {
        float cost = cfg.CBasal * System.MathF.Pow(g.Body.Size, 1.5f)
                   + cfg.CArmor * g.Body.Armor * g.Body.Size
                   + cfg.CStore * g.Metabolism.StorageCap * g.Body.Size
                   + cfg.CLife * g.Metabolism.Lifespan / 1000f;

        foreach (var s in g.Sensors) cost += SensorCost(s, cfg);

        int enabledActuators = 0;
        foreach (var a in g.Actuators)
        {
            if (a.Enabled) enabledActuators++;
        }
        cost += cfg.ActuatorPassive * enabledActuators;

        int hiddenCount = 0;
        foreach (var n in g.Brain.Nodes)
        {
            if (n.Kind == Sim.Core.Brain.NodeKind.Hidden) hiddenCount++;
        }
        int enabledLinks = 0;
        foreach (var l in g.Brain.Links)
        {
            if (l.Enabled) enabledLinks++;
        }
        cost += cfg.CNode * hiddenCount + cfg.CLink * enabledLinks;

        return cost;
    }
}
