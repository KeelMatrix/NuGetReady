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

function Assert-Contract {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

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
        Assert-Contract ($metadata.SelectSingleNode("n:readme", $namespace).InnerText -eq "README.md") "README metadata is incorrect."
        Assert-Contract ($metadata.SelectSingleNode("n:icon", $namespace).InnerText -eq "icon.png") "Icon metadata is incorrect."
        Assert-Contract (@($metadata.packageTypes.packageType | ForEach-Object { $_.name }) -contains "DotnetTool") "Package type must be DotnetTool."
    }

    $groups = @($metadata.SelectNodes("n:dependencies/n:group", $namespace))
    Assert-Contract ($groups.Count -eq 1 -and $groups[0].targetFramework -eq "net8.0") "Expected one net8.0 dependency group."
    $dependencies = @($groups[0].SelectNodes("n:dependency", $namespace))
    $dependencyIds = @($dependencies | ForEach-Object { $_.id } | Sort-Object)
    Assert-Contract (($dependencyIds -join ",") -eq "KeelMatrix.Telemetry,NuGet.Packaging") "Unexpected dependency set: $($dependencyIds -join ', ')."
    $telemetry = $dependencies | Where-Object { $_.id -eq "KeelMatrix.Telemetry" } | Select-Object -First 1
    Assert-Contract ($null -ne $telemetry) "KeelMatrix.Telemetry dependency is missing."
    Assert-Contract ($telemetry.version -eq "[0.1.0]") "KeelMatrix.Telemetry must be pinned to [0.1.0]."
    Assert-Contract ($telemetry.exclude -eq "Build,Analyzers") "KeelMatrix.Telemetry dependency exclusions are incorrect."
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
                "README.md",
                "icon.png",
                "tools/net8.0/any/KeelMatrix.NuGetReady.dll",
                "tools/net8.0/any/KeelMatrix.Telemetry.dll",
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

        $unexpected = @($entries | Where-Object {
            ($allowed -notcontains $_) -and ($_ -notmatch '^package/services/metadata/core-properties/[^/]+\.psmdcp$')
        })
        Assert-Contract ($unexpected.Count -eq 0) "Unexpected package entries found: $($unexpected -join ', ')"

        $forbidden = @($entries | Where-Object {
            $_ -match '(^|/)(AGENTS\.md|CHANGELOG\.md|SECURITY\.md|PRIVACY\.md|NuGet\.config|global\.json|\.env[^/]*|keelmatrix\.telemetry\.json)$' -or
            $_ -match '\.(csproj|sln|yml|yaml|trx)$' -or
            $_ -match '(^|/)(tests?|artifacts|bin|obj|TestResults)(/|$)' -or
            $_ -match '(^|/)(apikey|api-key|access-token|credentials\.json|password|secret)$'
        })
        Assert-Contract ($forbidden.Count -eq 0) "Forbidden archive entries found: $($forbidden -join ', ')"
    }
    finally {
        $archive.Dispose()
    }
}

Inspect-Archive -Path $PackagePath -Symbols:$false
Inspect-Archive -Path $SymbolsPackagePath -Symbols:$true
Write-Host "Package inspection passed: $([IO.Path]::GetFileName($PackagePath)); $([IO.Path]::GetFileName($SymbolsPackagePath))"
