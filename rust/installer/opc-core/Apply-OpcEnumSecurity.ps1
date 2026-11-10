[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceFile)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $SourceFile).Path
$expected = '6e161739c72afba6dac806960d640ae2834d1f5ab950329a9cc4c9615117aa3c'
if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $expected) { throw 'OpcEnum.cpp differs from reviewed upstream input.' }
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$original = [IO.File]::ReadAllText($path, $utf8)
$newline = if ($original.Contains("`r`n")) { "`r`n" } else { "`n" }
function Replace-ExactlyOnce([string]$text, [string]$old, [string]$new) {
    $at = $text.IndexOf($old, [StringComparison]::Ordinal)
    if ($at -lt 0 -or $text.IndexOf($old, $at + $old.Length, [StringComparison]::Ordinal) -ge 0) { throw 'Expected unique upstream patch context not found.' }
    return $text.Substring(0, $at) + $new + $text.Substring($at + $old.Length)
}
$text = Replace-ExactlyOnce $original '#include "stdafx.h"' ("#include `"stdafx.h`"$newline#include <sddl.h>")
$helper = @'
// iCem: explicit per-AppID local-only baseline; no machine-wide DCOM defaults.
// The operator SID is granted separately by the reviewed, consent-driven helper.
static HRESULT IcemSetOpcEnumSecurity(HKEY key)
{
    const struct { LPCWSTR name; LPCWSTR sddl; } entries[] = {
        { L"AccessPermission", L"O:SYG:SYD:(A;;0x3;;;SY)(A;;0x3;;;BA)" },
        { L"LaunchPermission", L"O:SYG:SYD:(A;;0xb;;;SY)(A;;0xb;;;BA)" }
    };
    for (const auto& entry : entries)
    {
        PSECURITY_DESCRIPTOR descriptor = NULL;
        ULONG size = 0;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                entry.sddl, SDDL_REVISION_1, &descriptor, &size))
            return HRESULT_FROM_WIN32(GetLastError());
        const LSTATUS status = RegSetValueExW(key, entry.name, 0, REG_BINARY,
            static_cast<const BYTE*>(descriptor), size);
        LocalFree(descriptor);
        if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    }
    const DWORD level = RPC_C_AUTHN_LEVEL_PKT_PRIVACY;
    const LSTATUS status = RegSetValueExW(key, L"AuthenticationLevel", 0, REG_DWORD,
        reinterpret_cast<const BYTE*>(&level), sizeof(level));
    return status == ERROR_SUCCESS ? S_OK : HRESULT_FROM_WIN32(status);
}

'@ -replace "`r?`n", $newline
$text = Replace-ExactlyOnce $text '// RegisterServer' ($helper + '// RegisterServer')
$oldService = ("`t// Remove any previous service since it may point to${newline}`t// the incorrect file${newline}`tUninstall();")
$newService = ("`t// Never remove or adopt an existing OpcEnum service during registration.${newline}`tif (IsInstalled()) { CoUninitialize(); return HRESULT_FROM_WIN32(ERROR_SERVICE_EXISTS); }")
$text = Replace-ExactlyOnce $text $oldService $newService
$startMarker = "`t// check for OSes earlier than XP SP2"
$endMarker = "`tif (bService)"
$start = $text.IndexOf($startMarker, [StringComparison]::Ordinal)
$end = $text.IndexOf($endMarker, $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -le $start -or $text.IndexOf($startMarker, $start + 1, [StringComparison]::Ordinal) -ge 0) { throw 'Unexpected upstream security block.' }
$secure = "`t// Explicit protected baseline; operator grant requires separate consent.${newline}`thr = IcemSetOpcEnumSecurity(key);${newline}`tif (FAILED(hr)) { CoUninitialize(); return hr; }${newline}${newline}"
$text = $text.Substring(0, $start) + $secure + $text.Substring($end)
$oldExisting = ("`tif (IsInstalled())${newline}`t`treturn TRUE;")
$newExisting = ("`tif (IsInstalled())${newline}`t`treturn FALSE; // never silently adopt an existing service")
$text = Replace-ExactlyOnce $text $oldExisting $newExisting
$text = Replace-ExactlyOnce $text "`t`t`tInstall();" "`t`t`tif (!Install()) { CoUninitialize(); return E_FAIL; }"
$oldInit = ("`t_ASSERTE(SUCCEEDED(hr));${newline}${newline}`t// This tells COM to use the security settings")
$newInit = ("`tif (FAILED(hr)) return;${newline}${newline}`t// This tells COM to use the security settings")
$text = Replace-ExactlyOnce $text $oldInit $newInit
$oldFailure = ("`tif (FAILED(hr))${newline}`t`tLogEvent(`"CoInitializeSecurity failed: Error #0x%x`", hr);")
$newFailure = ("`tif (FAILED(hr)) { LogEvent(`"CoInitializeSecurity failed: Error #0x%x`", hr); CoUninitialize(); return; }")
$text = Replace-ExactlyOnce $text $oldFailure $newFailure
[IO.File]::WriteAllText($path, $text, $utf8)
Write-Output ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())
