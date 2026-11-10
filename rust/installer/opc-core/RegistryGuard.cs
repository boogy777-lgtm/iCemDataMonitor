using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace Icem.OpcCoreGuard
{
    internal static class RegistryGuard
    {
        // MSI embeds this fixed, build-validated inventory. No command-line paths or
        // properties affect the search; unreadable registry is a conflict, not absence.
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 0) return 1;
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("opc-conflicts.txt"))
                {
                    if (stream == null) return 1;
                    using (var reader = new StreamReader(stream))
                    {
                        int count = 0;
                        string path;
                        while ((path = reader.ReadLine()) != null)
                        {
                            if (path.Length == 0 || path.Length > 512 || ++count > 2048) return 1;
                            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                            {
                                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                                using (RegistryKey existing = root.OpenSubKey(path, false))
                                    if (existing != null) { Console.Error.WriteLine("OPC registration already exists: " + path + " (" + view + ")"); return 1; }
                            }
                        }
                        return count >= 20 ? 0 : 1;
                    }
                }
            }
            catch (Exception error) { Console.Error.WriteLine("OPC registration check failed: " + error.Message); return 1; }
        }
    }
}
