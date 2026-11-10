param([string] $Name, [string] $Rights)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class MutexAclFixture {
    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr sd, IntPtr size);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern SafeWaitHandle CreateMutexW(ref SecurityAttributes attrs, bool owned, string name);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern SafeWaitHandle CreateMutexW(IntPtr attrs, bool owned, string name);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr memory);
}
'@
$sd = [IntPtr]::Zero
if (-not [MutexAclFixture]::ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;$Rights;;;WD)", 1, [ref]$sd, [IntPtr]::Zero)) { throw "SDDL error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
try {
    $sa = [MutexAclFixture+SecurityAttributes]::new()
    $sa.Length = [Runtime.InteropServices.Marshal]::SizeOf([type][MutexAclFixture+SecurityAttributes])
    $sa.Descriptor = $sd
    $owner = [MutexAclFixture]::CreateMutexW([ref]$sa, $false, $Name)
    if ($owner.IsInvalid) { throw "create fixture error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
    try {
        $old = [MutexAclFixture]::CreateMutexW([IntPtr]::Zero, $false, $Name)
        if (-not $old.IsInvalid) { $old.Dispose(); throw 'old ALL_ACCESS unexpectedly succeeded' }
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        if ($errorCode -ne 5) { throw "old constructor error $errorCode, expected 5" }
        [Console]::Out.WriteLine('READY')
        [Console]::Out.Flush()
        [void][Console]::In.ReadLine()
    } finally { $owner.Dispose() }
} finally { [void][MutexAclFixture]::LocalFree($sd) }
