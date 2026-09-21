[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "package-sensitive-path-policy.ps1")

$root = (Resolve-Path -LiteralPath $ProjectDirectory).Path
$projectFileName = "$(Split-Path -Leaf $root).csproj"
$offenders = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($root, $_.FullName).Replace("\", "/")
        if ($relative -ne $projectFileName -and $relative -notmatch '^(?:bin|obj)(?:/|$)' -and (Test-SensitivePackagePath $relative)) { $relative }
    })

if ($offenders.Count -gt 0) {
    throw "NuGetReady refuses to pack sensitive local input: $($offenders -join ', ')"
}
