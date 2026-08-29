namespace Sim.Persistence;

/// <summary>
/// Placeholder writer for `--no-db` runs (M1–M3). The real Npgsql binary-COPY writer and its
/// table sinks arrive in M4 (§8).
/// </summary>
public sealed class NullWriter
{
    public static readonly NullWriter Instance = new();

    private NullWriter() { }
}
