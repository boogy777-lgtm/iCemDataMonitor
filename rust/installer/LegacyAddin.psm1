#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$AssemblyName = 'Burservis.iCem.DataMonitor'
$Framework = '.NETFramework,Version=v4.8'
function Get-LegacyAddinSdkReferences([string] $RepositoryRoot) {
    $Project = [xml](Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot "$AssemblyName/$AssemblyName.csproj"))
    return @($Project.SelectNodes('//*[local-name()="Reference"]') | ForEach-Object {
        $Hint = $_.SelectSingleNode('*[local-name()="HintPath"]')
        if ($Hint -and $Hint.InnerText -like '*Halliburton\iCem\Bin\*') {
            [pscustomobject]@{ name = [string]$_.Include; path = $Hint.InnerText }
        }
    })
}
function Get-LegacyAddinInputs([string] $RepositoryRoot) {
    $ProjectPath = "$AssemblyName/$AssemblyName.csproj"
    $Project = [xml](Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot $ProjectPath))
    $Compile = @($Project.SelectNodes('//*[local-name()="Compile"]'))
    if (-not $Compile.Count) { throw 'AddIn project has no explicit compile inputs' }
    $Paths = @($ProjectPath) + @($Compile | ForEach-Object { "$AssemblyName/$($_.Include.Replace('\', '/'))" })
    foreach ($File in @('Directory.Build.props', 'Directory.Build.targets', 'global.json')) {
        if (Test-Path -LiteralPath (Join-Path $RepositoryRoot $File)) { $Paths += $File }
    }
    return @($Paths | Sort-Object -Unique | ForEach-Object {
        if ($_ -match '(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($_)) { throw 'AddIn compile path escapes project' }
        # Hash canonical text, independent of Windows checkout CRLF conversion.
        $Text = [IO.File]::ReadAllText((Join-Path $RepositoryRoot $_)).Replace("`r`n", "`n")
        [pscustomobject]@{ path = $_; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant() }
    })
}
function Assert-LegacyAddinAssembly([string] $Path) {
    $Stream = [IO.File]::OpenRead($Path)
    $Pe = [Reflection.PortableExecutable.PEReader]::new($Stream)
    try {
        if (-not $Pe.HasMetadata -or $Pe.PEHeaders.CoffHeader.Machine -ne [Reflection.PortableExecutable.Machine]::Amd64 -or -not $Pe.PEHeaders.IsDll) { throw 'AddIn must be an AMD64 managed DLL' }
        $Metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($Pe)
        $Assembly = $Metadata.GetAssemblyDefinition()
        if ($Metadata.GetString($Assembly.Name) -cne $AssemblyName) { throw 'Unexpected AddIn assembly identity' }
        $Matches = 0
        foreach ($Handle in $Assembly.GetCustomAttributes()) {
            $Attribute = $Metadata.GetCustomAttribute($Handle)
            $Token = [Reflection.Metadata.Ecma335.MetadataTokens]::GetToken($Attribute.Constructor)
            if (($Token -band 0xff000000) -ne 0x0a000000) { continue }
            $Member = $Metadata.GetMemberReference([Reflection.Metadata.Ecma335.MetadataTokens]::MemberReferenceHandle($Token -band 0xffffff))
            $Parent = [Reflection.Metadata.Ecma335.MetadataTokens]::GetToken($Member.Parent)
            if (($Parent -band 0xff000000) -ne 0x01000000) { continue }
            $Type = $Metadata.GetTypeReference([Reflection.Metadata.Ecma335.MetadataTokens]::TypeReferenceHandle($Parent -band 0xffffff))
            if ($Metadata.GetString($Type.Namespace) -cne 'System.Runtime.Versioning' -or $Metadata.GetString($Type.Name) -cne 'TargetFrameworkAttribute') { continue }
            $Blob = $Metadata.GetBlobBytes($Attribute.Value)
            $Expected = [Text.Encoding]::UTF8.GetBytes($Framework)
            if ($Blob.Length -ge $Expected.Length + 3 -and $Blob[0] -eq 1 -and $Blob[1] -eq 0 -and $Blob[2] -eq $Expected.Length -and
                [Text.Encoding]::UTF8.GetString($Blob, 3, $Expected.Length) -ceq $Framework) { $Matches++ }
        }
        if ($Matches -ne 1) { throw 'AddIn requires exactly one .NET Framework 4.8 target attribute' }
    } finally { $Pe.Dispose(); $Stream.Dispose() }
}
function Test-LegacyAddinBundle([string] $Directory, [string] $RepositoryRoot) {
    $Manifest = Get-Content -Raw -LiteralPath (Join-Path $Directory 'addin-manifest.json') | ConvertFrom-Json
    if ($Manifest.schema_version -ne 1 -or $Manifest.assembly -cne $AssemblyName -or $Manifest.framework -cne $Framework -or
        $Manifest.source_commit -notmatch '^[0-9a-f]{40}$' -or $Manifest.sources.Count -eq 0 -or $Manifest.sdk_references.Count -eq 0 -or
        [string]::IsNullOrWhiteSpace($Manifest.build_tool)) { throw 'Invalid AddIn provenance manifest' }
    $Names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($Reference in $Manifest.sdk_references) {
        if ([string]::IsNullOrWhiteSpace($Reference.name) -or $Reference.sha256 -notmatch '^[0-9a-f]{64}$' -or -not $Names.Add($Reference.name)) { throw 'Invalid/duplicate AddIn SDK reference provenance' }
    }
    $Dll = Join-Path $Directory "$AssemblyName.dll"
    if ((Get-Item -LiteralPath $Dll).Length -ne $Manifest.bytes -or (Get-FileHash -LiteralPath $Dll).Hash -ine $Manifest.sha256) { throw 'AddIn DLL hash/size mismatch' }
    if (@(Get-ChildItem -LiteralPath $Directory -File -Recurse -Force).Count -ne 2) { throw 'AddIn bundle must contain exactly DLL and provenance manifest; no vendor DLL redistribution' }
    Assert-LegacyAddinAssembly $Dll
    if ($RepositoryRoot) {
        $Sdk = @(Get-LegacyAddinSdkReferences $RepositoryRoot)
        if (-not $Sdk.Count -or $Sdk.Count -ne $Names.Count) { throw 'AddIn SDK inventory mismatch' }
        foreach ($Reference in $Sdk) { if (-not $Names.Contains($Reference.name)) { throw "Missing AddIn SDK provenance: $($Reference.name)" } }
        $Expected = @(Get-LegacyAddinInputs $RepositoryRoot)
        if ($Expected.Count -ne $Manifest.sources.Count) { throw 'AddIn source inventory mismatch' }
        foreach ($InputFile in $Expected) {
            $Entry = @($Manifest.sources | Where-Object path -CEQ $InputFile.path)
            if ($Entry.Count -ne 1 -or $Entry[0].sha256 -cne $InputFile.sha256) { throw "AddIn source mismatch: $($InputFile.path)" }
        }
    }
    return $Manifest
}
function New-LegacyAddinBundle([string] $RepositoryRoot, [string] $Dll, [string] $OutputDirectory,
    [string] $Commit, [object[]] $SdkReferences, [string] $BuildTool) {
    Assert-LegacyAddinAssembly $Dll
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'AddIn bundle output already exists' }
    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    Copy-Item -LiteralPath $Dll -Destination (Join-Path $OutputDirectory "$AssemblyName.dll")
    $Manifest = [ordered]@{
        schema_version = 1; assembly = $AssemblyName; framework = $Framework
        source_commit = $Commit; sources = @(Get-LegacyAddinInputs $RepositoryRoot)
        sdk_references = $SdkReferences; build_tool = $BuildTool
        sha256 = (Get-FileHash -LiteralPath $Dll).Hash.ToLowerInvariant(); bytes = (Get-Item -LiteralPath $Dll).Length
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'addin-manifest.json'), ($Manifest | ConvertTo-Json -Depth 8))
    $null = Test-LegacyAddinBundle $OutputDirectory $RepositoryRoot
}
Export-ModuleMember -Function Get-LegacyAddinInputs, Get-LegacyAddinSdkReferences, Assert-LegacyAddinAssembly, Test-LegacyAddinBundle, New-LegacyAddinBundle
