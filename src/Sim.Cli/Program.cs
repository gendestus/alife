using System;
using System.Threading.Tasks;

namespace Sim.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: sim <bench|run|resume|migrate> [options]");
            return 1;
        }

        string command = args[0];
        string[] rest = args[1..];

        try
        {
            return command switch
            {
                "bench" => BenchCommand.Run(rest),
                "migrate" => await MigrateCommand.RunAsync(rest),
                "query" => await QueryCommand.RunAsync(rest),
                "run" => await RunCommand.RunAsync(rest),
                "resume" => await ResumeCommand.RunAsync(rest),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'. usage: sim <bench|run|resume|migrate> [options]");
        return 1;
    }
}
