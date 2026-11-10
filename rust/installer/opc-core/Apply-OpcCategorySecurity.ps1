[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceFile)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $SourceFile).Path
if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine '059f2526b0aef9d13c52c19fd7ea123e7c5669467f979abb819a5891011c1e13') { throw 'CategoryManager.cpp differs from reviewed upstream input.' }
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$text = [IO.File]::ReadAllText($path, $utf8)
$newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
$shared = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $path) '../../Shared/OpcUtilityClasses/COpcComModule.cpp'))
if ((Get-FileHash -LiteralPath $shared -Algorithm SHA256).Hash -ine '34561fceb68fcce65a966c0ceb13f1fe5a5061a01ed7fc0158f597ea1fa796fd') { throw 'COpcComModule.cpp differs from reviewed upstream input.' }
$sharedText = [IO.File]::ReadAllText($shared, $utf8)
$callPattern = '(?s)CoInitializeSecurity\(\s*cSecurity\s*,\s*-1\s*,\s*NULL\s*,\s*NULL\s*,\s*RPC_C_AUTHN_LEVEL_PKT\s*,\s*RPC_C_IMP_LEVEL_IMPERSONATE\s*,\s*NULL\s*,\s*EOAC_NONE\s*,\s*NULL\s*\)'
if ([regex]::Matches($sharedText, $callPattern).Count -ne 2) { throw 'Unexpected COpcComModule COM security call inventory.' }
$sharedText = [regex]::Replace($sharedText, $callPattern,
    'CoInitializeSecurity(reinterpret_cast<PSECURITY_DESCRIPTOR>(&m_cAppID), -1, NULL, NULL, RPC_C_AUTHN_LEVEL_PKT_PRIVACY, RPC_C_IMP_LEVEL_IMPERSONATE, NULL, EOAC_APPID, NULL)')
$tooLatePattern = '(?s)if \(FAILED\(hResult\)\)\s*\{\s*// security may have already been initialized when registering classes from config file\.\s*if \(hResult != RPC_E_TOO_LATE\)\s*\{\s*return;\s*\}\s*\}'
if ([regex]::Matches($sharedText, $tooLatePattern).Count -ne 1) { throw 'Unexpected RPC_E_TOO_LATE handling.' }
$sharedText = [regex]::Replace($sharedText, $tooLatePattern, 'if (FAILED(hResult)) { return; }')
$include = '#include "StdAfx.h"'
$includeAt = $text.IndexOf($include, [StringComparison]::Ordinal)
if ($includeAt -lt 0 -or $text.IndexOf($include, $includeAt + $include.Length, [StringComparison]::Ordinal) -ge 0) { throw 'CategoryManager include context is not unique.' }
$text = $text.Substring(0, $includeAt) + $include + $newline + '#include <sddl.h>' + $text.Substring($includeAt + $include.Length)
$needle = 'OPC_START_LOCAL_SERVER_EX(hInstance, lpCmdLine);'
$at = $text.IndexOf($needle, [StringComparison]::Ordinal)
if ($at -lt 0 -or $text.IndexOf($needle, $at + $needle.Length, [StringComparison]::Ordinal) -ge 0) { throw 'CategoryManager source context is not unique.' }
# Keep the upstream EX path's module lifetime and WinMain call, but omit
# RegisterFromFiles: its AllowEveryoneAccess attribute widens DCOM permissions.
$replacement = @'
// iCem: CategoryManager never imports external AllowEveryoneAccess settings.
    // Reject alternate spellings/extra options rather than bypassing this preflight.
    const bool isRegister = _tcsicmp(lpCmdLine, _T("/RegServer")) == 0;
    const bool isUnregister = _tcsicmp(lpCmdLine, _T("/UnRegServer")) == 0;
    const bool isEmbedding = _tcsicmp(lpCmdLine, _T("/Embedding")) == 0 ||
        _tcsicmp(lpCmdLine, _T("-Embedding")) == 0;
    if (!isRegister && !isUnregister && !isEmbedding && lpCmdLine[0] != 0)
        return ERROR_INVALID_PARAMETER;
    // The MSI invokes /RegServer only after its separate foreign-registration guard.
    // Create a new per-AppID baseline, refusing to adopt an existing registration.
    if (isRegister)
    {
        HKEY key = NULL;
        DWORD disposition = 0;
        LSTATUS status = RegCreateKeyExW(HKEY_LOCAL_MACHINE,
            L"SOFTWARE\\Classes\\AppID\\{CC7D4C09-B26B-45B3-B482-2DECA09B832A}",
            0, NULL, 0, KEY_WRITE | KEY_WOW64_64KEY, NULL, &key, &disposition);
        if (status != ERROR_SUCCESS) return static_cast<int>(status);
        if (disposition != REG_CREATED_NEW_KEY) { RegCloseKey(key); return ERROR_ALREADY_EXISTS; }
        const struct { LPCWSTR name; LPCWSTR sddl; } entries[] = {
            { L"AccessPermission", L"O:SYG:SYD:(A;;0x3;;;SY)(A;;0x3;;;BA)" },
            { L"LaunchPermission", L"O:SYG:SYD:(A;;0xb;;;SY)(A;;0xb;;;BA)" }
        };
        for (const auto& entry : entries)
        {
            PSECURITY_DESCRIPTOR descriptor = NULL;
            ULONG size = 0;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(entry.sddl, SDDL_REVISION_1, &descriptor, &size))
            { status = GetLastError(); break; }
            status = RegSetValueExW(key, entry.name, 0, REG_BINARY, static_cast<const BYTE*>(descriptor), size);
            LocalFree(descriptor);
            if (status != ERROR_SUCCESS) break;
        }
        if (status == ERROR_SUCCESS)
        {
            const DWORD level = RPC_C_AUTHN_LEVEL_PKT_PRIVACY;
            status = RegSetValueExW(key, L"AuthenticationLevel", 0, REG_DWORD,
                reinterpret_cast<const BYTE*>(&level), sizeof(level));
        }
        RegCloseKey(key);
        if (status != ERROR_SUCCESS) return static_cast<int>(status);
    }
    if (!isUnregister)
    {
        HKEY key = NULL;
        LSTATUS status = RegOpenKeyExW(HKEY_LOCAL_MACHINE,
            L"SOFTWARE\\Classes\\AppID\\{CC7D4C09-B26B-45B3-B482-2DECA09B832A}",
            0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key);
        if (status != ERROR_SUCCESS) return static_cast<int>(status);
        DWORD type = 0, level = 0, size = sizeof(level);
        status = RegQueryValueExW(key, L"AuthenticationLevel", NULL, &type,
            reinterpret_cast<BYTE*>(&level), &size);
        RegCloseKey(key);
        if (status != ERROR_SUCCESS || type != REG_DWORD || size != sizeof(level) ||
            level != RPC_C_AUTHN_LEVEL_PKT_PRIVACY) return ERROR_ACCESS_DENIED;
    }
    COpcComModule* pModule = new COpcComModule(
        g_tsVendorName, g_tsApplicationName, g_tsApplicationDescription,
        g_cAppID, g_pClassTable, g_pCategoryTable);
    DWORD dwResult = OpcWinMain(pModule, hInstance, lpCmdLine);
    delete pModule;
    return dwResult;
'@ -replace "`r?`n", $newline
$text = $text.Substring(0, $at) + $replacement + $text.Substring($at + $needle.Length)
[IO.File]::WriteAllText($path, $text, $utf8)
[IO.File]::WriteAllText($shared, $sharedText, $utf8)
Write-Output ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())
