using System.IO.Compression;
using System.Text;

namespace KeelMatrix.NuGetReady.Tests;

internal sealed class PackageFixture : IDisposable
{
    private PackageFixture(DirectoryInfo root, string artifactsPath)
    {
        Root = root;
        ArtifactsPath = artifactsPath;
    }

    public DirectoryInfo Root { get; }

    public string ArtifactsPath { get; }

    public static PackageFixture Create()
    {
        var root = Directory.CreateTempSubdirectory("nugetready-tests-");
        var artifacts = Directory.CreateDirectory(Path.Combine(root.FullName, "artifacts"));
        return new PackageFixture(root, artifacts.FullName);
    }

    public string AddPackage(
        string fileName,
        string id,
        string version,
        string kind = "library",
        bool includeReadme = true,
        bool includeIcon = true,
        bool includeLicense = true,
        string? dependencyFramework = "net8.0",
        bool includeSensitiveFile = false,
        bool multiTarget = false)
    {
        var path = Path.Combine(ArtifactsPath, fileName);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var frameworks = multiTarget ? new[] { "net8.0", "netstandard2.0" } : new[] { "net8.0" };
        var dependencyFrameworks = multiTarget ? frameworks : dependencyFramework is null ? Array.Empty<string>() : new[] { dependencyFramework };
        var dependencies = dependencyFramework is null
            ? string.Empty
            : $"<dependencies>{string.Join(string.Empty, dependencyFrameworks.Select(framework => $"<group targetFramework=\"{framework}\"><dependency id=\"Example.Dependency\" version=\"[1.0.0]\" /></group>"))}</dependencies>";
        var license = includeLicense ? "<license type=\"expression\">MIT</license>" : string.Empty;
        var readme = includeReadme ? "<readme>README.md</readme>" : string.Empty;
        var icon = includeIcon ? "<icon>icon.png</icon>" : string.Empty;
        AddText(archive, $"{id}.nuspec", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>KeelMatrix</authors>
                <description>A package used to verify NuGetReady archive checks.</description>
                <tags>nuget;test</tags>
                {license}
                {readme}
                {icon}
                <repository type="git" url="https://github.com/KeelMatrix/NuGetReady" />
                {dependencies}
              </metadata>
            </package>
            """);

        if (includeReadme)
        {
            AddText(archive, "README.md", "Package README.");
        }

        if (includeIcon)
        {
            AddBytes(archive, "icon.png", new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        }

        if (kind.Equals("dotnetTool", StringComparison.OrdinalIgnoreCase))
        {
            AddText(archive, "tools/net8.0/any/DotnetToolSettings.xml", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <DotNetCliTool Version="1">
                  <Commands><Command Name="{id}" EntryPoint="{id}.dll" Runner="dotnet" /></Commands>
                </DotNetCliTool>
                """);
            AddBytes(archive, $"tools/net8.0/any/{id}.dll", new byte[] { 1, 2, 3 });
            AddBytes(archive, $"tools/net8.0/any/{id}.deps.json", new byte[] { 4, 5, 6 });
        }
        else
        {
            foreach (var framework in frameworks)
            {
                AddBytes(archive, $"lib/{framework}/{id}.dll", new byte[] { 1, 2, 3 });
            }
        }

        if (includeSensitiveFile)
        {
            AddText(archive, ".github/workflows/release.yml", "token: example");
        }

        return path;
    }

    public string AddSymbols(string fileName, string id, string version, bool includePdb = true)
    {
        var path = Path.Combine(ArtifactsPath, fileName);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        AddText(archive, $"{id}.nuspec", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
              </metadata>
            </package>
            """);
        if (includePdb)
        {
            AddBytes(archive, $"lib/net8.0/{id}.pdb", new byte[] { 7, 8, 9 });
        }

        return path;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Root.Delete(recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(25);
            }
        }

        Root.Delete(recursive: true);
    }

    private static void AddText(ZipArchive archive, string name, string content)
    {
        AddBytes(archive, name, Encoding.UTF8.GetBytes(content));
    }

    private static void AddBytes(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var entryStream = entry.Open();
        entryStream.Write(content);
    }
}
