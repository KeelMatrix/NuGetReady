[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$policyPath = Join-Path $PSScriptRoot "package-sensitive-paths.json"
$policy = Get-Content -Raw -LiteralPath $policyPath | ConvertFrom-Json

function Test-SensitivePackagePath {
    param([string]$Path)

    $normalized = $Path.Replace("\", "/").TrimStart("/")
    $lower = $normalized.ToLowerInvariant()
    if (@($policy.pathFragments) | Where-Object { $lower.Contains([string]$_, [StringComparison]::Ordinal) }) { return $true }

    $segments = $lower.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    if (@($policy.pathSegments) | Where-Object { $segments -contains ([string]$_).ToLowerInvariant() }) { return $true }

    $name = [IO.Path]::GetFileName($lower)
    if (@($policy.exactFileNames) | Where-Object { $name -eq ([string]$_).ToLowerInvariant() }) { return $true }
    if (@($policy.fileNamePrefixes) | Where-Object { $name.StartsWith(([string]$_).ToLowerInvariant(), [StringComparison]::Ordinal) }) { return $true }
    if (@($policy.fileNameSuffixes) | Where-Object { $name.EndsWith(([string]$_).ToLowerInvariant(), [StringComparison]::Ordinal) }) { return $true }
    if (@($policy.fileExtensions) | Where-Object { $name.EndsWith(([string]$_).ToLowerInvariant(), [StringComparison]::Ordinal) }) { return $true }
    return $false
}

$root = (Resolve-Path -LiteralPath $ProjectDirectory).Path
$projectFileName = "$(Split-Path -Leaf $root).csproj"
$offenders = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($root, $_.FullName).Replace("\", "/")
        if ($relative -ne $projectFileName -and $relative -notmatch '^(?:bin|obj)(?:/|$)' -and (Test-SensitivePackagePath $relative)) { $relative }
    })

if ($offenders.Count -gt 0) {
    throw "NuGetReady refuses to pack sensitive local input: $($offenders -join ', ')"
}
