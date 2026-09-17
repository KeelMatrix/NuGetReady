using System.Text;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class SeededCorpusTests : IClassFixture<RealCorpusFixture>
{
    private readonly RealCorpusFixture corpus;

    public SeededCorpusTests(RealCorpusFixture corpus)
    {
        this.corpus = corpus;
    }

    [Fact]
    public void Missing_package_readme_is_detected()
    {
        var package = ArchiveMutator.RemoveEntry(corpus.Standard, "README.md", "missing-readme.nupkg");
        var report = RunArchive(package, "Fixture.Standard", "missing-readme.nupkg");

        Assert.Contains(report.Failures, failure => failure.Message.Contains("README", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrong_package_id_and_version_are_detected()
    {
        var package = ArchiveMutator.ReplaceNuspecText(
            corpus.Standard,
            text => text.Replace("<id>Fixture.Standard</id>", "<id>Wrong.Package</id>", StringComparison.Ordinal)
                .Replace("<version>1.0.0</version>", "<version>2.0.0</version>", StringComparison.Ordinal),
            "wrong-identity.nupkg");
        var report = RunArchive(package, "Fixture.Standard", "wrong-identity.nupkg");

        Assert.Contains(report.Failures, failure => failure.Message.Contains("identity", StringComparison.Ordinal));
        Assert.Contains(report.Failures, failure => failure.Message.Contains("version", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_license_and_icon_are_detected()
    {
        var package = ArchiveMutator.ReplaceNuspecText(
            corpus.Standard,
            text => text.Replace("<license type=\"expression\">MIT</license>", string.Empty, StringComparison.Ordinal)
                .Replace("<icon>icon.png</icon>", string.Empty, StringComparison.Ordinal),
            "missing-metadata.nupkg");
        var report = RunArchive(package, "Fixture.Standard", "missing-metadata.nupkg");

        Assert.Contains(report.Failures, failure => failure.Message.Contains("license", StringComparison.Ordinal));
        Assert.Contains(report.Failures, failure => failure.Message.Contains("icon", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrong_tfm_dependency_group_is_detected()
    {
        var package = ArchiveMutator.ReplaceNuspecText(
            corpus.Standard,
            text => text.Replace("targetFramework=\"net8.0\"", "targetFramework=\"net7.0\"", StringComparison.Ordinal),
            "wrong-tfm-group.nupkg");
        var report = RunArchive(package, "Fixture.Standard", "wrong-tfm-group.nupkg");

        Assert.Contains(report.Failures, failure => failure.CheckId == "dependency-groups");
    }

    [Fact]
    public void Unexpected_internal_file_is_detected()
    {
        var package = ArchiveMutator.AddEntry(corpus.Standard, ".github/workflows/release.yml", Encoding.UTF8.GetBytes("permissions: write-all"), "unexpected-internal-file.nupkg");
        var report = RunArchive(package, "Fixture.Standard", "unexpected-internal-file.nupkg");

        Assert.Contains(report.Failures, failure => failure.CheckId == "archive-security");
    }

    [Fact]
    public void Local_or_private_dependency_cannot_restore_from_the_public_path()
    {
        var package = ArchiveMutator.ReplaceNuspecText(
            corpus.Standard,
            text => text.Replace("</metadata>", "<dependencies><group targetFramework=\"net8.0\"><dependency id=\"Private.Local.Package\" version=\"[1.0.0]\" /></group></dependencies></metadata>", StringComparison.Ordinal),
            "private-dependency.nupkg");
        using var scenario = Scenario(package, "Fixture.Standard", "private-dependency.nupkg");
        var publicFeed = Directory.CreateDirectory(Path.Combine(scenario.Root.FullName, "empty-public-feed"));

        var outcome = ConsumerRehearsal.RunDetailed(
            scenario.Config,
            scenario.ArtifactsPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: publicFeed.FullName)).Single();

        Assert.Equal("error", outcome.Result.Status);
        Assert.True(
            outcome.Diagnostic.Contains("NU1100", StringComparison.OrdinalIgnoreCase) ||
            outcome.Diagnostic.Contains("NU1101", StringComparison.OrdinalIgnoreCase) ||
            outcome.Diagnostic.Contains("Unable to resolve", StringComparison.OrdinalIgnoreCase) ||
            outcome.Diagnostic.Contains("Unable to find package", StringComparison.OrdinalIgnoreCase),
            outcome.Diagnostic);
    }

    [Fact]
    public void Missing_required_symbol_package_is_detected()
    {
        using var scenario = Scenario(corpus.Standard, "Fixture.Standard", "Fixture.Standard.1.0.0.nupkg");
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages = new List<PackageExpectation>
            {
                new()
                {
                    Id = "Fixture.Standard",
                    Kind = "library",
                    Version = "1.0.0",
                    Artifacts = new List<string> { "Fixture.Standard.1.0.0.nupkg", "Fixture.Standard.1.0.0.snupkg" }
                }
            }
        };

        var report = CheckRunner.Run(config, scenario.ArtifactsPath);

        Assert.Contains(report.Failures, failure => failure.Message.Contains("snupkg", StringComparison.OrdinalIgnoreCase) || failure.Message.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_symbols_inside_a_required_symbol_package_are_detected()
    {
        var package = ArchiveMutator.RemoveEntry(corpus.StandardSymbols, "lib/net8.0/Standard.pdb", "missing-symbol-file.snupkg");
        var report = RunArchive(package, "Fixture.Standard", "missing-symbol-file.snupkg", symbols: true);

        Assert.Contains(report.Failures, failure => failure.Message.Contains("symbol file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Wrong_internal_dependency_version_is_detected()
    {
        var package = ArchiveMutator.ReplaceNuspecText(
            corpus.RelatedConsumer,
            text => text.Replace("version=\"1.0.0\" exclude=\"Build,Analyzers\"", "version=\"2.0.0\" exclude=\"Build,Analyzers\"", StringComparison.Ordinal),
            "wrong-internal-version.nupkg");
        using var scenario = Scenario(package, "Fixture.Related.Consumer", "wrong-internal-version.nupkg");
        var config = scenario.Config;
        config.Packages!.Add(new PackageExpectation { Id = "Fixture.Related.Core", Kind = "library", Version = "1.0.0", Artifacts = new List<string> { Path.GetFileName(corpus.RelatedCore) } });
        File.Copy(corpus.RelatedCore, Path.Combine(scenario.ArtifactsPath, Path.GetFileName(corpus.RelatedCore)));

        var report = CheckRunner.Run(config, scenario.ArtifactsPath);

        Assert.Contains(report.Failures, failure => failure.CheckId == "dependency-coherence");
    }

    [Fact]
    public void Broad_unexpected_artifact_set_is_detected()
    {
        using var scenario = Scenario(corpus.Standard, "Fixture.Standard", "Fixture.Standard.1.0.0.nupkg");
        File.WriteAllBytes(Path.Combine(scenario.ArtifactsPath, "unexpected.1.0.0.nupkg"), [1, 2, 3]);

        var report = CheckRunner.Run(scenario.Config, scenario.ArtifactsPath);

        Assert.Contains(report.Failures, failure => failure.Message.Contains("Unintended artifact", StringComparison.Ordinal));
    }

    [Fact]
    public void Tool_that_installs_but_cannot_execute_is_detected()
    {
        var package = corpus.ToolFailure;
        using var scenario = Scenario(package, "Fixture.ToolFailure", Path.GetFileName(package));
        scenario.Config.Packages![0].Command = "fixture-tool-failure";
        scenario.Config.Packages[0].Smoke = new List<string> { "--help" };

        var outcome = ConsumerRehearsal.RunDetailed(scenario.Config, scenario.ArtifactsPath, TimeSpan.FromMinutes(2)).Single();

        Assert.Equal("fail", outcome.Result.Status);
        Assert.Contains("smoke", outcome.Result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tool_with_required_assembly_absent_is_detected()
    {
        var package = ArchiveMutator.RemoveEntry(corpus.Tool, "tools/net8.0/any/Fixture.Tool.dll", "missing-tool-assembly.nupkg");
        using var scenario = Scenario(package, "Fixture.Tool", "missing-tool-assembly.nupkg");
        scenario.Config.Packages![0].Command = "fixture-tool";
        scenario.Config.Packages[0].Smoke = new List<string> { "--help" };

        var outcome = ConsumerRehearsal.RunDetailed(scenario.Config, scenario.ArtifactsPath, TimeSpan.FromMinutes(2)).Single();

        Assert.NotEqual("pass", outcome.Result.Status);
    }

    private static ReadinessReport RunArchive(string package, string id, string artifact, bool symbols = false)
    {
        using var scenario = Scenario(package, id, artifact);
        return CheckRunner.Run(scenario.Config, scenario.ArtifactsPath);
    }

    private static SeededScenario Scenario(string package, string id, string artifact)
    {
        var root = Directory.CreateTempSubdirectory("nugetready-seeded-scenario-");
        var artifacts = Directory.CreateDirectory(Path.Combine(root.FullName, "packages"));
        File.Copy(package, Path.Combine(artifacts.FullName, artifact));
        return new SeededScenario(root, artifacts.FullName, new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages = new List<PackageExpectation>
            {
                new() { Id = id, Kind = id.Contains("Tool", StringComparison.Ordinal) ? "dotnetTool" : "library", Version = "1.0.0", Artifacts = new List<string> { artifact } }
            }
        });
    }
}

internal sealed class SeededScenario : IDisposable
{
    public SeededScenario(DirectoryInfo root, string artifactsPath, NuGetReadyConfig config)
    {
        Root = root;
        ArtifactsPath = artifactsPath;
        Config = config;
    }

    public DirectoryInfo Root { get; }

    public string ArtifactsPath { get; }

    public NuGetReadyConfig Config { get; }

    public void Dispose()
    {
        try
        {
            Root.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class RealCorpusFixture : IDisposable
{
    private readonly PackedCorpus packed;

    public RealCorpusFixture()
    {
        packed = PackedCorpus.Create();
        Standard = packed.Pack("Standard/Standard.csproj");
        StandardSymbols = Path.Combine(packed.OutputPath, "Fixture.Standard.1.0.0.snupkg");
        Tool = packed.Pack("Tool/Tool.csproj");
        ToolFailure = packed.Pack("ToolFailure/ToolFailure.csproj");
        RelatedCore = packed.Pack("Related.Core/Related.Core.csproj");
        RelatedConsumer = packed.Pack("Related.Consumer/Related.Consumer.csproj", packed.OutputPath);
    }

    public string Standard { get; }

    public string StandardSymbols { get; }

    public string Tool { get; }

    public string ToolFailure { get; }

    public string RelatedCore { get; }

    public string RelatedConsumer { get; }

    public void Dispose()
    {
        packed.Dispose();
    }
}
