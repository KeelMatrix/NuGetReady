[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDirectory,

    [Parameter(Mandatory = $true)]
    [string]$PackItemsFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "package-sensitive-path-policy.ps1")

$root = (Resolve-Path -LiteralPath $ProjectDirectory).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $root "..\..")).Path
$itemsPath = (Resolve-Path -LiteralPath $PackItemsFile).Path
$offenders = @()
$items = @()

foreach ($line in Get-Content -LiteralPath $itemsPath) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split '\|', 2
    if ($parts.Count -ne 2) { throw "NuGetReady received a malformed evaluated pack-item record." }

    $source = [IO.Path]::GetFullPath($parts[0])
    $destination = $parts[1].Replace("\", "/").TrimStart("/")
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "NuGetReady received an evaluated pack input that does not exist: $source"
    }

    $sourceRelative = [IO.Path]::GetRelativePath($repositoryRoot, $source).Replace("\", "/")
    if ($sourceRelative -like "../*" -or $sourceRelative -eq "..") {
        throw "NuGetReady refuses to pack an input outside the repository boundary: $source"
    }

    if ([string]::IsNullOrWhiteSpace($destination)) {
        $destination = [IO.Path]::GetFileName($source)
    }

    $items += [pscustomobject]@{
        SourceRelative = $sourceRelative
        Destination = $destination
    }
}

$siblings = @($items | ForEach-Object { $_.SourceRelative })
foreach ($item in $items) {
    if ((Test-SensitivePackSourcePath $item.SourceRelative -SiblingPaths $siblings) -or
        (Test-SensitivePackagePath $item.Destination -SiblingPaths @($items | ForEach-Object { $_.Destination }))) {
        $offenders += "$($item.SourceRelative) -> $($item.Destination)"
    }
}

if ($offenders.Count -gt 0) {
    throw "NuGetReady refuses to pack sensitive local input: $($offenders -join ', ')"
}
