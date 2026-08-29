using System;

namespace Sim.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: sim <bench|run|resume> [options]");
            return 1;
        }

        string command = args[0];
        string[] rest = args[1..];

        try
        {
            return command switch
            {
                "bench" => BenchCommand.Run(rest),
                "run" => throw new NotImplementedException("`sim run` arrives with persistence in M4."),
                "resume" => throw new NotImplementedException("`sim resume` arrives with checkpointing in M4."),
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
        Console.Error.WriteLine($"unknown command '{command}'. usage: sim <bench|run|resume> [options]");
        return 1;
    }
}
