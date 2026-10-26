param([string] $Path, [string] $Map, [string] $Mutex, [switch] $SmallSection)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition (Get-Content -Raw (Join-Path $PSScriptRoot 'BusProducer.cs'))
[BusProducer]::Run($Path, $Map, $Mutex, $SmallSection.IsPresent)
