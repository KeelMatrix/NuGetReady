using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace KeelMatrix.NuGetReady.Tests;

internal sealed partial class PackedCorpus : IDisposable
{
    private PackedCorpus(DirectoryInfo root, string outputPath, string packageCachePath, string repositoryRoot)
    {
        Root = root;
        OutputPath = outputPath;
        PackageCachePath = packageCachePath;
        RepositoryRoot = repositoryRoot;
    }

    public DirectoryInfo Root { get; }

    public string OutputPath { get; }

    public string PackageCachePath { get; }

    public string RepositoryRoot { get; }

    public static PackedCorpus Create()
    {
        var root = Directory.CreateTempSubdirectory("nugetready-packed-corpus-");
        var output = Directory.CreateDirectory(Path.Combine(root.FullName, "packages"));
        var packageCache = Directory.CreateDirectory(Path.Combine(root.FullName, "nuget-packages"));
        return new PackedCorpus(root, output.FullName, packageCache.FullName, FindRepositoryRoot());
    }

    public string Pack(string projectRelativePath, params string[] sources)
    {
        var projectPath = Path.Combine(RepositoryRoot, "tests", "Fixtures", "ReleaseCorpus", projectRelativePath);
        var arguments = new List<string>
        {
            "pack",
            projectPath,
            "--configuration",
            "Release",
            "--output",
            OutputPath,
            "--include-symbols",
            "-p:SymbolPackageFormat=snupkg",
            "--configfile",
            Path.Combine(RepositoryRoot, "NuGet.config"),
            "--ignore-failed-sources",
            "--disable-parallel",
            "--nologo",
            "-p:UseSharedCompilation=false"
        };
        foreach (var source in sources)
        {
            arguments.Add("--source");
            arguments.Add(source);
        }

        var result = BoundedProcess.RunAsync(
            "dotnet",
            arguments,
            RepositoryRoot,
            new Dictionary<string, string?>
            {
                ["DOTNET_NOLOGO"] = "1",
                ["NUGET_PACKAGES"] = PackageCachePath,
                ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(Root.FullName, "http-cache"),
                ["NUGET_XMLDOC_MODE"] = "skip",
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
            },
            TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
        if (!result.Started || result.TimedOut || result.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException($"dotnet pack failed for {projectRelativePath}.\n{Combine(result)}");
        }

        var packages = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => CreatedPackageRegex().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .Where(path => !path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .ToArray();
        if (packages.Length == 0)
        {
            throw new Xunit.Sdk.XunitException($"dotnet pack produced no package for {projectRelativePath}.\n{Combine(result)}");
        }

        return packages[^1];
    }

    [GeneratedRegex("Successfully created package '([^']+\\.nupkg)'", RegexOptions.IgnoreCase)]
    private static partial Regex CreatedPackageRegex();

    public void Dispose()
    {
        TryDelete(Root);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeelMatrix.NuGetReady.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException("NuGetReady repository root could not be located.");
    }

    private static string Combine(ProcessResult result)
    {
        return string.Join("\n", new[] { result.StandardOutput, result.StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static void TryDelete(DirectoryInfo directory)
    {
        try
        {
            if (directory.Exists)
            {
                directory.Delete(recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class ArchiveMutator
{
    public static string ReplaceNuspecText(string packagePath, Func<string, string> replacement, string? outputName = null)
    {
        var outputPath = Path.Combine(Path.GetDirectoryName(packagePath)!, outputName ?? Path.GetFileNameWithoutExtension(packagePath) + ".mutated.nupkg");
        using (var input = ZipFile.OpenRead(packagePath))
        using (var output = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            foreach (var entry in input.Entries)
            {
                var destination = output.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                using var destinationStream = destination.Open();
                using var sourceStream = entry.Open();
                if (entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(sourceStream, Encoding.UTF8);
                    var text = reader.ReadToEnd();
                    using var writer = new StreamWriter(destinationStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: false);
                    writer.Write(replacement(text));
                }
                else
                {
                    sourceStream.CopyTo(destinationStream);
                }
            }
        }

        return outputPath;
    }

    public static string RemoveEntry(string packagePath, string entryName, string? outputName = null)
    {
        return ReplaceEntries(packagePath, outputName, entry => !entry.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase));
    }

    public static string AddEntry(string packagePath, string entryName, byte[] content, string? outputName = null)
    {
        var outputPath = Path.Combine(Path.GetDirectoryName(packagePath)!, outputName ?? Path.GetFileNameWithoutExtension(packagePath) + ".added.nupkg");
        using (var input = ZipFile.OpenRead(packagePath))
        using (var output = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            foreach (var entry in input.Entries)
            {
                var destination = output.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                using var destinationStream = destination.Open();
                using var sourceStream = entry.Open();
                sourceStream.CopyTo(destinationStream);
            }

            var added = output.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var addedStream = added.Open();
            addedStream.Write(content);
        }

        return outputPath;
    }

    public static string ReplaceEntry(string packagePath, string entryName, byte[] content, string? outputName = null)
    {
        return ReplaceEntries(packagePath, outputName, entry => true, (entry, destination) =>
        {
            if (entry.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase))
            {
                destination.Write(content);
                return true;
            }

            return false;
        });
    }

    private static string ReplaceEntries(
        string packagePath,
        string? outputName,
        Func<ZipArchiveEntry, bool> include,
        Func<ZipArchiveEntry, Stream, bool>? replace = null)
    {
        var outputPath = Path.Combine(Path.GetDirectoryName(packagePath)!, outputName ?? Path.GetFileNameWithoutExtension(packagePath) + ".mutated.nupkg");
        using (var input = ZipFile.OpenRead(packagePath))
        using (var output = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            foreach (var entry in input.Entries.Where(include))
            {
                var destination = output.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                using var destinationStream = destination.Open();
                if (replace is not null && replace(entry, destinationStream))
                {
                    continue;
                }

                using var sourceStream = entry.Open();
                sourceStream.CopyTo(destinationStream);
            }
        }

        return outputPath;
    }
}
