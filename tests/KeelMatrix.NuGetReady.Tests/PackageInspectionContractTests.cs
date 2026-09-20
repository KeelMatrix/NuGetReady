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

    private static string CreateArchive(string path, bool symbols, string? extraEntry)
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
            if (extraEntry is not null)
            {
                AddBytes(archive, extraEntry, new byte[] { 1 });
            }
        }
        else
        {
            AddBytes(archive, "tools/net8.0/any/KeelMatrix.NuGetReady.pdb", new byte[] { 1 });
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
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
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
