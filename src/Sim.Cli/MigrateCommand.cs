using System;
using System.IO;
using System.Threading.Tasks;
using Npgsql;

namespace Sim.Cli;

/// <summary>`sim migrate --db "..."` — applies db/schema.sql (idempotent, safe to re-run).</summary>
internal static class MigrateCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? db = null;
        string schemaPath = "db/schema.sql";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": db = args[++i]; break;
                case "--schema": schemaPath = args[++i]; break;
                default: throw new ArgumentException($"Unknown migrate argument '{args[i]}'.");
            }
        }

        if (db is null) throw new ArgumentException("migrate requires --db \"Host=...;Database=...;Username=...;Password=...\"");

        string sql = await File.ReadAllTextAsync(schemaPath);

        await using var conn = new NpgsqlConnection(db);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();

        Console.WriteLine($"applied {schemaPath} to {conn.Host}:{conn.Port}/{conn.Database}");
        return 0;
    }
}
