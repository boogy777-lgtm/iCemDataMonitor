#requires -Version 7.4
# File-only structural test plus a self-contained in-process ATL fixture.
# Never activates OPCEnum, loads a system OPC DLL or changes registration.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $UpstreamSource,
    [string] $Generator = 'Visual Studio 17 2022',
    [switch] $KeepWork
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $UpstreamSource).Path
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine '00750842910e8f1fc0b793d1109bfc3b04434150773fe82699773796701b2daa') { throw 'Unexpected OpcSeLst.cpp input.' }
$before = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$work = Join-Path ([IO.Path]::GetTempPath()) ('icem-opc-enum-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
$copy = Join-Path $work 'OpcSeLst.cpp'
Copy-Item -LiteralPath $source -Destination $copy
$script:checks = 0
try {
    $patched = & (Join-Path $PSScriptRoot 'Apply-OpcEnumEnumeration.ps1') -SourceFile $copy | Select-Object -Last 1
    if ($patched -cne '2335e3332a44acd2f94a6b0d8f3a47ab714863d1a674e9b972591b769057895b') { throw "Unexpected patched enumeration source: $patched" }
    $text = [IO.File]::ReadAllText($copy)
    if ([regex]::Matches($text, 'IcemCreateGuidEnum<').Count -ne 2 -or
        [regex]::Matches($text, 'AtlFlagTakeOwnership').Count -ne 1 -or
        [regex]::Matches($text, 'EnumType::CreateInstance').Count -ne 1 -or
        $text -match 'new std::vector<GUID>|delete newEnum|new Enum(GUID|CLSID)|->Init\(NULL, NULL' -or
        $text -notmatch 'return IcemCreateGuidEnum<IEnumCLSID, &IID_IEnumCLSID, CLSID>\(guids,' -or
        $text -notmatch 'return IcemCreateGuidEnum<IOPCEnumGUID, &IID_IOPCEnumGUID, GUID>\(guids,') {
        throw 'PATTERN_GUARD: enumeration ownership/empty-set patch is incomplete or a legacy mechanism returned.'
    }
    $script:checks++
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Apply-OpcEnumEnumeration.ps1') -SourceFile $copy | Out-Null }
    catch {
        if ($_.Exception.Message -cne 'OpcSeLst.cpp differs from reviewed upstream input.') { throw }
        $rejected = $true
    }
    if (-not $rejected -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $before) { throw 'Repeat-application or source-integrity guard failed.' }
    $script:checks++

    # Compile the exact helper bytes from the patched file in an isolated object.
    $start = $text.IndexOf('// iCem: ATL CComEnum returns E_FAIL', [StringComparison]::Ordinal)
    $end = $text.IndexOf('#define MIDL_DEFINE_GUID', $start, [StringComparison]::Ordinal)
    if ($start -lt 0 -or $end -le $start) { throw 'Missing helper scope for the behavioral fixture.' }
    $helper = $text.Substring($start, $end - $start)
    $fixture = @'
#include <windows.h>
#include <atlbase.h>
#include <atlcom.h>
#include <vector>
#include <new>
#include <cstdio>

// CComObject locks its ATL module; the real server owns CServiceModule here.
class TestAtlModule : public CAtlModule
{
public:
    HRESULT AddCommonRGSReplacements(IRegistrarBase*) noexcept override { return S_OK; }
};
TestAtlModule g_atlModule;

static const IID IID_IOPCEnumGUID_Test =
    {0x55C382C8,0x21C7,0x4E88,{0x96,0xC1,0xBE,0xCF,0xB1,0xE3,0xF4,0x83}};

MIDL_INTERFACE("55C382C8-21C7-4E88-96C1-BECFB1E3F483") IOPCEnumGUID_Test : public IUnknown
{
public:
    virtual HRESULT STDMETHODCALLTYPE Next(ULONG celt, GUID* items, ULONG* fetched) = 0;
    virtual HRESULT STDMETHODCALLTYPE Skip(ULONG celt) = 0;
    virtual HRESULT STDMETHODCALLTYPE Reset() = 0;
    virtual HRESULT STDMETHODCALLTYPE Clone(IOPCEnumGUID_Test** clone) = 0;
};

HELPER_HERE

static int Fail(const char* message)
{
    std::fprintf(stderr, "FAIL %s\n", message);
    return 1;
}

static HRESULT Create(const std::vector<GUID>& source, IOPCEnumGUID_Test** result)
{
    IUnknown* unknown = NULL;
    HRESULT hr = IcemCreateGuidEnum<IOPCEnumGUID_Test, &IID_IOPCEnumGUID_Test, GUID>(source, &unknown);
    if (FAILED(hr))
        return hr;
    hr = unknown->QueryInterface(IID_IOPCEnumGUID_Test, reinterpret_cast<void**>(result));
    unknown->Release();
    return hr;
}

int main()
{
    GUID first = {0x11111111,0x1111,0x1111,{1,1,1,1,1,1,1,1}};
    GUID second = {0x22222222,0x2222,0x2222,{2,2,2,2,2,2,2,2}};
    IOPCEnumGUID_Test* enumerator = NULL;
    IOPCEnumGUID_Test* clone = NULL;
    GUID item = {};
    ULONG fetched = 99;

    if (FAILED(Create(std::vector<GUID>(), &enumerator)) || enumerator == NULL)
        return Fail("empty snapshot did not create an enumerator");
    HRESULT hr = enumerator->Next(1, &item, &fetched);
    if (hr != S_FALSE || fetched != 0)
        return Fail("empty snapshot must be S_FALSE/0");
    enumerator->Release();

    std::vector<GUID> source;
    source.push_back(first);
    source.push_back(second);
    if (FAILED(Create(source, &enumerator)) || enumerator == NULL)
        return Fail("nonempty snapshot did not create an enumerator");
    hr = enumerator->Next(1, &item, &fetched);
    if (hr != S_OK || fetched != 1 || !IsEqualGUID(item, first))
        return Fail("first GUID mismatch");
    hr = enumerator->Skip(1);
    if (hr != S_OK)
        return Fail("skip failed");
    hr = enumerator->Next(1, &item, &fetched);
    if (hr != S_FALSE || fetched != 0)
        return Fail("past-the-end must be S_FALSE/0");
    hr = enumerator->Reset();
    if (hr != S_OK)
        return Fail("reset failed");
    hr = enumerator->Clone(&clone);
    if (hr != S_OK || clone == NULL)
        return Fail("clone failed");
    hr = clone->Next(1, &item, &fetched);
    if (hr != S_OK || fetched != 1 || !IsEqualGUID(item, first))
        return Fail("clone must enumerate independently from its start");
    hr = enumerator->Next(1, &item, &fetched);
    if (hr != S_OK || fetched != 1 || !IsEqualGUID(item, first))
        return Fail("reset must restart the original enumerator");
    clone->Release();
    enumerator->Release();
    std::printf("PASS: exact adapter bytes preserve empty S_FALSE/0, ordered GUIDs, Skip, Reset and independent Clone.\n");
    return 0;
}
'@
    $fixture = $fixture.Replace('HELPER_HERE', $helper)
    [IO.File]::WriteAllText((Join-Path $work 'OpcEnumAdapterTests.cpp'), $fixture, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $work 'CMakeLists.txt'), @'
cmake_minimum_required(VERSION 3.20)
project(icem_opc_enum_adapter_test CXX)
add_executable(enum-adapter-test OpcEnumAdapterTests.cpp)
target_compile_features(enum-adapter-test PRIVATE cxx_std_17)
'@, [Text.UTF8Encoding]::new($false))
    & cmake -S $work -B (Join-Path $work 'build') -G $Generator -A x64 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the isolated enum-adapter fixture.' }
    & cmake --build (Join-Path $work 'build') --config Release | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile the isolated enum-adapter fixture.' }
    & (Join-Path $work 'build/Release/enum-adapter-test.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated enum-adapter behavior failed.' }
    $script:checks++
    Write-Output "PASS: $script:checks enumeration patch, ownership and empty-set behavior checks; no OPC activation."
} finally {
    if ($KeepWork) { Write-Output "Fixture retained for diagnosis: $work" }
    else {
        # Delete only the unique fixture directory created above.
        foreach ($file in Get-ChildItem -LiteralPath $work -File -Recurse) { [IO.File]::Delete($file.FullName) }
        foreach ($directory in Get-ChildItem -LiteralPath $work -Directory -Recurse | Sort-Object FullName -Descending) { [IO.Directory]::Delete($directory.FullName) }
        [IO.Directory]::Delete($work)
    }
}
