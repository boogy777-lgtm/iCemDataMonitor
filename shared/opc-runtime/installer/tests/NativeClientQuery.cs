using System;
using System.IO;
using System.Reflection;

namespace Icem.OpcCoreGuard
{
    internal static class NativeClientQuery
    {
        // Actual read-only Windows MSI API smoke, separate from the pure fixtures.
        // No installation/repair/registration API is referenced here or in the query.
        private static int Main()
        {
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("opc-components.txt"))
            using (var reader = new StreamReader(resource))
            {
                int count = 0;
                string code;
                while ((code = reader.ReadLine()) != null)
                {
                    uint status = RegistryGuard.QueryFirstClient(code);
                    Console.WriteLine(code + " native client-query status " + status);
                    if (status != 0 && status != 259)
                    {
                        Console.Error.WriteLine("Native MSI query contract failed: " + status + "; all-user enumeration requires a fully elevated token.");
                        return 1;
                    }
                    count++;
                }
                if (count != 18) throw new Exception("Native query inventory incomplete");
                Console.WriteLine("PASS: 18 real read-only MSI client queries (existing client or verified absence).");
                return 0;
            }
        }
    }
}
