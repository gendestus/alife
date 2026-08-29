using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Sim.Core.Config;

namespace Sim.Cli;

internal static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static SimConfig Load(string path)
    {
        string json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<SimConfig>(json, JsonOptions);
        return config ?? throw new InvalidOperationException($"Failed to parse config at '{path}'.");
    }

    /// <summary>Applies "section.property=value" overrides (e.g. "logging.positionsEvery=25") via reflection.</summary>
    public static void ApplyOverride(SimConfig config, string keyValue)
    {
        int eq = keyValue.IndexOf('=');
        if (eq < 0) throw new ArgumentException($"--set expects key=value, got '{keyValue}'.");

        string key = keyValue[..eq];
        string value = keyValue[(eq + 1)..];
        string[] parts = key.Split('.');
        if (parts.Length != 2)
            throw new ArgumentException($"--set expects 'section.property=value', got '{keyValue}'.");

        var sectionProp = FindProperty(typeof(SimConfig), parts[0]);
        object section = sectionProp.GetValue(config)
            ?? throw new InvalidOperationException($"Config section '{parts[0]}' is null.");
        var valueProp = FindProperty(section.GetType(), parts[1]);

        object converted = ConvertValue(value, valueProp.PropertyType);
        valueProp.SetValue(section, converted);
    }

    private static PropertyInfo FindProperty(Type type, string name)
    {
        return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? throw new ArgumentException($"Unknown config property '{name}' on {type.Name}.");
    }

    private static object ConvertValue(string raw, Type targetType)
    {
        if (targetType == typeof(bool)) return bool.Parse(raw);
        if (targetType == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
        if (targetType == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
        if (targetType == typeof(string)) return raw;
        throw new NotSupportedException($"Unsupported config value type '{targetType.Name}'.");
    }
}
