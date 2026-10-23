using System;

internal static class PairPreparationEntry
{
    public static int Main(string[] args)
    {
        if (Environment.Version.Major != 4) throw new Exception("helper must execute on CLR4 / .NET Framework");
        Console.WriteLine("net48 CLR " + Environment.Version);
        PairPreparationGolden.RunVectors();
        Console.WriteLine("net48 pair preparation golden vectors passed");
        return 0;
    }
}
