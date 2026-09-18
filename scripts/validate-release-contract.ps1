[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,

    [ValidateSet("PreRelease", "Tag")]
    [string]$Mode = "PreRelease",

    [string]$ExpectedVersion,

    [string]$TagVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Fail-Contract {
    param([string]$Message)

    throw "Release contract validation failed: $Message"
}

function Read-RequiredFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail-Contract "Required file is missing: $Path"
    }

    return Get-Content -LiteralPath $Path -Raw
}

function Normalize-Version {
    param(
        [string]$Value,
        [string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        Fail-Contract "$Label is empty."
    }

    $normalized = $Value.Trim()
    if ($normalized.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    if ($normalized -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
        Fail-Contract "$Label '$Value' is not a supported semantic version."
    }

    return $normalized
}

function Normalize-Text {
    param([string]$Value)

    return ([Regex]::Replace($Value.ToLowerInvariant(), "[^a-z0-9]+", " ")).Trim()
}

function Get-PropertyValue {
    param(
        [xml]$Document,
        [string]$Name
    )

    $node = $Document.SelectSingleNode("//*[local-name()='PropertyGroup']/*[local-name()='$Name']")
    if ($null -eq $node) {
        return $null
    }

    return $node.InnerText.Trim()
}

function Get-HeadingSections {
    param([string[]]$Lines)

    $sections = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index] -match '^\s*##\s+\[(?<label>[^\]]+)\](?:\s+-\s*(?<date>\d{4}-\d{2}-\d{2}))?\s*$') {
            $sections.Add([pscustomobject]@{
                    Index = $index
                    Label = $Matches.label.Trim()
                    Date = if ($Matches.ContainsKey("date")) { $Matches.date } else { $null }
                })
        }
    }

    return $sections
}

function Get-SectionLines {
    param(
        [string[]]$Lines,
        [object]$Section,
        [object[]]$Sections
    )

    $next = $Lines.Count
    foreach ($candidate in $Sections) {
        if ($candidate.Index -gt $Section.Index) {
            $next = $candidate.Index
            break
        }
    }

    if ($next -le $Section.Index + 1) {
        return @()
    }

    return @($Lines[($Section.Index + 1)..($next - 1)])
}

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$buildPropsPath = Join-Path $root "Directory.Build.props"
$packagesPropsPath = Join-Path $root "Directory.Packages.props"
$changelogPath = Join-Path $root "CHANGELOG.md"
$configPath = Join-Path $root "nugetready.json"
$readmePaths = @(
    (Join-Path $root "README.md"),
    (Join-Path $root "src/KeelMatrix.NuGetReady/README.md")
)

$buildProps = [xml](Read-RequiredFile $buildPropsPath)
$packageVersionValue = Get-PropertyValue -Document $buildProps -Name "PackageVersion"
$declaredBuildVersion = Get-PropertyValue -Document $buildProps -Name "Version"
if ([string]::IsNullOrWhiteSpace($declaredBuildVersion)) {
    Fail-Contract "Directory.Build.props does not declare Version."
}

$buildVersion = Normalize-Version -Value $declaredBuildVersion -Label "Directory.Build.props Version"
if (-not [string]::IsNullOrWhiteSpace($packageVersionValue) -and $packageVersionValue -notmatch '^\$\(') {
    $packageVersion = Normalize-Version -Value $packageVersionValue -Label "Directory.Build.props PackageVersion"
    if ($packageVersion -ne $buildVersion) {
        Fail-Contract "Directory.Build.props Version '$buildVersion' and PackageVersion '$packageVersion' disagree."
    }
}
else {
    $packageVersion = $buildVersion
}

$targetVersion = if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) { $packageVersion } else { Normalize-Version -Value $ExpectedVersion -Label "Expected version" }
if ($targetVersion -ne $packageVersion) {
    Fail-Contract "Expected version '$targetVersion' does not match package version '$packageVersion'."
}

if ($Mode -eq "Tag") {
    if ([string]::IsNullOrWhiteSpace($TagVersion)) {
        Fail-Contract "Tag mode requires TagVersion."
    }

    $normalizedTagVersion = Normalize-Version -Value $TagVersion -Label "Tag version"
    if ($normalizedTagVersion -ne $targetVersion) {
        Fail-Contract "Tag version '$normalizedTagVersion' does not match package version '$targetVersion'."
    }
}

$shippingProjects = @()
foreach ($projectPath in Get-ChildItem -LiteralPath (Join-Path $root "src") -Filter "*.csproj" -File -Recurse) {
    try {
        $project = [xml](Read-RequiredFile $projectPath.FullName)
    }
    catch {
        Fail-Contract "Packable project metadata is not valid XML: $($projectPath.FullName)."
    }

    $packageId = Get-PropertyValue -Document $project -Name "PackageId"
    $isPackable = Get-PropertyValue -Document $project -Name "IsPackable"
    if ($packageId -eq "KeelMatrix.NuGetReady" -or $isPackable -eq "true") {
        $shippingProjects += [pscustomobject]@{
            Path = $projectPath.FullName
            Document = $project
            PackageId = $packageId
        }
    }
}

$targetProjects = @($shippingProjects | Where-Object { $_.PackageId -eq "KeelMatrix.NuGetReady" })
if ($targetProjects.Count -ne 1) {
    Fail-Contract "Expected exactly one packable KeelMatrix.NuGetReady project; found $($targetProjects.Count)."
}

foreach ($project in $targetProjects) {
    $projectVersion = Get-PropertyValue -Document $project.Document -Name "PackageVersion"
    if (-not [string]::IsNullOrWhiteSpace($projectVersion) -and $projectVersion -notmatch '^\$\(') {
        if ((Normalize-Version -Value $projectVersion -Label "Project PackageVersion") -ne $targetVersion) {
            Fail-Contract "Project package version does not match '$targetVersion'."
        }
    }

    $releaseNotes = Get-PropertyValue -Document $project.Document -Name "PackageReleaseNotes"
    if ([string]::IsNullOrWhiteSpace($releaseNotes)) {
        Fail-Contract "Shipping project package metadata must declare PackageReleaseNotes."
    }
}

$packageVersions = @{}
$packagesProps = [xml](Read-RequiredFile $packagesPropsPath)
foreach ($packageVersionNode in $packagesProps.SelectNodes("//*[local-name()='PackageVersion']")) {
    $id = $packageVersionNode.GetAttribute("Include")
    $version = $packageVersionNode.GetAttribute("Version")
    if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($version)) {
        Fail-Contract "Every central PackageVersion entry must have Include and Version."
    }

    $packageVersions[$id] = $version
}

foreach ($project in $shippingProjects) {
    foreach ($reference in $project.Document.SelectNodes("//*[local-name()='PackageReference']")) {
        $id = $reference.GetAttribute("Include")
        if ([string]::IsNullOrWhiteSpace($id)) {
            continue
        }

        $directVersion = $reference.GetAttribute("Version")
        if (-not $packageVersions.ContainsKey($id) -and [string]::IsNullOrWhiteSpace($directVersion)) {
            Fail-Contract "Dependency '$id' has no centralized or project-local version."
        }

        if ($packageVersions.ContainsKey($id) -and -not [string]::IsNullOrWhiteSpace($directVersion) -and $directVersion -ne $packageVersions[$id]) {
            Fail-Contract "Dependency '$id' has conflicting project and central versions."
        }
    }
}

$config = $null
try {
    $config = (Read-RequiredFile $configPath) | ConvertFrom-Json
}
catch {
    Fail-Contract "nugetready.json is not valid JSON."
}

$configPackages = @($config.packages)
if ($configPackages.Count -ne 1) {
    Fail-Contract "nugetready.json must declare exactly one shipping package."
}

$configPackage = $configPackages[0]
if ($configPackage.id -ne "KeelMatrix.NuGetReady") {
    Fail-Contract "nugetready.json package ID '$($configPackage.id)' does not match KeelMatrix.NuGetReady."
}
if ((Normalize-Version -Value ([string]$configPackage.version) -Label "nugetready.json package version") -ne $targetVersion) {
    Fail-Contract "nugetready.json package version does not match '$targetVersion'."
}

$expectedArtifacts = @(
    "KeelMatrix.NuGetReady.$targetVersion.nupkg",
    "KeelMatrix.NuGetReady.$targetVersion.snupkg"
)
$actualArtifacts = @($configPackage.artifacts | ForEach-Object { [string]$_ })
if (@(Compare-Object ($expectedArtifacts | Sort-Object) ($actualArtifacts | Sort-Object)).Count -ne 0) {
    Fail-Contract "nugetready.json must declare exactly $($expectedArtifacts -join ', ')."
}

$documentation = @()
foreach ($readmePath in $readmePaths) {
    $documentation += Read-RequiredFile $readmePath
}

$installCommands = @($documentation | Select-String -Pattern '(?im)^.*dotnet\s+tool\s+install\b.*$' | ForEach-Object { $_.Line })
if ($installCommands.Count -eq 0) {
    Fail-Contract "No dotnet tool install command was found in the consumer documentation."
}

foreach ($command in $installCommands) {
    if ($command -notmatch '(?i)\bKeelMatrix\.NuGetReady\b') {
        Fail-Contract "Install command does not install KeelMatrix.NuGetReady: $command"
    }

    $versionMatch = [Regex]::Match($command, '(?i)--version\s+(?<version>[0-9A-Za-z.+-]+)')
    if ($versionMatch.Success -and (Normalize-Version -Value $versionMatch.Groups["version"].Value -Label "Install command version") -ne $targetVersion) {
        Fail-Contract "Install command version does not match '$targetVersion': $command"
    }
}

foreach ($dependency in $packageVersions.GetEnumerator()) {
    $versionMatches = @()
    foreach ($text in $documentation) {
        $versionMatches += [Regex]::Matches(
            $text,
            "(?i)(?<![A-Za-z0-9_.-])$([Regex]::Escape($dependency.Key))\s+(?<version>[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?)"
        )
    }

    foreach ($match in $versionMatches) {
        $documentedVersion = Normalize-Version -Value $match.Groups["version"].Value -Label "Documented dependency version"
        $centralVersion = Normalize-Version -Value $dependency.Value.Trim('[', ']') -Label "Central dependency version"
        if ($documentedVersion -ne $centralVersion) {
            Fail-Contract "Documentation version for dependency '$($dependency.Key)' does not match '$($dependency.Value)'."
        }
    }
}

$changelog = Read-RequiredFile $changelogPath
$changelogLines = $changelog -split '\r?\n'
$sections = @(Get-HeadingSections -Lines $changelogLines)
$unreleased = $sections | Where-Object { $_.Label -ieq "Unreleased" } | Select-Object -First 1
$release = $sections | Where-Object { $_.Label -eq $targetVersion } | Select-Object -First 1
if ($null -eq $release) {
    Fail-Contract "CHANGELOG.md does not contain a released [$targetVersion] section outside [Unreleased]."
}
if ([string]::IsNullOrWhiteSpace($release.Date)) {
    Fail-Contract "CHANGELOG.md release [$targetVersion] must include an ISO release date."
}

$releaseLines = @(Get-SectionLines -Lines $changelogLines -Section $release -Sections $sections)
$releaseBody = $releaseLines -join "`n"
if ($releaseBody -match '(?i)\b(planned|unreleased|tbd|pending|coming\s+soon|not\s+yet\s+published|not\s+yet\s+available|not\s+published|pre[-\s]?release)\b') {
    Fail-Contract "CHANGELOG.md release [$targetVersion] contains pre-release wording."
}

$releaseCategories = @($releaseLines | Where-Object { $_ -match '^\s*###\s+(?<category>.+?)\s*$' } | ForEach-Object { $Matches.category.Trim() })
if ($releaseCategories.Count -eq 0 -or -not ($releaseCategories -contains "Added")) {
    Fail-Contract "The first public release [$targetVersion] must contain an Added section."
}
foreach ($category in $releaseCategories) {
    if ($category -ne "Added") {
        Fail-Contract "The first public release [$targetVersion] may contain only an Added section; found '$category'."
    }
}

if ($releaseBody -match '(?i)\b(now|no\s+longer|previously|formerly|used\s+to|fixed|fixes|corrected|resolved|addressed)\b|this\s+removes|this\s+fixes|changed\s+from') {
    Fail-Contract "The first public release [$targetVersion] contains remediation chronology."
}

if ($Mode -eq "Tag" -and $null -ne $unreleased) {
    $unreleasedLines = @(Get-SectionLines -Lines $changelogLines -Section $unreleased -Sections $sections)
    $releaseNormalized = Normalize-Text $releaseBody
    foreach ($bullet in @($unreleasedLines | Where-Object { $_ -match '^\s*[-*+]\s+(?<text>.+?)\s*$' } | ForEach-Object { $Matches.text.Trim() })) {
        $bulletNormalized = Normalize-Text $bullet
        if (-not $releaseNormalized.Contains($bulletNormalized, [StringComparison]::Ordinal)) {
            Fail-Contract "[Unreleased] contains an entry not documented by the finalized [$targetVersion] release: $bullet"
        }
    }
}

Write-Host "Release contract passed: mode=$Mode; version=$targetVersion; package=KeelMatrix.NuGetReady; changelog=[$targetVersion]"
