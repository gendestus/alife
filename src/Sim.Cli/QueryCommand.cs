using System;
using System.Text;
using System.Threading.Tasks;
using Npgsql;

namespace Sim.Cli;

/// <summary>`sim query --db "..." --sql "SELECT ..."` — debug/verification helper, prints results as tab-separated text.</summary>
internal static class QueryCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? db = null;
        string? sql = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": db = args[++i]; break;
                case "--sql": sql = args[++i]; break;
                default: throw new ArgumentException($"Unknown query argument '{args[i]}'.");
            }
        }

        if (db is null || sql is null) throw new ArgumentException("query requires --db \"...\" and --sql \"...\"");

        await using var conn = new NpgsqlConnection(db);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var header = new StringBuilder();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (i > 0) header.Append('\t');
            header.Append(reader.GetName(i));
        }
        Console.WriteLine(header.ToString());

        int rowCount = 0;
        while (await reader.ReadAsync())
        {
            var row = new StringBuilder();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0) row.Append('\t');
                row.Append(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i));
            }
            Console.WriteLine(row.ToString());
            rowCount++;
        }
        Console.Error.WriteLine($"({rowCount} rows)");

        return 0;
    }
}
