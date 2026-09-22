using System.IO.Compression;
using System.Text;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class PackageInspectionContractTests
{
    [Fact]
    public async Task Package_inspection_rejects_an_unexpected_payload_entry()
    {
        using var fixture = PackageFixture.Create();
        var package = CreateArchive(
            Path.Combine(fixture.ArtifactsPath, "KeelMatrix.NuGetReady.0.1.0.nupkg"),
            symbols: false,
            extraEntry: "unexpected.payload");
        var symbols = CreateArchive(
            Path.Combine(fixture.ArtifactsPath, "KeelMatrix.NuGetReady.0.1.0.snupkg"),
            symbols: true,
            extraEntry: null);
        var script = FindRepositoryFile("scripts", "inspect-package.ps1");

        var result = await BoundedProcess.RunAsync(
            "pwsh",
            [
                "-NoProfile",
                "-File",
                script,
                "-PackagePath",
                package,
                "-SymbolsPackagePath",
                symbols
            ],
            fixture.Root.FullName,
            new Dictionary<string, string?> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
            TimeSpan.FromSeconds(30));

        Assert.True(result.Started, result.StandardError);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Package_inspection_rejects_an_extended_sensitive_name_family()
    {
        using var fixture = PackageFixture.Create();
        var package = CreateArchive(
            Path.Combine(fixture.ArtifactsPath, "KeelMatrix.NuGetReady.0.1.0.nupkg"),
            symbols: false,
            extraEntry: "nested/local-telemetry.json.bak");
        var symbols = CreateArchive(
            Path.Combine(fixture.ArtifactsPath, "KeelMatrix.NuGetReady.0.1.0.snupkg"),
            symbols: true,
            extraEntry: null);
        var script = FindRepositoryFile("scripts", "inspect-package.ps1");

        var result = await BoundedProcess.RunAsync(
            "pwsh",
            [
                "-NoProfile",
                "-File",
                script,
                "-PackagePath",
                package,
                "-SymbolsPackagePath",
                symbols
            ],
            fixture.Root.FullName,
            new Dictionary<string, string?> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
            TimeSpan.FromSeconds(30));

        Assert.True(result.Started, result.StandardError);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Forbidden archive entries found", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Package_inspection_rejects_a_manifest_exempt_tool_payload_assembly_as_unexpected()
    {
        using var fixture = PackageFixture.Create();
        var package = CreateArchive(
            Path.Combine(fixture.ArtifactsPath, "KeelMatrix.NuGetReady.0.1.0.nupkg"),
            symbols: false,
            extraEntry: "tools/net8.0/any/NuGet.Configuration.Extensions.dll");
        var symbols = CreateArchive(
            Path.Combine(fixture.ArtifactsPath, "KeelMatrix.NuGetReady.0.1.0.snupkg"),
            symbols: true,
            extraEntry: null);
        var script = FindRepositoryFile("scripts", "inspect-package.ps1");

        var result = await BoundedProcess.RunAsync(
            "pwsh",
            [
                "-NoProfile",
                "-File",
                script,
                "-PackagePath",
                package,
                "-SymbolsPackagePath",
                symbols
            ],
            fixture.Root.FullName,
            new Dictionary<string, string?> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
            TimeSpan.FromSeconds(30));

        Assert.True(result.Started, result.StandardError);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unexpected package entries", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "tools/net8.0/any/Unexpected.dll")]
    [InlineData(false, "tools/net8.0/any/Unexpected.xml")]
    [InlineData(true, "tools/net8.0/any/Unexpected.dll")]
    [InlineData(true, "tools/net8.0/any/Unexpected.xml")]
    public async Task Package_inspection_rejects_unexpected_binary_and_xml_entries_in_both_archive_types(bool symbolsArchive, string unexpectedEntry)
    {
        using var fixture = PackageFixture.Create();
        var package = CreateArchive(
            Path.Combine(fixture.ArtifactsPath, "KeelMatrix.NuGetReady.0.1.0.nupkg"),
            symbols: false,
            extraEntry: symbolsArchive ? null : unexpectedEntry);
        var symbols = CreateArchive(
            Path.Combine(fixture.ArtifactsPath, "KeelMatrix.NuGetReady.0.1.0.snupkg"),
            symbols: true,
            extraEntry: symbolsArchive ? unexpectedEntry : null);
        var script = FindRepositoryFile("scripts", "inspect-package.ps1");

        var result = await BoundedProcess.RunAsync(
            "pwsh",
            [
                "-NoProfile",
                "-File",
                script,
                "-PackagePath",
                package,
                "-SymbolsPackagePath",
                symbols
            ],
            fixture.Root.FullName,
            new Dictionary<string, string?> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
            TimeSpan.FromSeconds(30));

        Assert.True(result.Started, result.StandardError);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unexpected package entries", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Package_inspection_rejects_the_manifest_generated_exact_name_corpus_through_the_sensitive_rule()
    {
        using var fixture = PackageFixture.Create();
        var manifest = PackageSensitiveFilePolicy.ReadManifestForTests();
        var corpus = SensitivePathCorpus.GenerateRejectCorpus(manifest);
        var symbols = CreateArchive(
            Path.Combine(fixture.ArtifactsPath, "KeelMatrix.NuGetReady.0.1.0.snupkg"),
            symbols: true,
            extraEntry: null);
        var script = FindRepositoryFile("scripts", "inspect-package.ps1");

        var index = 0;
        foreach (var batch in corpus.Chunk(32))
        {
            var package = CreateArchive(
                Path.Combine(fixture.ArtifactsPath, $"KeelMatrix.NuGetReady.0.1.0-{index++}.nupkg"),
                symbols: false,
                extraEntries: batch);

            var result = await BoundedProcess.RunAsync(
                "pwsh",
                [
                    "-NoProfile",
                    "-File",
                    script,
                    "-PackagePath",
                    package,
                    "-SymbolsPackagePath",
                    symbols
                ],
                fixture.Root.FullName,
                new Dictionary<string, string?> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
                TimeSpan.FromSeconds(30));

            Assert.True(result.Started, result.StandardError);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Forbidden archive entries found", result.StandardError, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal((corpus.Count + 31) / 32, index);
    }

    [Fact]
    public async Task Pack_fails_closed_for_local_environment_and_telemetry_files_even_when_directory_targets_are_disabled()
    {
        var project = FindRepositoryFile("src", "KeelMatrix.NuGetReady", "KeelMatrix.NuGetReady.csproj");
        var projectDirectory = Path.GetDirectoryName(project)!;
        var sensitiveFiles = new[] { ".env.local", "keelmatrix.telemetry.json" }
            .Select(name => Path.Combine(projectDirectory, name))
            .ToArray();
        var output = Directory.CreateTempSubdirectory("nugetready-pack-guard-");
        try
        {
            foreach (var path in sensitiveFiles)
            {
                File.WriteAllText(path, "local-only");
            }

            var result = await BoundedProcess.RunAsync(
                "dotnet",
                [
                    "pack",
                    project,
                    "-c",
                    "Release",
                    "--no-build",
                    "--no-restore",
                    "-p:ImportDirectoryBuildTargets=false",
                    "-p:ImportDirectoryTargets=false",
                    "-o",
                    output.FullName
                ],
                FindRepositoryFile(),
                new Dictionary<string, string?> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
                TimeSpan.FromSeconds(30));

            Assert.NotEqual(0, result.ExitCode);
        }
        finally
        {
            foreach (var path in sensitiveFiles)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            output.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(".local-telemetry.json.bak")]
    [InlineData("xlocal-telemetry.json.bak")]
    [InlineData(".keelmatrix.telemetry.json")]
    [InlineData("xNuGet.config.bak")]
    [InlineData(".global.jsonx")]
    [InlineData("xAGENTS.md.bak")]
    [InlineData(".apikeyx")]
    [InlineData("xcredentials.json.bak")]
    public async Task Pack_fails_closed_for_extended_sensitive_name_families_and_writes_no_archives(string sensitiveFile)
    {
        var project = FindRepositoryFile("src", "KeelMatrix.NuGetReady", "KeelMatrix.NuGetReady.csproj");
        var projectDirectory = Path.GetDirectoryName(project)!;
        var sensitivePath = Path.Combine(projectDirectory, sensitiveFile);
        var output = Directory.CreateTempSubdirectory("nugetready-pack-family-guard-");
        try
        {
            File.WriteAllText(sensitivePath, "local-only");

            var result = await BoundedProcess.RunAsync(
                "dotnet",
                [
                    "pack",
                    project,
                    "-c",
                    "Release",
                    "--no-build",
                    "--no-restore",
                    "-p:ImportDirectoryBuildTargets=false",
                    "-p:ImportDirectoryTargets=false",
                    "-o",
                    output.FullName
                ],
                FindRepositoryFile(),
                new Dictionary<string, string?> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
                TimeSpan.FromSeconds(30));

            Assert.NotEqual(0, result.ExitCode);
            var archives = Directory.EnumerateFiles(output.FullName, "*.nupkg")
                .Concat(Directory.EnumerateFiles(output.FullName, "*.snupkg"))
                .ToArray();
            Assert.True(archives.Length == 0, $"Sensitive pack input produced archives: {string.Join(", ", archives)}\n{result.StandardOutput}\n{result.StandardError}");
        }
        finally
        {
            if (File.Exists(sensitivePath))
            {
                File.Delete(sensitivePath);
            }

            output.Delete(recursive: true);
        }
    }

    private static string CreateArchive(string path, bool symbols, string? extraEntry)
    {
        return CreateArchive(path, symbols, extraEntry is null ? Array.Empty<string>() : new[] { extraEntry });
    }

    private static string CreateArchive(string path, bool symbols, IEnumerable<string> extraEntries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        AddText(archive, "KeelMatrix.NuGetReady.nuspec", Nuspec(symbols));
        if (!symbols)
        {
            AddText(archive, "README.md", "readme");
            AddBytes(archive, "icon.png", new byte[] { 1 });
            AddBytes(archive, "tools/net8.0/any/KeelMatrix.NuGetReady.dll", new byte[] { 1 });
            AddBytes(archive, "tools/net8.0/any/KeelMatrix.Telemetry.dll", new byte[] { 1 });
            AddBytes(archive, "tools/net8.0/any/DotnetToolSettings.xml", new byte[] { 1 });
        }
        else
        {
            AddBytes(archive, "tools/net8.0/any/KeelMatrix.NuGetReady.pdb", new byte[] { 1 });
        }

        foreach (var extraEntry in extraEntries)
        {
            AddBytes(archive, extraEntry, new byte[] { 1 });
        }

        return path;
    }

    private static string Nuspec(bool symbols)
    {
        var packageType = symbols ? "<packageTypes><packageType name=\"SymbolsPackage\" /></packageTypes>" : "<packageTypes><packageType name=\"DotnetTool\" /></packageTypes>";
        var metadata = symbols
            ? string.Empty
            : "<readme>README.md</readme><icon>icon.png</icon>";
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>KeelMatrix.NuGetReady</id>
                <version>0.1.0</version>
                {metadata}
                {packageType}
                <dependencies><group targetFramework="net8.0"><dependency id="KeelMatrix.Telemetry" version="[0.1.0]" exclude="Build,Analyzers" /><dependency id="NuGet.Packaging" version="[7.9.0]" /></group></dependencies>
              </metadata>
            </package>
            """;
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = parts.Length == 0 ? directory.FullName : Path.Combine([directory.FullName, .. parts]);
            if ((parts.Length == 0 && File.Exists(Path.Combine(path, "KeelMatrix.NuGetReady.sln"))) || File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException("Repository file could not be located.");
    }

    private static void AddText(ZipArchive archive, string name, string content) => AddBytes(archive, name, Encoding.UTF8.GetBytes(content));

    private static void AddBytes(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(content);
    }
}
