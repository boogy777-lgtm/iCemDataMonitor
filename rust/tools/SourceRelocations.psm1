Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BaselineMetadata.psm1')

function Read-SourceRelocations([string] $Path, [string[]] $BaselinePaths, [string[]] $DeletedPaths) {
    $Text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true))
    $Document = [System.Text.Json.JsonDocument]::Parse($Text)
    try { Assert-UniqueJsonKeys $Document.RootElement 'Duplicate source-relocation JSON key' } finally { $Document.Dispose() }
    $Map = ConvertFrom-Json -InputObject $Text -AsHashtable
    Assert-ExactFields $Map @('schema_version', 'relocations') 'Unknown or missing source-relocation fields'
    if ($Map.schema_version -isnot [long] -or $Map.schema_version -ne 1 -or $Map.relocations -isnot [array]) {
        throw 'Invalid source-relocation schema'
    }
    $Sources = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $Destinations = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $Known = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($Source in $BaselinePaths) { if (-not $Known.Add($Source)) { throw "Duplicate baseline path: $Source" } }
    $Result = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($Entry in $Map.relocations) {
        Assert-ExactFields $Entry @('source_path', 'destination_path') 'Unknown or missing source-relocation fields'
        $Source = $Entry.source_path
        $Destination = $Entry.destination_path
        if (-not (Test-CanonicalPath $Source) -or -not (Test-CanonicalPath $Destination)) { throw 'Noncanonical source-relocation path' }
        if (-not $Known.Contains($Source)) { throw "Unknown relocation source: $Source" }
        if (-not $Sources.Add($Source)) { throw "Duplicate relocation source: $Source" }
        if ($Source -ceq $Destination) { throw "Identity is not a relocation: $Source" }
        if (-not $Destinations.Add($Destination)) { throw "Colliding/case-alias relocation destination: $Destination" }
        $Result.Add($Source, $Destination)
    }
    $Deleted = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($Source in $DeletedPaths) { $null = $Deleted.Add($Source) }
    $PhysicalOwners = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($Source in $BaselinePaths) {
        if ($Deleted.Contains($Source)) { continue }
        $Physical = if ($Result.ContainsKey($Source)) { $Result[$Source] } else { $Source }
        if (-not $PhysicalOwners.TryAdd($Physical, $Source)) {
            throw "Colliding effective source paths: $Source and $($PhysicalOwners[$Physical]) -> $Physical"
        }
    }
    foreach ($Physical in $PhysicalOwners.Keys) {
        $Parts = $Physical.Split('/')
        for ($Length = 1; $Length -lt $Parts.Count; $Length++) {
            $Ancestor = ($Parts[0..($Length - 1)] -join '/')
            if ($PhysicalOwners.ContainsKey($Ancestor)) {
                throw "Effective source file is also a path ancestor: $($PhysicalOwners[$Ancestor]) and $($PhysicalOwners[$Physical])"
            }
        }
    }
    return ,$Result
}

function Assert-NoRelocationLinks([string] $Root, [string] $Relative) {
    $Current = $Root
    $Parts = $Relative.Split('/')
    for ($Index = 0; $Index -lt $Parts.Count; $Index++) {
        $Current = Join-Path $Current $Parts[$Index]
        try { $Entry = Get-Item -Force -LiteralPath $Current -ErrorAction Stop }
        catch [System.Management.Automation.ItemNotFoundException] { return }
        if ($null -ne $Entry -and (($Entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $Entry.LinkType)) {
            throw "Source relocation path contains a link/reparse point: $Relative"
        }
    }
}

Export-ModuleMember -Function Read-SourceRelocations, Assert-NoRelocationLinks
