using System;
using System.Runtime.InteropServices;

namespace Icem.OpcCoreAcceptance
{
    // Test-only, independently built Windows COM client. Interface/vtable order is
    // from the pinned opccomn.idl; no server/sidecar code or vendor client library.
    [ComImport, Guid("9DD0B56C-AD9E-43EE-8305-487F3188BF7A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOPCServerList2
    {
        [PreserveSig] int EnumClassesOfCategories(uint implementedCount, IntPtr implemented,
            uint requiredCount, IntPtr required, out IOPCEnumGUID enumerator);
        [PreserveSig] int GetClassDetails(ref Guid clsid, out IntPtr progId, out IntPtr userType, out IntPtr versionIndependentProgId);
        [PreserveSig] int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);
    }

    [ComImport, Guid("55C382C8-21C7-4E88-96C1-BECFB1E3F483"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOPCEnumGUID
    {
        // This private client requests exactly one element, hence a single Guid.
        [PreserveSig] int Next(uint count, out Guid item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IOPCEnumGUID clone);
    }

    internal static class OpcComClient
    {
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeSecurity(IntPtr descriptor, int authenticationServices, IntPtr services,
            IntPtr reserved, uint authenticationLevel, uint impersonationLevel, IntPtr authenticationList, uint capabilities, IntPtr reserved2);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoSetProxyBlanket(IntPtr proxy, uint authenticationService, uint authorizationService,
            IntPtr principal, uint authenticationLevel, uint impersonationLevel, IntPtr authenticationInfo, uint capabilities);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoQueryProxyBlanket(IntPtr proxy, out uint authenticationService, out uint authorizationService,
            out IntPtr principal, out uint authenticationLevel, out uint impersonationLevel, out IntPtr authenticationInfo, out uint capabilities);

        private static void Success(int result, string operation)
        {
            if (result < 0) throw new COMException(operation, result);
        }

        private static void Secure(object proxy, Type contract)
        {
            // Set/query the actual interface pointer, not an unrelated IUnknown
            // blanket that might leave the interface under test at a lower level.
            IntPtr pointer = Marshal.GetComInterfaceForObject(proxy, contract);
            try
            {
                Success(CoSetProxyBlanket(pointer, 10, 0, IntPtr.Zero, 6, 3, IntPtr.Zero, 0), "CoSetProxyBlanket");
                IntPtr principal = IntPtr.Zero;
                try
                {
                    Success(CoQueryProxyBlanket(pointer, out uint authenticationService, out _, out principal,
                        out uint level, out _, out _, out _), "CoQueryProxyBlanket");
                    if (authenticationService != 10 || level != 6) throw new InvalidOperationException("Packet-privacy blanket was not retained");
                }
                finally { if (principal != IntPtr.Zero) Marshal.FreeCoTaskMem(principal); }
            }
            finally { Marshal.Release(pointer); }
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }

        [STAThread]
        private static int Main()
        {
            // CoCreateInstance can start the installed OpcEnum service: execution
            // belongs only in the gated disposable fixture, never on a workstation.
            bool initialized = false;
            object server = null;
            IOPCEnumGUID enumerator = null, clone = null;
            IntPtr categories = IntPtr.Zero;
            try
            {
                Success(CoInitializeEx(IntPtr.Zero, 2), "CoInitializeEx");
                initialized = true;
                Success(CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero, 6, 3, IntPtr.Zero, 0, IntPtr.Zero), "CoInitializeSecurity");
                Guid clsid = new Guid("13486D51-4821-11D2-A494-3CB306C10000");
                Guid iid = typeof(IOPCServerList2).GUID;
                Success(CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out IntPtr pointer), "CoCreateInstance(OPC.ServerList)");
                try { server = Marshal.GetObjectForIUnknown(pointer); }
                finally { Marshal.Release(pointer); }
                Secure(server, typeof(IOPCServerList2));
                categories = Marshal.AllocCoTaskMem(16);
                Marshal.StructureToPtr(new Guid("63D5F432-CFE4-11D1-B2C8-0060083BA1FB"), categories, false);
                Success(((IOPCServerList2)server).EnumClassesOfCategories(1, categories, 0, IntPtr.Zero, out enumerator), "EnumClassesOfCategories");
                if (enumerator == null) throw new InvalidOperationException("Server returned no enumeration object");
                Secure(enumerator, typeof(IOPCEnumGUID));
                int next = enumerator.Next(1, out _, out uint fetched);
                Console.Error.WriteLine("IOPCEnumGUID.Next HRESULT=0x" + next.ToString("X8") + "; fetched=" + fetched);
                if ((next != 0 && next != 1) || fetched > 1 || (next == 0 && fetched != 1) || (next == 1 && fetched != 0))
                    throw new InvalidOperationException("IOPCEnumGUID.Next returned an inconsistent HRESULT/count: 0x" + next.ToString("X8") + "/" + fetched);
                Success(enumerator.Reset(), "IOPCEnumGUID.Reset");
                Success(enumerator.Clone(out clone), "IOPCEnumGUID.Clone");
                if (clone == null) throw new InvalidOperationException("Server returned no cloned enumerator");
                Secure(clone, typeof(IOPCEnumGUID));
                Success(clone.Reset(), "Cloned IOPCEnumGUID.Reset");
                Console.WriteLine("PASS COM/RPC: architecture=" + (IntPtr.Size * 8) + "; authentication=packet-privacy; Enum/Next/Reset/Clone completed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("COM/RPC FAIL: " + error.GetType().Name + " HRESULT=0x" + error.HResult.ToString("X8") + " " + error.Message);
                return 1;
            }
            finally
            {
                Release(clone);
                Release(enumerator);
                Release(server);
                if (categories != IntPtr.Zero) Marshal.FreeCoTaskMem(categories);
                if (initialized) CoUninitialize();
            }
        }
    }
}
