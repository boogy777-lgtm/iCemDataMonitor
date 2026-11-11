Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-UniqueJsonKeys([System.Text.Json.JsonElement] $Node, [string] $ErrorMessage) {
    if ($Node.ValueKind -eq 'Object') {
        $Names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($Property in $Node.EnumerateObject()) {
            if (-not $Names.Add($Property.Name)) { throw $ErrorMessage }
            Assert-UniqueJsonKeys $Property.Value $ErrorMessage
        }
    } elseif ($Node.ValueKind -eq 'Array') {
        foreach ($Child in $Node.EnumerateArray()) { Assert-UniqueJsonKeys $Child $ErrorMessage }
    }
}

function Assert-ExactFields($Value, [string[]] $Names, [string] $ErrorMessage) {
    if ($Value -isnot [System.Collections.IDictionary] -or
        $Value.Count -ne $Names.Count -or @($Value.Keys | Where-Object { $_ -cnotin $Names }).Count) {
        throw $ErrorMessage
    }
}

function Test-CanonicalPath($Value) {
    if ($Value -isnot [string] -or $Value.Length -eq 0 -or
        $Value -cmatch '(^/|[\\:\x00-\x1f\x7f<>:"|?*])') { return $false }
    foreach ($Part in $Value.Split('/')) {
        if ($Part -cin @('', '.', '..') -or $Part.EndsWith(' ') -or $Part.EndsWith('.')) { return $false }
    }
    return $true
}

Export-ModuleMember -Function Assert-UniqueJsonKeys, Assert-ExactFields, Test-CanonicalPath
