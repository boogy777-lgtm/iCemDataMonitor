using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Icem.OpcCoreGuard
{
    internal static class RegistryGuard
    {
        // Read-only query. Index zero suffices: fresh install accepts no existing
        // client, in any per-machine/per-user context. Supply the product buffer:
        // Windows returns ERROR_INVALID_PARAMETER when every output is null.
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint MsiEnumClientsExW(string component, string userSid,
            uint context, uint index, StringBuilder product, out uint installedContext, IntPtr sid, IntPtr sidLength);

        internal static uint QueryFirstClient(string component)
        {
            return MsiEnumClientsExW(component, "S-1-1-0", 7, 0,
                new StringBuilder(39), out _, IntPtr.Zero, IntPtr.Zero);
        }

        internal static bool ComponentsAbsent(TextReader reader, Func<string, uint> firstClient)
        {
            var codes = new HashSet<string>(StringComparer.Ordinal);
            string code;
            while ((code = reader.ReadLine()) != null)
            {
                if (!Guid.TryParseExact(code, "B", out Guid parsed) ||
                    code != parsed.ToString("B").ToUpperInvariant() || !codes.Add(code)) return false;
            }
            if (codes.Count != 18) return false;
            foreach (string component in codes)
            {
                uint result = firstClient(component);
                // Only ERROR_NO_MORE_ITEMS proves absence. Access denied/corrupt
                // MSI state/unknown results must not be treated as a clean machine.
                if (result != 259)
                {
                    Console.Error.WriteLine("OPC component has an MSI client or cannot be verified: " + component + " (status " + result + ")");
                    return false;
                }
            }
            return true;
        }

        // MSI embeds this fixed, build-validated inventory. No command-line paths or
        // properties affect the search; unreadable registry is a conflict, not absence.
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 0) return 1;
                using (Stream components = Assembly.GetExecutingAssembly().GetManifestResourceStream("opc-components.txt"))
                {
                    if (components == null) return 1;
                    using (var reader = new StreamReader(components))
                        if (!ComponentsAbsent(reader, QueryFirstClient)) return 1;
                }
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
