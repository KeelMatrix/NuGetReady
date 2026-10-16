[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$SymbolsPackagePath,

    [string]$ExpectedVersion = "0.1.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "package-sensitive-path-policy.ps1")

function Assert-Contract {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Read-SourcePackageProperty {
    param([string]$Name)

    $node = $sourceProject.SelectSingleNode("//*[local-name()='PropertyGroup']/*[local-name()='$Name']")
    Assert-Contract ($null -ne $node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) "Shipping project is missing $Name metadata."
    return $node.InnerText.Trim()
}

$sourceProjectPath = Join-Path $PSScriptRoot "../src/KeelMatrix.NuGetReady/KeelMatrix.NuGetReady.csproj"
$sourceProject = [xml](Get-Content -LiteralPath $sourceProjectPath -Raw)
$expectedAuthors = Read-SourcePackageProperty "Authors"
$expectedPackageDescription = Read-SourcePackageProperty "Description"
$expectedProjectUrl = Read-SourcePackageProperty "PackageProjectUrl"
$expectedRepositoryUrl = Read-SourcePackageProperty "RepositoryUrl"
$expectedRepositoryType = Read-SourcePackageProperty "RepositoryType"
$expectedPackageType = Read-SourcePackageProperty "PackageType"
$expectedReadme = Read-SourcePackageProperty "PackageReadmeFile"
$websiteManifestPath = Join-Path $PSScriptRoot "../keelmatrix.website.json"
$websiteManifest = $null
try {
    $websiteManifest = (Get-Content -LiteralPath $websiteManifestPath -Raw) | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw "Website classification manifest is missing or invalid."
}
Assert-Contract ($null -ne $websiteManifest -and $websiteManifest -is [System.Management.Automation.PSCustomObject]) "Website classification manifest must be a JSON object."
$websitePackage = @($websiteManifest.packages.PSObject.Properties | Where-Object { $_.Name -ceq "KeelMatrix.NuGetReady" })
Assert-Contract ($websitePackage.Count -eq 1) "Website classification manifest must classify KeelMatrix.NuGetReady exactly once."
$websiteClassification = $websitePackage[0].Value
Assert-Contract ($websiteClassification.visibility -cin @("public-product", "internal-package")) "Website package visibility is invalid."
Assert-Contract ($websiteClassification.role -cin @("primary", "component")) "Website package role is invalid."
$expectedVisibilityTag = if ($websiteClassification.visibility -ceq "public-product") { "keelmatrix-public-product" } else { "keelmatrix-internal-package" }
$expectedRoleTag = if ($websiteClassification.role -ceq "primary") { "keelmatrix-primary" } else { "keelmatrix-component" }

function Read-Nuspec {
    param([IO.Compression.ZipArchive]$Archive)

    $entry = $Archive.Entries | Where-Object { $_.FullName -eq "KeelMatrix.NuGetReady.nuspec" } | Select-Object -First 1
    Assert-Contract ($null -ne $entry) "The package nuspec is missing."
    $reader = [IO.StreamReader]::new($entry.Open())
    try {
        return [xml]$reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

function Assert-NuspecContract {
    param(
        [xml]$Nuspec,
        [bool]$Symbols
    )

    $namespace = [System.Xml.XmlNamespaceManager]::new($Nuspec.NameTable)
    $namespace.AddNamespace("n", $Nuspec.DocumentElement.NamespaceURI)
    $metadata = $Nuspec.SelectSingleNode("/n:package/n:metadata", $namespace)
    Assert-Contract ($null -ne $metadata) "Package metadata is missing."
    Assert-Contract ($metadata.id -eq "KeelMatrix.NuGetReady") "Package ID is incorrect."
    Assert-Contract ($metadata.version -eq $ExpectedVersion) "Package version is $($metadata.version), expected $ExpectedVersion."
    if ($Symbols) {
        Assert-Contract (@($metadata.packageTypes.packageType | ForEach-Object { $_.name }) -contains "SymbolsPackage") "Package type must be SymbolsPackage."
    }
    else {
        Assert-Contract ($metadata.SelectSingleNode("n:authors", $namespace).InnerText -ceq $expectedAuthors) "Package authors are incorrect."
        Assert-Contract ($metadata.SelectSingleNode("n:description", $namespace).InnerText -ceq $expectedPackageDescription) "Package description is incorrect."
        Assert-Contract ($metadata.SelectSingleNode("n:projectUrl", $namespace).InnerText -ceq $expectedProjectUrl) "Package project URL is incorrect."
        $repository = $metadata.SelectSingleNode("n:repository", $namespace)
        Assert-Contract ($null -ne $repository -and $repository.GetAttribute("type") -ceq $expectedRepositoryType -and $repository.GetAttribute("url") -ceq $expectedRepositoryUrl) "Package repository metadata is incorrect."
        $tagValues = @($metadata.SelectSingleNode("n:tags", $namespace).InnerText -split '[;\s]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $visibilityTags = @($tagValues | Where-Object { $_ -ieq "keelmatrix-public-product" -or $_ -ieq "keelmatrix-internal-package" })
        $roleTags = @($tagValues | Where-Object { $_ -ieq "keelmatrix-primary" -or $_ -ieq "keelmatrix-component" })
        Assert-Contract ($visibilityTags.Count -eq 1 -and $roleTags.Count -eq 1) "Package website classification tags are missing or ambiguous."
        Assert-Contract ([String]::Equals($visibilityTags[0], $expectedVisibilityTag, [StringComparison]::Ordinal) -and [String]::Equals($roleTags[0], $expectedRoleTag, [StringComparison]::Ordinal)) "Package website classification tags do not agree with keelmatrix.website.json."
        Assert-Contract ($metadata.SelectSingleNode("n:readme", $namespace).InnerText -ceq $expectedReadme) "README metadata is incorrect."
        Assert-Contract ($metadata.SelectSingleNode("n:icon", $namespace).InnerText -eq "icon.png") "Icon metadata is incorrect."
        Assert-Contract (@($metadata.packageTypes.packageType | ForEach-Object { $_.name }) -ccontains $expectedPackageType) "Package type is incorrect."
    }

    $groups = @($metadata.SelectNodes("n:dependencies/n:group", $namespace))
    Assert-Contract ($groups.Count -eq 1 -and $groups[0].targetFramework -eq "net8.0") "Expected one net8.0 dependency group."
    $dependencies = @($groups[0].SelectNodes("n:dependency", $namespace))
    $dependencyIds = @($dependencies | ForEach-Object { $_.id } | Sort-Object)
    Assert-Contract (($dependencyIds -join ",") -eq "KeelMatrix.Telemetry,NuGet.Packaging,YamlDotNet") "Unexpected dependency set: $($dependencyIds -join ', ')."
    $telemetry = $dependencies | Where-Object { $_.id -eq "KeelMatrix.Telemetry" } | Select-Object -First 1
    Assert-Contract ($null -ne $telemetry) "KeelMatrix.Telemetry dependency is missing."
    Assert-Contract ($telemetry.version -eq "[0.1.1]") "KeelMatrix.Telemetry must be pinned to [0.1.1]."
    Assert-Contract ($telemetry.exclude -eq "Build,Analyzers") "KeelMatrix.Telemetry dependency exclusions are incorrect."
    $yaml = $dependencies | Where-Object { $_.id -eq "YamlDotNet" } | Select-Object -First 1
    Assert-Contract ($null -ne $yaml) "YamlDotNet dependency is missing."
    Assert-Contract ($yaml.version -eq "18.1.0") "YamlDotNet must use version 18.1.0."
    Assert-Contract ($yaml.exclude -eq "Build,Analyzers") "YamlDotNet dependency exclusions are incorrect."
}

function Inspect-Archive {
    param(
        [string]$Path,
        [bool]$Symbols
    )

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $archive = [IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        if (-not $Symbols) {
            foreach ($required in @(
                $expectedReadme,
                "icon.png",
                "tools/net8.0/any/KeelMatrix.NuGetReady.dll",
                "tools/net8.0/any/KeelMatrix.Telemetry.dll",
                "tools/net8.0/any/YamlDotNet.dll",
                "tools/net8.0/any/DotnetToolSettings.xml")) {
                Assert-Contract ($entries -contains $required) "Required package entry is missing: $required"
            }

            Assert-NuspecContract (Read-Nuspec $archive) $false
            $allowed = @(
                "_rels/.rels",
                "KeelMatrix.NuGetReady.nuspec",
                "README.md",
                "icon.png",
                "[Content_Types].xml",
                "tools/net8.0/any/DotnetToolSettings.xml",
                "tools/net8.0/any/KeelMatrix.NuGetReady.dll",
                "tools/net8.0/any/KeelMatrix.NuGetReady.deps.json",
                "tools/net8.0/any/KeelMatrix.NuGetReady.runtimeconfig.json",
                "tools/net8.0/any/KeelMatrix.NuGetReady.pdb",
                "tools/net8.0/any/KeelMatrix.Telemetry.dll",
                "tools/net8.0/any/Newtonsoft.Json.dll",
                "tools/net8.0/any/NuGet.Common.dll",
                "tools/net8.0/any/NuGet.Configuration.dll",
                "tools/net8.0/any/NuGet.Frameworks.dll",
                "tools/net8.0/any/NuGet.Packaging.dll",
                "tools/net8.0/any/NuGet.Versioning.dll",
                "tools/net8.0/any/System.Security.Cryptography.Pkcs.dll",
                "tools/net8.0/any/System.Security.Cryptography.ProtectedData.dll",
                "tools/net8.0/any/YamlDotNet.dll",
                "tools/net8.0/any/runtimes/win/lib/net8.0/System.Security.Cryptography.Pkcs.dll"
            )
        }
        else {
            Assert-Contract ($entries -contains "tools/net8.0/any/KeelMatrix.NuGetReady.pdb") "Symbol package PDB is missing."
            Assert-NuspecContract (Read-Nuspec $archive) $true
            $allowed = @(
                "_rels/.rels",
                "KeelMatrix.NuGetReady.nuspec",
                "tools/net8.0/any/KeelMatrix.NuGetReady.pdb",
                "[Content_Types].xml"
            )
        }

        $forbidden = @($entries | Where-Object { Test-SensitivePackagePath $_ -SiblingPaths $entries })
        Assert-Contract ($forbidden.Count -eq 0) "Forbidden archive entries found: $($forbidden -join ', ')"

        $unexpected = @($entries | Where-Object {
            ($allowed -notcontains $_) -and
            ($_ -notmatch '^package/services/metadata/core-properties/[^/]+\.psmdcp$')
        })
        Assert-Contract ($unexpected.Count -eq 0) "Unexpected package entries found: $($unexpected -join ', ')"
    }
    finally {
        $archive.Dispose()
    }
}

Inspect-Archive -Path $PackagePath -Symbols:$false
Inspect-Archive -Path $SymbolsPackagePath -Symbols:$true
Write-Host "Package inspection passed: $([IO.Path]::GetFileName($PackagePath)); $([IO.Path]::GetFileName($SymbolsPackagePath))"
