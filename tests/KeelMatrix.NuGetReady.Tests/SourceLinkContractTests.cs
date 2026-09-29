using System.IO.Compression;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class SourceLinkContractTests
{
    [Fact]
    public void Public_cli_rehearses_source_link_mapping_classes_and_rejects_uncovered_documents()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("SourceLinkSingle/SourceLinkSingle.csproj");
        var symbols = Path.Combine(corpus.OutputPath, "Fixture.SourceLinkSingle.1.0.0.snupkg");
        var packages = new List<PackageExpectation>();

        AddVariant(
            corpus,
            package,
            symbols,
            packages,
            "Fixture.SourceLinkWildcard",
            sourceLinkJson: null);
        AddVariant(
            corpus,
            package,
            symbols,
            packages,
            "Fixture.SourceLinkExact",
            "{\"documents\":{\"/_/a.cs\":\"https://e/a\"}}");
        AddVariant(
            corpus,
            package,
            symbols,
            packages,
            "Fixture.SourceLinkMixed",
            "{\"documents\":{\"/_/a.cs\":\"https://e/a\",\"/_/*\":\"https://w/*\"}}");
        AddVariant(
            corpus,
            package,
            symbols,
            packages,
            "Fixture.SourceLinkOverlapping",
            "{\"documents\":{\"/_/*\":\"https://w/*\",\"/_/a*\":\"https://a/*\"}}");
        AddVariant(
            corpus,
            package,
            symbols,
            packages,
            "Fixture.SourceLinkUncovered",
            "{\"documents\":{\"/_/missing.cs\":\"https://e/m\"}}");

        File.Delete(package);
        File.Delete(symbols);

        var exitCode = PublicCliTestSupport.Run(corpus, packages, out var output);

        Assert.True(exitCode == 1, $"exitCode={exitCode}; output={output}");
        Assert.Contains("Fixture.SourceLinkUncovered", output, StringComparison.Ordinal);
        Assert.Contains("does not cover all source documents", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fixture.SourceLinkWildcard", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Fixture.SourceLinkExact", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Fixture.SourceLinkMixed", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Fixture.SourceLinkOverlapping", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_cli_rejects_partial_package_symbol_coverage_without_using_an_unrelated_pdb_as_an_exclusion()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var symbols = Path.Combine(corpus.OutputPath, "Fixture.Standard.1.0.0.snupkg");
        byte[] assembly;
        byte[] pdb;
        using (var archive = ZipFile.OpenRead(package))
        {
            using var assemblyStream = archive.Entries.Single(entry => entry.FullName == "lib/net8.0/Standard.dll").Open();
            using var assemblyBuffer = new MemoryStream();
            assemblyStream.CopyTo(assemblyBuffer);
            assembly = assemblyBuffer.ToArray();
        }

        using (var archive = ZipFile.OpenRead(symbols))
        {
            using var pdbStream = archive.Entries.Single(entry => entry.FullName == "lib/net8.0/Standard.pdb").Open();
            using var pdbBuffer = new MemoryStream();
            pdbStream.CopyTo(pdbBuffer);
            pdb = pdbBuffer.ToArray();
        }

        var mutatedPackage = ArchiveMutator.AddEntry(
            package,
            "lib/net8.0/Owned.Second.dll",
            assembly,
            "partial-symbol-coverage.nupkg");
        var mutatedSymbols = ArchiveMutator.AddEntry(
            symbols,
            "lib/net8.0/Unrelated.pdb",
            pdb,
            "partial-symbol-coverage.snupkg");
        var packageWithSecondAssembly = Path.Combine(corpus.OutputPath, "Fixture.Standard.1.0.0.nupkg");
        var symbolsWithUnrelatedPdb = Path.Combine(corpus.OutputPath, "Fixture.Standard.1.0.0.snupkg");
        File.Delete(package);
        File.Delete(symbols);
        File.Move(mutatedPackage, packageWithSecondAssembly);
        File.Move(mutatedSymbols, symbolsWithUnrelatedPdb);

        var exitCode = PublicCliTestSupport.Run(
            corpus,
            [new PackageExpectation
            {
                Id = "Fixture.Standard",
                Kind = "library",
                Version = "1.0.0",
                Artifacts = [Path.GetFileName(packageWithSecondAssembly), Path.GetFileName(symbolsWithUnrelatedPdb)]
            }],
            out var output);

        Assert.True(exitCode == 1, $"exitCode={exitCode}; output={output}");
        Assert.Contains("does not provide PDB coverage for every package assembly", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Public_cli_accepts_declared_dependency_and_framework_symbol_controls()
    {
        using var corpus = PackedCorpus.Create();
        var standard = corpus.Pack("Standard/Standard.csproj");
        var dependency = corpus.Pack("GenericImplementation/GenericImplementation.csproj");
        var dependencyAssembly = ReadArchiveEntry(dependency, "lib/net8.0/GenericImplementation.dll");

        var withDeclaredDependency = ArchiveMutator.AddEntry(
            standard,
            "tools/net8.0/any/Fixture.GenericImplementation.dll",
            dependencyAssembly,
            "standard-with-dependency.nupkg");
        var withFrameworkControl = ArchiveMutator.AddEntry(
            withDeclaredDependency,
            "tools/net8.0/any/System.Runtime.FrameworkOnly.dll",
            dependencyAssembly,
            "standard-with-symbol-controls.nupkg");
        var finalPackage = ArchiveMutator.ReplaceNuspecText(
            withFrameworkControl,
            text => AddDependencyGroup(text, "Fixture.GenericImplementation"),
            "standard-with-dependency-final.nupkg");

        File.Delete(standard);
        File.Delete(withDeclaredDependency);
        File.Delete(withFrameworkControl);
        File.Move(finalPackage, standard);

        var exitCode = PublicCliTestSupport.Run(
            corpus,
            [
                LibraryExpectation("Fixture.Standard", standard),
                LibraryExpectation("Fixture.GenericImplementation", dependency)
            ],
            out var output);

        Assert.True(exitCode == 0, $"exitCode={exitCode}; output={output}");
    }

    private static void AddVariant(
        PackedCorpus corpus,
        string package,
        string symbols,
        List<PackageExpectation> packages,
        string id,
        string? sourceLinkJson)
    {
        string finalPackage;
        string finalSymbols = Path.Combine(corpus.OutputPath, id + ".1.0.0.snupkg");
        if (sourceLinkJson is null)
        {
            finalPackage = ArchiveMutator.ReplaceNuspecText(
                package,
                text => text.Replace("<id>Fixture.SourceLinkSingle</id>", $"<id>{id}</id>", StringComparison.Ordinal),
                id + ".1.0.0.nupkg");
            finalSymbols = ArchiveMutator.ReplaceNuspecText(
                symbols,
                text => text.Replace("<id>Fixture.SourceLinkSingle</id>", $"<id>{id}</id>", StringComparison.Ordinal),
                Path.GetFileName(finalSymbols));
        }
        else
        {
            var raw = ArchiveMutator.RewriteSourceLinkMappings(
                package,
                symbols,
                sourceLinkJson,
                id + ".raw.nupkg",
                id + ".raw.snupkg");
            finalPackage = ArchiveMutator.ReplaceNuspecText(
                raw.PackagePath,
                text => text.Replace("<id>Fixture.SourceLinkSingle</id>", $"<id>{id}</id>", StringComparison.Ordinal),
                id + ".1.0.0.nupkg");
            File.Delete(raw.PackagePath);
            var renamedSymbols = ArchiveMutator.ReplaceNuspecText(
                raw.SymbolsPath,
                text => text.Replace("<id>Fixture.SourceLinkSingle</id>", $"<id>{id}</id>", StringComparison.Ordinal),
                Path.GetFileName(finalSymbols));
            File.Delete(raw.SymbolsPath);
            File.Move(renamedSymbols, finalSymbols);
        }

        packages.Add(new PackageExpectation
        {
            Id = id,
            Kind = "library",
            Version = "1.0.0",
            Artifacts = [Path.GetFileName(finalPackage), Path.GetFileName(finalSymbols)]
        });
    }

    private static string AddDependencyGroup(string nuspec, string dependencyId)
    {
        var dependencyGroup = $"<dependencies><group targetFramework=\"net8.0\"><dependency id=\"{dependencyId}\" version=\"[1.0.0]\" /></group></dependencies>";
        if (nuspec.Contains("<dependencies />", StringComparison.Ordinal))
        {
            return nuspec.Replace("<dependencies />", dependencyGroup, StringComparison.Ordinal);
        }

        var start = nuspec.IndexOf("<dependencies>", StringComparison.Ordinal);
        var end = nuspec.IndexOf("</dependencies>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            return nuspec[..start] + dependencyGroup + nuspec[(end + "</dependencies>".Length)..];
        }

        throw new Xunit.Sdk.XunitException($"The fixture nuspec did not contain a dependency section for {dependencyId}.");
    }

    private static PackageExpectation LibraryExpectation(string id, string package)
    {
        return new PackageExpectation
        {
            Id = id,
            Kind = "library",
            Version = "1.0.0",
            Artifacts =
            [
                Path.GetFileName(package),
                Path.GetFileNameWithoutExtension(package) + ".snupkg"
            ]
        };
    }

    private static byte[] ReadArchiveEntry(string archivePath, string entryName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var source = archive.Entries.Single(entry => entry.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase)).Open();
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    }
}
