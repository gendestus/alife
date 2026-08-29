using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sim.Core.Brain;

namespace Sim.Core.Genetics;

/// <summary>
/// Canonical JSON (sorted keys, fixed "R" float formatting) for hashing and storage (§4).
/// Hand-written with Utf8JsonWriter rather than JsonSerializer so key order and float
/// formatting are exactly controlled and stable across .NET versions.
/// </summary>
public static class GenomeJson
{
    public static string ToCanonicalJson(this Genome g)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            WriteGenome(writer, g);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static byte[] Hash(this Genome g)
    {
        byte[] json = Encoding.UTF8.GetBytes(g.ToCanonicalJson());
        return SHA256.HashData(json);
    }

    private static void WriteGenome(Utf8JsonWriter w, Genome g)
    {
        w.WriteStartObject();

        w.WritePropertyName("actuators");
        w.WriteStartArray();
        foreach (var a in g.Actuators) WriteActuator(w, a);
        w.WriteEndArray();

        w.WritePropertyName("body");
        w.WriteStartObject();
        WriteFloat(w, "armor", g.Body.Armor);
        WriteFloat(w, "colorB", g.Body.ColorB);
        WriteFloat(w, "colorG", g.Body.ColorG);
        WriteFloat(w, "colorR", g.Body.ColorR);
        WriteFloat(w, "size", g.Body.Size);
        WriteFloat(w, "speed", g.Body.Speed);
        w.WriteEndObject();

        w.WritePropertyName("brain");
        w.WriteStartObject();
        w.WritePropertyName("links");
        w.WriteStartArray();
        foreach (var l in g.Brain.Links) WriteLink(w, l);
        w.WriteEndArray();
        w.WritePropertyName("nodes");
        w.WriteStartArray();
        foreach (var n in g.Brain.Nodes) WriteNode(w, n);
        w.WriteEndArray();
        w.WriteEndObject();

        w.WritePropertyName("meta");
        w.WriteStartObject();
        WriteFloat(w, "mutationRate", g.Meta.MutationRate);
        WriteFloat(w, "structuralRate", g.Meta.StructuralRate);
        w.WriteEndObject();

        w.WritePropertyName("metabolism");
        w.WriteStartObject();
        WriteFloat(w, "diet", g.Metabolism.Diet);
        WriteFloat(w, "lifespan", g.Metabolism.Lifespan);
        WriteFloat(w, "storageCap", g.Metabolism.StorageCap);
        w.WriteEndObject();

        w.WritePropertyName("repro");
        w.WriteStartObject();
        WriteFloat(w, "eggInvestment", g.Repro.EggInvestment);
        WriteFloat(w, "eggThreshold", g.Repro.EggThreshold);
        w.WriteEndObject();

        w.WritePropertyName("sensors");
        w.WriteStartArray();
        foreach (var s in g.Sensors) WriteSensor(w, s);
        w.WriteEndArray();

        w.WriteEndObject();
    }

    private static void WriteSensor(Utf8JsonWriter w, SensorGene s)
    {
        w.WriteStartObject();
        WriteFloat(w, "angle", s.Angle);
        w.WriteNumber("channel", s.Channel);
        w.WriteBoolean("enabled", s.Enabled);
        WriteFloat(w, "fov", s.Fov);
        w.WriteNumber("id", s.Id);
        w.WriteString("kind", s.Kind.ToString());
        WriteFloat(w, "range", s.Range);
        w.WriteEndObject();
    }

    private static void WriteActuator(Utf8JsonWriter w, ActuatorGene a)
    {
        w.WriteStartObject();
        w.WriteNumber("channel", a.Channel);
        w.WriteBoolean("enabled", a.Enabled);
        w.WriteNumber("id", a.Id);
        w.WriteString("kind", a.Kind.ToString());
        WriteFloat(w, "strength", a.Strength);
        w.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter w, BrainNode n)
    {
        w.WriteStartObject();
        if (n.BindGeneId is { } geneId) w.WriteNumber("bindGeneId", geneId); else w.WriteNull("bindGeneId");
        if (n.BindSlot is { } slot) w.WriteNumber("bindSlot", slot); else w.WriteNull("bindSlot");
        w.WriteNumber("id", n.Id);
        w.WriteString("kind", n.Kind.ToString());
        w.WriteEndObject();
    }

    private static void WriteLink(Utf8JsonWriter w, BrainLink l)
    {
        w.WriteStartObject();
        w.WriteBoolean("enabled", l.Enabled);
        w.WriteNumber("from", l.From);
        w.WriteNumber("innovation", l.Innovation);
        w.WriteNumber("to", l.To);
        WriteFloat(w, "weight", l.Weight);
        w.WriteEndObject();
    }

    private static void WriteFloat(Utf8JsonWriter w, string name, float value)
    {
        // Fixed "R" (round-trip) formatting per §4, written as a raw JSON number token.
        w.WritePropertyName(name);
        w.WriteRawValue(value.ToString("R", CultureInfo.InvariantCulture), skipInputValidation: true);
    }
}
