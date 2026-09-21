Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$packageSensitivePathPolicy = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "package-sensitive-paths.json") | ConvertFrom-Json

function Get-FamilyValues {
    param([string]$Source)

    switch ($Source) {
        "exactFileNames" { return @($packageSensitivePathPolicy.exactFileNames) }
        "fileNamePrefixes" { return @($packageSensitivePathPolicy.fileNamePrefixes) }
        "fileNameSuffixes" { return @($packageSensitivePathPolicy.fileNameSuffixes) }
        "fileExtensions" { return @($packageSensitivePathPolicy.fileExtensions) }
        "fileNameFragments" { return @($packageSensitivePathPolicy.fileNameFragments) }
        "pathSegments" { return @($packageSensitivePathPolicy.pathSegments) }
        default { throw "Unsupported package-sensitive family rule source: $Source" }
    }
}

function Test-ExtensionFamily {
    param(
        [string]$Segment,
        [string]$Value
    )

    $start = $Segment.IndexOf($Value, [StringComparison]::Ordinal)
    while ($start -ge 0) {
        $end = $start + $Value.Length
        if ($end -eq $Segment.Length -or $Segment[$end] -eq '.') { return $true }

        $nextStart = $end
        $remaining = $Segment.Length - $nextStart
        $next = $Segment.Substring($nextStart, $remaining)
        $relative = $next.IndexOf($Value, [StringComparison]::Ordinal)
        $start = if ($relative -ge 0) { $nextStart + $relative } else { -1 }
    }

    return $false
}

function Test-SensitivePackagePath {
    param([string]$Path)

    $normalized = $Path.Replace("\", "/").TrimStart("/")
    $lower = $normalized.ToLowerInvariant()
    if (@($packageSensitivePathPolicy.pathFragments) | Where-Object { $lower.Contains([string]$_, [StringComparison]::Ordinal) }) { return $true }

    $segments = $lower.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    if (@($packageSensitivePathPolicy.pathSegments) | Where-Object { $segments -contains ([string]$_).ToLowerInvariant() }) { return $true }

    foreach ($familyRule in @($packageSensitivePathPolicy.familyRules)) {
        if ([string]$familyRule.scope -ne "pathSegments") {
            throw "Unsupported package-sensitive family rule scope: $($familyRule.scope)"
        }

        $values = @(Get-FamilyValues ([string]$familyRule.source) | ForEach-Object { ([string]$_).ToLowerInvariant() })
        foreach ($segment in $segments) {
            if (@($packageSensitivePathPolicy.familyExceptions) | Where-Object { $segment -eq ([string]$_).ToLowerInvariant() }) { continue }

            foreach ($value in $values) {
                if ($familyRule.match -eq "prefix" -and $segment.StartsWith($value, [StringComparison]::Ordinal)) { return $true }
                if ($familyRule.match -eq "extension" -and (Test-ExtensionFamily $segment $value)) { return $true }
                if ($familyRule.match -eq "contains" -and $segment.Contains($value, [StringComparison]::Ordinal)) { return $true }
                if ($familyRule.match -notin @("prefix", "extension", "contains")) {
                    throw "Unsupported package-sensitive family rule match: $($familyRule.match)"
                }
            }
        }
    }

    $name = [IO.Path]::GetFileName($lower)
    if (@($packageSensitivePathPolicy.exactFileNames) | Where-Object { $name -eq ([string]$_).ToLowerInvariant() }) { return $true }
    if (@($packageSensitivePathPolicy.fileNamePrefixes) | Where-Object { $name.StartsWith(([string]$_).ToLowerInvariant(), [StringComparison]::Ordinal) }) { return $true }
    if (@($packageSensitivePathPolicy.fileNameSuffixes) | Where-Object { $name.EndsWith(([string]$_).ToLowerInvariant(), [StringComparison]::Ordinal) }) { return $true }
    if (@($packageSensitivePathPolicy.fileExtensions) | Where-Object { $name.EndsWith(([string]$_).ToLowerInvariant(), [StringComparison]::Ordinal) }) { return $true }
    return $false
}
