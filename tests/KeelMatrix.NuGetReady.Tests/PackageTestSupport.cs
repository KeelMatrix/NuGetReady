using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
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
            "-p:UseSharedCompilation=false",
            // The harness can run inside a parent workspace that has its own
            // Directory.Build.targets. Fixture packs must be self-contained.
            "-p:ImportDirectoryBuildTargets=false"
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

internal static class PublicCliTestSupport
{
    public static int Run(PackedCorpus corpus, IReadOnlyList<PackageExpectation> packages, out string output)
    {
        var configPath = Path.Combine(corpus.Root.FullName, "nugetready.json");
        File.WriteAllText(
            configPath,
            System.Text.Json.JsonSerializer.Serialize(new NuGetReadyConfig
            {
                SchemaVersion = 1,
                Packages = packages.ToList()
            }));

        var originalOutput = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            return NuGetReadyApplication.Run(
                ["check", "--config", configPath, "--artifacts", corpus.OutputPath, "--format", "text"],
                new NuGetReadyTelemetry());
        }
        finally
        {
            Console.SetOut(originalOutput);
            output = captured.ToString();
        }
    }
}

internal static class ArchiveMutator
{
    public static (string PackagePath, string SymbolsPath) RewriteSourceLinkMappings(
        string packagePath,
        string symbolsPath,
        string sourceLinkJson,
        string packageOutputName,
        string symbolsOutputName)
    {
        var (pdbPath, pdbBytes, sourceLinkBlob, idOffset) = ReadSourceLinkPdb(symbolsPath);
        var replacement = Encoding.UTF8.GetBytes(sourceLinkJson);
        if (replacement.Length > sourceLinkBlob.Length)
        {
            throw new Xunit.Sdk.XunitException("The SourceLink fixture mapping is larger than the source PDB mapping blob.");
        }

        var sourceLinkOffset = IndexOf(pdbBytes, sourceLinkBlob);
        if (sourceLinkOffset < 0)
        {
            throw new Xunit.Sdk.XunitException("The SourceLink fixture mapping was not found in the PDB image.");
        }

        Array.Clear(pdbBytes, sourceLinkOffset, sourceLinkBlob.Length);
        replacement.CopyTo(pdbBytes.AsSpan(sourceLinkOffset));
        for (var index = replacement.Length; index < sourceLinkBlob.Length; index++)
        {
            pdbBytes[sourceLinkOffset + index] = (byte)' ';
        }

        var assemblyPath = ReadArchiveEntryPath(packagePath, path =>
            path.StartsWith("lib/net8.0/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        var assemblyBytes = ReadArchiveEntry(packagePath, assemblyPath);
        UpdatePdbChecksum(assemblyBytes, pdbBytes, idOffset);

        var updatedPackage = ReplaceEntry(packagePath, assemblyPath, assemblyBytes, packageOutputName);
        var updatedSymbols = ReplaceEntry(symbolsPath, pdbPath, pdbBytes, symbolsOutputName);
        return (updatedPackage, updatedSymbols);
    }

    public static string AddGeneratedEntries(string packagePath, int count, string outputName)
    {
        var outputPath = Path.Combine(Path.GetDirectoryName(packagePath)!, outputName);
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

            for (var index = 0; index < count; index++)
            {
                var entry = output.CreateEntry($"payload/{index}.bin", CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.WriteByte(1);
            }
        }

        return outputPath;
    }

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

    public static string ReplaceEntryPaths(string packagePath, Func<string, string> replacement, string? outputName = null)
    {
        var outputPath = Path.Combine(Path.GetDirectoryName(packagePath)!, outputName ?? Path.GetFileNameWithoutExtension(packagePath) + ".renamed.nupkg");
        using (var input = ZipFile.OpenRead(packagePath))
        using (var output = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            foreach (var entry in input.Entries)
            {
                var destination = output.CreateEntry(replacement(entry.FullName), CompressionLevel.NoCompression);
                using var destinationStream = destination.Open();
                using var sourceStream = entry.Open();
                sourceStream.CopyTo(destinationStream);
            }
        }

        return outputPath;
    }

    private static (string Path, byte[] Bytes, byte[] SourceLinkBlob, int IdOffset) ReadSourceLinkPdb(string symbolsPath)
    {
        var pdbPath = ReadArchiveEntryPath(symbolsPath, path => path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
        var pdbBytes = ReadArchiveEntry(symbolsPath, pdbPath);
        using var stream = new MemoryStream(pdbBytes, writable: false);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var metadata = provider.GetMetadataReader();
        var sourceLinkGuid = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");
        foreach (var handle in metadata.CustomDebugInformation)
        {
            var information = metadata.GetCustomDebugInformation(handle);
            if (metadata.GetGuid(information.Kind) == sourceLinkGuid)
            {
                return (pdbPath, pdbBytes, metadata.GetBlobBytes(information.Value), metadata.DebugMetadataHeader!.IdStartOffset);
            }
        }

        throw new Xunit.Sdk.XunitException("The SourceLink fixture PDB did not contain a SourceLink mapping.");
    }

    private static string ReadArchiveEntryPath(string archivePath, Func<string, bool> predicate)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .Where(predicate)
            .Single();
    }

    private static byte[] ReadArchiveEntry(string archivePath, string entryPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var stream = archive.GetEntry(entryPath)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void UpdatePdbChecksum(byte[] assemblyBytes, byte[] pdbBytes, int idOffset)
    {
        using var assemblyStream = new MemoryStream(assemblyBytes, writable: false);
        using var peReader = new PEReader(assemblyStream);
        var checksumEntry = peReader.ReadDebugDirectory()
            .FirstOrDefault(entry => entry.Type == DebugDirectoryEntryType.PdbChecksum);
        if (checksumEntry.Type != DebugDirectoryEntryType.PdbChecksum)
        {
            throw new Xunit.Sdk.XunitException("The SourceLink fixture assembly did not contain a PDB checksum entry.");
        }

        var checksum = peReader.ReadPdbChecksumDebugDirectoryData(checksumEntry);
        var checksumInput = (byte[])pdbBytes.Clone();
        Array.Clear(checksumInput, idOffset, 20);
        // PDB checksums may legitimately use legacy algorithms selected by
        // the compiler; this helper must reproduce the declared algorithm.
#pragma warning disable CA5350, CA5351
        using HashAlgorithm algorithm = checksum.AlgorithmName.ToUpperInvariant() switch
        {
            "SHA256" => SHA256.Create(),
            "SHA384" => SHA384.Create(),
            "SHA512" => SHA512.Create(),
            "SHA1" => SHA1.Create(),
            "MD5" => MD5.Create(),
            _ => throw new Xunit.Sdk.XunitException("The SourceLink fixture used an unsupported PDB checksum algorithm.")
        };
#pragma warning restore CA5350, CA5351
        var replacement = algorithm.ComputeHash(checksumInput);
        var offset = IndexOf(assemblyBytes, checksum.Checksum.ToArray());
        if (offset < 0)
        {
            throw new Xunit.Sdk.XunitException("The SourceLink fixture PDB checksum was not found in the assembly image.");
        }

        replacement.CopyTo(assemblyBytes.AsSpan(offset));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle))
            {
                return start;
            }
        }

        return -1;
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
