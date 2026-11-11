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
foreach ($path in @($vcvars, $source, $testSource)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or $path.IndexOfAny($badChars) -ge 0 -or $path -cmatch '[^\x20-\x7e]') { throw 'Missing or unsupported probe build input.' }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$command = Join-Path $output 'build.cmd'
$dll = Join-Path $output 'BoundaryProbe.dll'
$obj = Join-Path $output 'BoundaryProbe.obj'
$test = Join-Path $output 'BoundaryProbeSelfTest.exe'
$testObj = Join-Path $output 'BoundaryProbeSelfTest.obj'
$content = @"
@echo off
call "$vcvars" amd64 >nul
if errorlevel 1 exit /b 1
cl.exe /nologo /EHsc /O2 /W4 /WX /LD /DUNICODE /D_UNICODE /Fo:"$obj" /Fe:"$dll" "$source" /link /INCREMENTAL:NO advapi32.lib msi.lib
if errorlevel 1 exit /b 1
cl.exe /nologo /EHsc /O2 /W4 /WX /DUNICODE /D_UNICODE /Fo:"$testObj" /Fe:"$test" "$testSource" /link /INCREMENTAL:NO advapi32.lib msi.lib
if errorlevel 1 exit /b 1
"$test"
exit /b %errorlevel%
"@
[IO.File]::WriteAllText($command, $content, [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $command
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Release boundary probe compilation or process-local API test failed.' }
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$dll.sha256", "$hash  BoundaryProbe.dll`n", [Text.Encoding]::ASCII)
Write-Output "TEST ONLY boundary probe: $dll SHA256 $hash"
