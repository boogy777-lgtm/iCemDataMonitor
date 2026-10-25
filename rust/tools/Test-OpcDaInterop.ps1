# CI-only matching proxy/stub. See ADR0019 before changing this native boundary.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'OPC RPC registration is restricted to ephemeral Windows Actions runners'
}
$Root = Split-Path $PSScriptRoot -Parent
$Fixture = Join-Path $Root 'tests/opc-da-interop'
$Work = Join-Path $env:RUNNER_TEMP ('icem-opc-rpc-' + [guid]::NewGuid().ToString('N'))
$Owner = [guid]::NewGuid().ToString('N')
$Proxy = '{A09B1F4C-8B64-44D1-B89F-0AA9A85F7981}'
$Owned = [System.Collections.Generic.List[string]]::new()
$Saved = @{}
$Classes = $null
$SavedClient = $env:ICEM_OPC_STA_CLIENT
function Native([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit $LASTEXITCODE" }
}
try {
    New-Item -ItemType Directory -Path $Work | Out-Null
    $Manifest = Get-Content -Raw (Join-Path $Fixture 'upstream.json') | ConvertFrom-Json
    if ($Manifest.commit -ne 'da44a99265f9706618bbe506c98f714f2aa46f0b') { throw 'Unexpected IDL pin' }
    foreach ($Entry in $Manifest.files.PSObject.Properties) {
        $Path = Join-Path $Fixture $Entry.Name
        if ((Get-Item $Path).Length -ne $Entry.Value.bytes -or
            (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Entry.Value.sha256) {
            throw "IDL provenance mismatch: $($Entry.Name)"
        }
        Copy-Item $Path $Work
    }
    $Vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $VS = & $Vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $VS) { throw 'MSVC Build Tools not found' }
    $DevCmd = Join-Path $VS 'Common7/Tools/VsDevCmd.bat'
    # Import only build variables; never echo the inherited environment/secrets.
    $BuildEnv = & $env:ComSpec /d /s /c "`"`"$DevCmd`" -no_logo -arch=x64 -host_arch=x64 >nul && set`""
    if ($LASTEXITCODE -ne 0) { throw 'VsDevCmd failed' }
    foreach ($Name in @('PATH', 'INCLUDE', 'LIB', 'LIBPATH')) {
        $Saved[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process')
        $Line = $BuildEnv | Where-Object { $_.StartsWith($Name + '=', [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if (-not $Line) { throw "Missing MSVC variable $Name" }
        [Environment]::SetEnvironmentVariable($Name, $Line.Substring($Name.Length + 1), 'Process')
    }
    Push-Location $Work
    try {
        foreach ($Name in @('opcda', 'opccomn')) {
            Native 'midl.exe' @('/nologo', '/env', 'x64', '/robust', '/Oicf', '/h', "$Name.h", '/iid', "${Name}_i.c", '/proxy', "${Name}_p.c", '/dlldata', 'dlldata.c', "$Name.idl")
        }
        @'
LIBRARY icem_opc_proxy
EXPORTS
 DllGetClassObject PRIVATE
 DllCanUnloadNow PRIVATE
 DllRegisterServer PRIVATE
 DllUnregisterServer PRIVATE
'@ | Set-Content -Encoding ascii proxy.def
        Native 'cl.exe' @('/nologo', '/c', '/O2', '/DWIN32', '/D_WIN32_WINNT=0x0601', '/DREGISTER_PROXY_DLL', '/DPROXY_CLSID_IS={0xa09b1f4c,0x8b64,0x44d1,{0xb8,0x9f,0x0a,0xa9,0xa8,0x5f,0x79,0x81}}', 'dlldata.c', 'opcda_p.c', 'opcda_i.c', 'opccomn_p.c', 'opccomn_i.c')
        Native 'cl.exe' @('/nologo', '/EHsc', '/std:c++17', '/W4', '/I.', (Join-Path $Fixture 'sta_client.cpp'), 'opcda_i.obj', '/Fe:sta_client.exe', '/link', 'ole32.lib', 'oleaut32.lib', 'uuid.lib')
        Native 'link.exe' @('/nologo', '/dll', '/out:icem_opc_proxy.dll', '/def:proxy.def', 'dlldata.obj', 'opcda_p.obj', 'opcda_i.obj', 'opccomn_p.obj', 'opccomn_i.obj', 'kernel32.lib', 'rpcrt4.lib', 'rpcns4.lib', 'oleaut32.lib', 'uuid.lib')
    } finally { Pop-Location }
    # Use generated IID definitions, never a handwritten ABI/interface IID list.
    $Iids = [System.Collections.Generic.List[string]]::new()
    foreach ($Name in @('opcda', 'opccomn')) {
        $Text = Get-Content -Raw (Join-Path $Work "${Name}_i.c")
        foreach ($Match in [regex]::Matches($Text, 'MIDL_DEFINE_GUID\(IID,\s*IID_\w+,\s*(0x[0-9a-fA-F]+),\s*(0x[0-9a-fA-F]+),\s*(0x[0-9a-fA-F]+),\s*(0x[0-9a-fA-F]+),\s*(0x[0-9a-fA-F]+),\s*(0x[0-9a-fA-F]+),\s*(0x[0-9a-fA-F]+),\s*(0x[0-9a-fA-F]+),\s*(0x[0-9a-fA-F]+),\s*(0x[0-9a-fA-F]+),\s*(0x[0-9a-fA-F]+)\)')) {
            $Parts = @(1..11 | ForEach-Object { [Convert]::ToUInt32($Match.Groups[$_].Value.Substring(2), 16) })
            $Id = '{' + ('{0:x8}-{1:x4}-{2:x4}-{3:x2}{4:x2}-{5:x2}{6:x2}{7:x2}{8:x2}{9:x2}{10:x2}' -f $Parts) + '}'
            $Iids.Add($Id.ToUpperInvariant())
        }
    }
    if ($Iids.Count -lt 10 -or -not $Iids.Contains('{39C13A70-011E-11D0-9675-0020AFD8ADB3}')) { throw 'Generated IID discovery failed' }
    $Keys = @("CLSID\$Proxy") + @($Iids | Sort-Object -Unique | ForEach-Object { "Interface\$_" })
    foreach ($Hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach ($View in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $Base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, $View)
            try {
                foreach ($Key in $Keys) {
                    $Existing = $Base.OpenSubKey("Software\Classes\$Key")
                    if ($null -ne $Existing) { $Existing.Dispose(); throw "Refusing existing OPC mapping: $Hive/$View/$Key" }
                }
            } finally { $Base.Dispose() }
        }
    }
    $Base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    try { $Classes = $Base.CreateSubKey('Software\Classes', $true) } finally { $Base.Dispose() }
    foreach ($Key in $Keys) {
        $Node = $Classes.CreateSubKey($Key, $true)
        try { $Owned.Add($Key); $Node.SetValue('iCemCiOwner', $Owner) } finally { $Node.Dispose() }
    }
    foreach ($Id in $Iids | Sort-Object -Unique) {
        $Node = $Classes.CreateSubKey("Interface\$Id\ProxyStubClsid32", $true)
        try { $Node.SetValue('', $Proxy) } finally { $Node.Dispose() }
    }
    $Node = $Classes.CreateSubKey("CLSID\$Proxy\InprocServer32", $true)
    try { $Node.SetValue('', (Join-Path $Work 'icem_opc_proxy.dll')); $Node.SetValue('ThreadingModel', 'Both') } finally { $Node.Dispose() }
    $env:ICEM_OPC_STA_CLIENT = Join-Path $Work 'sta_client.exe'
    Push-Location $Root
    try {
        Native 'cargo' @('test', '-p', 'icem-opc-da-sidecar', '--test', 'windows_process', '--locked', '--offline', '--', '--ignored', '--nocapture')
    } finally { Pop-Location }
    Write-Host 'Matching pinned OPC proxy/stub + actual Rust MTA and independent C++ STA cross-process gates: PASS'
} finally {
    $CleanupErrors = [System.Collections.Generic.List[string]]::new()
    try {
        if ($null -ne $Classes) {
            try {
                foreach ($Key in $Owned) {
                    try {
                        $Node = $Classes.OpenSubKey($Key)
                        if ($null -eq $Node) { throw "Owned OPC mapping disappeared: $Key" }
                        try {
                            $Marker = $Node.GetValue('iCemCiOwner')
                            # Marker-write failure can leave our newly created empty root.
                            # Any unmarked content or changed marker is foreign: never delete.
                            $Empty = $Node.ValueCount -eq 0 -and $Node.SubKeyCount -eq 0
                            $MatchesOwner = $Marker -ceq $Owner -or ($null -eq $Marker -and $Empty)
                        } finally { $Node.Dispose() }
                        if (-not $MatchesOwner) { throw "Refusing cleanup of foreign OPC mapping: $Key" }
                        $Classes.DeleteSubKeyTree($Key, $false)
                    } catch { $CleanupErrors.Add($_.Exception.Message) }
                }
            } finally { $Classes.Dispose() }
        }
    } finally {
        try {
            foreach ($Name in $Saved.Keys) { [Environment]::SetEnvironmentVariable($Name, $Saved[$Name], 'Process') }
            $env:ICEM_OPC_STA_CLIENT = $SavedClient
        } finally {
            if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
        }
    }
    if ($CleanupErrors.Count -ne 0) { throw ('OPC registry cleanup failed: ' + ($CleanupErrors -join '; ')) }
}
