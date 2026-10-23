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
$repositoryRootPath = (Resolve-Path $RepositoryRoot).Path
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$launchGuard = Join-Path $repositoryRootPath 'build/Test-NestedPwshLaunch.ps1'
Invoke-NestedPwsh -SuppressOutput -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $launchGuard, '-SelfTest')
if ($global:LASTEXITCODE -ne 0) { throw 'Nested PowerShell launch guard self-test failed.' }
Invoke-NestedPwsh -SuppressOutput -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $launchGuard)
if ($global:LASTEXITCODE -ne 0) { throw 'Nested PowerShell launch guard failed.' }

function Fail-Contract {
    param([string]$Message)

    [Console]::Error.WriteLine("Release contract validation failed: $Message")
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

function Get-MarkdownViews {
    param([string[]]$Lines)

    $views = [System.Collections.Generic.List[object]]::new()
    $inComment = $false
    $fenceCharacter = $null
    $fenceLength = 0
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $line = $Lines[$index]
        if ($null -ne $fenceCharacter) {
            $closing = [Regex]::Match($line, '^( {0,3})(?<marker>`{3,}|~{3,})[ \t]*$')
            if ($closing.Success -and $closing.Groups['marker'].Value[0] -eq $fenceCharacter -and $closing.Groups['marker'].Value.Length -ge $fenceLength) {
                $fenceCharacter = $null
                $fenceLength = 0
            }

            $null = $views.Add([pscustomobject]@{ Visible = $false; Text = $line })
            continue
        }

        $builder = [System.Text.StringBuilder]::new()
        $cursor = 0
        while ($cursor -lt $line.Length) {
            if ($inComment) {
                $close = $line.IndexOf('-->', $cursor, [StringComparison]::Ordinal)
                if ($close -lt 0) {
                    $cursor = $line.Length
                    break
                }

                $inComment = $false
                $cursor = $close + 3
                continue
            }

            $open = $line.IndexOf('<!--', $cursor, [StringComparison]::Ordinal)
            if ($open -lt 0) {
                [void]$builder.Append($line, $cursor, $line.Length - $cursor)
                $cursor = $line.Length
                break
            }

            [void]$builder.Append($line, $cursor, $open - $cursor)
            $inComment = $true
            $cursor = $open + 4
        }

        $text = $builder.ToString()
        $opening = [Regex]::Match($text, '^( {0,3})(?<marker>`{3,}|~{3,})')
        if ($opening.Success) {
            $fenceCharacter = $opening.Groups['marker'].Value[0]
            $fenceLength = $opening.Groups['marker'].Value.Length
            $null = $views.Add([pscustomobject]@{ Visible = $false; Text = $text })
            continue
        }

        $visible = -not [string]::IsNullOrWhiteSpace($text) -and $text -notmatch '^(?: {4}|\t)'
        $null = $views.Add([pscustomobject]@{ Visible = $visible; Text = $text })
    }

    return $views.ToArray()
}

function Get-VisibleMarkdownFlags {
    param([string[]]$Lines)

    return @((Get-MarkdownViews -Lines $Lines) | ForEach-Object { [bool]$_.Visible })
}

function Get-HeadingSections {
    param([string[]]$Lines)

    $sections = [System.Collections.Generic.List[object]]::new()
    $views = @(Get-MarkdownViews -Lines $Lines)
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        if ($views[$index].Visible -and $views[$index].Text -match '^ {0,3}##\s+\[(?<label>[^\]]+)\](?:\s+-\s*(?<date>\d{4}-\d{2}-\d{2}))?\s*$') {
            $null = $sections.Add([pscustomobject]@{
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

    if ($next -le $Section.Index + 1) { return @() }
    $views = @(Get-MarkdownViews -Lines $Lines)
    return @((($Section.Index + 1)..($next - 1)) | Where-Object { $views[$_].Visible } | ForEach-Object { $views[$_].Text })
}

function Get-RealReleaseContent {
    param([string[]]$Lines)

    $categories = [System.Collections.Generic.List[string]]::new()
    $hasAddedEntry = $false
    $currentCategory = $null
    $views = @(Get-MarkdownViews -Lines $Lines)
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $line = $views[$index].Text
        if (-not $views[$index].Visible) { continue }
        if ($line -match '^ {0,3}###\s+(?<category>.+?)\s*$') {
            $currentCategory = $Matches.category.Trim()
            $null = $categories.Add($currentCategory)
            continue
        }

        if ($currentCategory -eq "Added" -and $line -match '^ {0,3}[-*+]\s+\S') {
            $hasAddedEntry = $true
        }
    }

    return [pscustomobject]@{
        Categories = @($categories)
        HasAddedEntry = $hasAddedEntry
    }
}

function Get-RealBulletEntries {
    param([string[]]$Lines)

    $entries = [System.Collections.Generic.List[string]]::new()
    $views = @(Get-MarkdownViews -Lines $Lines)
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $line = $views[$index].Text
        if ($views[$index].Visible -and $line -match '^ {0,3}[-*+]\s+(?<text>\S.*)$') {
            $null = $entries.Add($Matches.text.Trim())
        }
    }

    return @($entries)
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
$seenPackageIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($projectPath in Get-ChildItem -LiteralPath (Join-Path $root "src") -Filter "*.csproj" -File -Recurse) {
    try {
        $project = [xml](Read-RequiredFile $projectPath.FullName)
    }
    catch {
        Fail-Contract "Packable project metadata is not valid XML: $($projectPath.FullName)."
    }

    $packageId = Get-PropertyValue -Document $project -Name "PackageId"
    $isPackable = Get-PropertyValue -Document $project -Name "IsPackable"
    if (-not [string]::IsNullOrWhiteSpace($isPackable) -and
        -not $isPackable.Equals("true", [StringComparison]::OrdinalIgnoreCase) -and
        -not $isPackable.Equals("false", [StringComparison]::OrdinalIgnoreCase)) {
        Fail-Contract "Project IsPackable must be true or false: $($projectPath.FullName)."
    }
    if (-not [string]::IsNullOrWhiteSpace($isPackable) -and $isPackable.Equals("false", [StringComparison]::OrdinalIgnoreCase)) {
        continue
    }

    if ([string]::IsNullOrWhiteSpace($packageId)) {
        Fail-Contract "Every packable project under src must declare PackageId: $($projectPath.FullName)."
    }
    if (-not $seenPackageIds.Add($packageId)) {
        Fail-Contract "Packable projects under src must not share package ID '$packageId'."
    }

    $shippingProjects += [pscustomobject]@{
        Path = $projectPath.FullName
        Document = $project
        PackageId = $packageId
    }
}

$websiteManifestPath = Join-Path $root "keelmatrix.website.json"
$websiteManifest = $null
try {
    $websiteManifest = (Read-RequiredFile $websiteManifestPath) | ConvertFrom-Json -ErrorAction Stop
}
catch {
    Fail-Contract "keelmatrix.website.json is not valid JSON."
}

if ($null -eq $websiteManifest -or $websiteManifest -isnot [System.Management.Automation.PSCustomObject]) {
    Fail-Contract "keelmatrix.website.json must contain a JSON object."
}
$manifestFields = @($websiteManifest.PSObject.Properties | ForEach-Object { $_.Name })
if ($manifestFields.Count -ne 2 -or $manifestFields -cnotcontains "schemaVersion" -or $manifestFields -cnotcontains "packages") {
    Fail-Contract "keelmatrix.website.json must contain only schemaVersion and packages."
}
if (($websiteManifest.schemaVersion -isnot [int]) -and ($websiteManifest.schemaVersion -isnot [long])) {
    Fail-Contract "keelmatrix.website.json schemaVersion must be the integer 1."
}
if ($websiteManifest.schemaVersion -ne 1) {
    Fail-Contract "keelmatrix.website.json supports only schemaVersion 1."
}
if ($websiteManifest.packages -isnot [System.Management.Automation.PSCustomObject]) {
    Fail-Contract "keelmatrix.website.json packages must be an object keyed by package ID."
}

$manifestPackageProperties = @($websiteManifest.packages.PSObject.Properties)
$sourcePackageIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($project in $shippingProjects) {
    [void]$sourcePackageIds.Add($project.PackageId)
}
$manifestPackageIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$primaryCount = 0
foreach ($manifestPackageProperty in $manifestPackageProperties) {
    $packageId = $manifestPackageProperty.Name
    if (-not $manifestPackageIds.Add($packageId)) {
        Fail-Contract "keelmatrix.website.json contains duplicate package ID '$packageId'."
    }
    if (-not $sourcePackageIds.Contains($packageId)) {
        Fail-Contract "keelmatrix.website.json package '$packageId' does not match a packable project under src."
    }

    $classification = $manifestPackageProperty.Value
    if ($classification -isnot [System.Management.Automation.PSCustomObject]) {
        Fail-Contract "keelmatrix.website.json classification for '$packageId' must be an object."
    }
    $classificationFields = @($classification.PSObject.Properties | ForEach-Object { $_.Name })
    if ($classificationFields.Count -ne 2 -or $classificationFields -cnotcontains "visibility" -or $classificationFields -cnotcontains "role") {
        Fail-Contract "keelmatrix.website.json classification for '$packageId' must contain only visibility and role."
    }
    if ($classification.visibility -cnotin @("public-product", "internal-package")) {
        Fail-Contract "keelmatrix.website.json visibility for '$packageId' must be public-product or internal-package."
    }
    if ($classification.role -cnotin @("primary", "component")) {
        Fail-Contract "keelmatrix.website.json role for '$packageId' must be primary or component."
    }
    if ($classification.role -ceq "primary") {
        $primaryCount++
    }
}

if ($manifestPackageIds.Count -ne $sourcePackageIds.Count) {
    Fail-Contract "keelmatrix.website.json package set must exactly match packable projects under src."
}
foreach ($packageId in $sourcePackageIds) {
    if (-not $manifestPackageIds.Contains($packageId)) {
        Fail-Contract "Packable package '$packageId' is missing from keelmatrix.website.json."
    }
}
if ($primaryCount -ne 1) {
    Fail-Contract "keelmatrix.website.json must declare exactly one primary package; found $primaryCount."
}

$canonicalRepositoryUrl = "https://github.com/KeelMatrix/NuGetReady"
$canonicalPackageDescription = "Rehearse a NuGet release from the exact built artifacts and prove clean isolated consumer installation before publishing."
foreach ($project in $shippingProjects) {
    $classificationProperty = @($manifestPackageProperties | Where-Object { $_.Name -ceq $project.PackageId })
    if ($classificationProperty.Count -ne 1) {
        Fail-Contract "Packable package '$($project.PackageId)' must have exactly one manifest classification."
    }
    $classification = $classificationProperty[0].Value
    $tagsValue = Get-PropertyValue -Document $project.Document -Name "PackageTags"
    if ([string]::IsNullOrWhiteSpace($tagsValue)) {
        Fail-Contract "Package '$($project.PackageId)' must declare PackageTags with website sentinels."
    }
    $tagValues = @($tagsValue.Split(';') | ForEach-Object { $_.Trim() })
    $visibilityTags = @($tagValues | Where-Object { $_ -ieq "keelmatrix-public-product" -or $_ -ieq "keelmatrix-internal-package" })
    $roleTags = @($tagValues | Where-Object { $_ -ieq "keelmatrix-primary" -or $_ -ieq "keelmatrix-component" })
    if ($visibilityTags.Count -ne 1 -or $roleTags.Count -ne 1) {
        Fail-Contract "Package '$($project.PackageId)' must have exactly one website visibility sentinel and one role sentinel."
    }

    $expectedVisibilityTag = if ($classification.visibility -ceq "public-product") { "keelmatrix-public-product" } else { "keelmatrix-internal-package" }
    $expectedRoleTag = if ($classification.role -ceq "primary") { "keelmatrix-primary" } else { "keelmatrix-component" }
    if (-not [String]::Equals($visibilityTags[0], $expectedVisibilityTag, [StringComparison]::Ordinal) -or
        -not [String]::Equals($roleTags[0], $expectedRoleTag, [StringComparison]::Ordinal)) {
        Fail-Contract "Package '$($project.PackageId)' website sentinel tags do not agree with keelmatrix.website.json."
    }

    $authors = Get-PropertyValue -Document $project.Document -Name "Authors"
    if (-not [String]::Equals($authors, "KeelMatrix", [StringComparison]::Ordinal)) {
        Fail-Contract "Package '$($project.PackageId)' Authors must be KeelMatrix."
    }
    $description = Get-PropertyValue -Document $project.Document -Name "Description"
    if ([string]::IsNullOrWhiteSpace($description)) {
        Fail-Contract "Package '$($project.PackageId)' must declare a truthful non-empty Description."
    }
    if ($project.PackageId -ceq "KeelMatrix.NuGetReady" -and
        -not [String]::Equals($description, $canonicalPackageDescription, [StringComparison]::Ordinal)) {
        Fail-Contract "KeelMatrix.NuGetReady Description must match the approved product description."
    }

    $repositoryUrl = Get-PropertyValue -Document $project.Document -Name "RepositoryUrl"
    $projectUrl = Get-PropertyValue -Document $project.Document -Name "PackageProjectUrl"
    if (-not [String]::Equals($repositoryUrl, $canonicalRepositoryUrl, [StringComparison]::Ordinal) -or
        -not [String]::Equals($projectUrl, $canonicalRepositoryUrl, [StringComparison]::Ordinal)) {
        Fail-Contract "Package '$($project.PackageId)' RepositoryUrl and PackageProjectUrl must be $canonicalRepositoryUrl without fragments."
    }
    if (-not [String]::Equals((Get-PropertyValue -Document $project.Document -Name "RepositoryType"), "git", [StringComparison]::Ordinal) -or
        -not [String]::Equals((Get-PropertyValue -Document $project.Document -Name "PublishRepositoryUrl"), "true", [StringComparison]::OrdinalIgnoreCase)) {
        Fail-Contract "Package '$($project.PackageId)' must declare git repository metadata and PublishRepositoryUrl=true."
    }

    $readmeFile = Get-PropertyValue -Document $project.Document -Name "PackageReadmeFile"
    if ([string]::IsNullOrWhiteSpace($readmeFile) -or [System.IO.Path]::GetFileName($readmeFile) -cne $readmeFile) {
        Fail-Contract "Package '$($project.PackageId)' PackageReadmeFile must name a root-level README file."
    }
    $projectDirectory = Split-Path -Parent $project.Path
    if (-not (Test-Path -LiteralPath (Join-Path $projectDirectory $readmeFile) -PathType Leaf)) {
        Fail-Contract "Package '$($project.PackageId)' PackageReadmeFile does not exist beside its project."
    }
    $readmePackItems = @($project.Document.SelectNodes("//*[local-name()='None']") | Where-Object {
        [String]::Equals($_.GetAttribute("Include"), $readmeFile, [StringComparison]::Ordinal) -and
        [String]::Equals($_.GetAttribute("Pack"), "true", [StringComparison]::OrdinalIgnoreCase) -and
        [String]::Equals($_.GetAttribute("PackagePath"), $readmeFile, [StringComparison]::Ordinal)
    })
    if ($readmePackItems.Count -ne 1) {
        Fail-Contract "Package '$($project.PackageId)' README must be packed at the package root and match PackageReadmeFile."
    }

    if ($project.PackageId -ceq "KeelMatrix.NuGetReady") {
        if (-not [String]::Equals((Get-PropertyValue -Document $project.Document -Name "PackageType"), "DotnetTool", [StringComparison]::Ordinal) -or
            -not [String]::Equals((Get-PropertyValue -Document $project.Document -Name "PackAsTool"), "true", [StringComparison]::OrdinalIgnoreCase) -or
            -not [String]::Equals((Get-PropertyValue -Document $project.Document -Name "ToolCommandName"), "nugetready", [StringComparison]::Ordinal)) {
            Fail-Contract "KeelMatrix.NuGetReady must be packaged as the nugetready .NET tool."
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
$targetReleases = @($sections | Where-Object { $_.Label -eq $targetVersion })
if ($targetReleases.Count -gt 1) {
    Fail-Contract "CHANGELOG.md must contain exactly one real [$targetVersion] release section; found $($targetReleases.Count)."
}

foreach ($section in $sections | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Date) }) {
    $parsedDate = [datetime]::MinValue
    if (-not [datetime]::TryParseExact(
            $section.Date,
            "yyyy-MM-dd",
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None,
            [ref]$parsedDate)) {
        Fail-Contract "CHANGELOG.md release [$($section.Label)] must use a real ISO calendar date; '$($section.Date)' is invalid."
    }
}

$release = $targetReleases | Select-Object -First 1

if ($Mode -eq "PreRelease") {
    if ($null -eq $unreleased) {
        Fail-Contract "CHANGELOG.md must contain an [Unreleased] section for a pre-release candidate."
    }

    $unreleasedLines = @(Get-SectionLines -Lines $changelogLines -Section $unreleased -Sections $sections)
    $realUnreleasedEntries = @(Get-RealBulletEntries -Lines $unreleasedLines)
    $hasUnreleasedEntry = $realUnreleasedEntries.Count -gt 0
    $hasFinalizedRelease = $null -ne $release -and -not [string]::IsNullOrWhiteSpace($release.Date)
    if (-not $hasUnreleasedEntry -and -not $hasFinalizedRelease) {
        Fail-Contract "CHANGELOG.md must contain planned [Unreleased] entries or a dated [$targetVersion] release for a pre-release candidate."
    }

    Write-Host "Pre-release contract passed: mode=$Mode; version=$targetVersion; package=KeelMatrix.NuGetReady; changelog=[Unreleased] or finalized [$targetVersion]"
    return
}

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

$releaseContent = Get-RealReleaseContent -Lines $releaseLines
$releaseCategories = @($releaseContent.Categories)
if ($releaseCategories.Count -eq 0 -or -not ($releaseCategories -contains "Added") -or -not $releaseContent.HasAddedEntry) {
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
    foreach ($bullet in @(Get-RealBulletEntries -Lines $unreleasedLines)) {
        $bulletNormalized = Normalize-Text $bullet
        if (-not $releaseNormalized.Contains($bulletNormalized, [StringComparison]::Ordinal)) {
            Fail-Contract "[Unreleased] contains an entry not documented by the finalized [$targetVersion] release: $bullet"
        }
    }
}

Write-Host "Release contract passed: mode=$Mode; version=$targetVersion; package=KeelMatrix.NuGetReady; changelog=[$targetVersion]"
