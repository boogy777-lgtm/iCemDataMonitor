Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Copy primitive fields only; no record/view COM reference escapes this function.
function Read-WindowsMsiRecords([object] $Database, [string] $Query, [string[]] $Columns) {
    if ($Columns.Count -lt 1 -or $Columns.Count -gt 16 -or @($Columns | Sort-Object -Unique).Count -ne $Columns.Count) { throw 'Invalid MSI record projection' }
    $View = $null
    $Executed = $false
    try {
        $View = $Database.OpenView($Query)
        $null = $View.Execute()
        $Executed = $true
        while ($Record = $View.Fetch()) {
            try {
                $Fields = [ordered]@{}
                for ($Index = 0; $Index -lt $Columns.Count; $Index++) { $Fields[$Columns[$Index]] = $Record.StringData($Index + 1) }
                [pscustomobject]$Fields
            } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($Record) }
        }
    } finally {
        if ($null -ne $View) {
            try { if ($Executed) { $null = $View.Close() } }
            finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($View) }
        }
    }
}
Export-ModuleMember -Function Read-WindowsMsiRecords
