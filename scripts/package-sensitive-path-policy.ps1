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
    param(
        [string]$Path,
        [string[]]$SiblingPaths = @()
    )

    $normalized = $Path.Replace("\", "/").TrimStart("/")
    $lower = $normalized.ToLowerInvariant()
    $name = [IO.Path]::GetFileName($lower)
    if ($name.EndsWith(".xml", [StringComparison]::OrdinalIgnoreCase)) {
        $directory = [IO.Path]::GetDirectoryName($lower)
        $stem = $name.Substring(0, $name.Length - 4)
        foreach ($siblingPath in @($SiblingPaths)) {
            $sibling = ([string]$siblingPath).Replace("\", "/").TrimStart("/").ToLowerInvariant()
            if ([IO.Path]::GetDirectoryName($sibling) -eq $directory) {
                $siblingName = [IO.Path]::GetFileName($sibling)
                if (@(".dll", ".exe", ".pdb", ".so", ".dylib") | Where-Object { $siblingName.EndsWith([string]$_, [StringComparison]::OrdinalIgnoreCase) }) {
                    $extension = $siblingName.LastIndexOf('.')
                    if ($extension -gt 0 -and $siblingName.Substring(0, $extension) -eq $stem) { return $false }
                }
            }
        }
    }

    if (@($packageSensitivePathPolicy.pathFragments) | Where-Object { $lower.Contains([string]$_, [StringComparison]::Ordinal) }) { return $true }

    $segments = $lower.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    $directorySegments = if ($segments.Count -gt 1) { $segments[0..($segments.Count - 2)] } else { @() }
    if (@($packageSensitivePathPolicy.pathSegments) | Where-Object { $directorySegments -contains ([string]$_).ToLowerInvariant() }) { return $true }

    foreach ($familyRule in @($packageSensitivePathPolicy.familyRules)) {
        if ([string]$familyRule.scope -ne "pathSegments") {
            throw "Unsupported package-sensitive family rule scope: $($familyRule.scope)"
        }

        $values = @(Get-FamilyValues ([string]$familyRule.source) | ForEach-Object { ([string]$_).ToLowerInvariant() })
        foreach ($segment in $directorySegments) {
            if (@($packageSensitivePathPolicy.familyExceptions) | Where-Object { $segment -eq ([string]$_).ToLowerInvariant() }) { continue }
            $exemptExtensions = @()
            if ($null -ne $familyRule.PSObject.Properties["exemptExtensions"]) {
                $exemptExtensions = @($familyRule.exemptExtensions) | ForEach-Object { ([string]$_).ToLowerInvariant() }
            }
            if ($exemptExtensions | Where-Object { $segment.EndsWith([string]$_, [StringComparison]::Ordinal) }) { continue }

            foreach ($value in $values) {
                if ($familyRule.match -eq "prefix" -and $segment.StartsWith($value, [StringComparison]::Ordinal)) { return $true }
                if ($familyRule.match -eq "extension" -and (Test-ExtensionFamily $segment $value)) { return $true }
                if ($familyRule.match -eq "contains") {
                    $boundary = if ($null -ne $familyRule.PSObject.Properties["boundary"]) { [string]$familyRule.boundary } else { "" }
                    $start = $segment.IndexOf($value, [StringComparison]::Ordinal)
                    while ($start -ge 0) {
                        $end = $start + $value.Length
                        if ($boundary -eq "endOrSeparator" -and ($end -eq $segment.Length -or -not [char]::IsLetterOrDigit($segment[$end]))) { return $true }
                        if ([string]::IsNullOrEmpty($boundary) -or $boundary -eq "none") { return $true }
                        $nextStart = $end
                        $remaining = $segment.Length - $nextStart
                        $next = $segment.Substring($nextStart, $remaining)
                        $relative = $next.IndexOf($value, [StringComparison]::Ordinal)
                        $start = if ($relative -ge 0) { $nextStart + $relative } else { -1 }
                    }
                }
                if ($familyRule.match -notin @("prefix", "extension", "contains")) {
                    throw "Unsupported package-sensitive family rule match: $($familyRule.match)"
                }
            }
        }
    }

    $nameWithoutLeadingDots = $name.TrimStart('.')
    if (@($packageSensitivePathPolicy.exactFileNames) | Where-Object {
            $exact = ([string]$_).ToLowerInvariant()
            $candidate = if ($name.StartsWith($exact, [StringComparison]::Ordinal)) { $name } elseif ($nameWithoutLeadingDots.StartsWith($exact, [StringComparison]::Ordinal)) { $nameWithoutLeadingDots } else { return $false }
            $remainder = $candidate.Substring($exact.Length)
            return $remainder.Length -eq 0 -or $remainder.StartsWith('.') -or
                -not (@('.dll', '.exe', '.pdb', '.so', '.dylib') | Where-Object { $name.EndsWith([string]$_, [StringComparison]::Ordinal) })
        }) { return $true }
    if (@($packageSensitivePathPolicy.fileNamePrefixes) | Where-Object { $name.StartsWith(([string]$_).ToLowerInvariant(), [StringComparison]::Ordinal) }) { return $true }
    if (@($packageSensitivePathPolicy.fileNameSuffixes) | Where-Object { Test-ExtensionFamily $name (([string]$_).ToLowerInvariant()) }) { return $true }
    if (@($packageSensitivePathPolicy.fileExtensions) | Where-Object { Test-ExtensionFamily $name (([string]$_).ToLowerInvariant()) }) { return $true }
    if (@($packageSensitivePathPolicy.fileNameFragments) | Where-Object {
            $isBinaryAssembly = @('.dll', '.exe', '.pdb', '.so', '.dylib') | Where-Object { $name.EndsWith([string]$_, [StringComparison]::Ordinal) }
            if ($isBinaryAssembly) { return $false }
            $fragment = ([string]$_).ToLowerInvariant()
            $start = $name.IndexOf($fragment, [StringComparison]::Ordinal)
            while ($start -ge 0) {
                $end = $start + $fragment.Length
                if ($end -eq $name.Length -or -not [char]::IsLetterOrDigit($name[$end])) { return $true }
                $nextStart = $end
                $remaining = $name.Length - $nextStart
                $next = $name.Substring($nextStart, $remaining)
                $relative = $next.IndexOf($fragment, [StringComparison]::Ordinal)
                $start = if ($relative -ge 0) { $nextStart + $relative } else { -1 }
            }
            return $false
        }) { return $true }
    return $false
}

function Remove-GeneratedOutputSegments {
    param([string]$Path)

    $segments = $Path.Replace("\", "/").TrimStart("/").Split('/', [StringSplitOptions]::RemoveEmptyEntries) |
        Where-Object { $_ -notin @("bin", "obj") }
    return ($segments -join "/")
}

function Test-SensitivePackSourcePath {
    param(
        [string]$Path,
        [string[]]$SiblingPaths = @()
    )

    $normalizedPath = Remove-GeneratedOutputSegments $Path
    $normalizedSiblings = @($SiblingPaths | ForEach-Object { Remove-GeneratedOutputSegments ([string]$_) })
    return Test-SensitivePackagePath $normalizedPath -SiblingPaths $normalizedSiblings
}
