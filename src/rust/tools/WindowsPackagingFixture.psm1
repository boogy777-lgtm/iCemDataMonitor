#requires -Version 7.4
# Installer-contract fixtures only. These DLL/PE files never enter a release build.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function New-WindowsPackagingFixture([string] $Directory) {
    $Root = Split-Path $PSScriptRoot -Parent
    $SourceRoot = Split-Path (Split-Path $Root -Parent) -Parent
    Import-Module (Join-Path $Root 'installer/LegacyAddin.psm1')
    $Repo = Join-Path $Directory 'repo'
    $Bins = Join-Path $Directory 'bins'
    $Bundle = Join-Path $Directory 'addin'
    [IO.Directory]::CreateDirectory($Bins) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $Repo 'src/rust/installer')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $Root 'installer/windows-payload.json') -Destination (Join-Path $Repo 'src/rust/installer/windows-payload.json')
    [IO.Directory]::CreateDirectory((Join-Path $Repo 'installer/assets')) | Out-Null
    foreach ($Name in @('gen-payload.ps1', 'gen-ui-bmps.ps1', 'assets/Burservis_logo_cyr_red.png', 'assets/app-icon.ico')) {
        Copy-Item -LiteralPath (Join-Path $SourceRoot "installer/$Name") -Destination (Join-Path $Repo "installer/$Name")
    }
    $Plan = Get-Content -Raw -LiteralPath (Join-Path $Root 'installer/windows-payload.json') | ConvertFrom-Json
    foreach ($File in $Plan.files) {
        $Target = Join-Path $Repo $File.source
        [IO.Directory]::CreateDirectory((Split-Path $Target -Parent)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $SourceRoot $File.source) -Destination $Target -Force
    }
    foreach ($Folder in $Plan.directories) {
        $Target = Join-Path $Repo $Folder.source
        [IO.Directory]::CreateDirectory($Target) | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $SourceRoot $Folder.source) -File -Filter $Folder.pattern | Copy-Item -Destination $Target
    }
    $Project = Join-Path $Repo 'src/net/Burservis.iCem.DataMonitor'
    [IO.Directory]::CreateDirectory($Project) | Out-Null
    [IO.File]::WriteAllText((Join-Path $Project 'Burservis.iCem.DataMonitor.csproj'), '<Project><ItemGroup><Compile Include="Fixture.cs" /><Reference Include="SYNTHETIC_TEST_ONLY"><HintPath>C:\Program Files\Halliburton\iCem\Bin\fixture.dll</HintPath></Reference></ItemGroup></Project>')
    $Code = '[assembly:System.Runtime.Versioning.TargetFrameworkAttribute(".NETFramework,Version=v4.8")] public class InstallerFixture {}'
    [IO.File]::WriteAllText((Join-Path $Project 'Fixture.cs'), $Code)
    # Roslyn emits valid managed metadata with a fixed assembly identity. This is
    # not a functioning AddIn or evidence of .NET Framework execution compatibility.
    Add-Type -TypeDefinition 'public class IcemInstallerFixtureCompiler {}'
    $Tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Code)
    $References = [Microsoft.CodeAnalysis.MetadataReference[]]@([AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES').Split([IO.Path]::PathSeparator) | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })
    $Options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithPlatform([Microsoft.CodeAnalysis.Platform]::X64)
    $Compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('Burservis.iCem.DataMonitor', [Microsoft.CodeAnalysis.SyntaxTree[]]@($Tree), $References, $Options)
    $Dll = Join-Path $Directory 'fixture.dll'
    $Stream = [IO.File]::Create($Dll)
    try { $Result = $Compilation.Emit($Stream); if (-not $Result.Success) { throw "Fixture compilation failed: $($Result.Diagnostics)" } } finally { $Stream.Dispose() }
    New-LegacyAddinBundle $Repo $Dll $Bundle ('1' * 40) @([pscustomobject]@{ name = 'SYNTHETIC_TEST_ONLY'; sha256 = ('0' * 64) }) 'synthetic installer-contract fixture'
    $Pe = [byte[]]::new(128)
    $Pe[0] = 0x4D; $Pe[1] = 0x5A; $Pe[0x3C] = 64
    $Pe[64] = 0x50; $Pe[65] = 0x45; $Pe[68] = 0x64; $Pe[69] = 0x86
    $Pe[88] = 0x0B; $Pe[89] = 2
    # MSI shortcut creation inspects executable metadata: a 128-byte PE header
    # is only a parser fixture, not a valid Windows shell target. Use an OS test
    # executable on Windows; never run or publish it as a Rust application.
    $BinaryBytes = if ($IsWindows) { [IO.File]::ReadAllBytes((Join-Path $env:SystemRoot 'System32/where.exe')) } else { $Pe }
    foreach ($Binary in $Plan.binaries) { [IO.File]::WriteAllBytes((Join-Path $Bins ($Binary.bin + '.exe')), $BinaryBytes) }
    return [pscustomobject]@{ repo = $Repo; bins = $Bins; addin = $Bundle; plan = $Plan; pe = $Pe }
}
Export-ModuleMember -Function New-WindowsPackagingFixture
