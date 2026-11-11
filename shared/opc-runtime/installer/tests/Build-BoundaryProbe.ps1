#requires -Version 7.4
# TEST ONLY. Compiles a read-only native DLL; does not run MSI or touch registry.
[CmdletBinding()]
param([Parameter(Mandatory)][string] $OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Probe build output must not already exist.' }
$badChars = [char[]]@('%', '!', [char]13, [char]10)
if ($output.IndexOfAny($badChars) -ge 0 -or $output -cmatch '[^\x20-\x7e]') { throw 'Unsupported command interpreter character in output path.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or @($vs).Count -ne 1 -or -not $vs) { throw 'A VS2022 C++ toolchain is required.' }
$vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvarsall.bat'
$source = Join-Path $PSScriptRoot 'BoundaryProbe.cpp'
$testSource = Join-Path $PSScriptRoot 'BoundaryProbeSelfTest.cpp'
$rawTestSource = Join-Path $PSScriptRoot 'RawRegistrySnapshotTests.cpp'
$rawHeader = Join-Path $PSScriptRoot '../RawRegistrySnapshot.h'
$descriptorTestSource = Join-Path $PSScriptRoot 'BoundedSecurityDescriptorTests.cpp'
$descriptorHeader = Join-Path $PSScriptRoot '../BoundedSecurityDescriptor.h'
foreach ($path in @($vcvars, $source, $testSource, $rawTestSource, $rawHeader, $descriptorTestSource, $descriptorHeader)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or $path.IndexOfAny($badChars) -ge 0 -or $path -cmatch '[^\x20-\x7e]') { throw 'Missing or unsupported probe build input.' }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$command = Join-Path $output 'build.cmd'
$dll = Join-Path $output 'BoundaryProbe.dll'
$obj = Join-Path $output 'BoundaryProbe.obj'
$test = Join-Path $output 'BoundaryProbeSelfTest.exe'
$testObj = Join-Path $output 'BoundaryProbeSelfTest.obj'
$rawTest = Join-Path $output 'RawRegistrySnapshotTests.exe'
$rawTestObj = Join-Path $output 'RawRegistrySnapshotTests.obj'
$descriptorTest = Join-Path $output 'BoundedSecurityDescriptorTests.exe'
$descriptorTestObj = Join-Path $output 'BoundedSecurityDescriptorTests.obj'
$content = @"
@echo off
call "$vcvars" amd64 >nul
if errorlevel 1 exit /b 1
cl.exe /nologo /EHsc /O2 /W4 /WX /LD /DUNICODE /D_UNICODE /Fo:"$obj" /Fe:"$dll" "$source" /link /INCREMENTAL:NO advapi32.lib msi.lib
if errorlevel 1 exit /b 1
cl.exe /nologo /EHsc /O2 /W4 /WX /DUNICODE /D_UNICODE /Fo:"$testObj" /Fe:"$test" "$testSource" /link /INCREMENTAL:NO advapi32.lib msi.lib
if errorlevel 1 exit /b 1
"$test"
if errorlevel 1 exit /b 1
cl.exe /nologo /EHsc /O2 /W4 /WX /Fo:"$rawTestObj" /Fe:"$rawTest" "$rawTestSource" /link /INCREMENTAL:NO
if errorlevel 1 exit /b 1
"$rawTest"
if errorlevel 1 exit /b 1
cl.exe /nologo /EHsc /O2 /W4 /WX /Fo:"$descriptorTestObj" /Fe:"$descriptorTest" "$descriptorTestSource" /link /INCREMENTAL:NO advapi32.lib
if errorlevel 1 exit /b 1
"$descriptorTest"
exit /b %errorlevel%
"@
[IO.File]::WriteAllText($command, $content, [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $command
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Release boundary probe compilation or process-local API test failed.' }
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$dll.sha256", "$hash  BoundaryProbe.dll`n", [Text.Encoding]::ASCII)
Write-Output "TEST ONLY boundary probe: $dll SHA256 $hash"
