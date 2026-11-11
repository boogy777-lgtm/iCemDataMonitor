# Build-time delivery boundaries; Windows PowerShell 5.1 compatible.
function Test-BuildPathWithin([string] $Path, [string] $Directory) {
    $Path = [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
    $Directory = [IO.Path]::GetFullPath($Directory).TrimEnd([char[]]@('\', '/'))
    return $Path.Equals($Directory, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith(($Directory + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)
}
function Test-BuildPathOverlap([string] $First, [string] $Second) {
    return (Test-BuildPathWithin $First $Second) -or (Test-BuildPathWithin $Second $First)
}
Export-ModuleMember -Function Test-BuildPathWithin, Test-BuildPathOverlap
