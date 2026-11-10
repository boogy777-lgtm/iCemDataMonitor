using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Icem.OpcCoreGuard
{
    internal static class RegistryGuardTests
    {
        // Pure fixtures: injected API results, no real registry/MSI API calls.
        private static int Main()
        {
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("opc-components.txt"))
            using (var reader = new StreamReader(resource))
            {
                string inventory = reader.ReadToEnd();
                int calls = 0;
                if (!RegistryGuard.ComponentsAbsent(new StringReader(inventory), _ => { calls++; return 259; }) || calls != 18)
                    throw new Exception("Clean inventory was not completely checked");
                uint[] conflicts = { 0, 5, 87, 234, 1610, 1627, uint.MaxValue };
                foreach (uint result in conflicts)
                {
                    calls = 0;
                    if (RegistryGuard.ComponentsAbsent(new StringReader(inventory), _ => { calls++; return result; }) || calls != 1)
                        throw new Exception("Existing client/error accepted: " + result);
                }
                string[] codes = inventory.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string[] malformed = {
                    "", string.Join("\n", codes.Skip(1)), string.Join("\n", codes.Concat(new[] { codes[0] })),
                    inventory.Replace(codes[0], "invalid-guid"), inventory.ToLowerInvariant()
                };
                foreach (string input in malformed)
                {
                    calls = 0;
                    if (RegistryGuard.ComponentsAbsent(new StringReader(input), _ => { calls++; return 259; }) || calls != 0)
                        throw new Exception("Invalid embedded inventory reached the native API");
                }
                Console.WriteLine("PASS: 13 pure component-owner guard checks; 18 canonical codes, all API errors fail closed.");
                return 0;
            }
        }
    }
}
