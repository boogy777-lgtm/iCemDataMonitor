#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'LegacyAddin.psm1') -Force
$Temp = Join-Path ([IO.Path]::GetTempPath()) ('icem-addin-input-tests-' + [guid]::NewGuid().ToString('N'))
$Repo = Join-Path $Temp 'repo'
$ProjectDirectory = Join-Path $Repo 'shared/icem-addin/src'
$Project = Join-Path $ProjectDirectory 'Burservis.iCem.DataMonitor.csproj'
function Write-TestProject([string[]] $Includes) {
    $Xml = [xml]'<Project><ItemGroup /></Project>'
    foreach ($Include in $Includes) {
        $Node = $Xml.CreateElement('Compile')
        $Node.SetAttribute('Include', $Include)
        $null = $Xml.DocumentElement.FirstChild.AppendChild($Node)
    }
    $Xml.Save($Project)
}
try {
    [IO.Directory]::CreateDirectory($ProjectDirectory) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $Repo 'shared')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $Temp 'repo-sibling')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $ProjectDirectory 'Local.cs'), '// local')
    [IO.File]::WriteAllText((Join-Path $Repo 'shared/Linked.cs'), "// linked`r`n")
    [IO.File]::WriteAllText((Join-Path $Temp 'outside.cs'), '// outside')
    [IO.File]::WriteAllText((Join-Path $Temp 'repo-sibling/Outside.cs'), '// outside sibling')
    Write-TestProject @('Local.cs', '..\..\..\shared\Linked.cs', '../../../shared/./Linked.cs')
    $Inputs = @(Get-LegacyAddinInputs $Repo)
    $Linked = @($Inputs | Where-Object path -CEQ 'shared/Linked.cs')
    $ExpectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("// linked`n"))).ToLowerInvariant()
    if ($Inputs.Count -ne 3 -or $Linked.Count -ne 1 -or $Linked[0].sha256 -cne $ExpectedHash) { throw 'Linked inputs must be canonical, deduplicated and hashed' }
    [IO.File]::WriteAllText((Join-Path $Repo 'shared/Linked.cs'), '// changed')
    $Changed = @(Get-LegacyAddinInputs $Repo | Where-Object path -CEQ 'shared/Linked.cs')
    if ($Changed[0].sha256 -ceq $ExpectedHash) { throw 'Linked source changes must change provenance' }
    foreach ($Include in @('..\..\..\..\outside.cs', '../../../../outside.cs', '../../../../repo-sibling/Outside.cs')) {
        Write-TestProject @($Include)
        $Rejected = $false
        try { Get-LegacyAddinInputs $Repo | Out-Null } catch {
            if ($_.Exception.Message -notlike '*compile path escapes repository*') { throw }
            $Rejected = $true
        }
        if (-not $Rejected) { throw "Escaping Compile input accepted: $Include" }
    }
    Write-Host 'AddIn input path/provenance tests passed.'
} finally {
    if (Test-Path -LiteralPath $Temp) { Remove-Item -LiteralPath $Temp -Recurse -Force }
}
