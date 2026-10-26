#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LegacyAddin.psm1')

function Join-PayloadPath([string] $Root, [string] $Relative) {
    $Parts = $Relative -split '[\\/]'
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains(':') -or @($Parts | Where-Object { $_ -in @('', '.', '..') }).Count) {
        throw "Expected a relative payload path: $Relative"
    }
    Join-Path $Root ($Parts -join [IO.Path]::DirectorySeparatorChar)
}
function Assert-PeX64([string] $Path) {
    $Reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($Reader.BaseStream.Length -lt 64 -or $Reader.ReadUInt16() -ne 0x5A4D) { throw "Not a Windows PE: $Path" }
        $Reader.BaseStream.Position = 0x3C
        $Offset = $Reader.ReadUInt32()
        if ($Offset -lt 64 -or $Offset -gt 1048576 -or $Offset -gt $Reader.BaseStream.Length - 26) { throw "Invalid PE header: $Path" }
        $Reader.BaseStream.Position = $Offset
        if ($Reader.ReadUInt32() -ne 0x00004550 -or $Reader.ReadUInt16() -ne 0x8664) { throw "Expected Windows AMD64 PE: $Path" }
        $Reader.BaseStream.Position = $Offset + 24
        if ($Reader.ReadUInt16() -ne 0x20B) { throw "Expected PE32+ executable: $Path" }
    } finally { $Reader.Dispose() }
}
function Read-WindowsPayloadPlan([string] $RepositoryRoot) {
    $Plan = Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot 'src/rust/installer/windows-payload.json') | ConvertFrom-Json
    if ($Plan.schema_version -ne 2 -or $Plan.target -ne 'x86_64-pc-windows-msvc' -or $Plan.addin_directory -cne 'addin' -or
        (@($Plan.binaries.role | Sort-Object) -join ',') -cne 'emulator,host,opc-da-sidecar,shell') {
        throw 'Invalid Windows payload schema/target/binaries'
    }
    return $Plan
}
function Test-WindowsPayload([string] $Directory) {
    $ManifestName = 'release-manifest.json'
    $Manifest = Get-Content -Raw -LiteralPath (Join-Path $Directory $ManifestName) | ConvertFrom-Json
    if ($Manifest.schema_version -ne 2 -or $Manifest.target -ne 'x86_64-pc-windows-msvc' -or $Manifest.readiness -ne 'development' -or $Manifest.addin_directory -cne 'addin') {
        throw 'Invalid release manifest'
    }
    $Expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($Entry in $Manifest.files) {
        $Path = Join-PayloadPath $Directory $Entry.path
        if (-not $Expected.Add($Entry.path) -or $Entry.path -ieq $ManifestName) { throw "Duplicate/reserved payload path: $($Entry.path)" }
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing payload file: $($Entry.path)" }
        if ((Get-Item -LiteralPath $Path).Length -ne $Entry.bytes -or
            (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Entry.sha256) { throw "Payload hash/size mismatch: $($Entry.path)" }
    }
    $Actual = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force | Where-Object {
        [IO.Path]::GetRelativePath($Directory, $_.FullName) -cne $ManifestName
    })
    if ($Actual.Count -ne $Expected.Count) { throw 'Unexpected payload file count' }
    foreach ($File in $Actual) {
        $Relative = [IO.Path]::GetRelativePath($Directory, $File.FullName).Replace('\', '/')
        if (-not $Expected.Contains($Relative)) { throw "Unexpected payload file: $Relative" }
    }
    foreach ($Binary in $Manifest.binaries) {
        if (-not $Expected.Contains($Binary.destination)) { throw "Unlisted executable: $($Binary.destination)" }
        Assert-PeX64 (Join-PayloadPath $Directory $Binary.destination)
        if ($Binary.role -in @('host', 'emulator')) {
            $null = Join-PayloadPath $Directory $Binary.templates_directory
            if (-not $Expected.Contains("$($Binary.templates_directory)/variable_registry.json")) { throw "Missing role registry: $($Binary.role)" }
        }
    }
    $null = Test-LegacyAddinBundle (Join-Path $Directory $Manifest.addin_directory)
    return $Manifest
}
function New-WindowsArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $RepositoryRoot,
        [Parameter(Mandatory)] [string] $BinaryDirectory,
        [Parameter(Mandatory)] [string] $AddinDirectory,
        [Parameter(Mandatory)] [string] $OutputDirectory,
        [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')] [string] $Version,
        [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $Commit,
        [Parameter(Mandatory)] [string] $Compiler
    )
    $RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    $Plan = Read-WindowsPayloadPlan $RepositoryRoot
    $null = Test-LegacyAddinBundle $AddinDirectory $RepositoryRoot
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $RepositoryRoot)
    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    $Name = "iCemDataMonitor-rust-dev-$Version-windows-x64.zip"
    $Archive = Join-Path $OutputDirectory $Name
    $Checksum = "$Archive.sha256"
    if ((Test-Path -LiteralPath $Archive) -or (Test-Path -LiteralPath $Checksum)) { throw "Release already exists: $Archive" }
    $Work = Join-Path $OutputDirectory ('.package-' + [guid]::NewGuid().ToString('N'))
    $Stage = Join-Path $Work 'payload'
    [IO.Directory]::CreateDirectory($Stage) | Out-Null
    try {
        $Entries = @($Plan.files)
        foreach ($AddinFileName in @('Burservis.iCem.DataMonitor.dll', 'addin-manifest.json')) {
            $Entries += [pscustomobject]@{ source = (Join-Path $AddinDirectory $AddinFileName); destination = "$($Plan.addin_directory)/$AddinFileName"; binary = $true }
        }
        foreach ($Directory in $Plan.directories) {
            $Source = Join-PayloadPath $RepositoryRoot $Directory.source
            $Files = @(Get-ChildItem -LiteralPath $Source -File -Filter $Directory.pattern | Sort-Object Name)
            if ($Files.Count -eq 0) { throw "Empty payload directory: $Source" }
            foreach ($File in $Files) {
                $Entries += [pscustomobject]@{ source = "$($Directory.source)/$($File.Name)"; destination = "$($Directory.destination)/$($File.Name)" }
            }
        }
        foreach ($Binary in $Plan.binaries) {
            $Source = Join-Path $BinaryDirectory ($Binary.bin + '.exe')
            Assert-PeX64 $Source
            $Entries += [pscustomobject]@{ source = $Source; destination = $Binary.destination; binary = $true }
        }
        $Destinations = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($Entry in $Entries) {
            $Destination = Join-PayloadPath $Stage $Entry.destination
            if (-not $Destinations.Add($Entry.destination) -or $Entry.destination -ieq 'release-manifest.json') { throw "Duplicate/reserved payload path: $($Entry.destination)" }
            $Source = if ($Entry.PSObject.Properties['binary']) { $Entry.source } else { Join-PayloadPath $RepositoryRoot $Entry.source }
            if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Missing source: $Source" }
            [IO.Directory]::CreateDirectory((Split-Path $Destination -Parent)) | Out-Null
            Copy-Item -LiteralPath $Source -Destination $Destination
        }
        $Files = @(Get-ChildItem -LiteralPath $Stage -Recurse -File | Sort-Object FullName | ForEach-Object {
            [ordered]@{ path = [IO.Path]::GetRelativePath($Stage, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
        $Manifest = [ordered]@{
            schema_version = 2; artifact_version = $Version; source_commit = $Commit; compiler = $Compiler
            addin_directory = $Plan.addin_directory
            target = $Plan.target; profile = 'release'; crt = 'static'; readiness = 'development'
            binaries = $Plan.binaries; files = $Files
            note = 'File hashes exclude this manifest. Hardware/UI/MSI parity and Authenticode signing are separate acceptance gates.'
        }
        [IO.File]::WriteAllText((Join-Path $Stage 'release-manifest.json'), ($Manifest | ConvertTo-Json -Depth 8))
        $null = Test-WindowsPayload $Stage
        $Zip = Join-Path $Work $Name
        [IO.Compression.ZipFile]::CreateFromDirectory($Stage, $Zip)
        $Extracted = Join-Path $Work 'verify'
        [IO.Compression.ZipFile]::ExtractToDirectory($Zip, $Extracted)
        $null = Test-WindowsPayload $Extracted
        $Hash = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText((Join-Path $Work 'checksum'), "$Hash  $Name`n")
        [IO.File]::Move($Zip, $Archive) # no overwrite; publish only after roundtrip validation
        [IO.File]::Move((Join-Path $Work 'checksum'), $Checksum)
        [pscustomobject]@{ archive = $Archive; checksum = $Checksum; sha256 = $Hash; bytes = (Get-Item -LiteralPath $Archive).Length }
    } finally { if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force } }
}
Export-ModuleMember -Function Read-WindowsPayloadPlan, New-WindowsArchive, Test-WindowsPayload
