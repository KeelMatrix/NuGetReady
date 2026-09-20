using System.IO.Compression;
using Xunit.Abstractions;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class ConsumerRehearsalTests
{
    private readonly ITestOutputHelper testOutput;

    public ConsumerRehearsalTests(ITestOutputHelper testOutput)
    {
        this.testOutput = testOutput;
    }

    [Fact]
    public void Packed_library_multitarget_build_assets_and_tool_rehearse_in_isolation()
    {
        using var corpus = PackedCorpus.Create();
        var standard = corpus.Pack("Standard/Standard.csproj");
        var multiTarget = corpus.Pack("MultiTarget/MultiTarget.csproj");
        var buildAssets = corpus.Pack("BuildAssets/BuildAssets.csproj");
        var tool = corpus.Pack("Tool/Tool.csproj");
        var config = Config(
            new PackageExpectation { Id = "Fixture.Standard", Kind = "library", Version = "1.0.0", Artifacts = Artifacts(standard) },
            new PackageExpectation { Id = "Fixture.MultiTarget", Kind = "multiTargetLibrary", Version = "1.0.0", Artifacts = Artifacts(multiTarget) },
            new PackageExpectation { Id = "Fixture.BuildAssets", Kind = "library", Version = "1.0.0", Artifacts = Artifacts(buildAssets) },
            new PackageExpectation { Id = "Fixture.Tool", Kind = "dotnetTool", Version = "1.0.0", Artifacts = Artifacts(tool), Command = "fixture-tool", Smoke = new List<string> { "--help" } });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath));

        Assert.Equal(4, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.Equal("pass", outcome.Result.Status));
        Assert.Contains("net8.0", outcomes.Single(outcome => outcome.Result.PackageId == "Fixture.MultiTarget").Result.Message, StringComparison.Ordinal);
        Assert.Contains("netstandard2.1", outcomes.Single(outcome => outcome.Result.PackageId == "Fixture.MultiTarget").Result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("consumer-api:", outcomes.Single(outcome => outcome.Result.PackageId == "Fixture.Standard").Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Related_packages_rehearse_with_the_internal_dependency_from_the_local_feed()
    {
        using var corpus = PackedCorpus.Create();
        var core = corpus.Pack("Related.Core/Related.Core.csproj");
        var consumer = corpus.Pack("Related.Consumer/Related.Consumer.csproj", corpus.OutputPath);
        var config = Config(
            new PackageExpectation { Id = "Fixture.Related.Core", Kind = "library", Version = "1.0.0", Artifacts = Artifacts(core) },
            new PackageExpectation { Id = "Fixture.Related.Consumer", Kind = "library", Version = "1.0.0", Artifacts = Artifacts(consumer) });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath));

        Assert.All(outcomes, outcome => Assert.Equal("pass", outcome.Result.Status));
    }

    [Fact]
    public void Related_packages_rehearse_when_declared_in_reverse_dependency_order()
    {
        using var corpus = PackedCorpus.Create();
        var core = corpus.Pack("Related.Core/Related.Core.csproj");
        var consumer = corpus.Pack("Related.Consumer/Related.Consumer.csproj", corpus.OutputPath);
        var config = Config(
            new PackageExpectation { Id = "Fixture.Related.Consumer", Kind = "library", Version = "1.0.0", Artifacts = Artifacts(consumer) },
            new PackageExpectation { Id = "Fixture.Related.Core", Kind = "library", Version = "1.0.0", Artifacts = Artifacts(core) });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath));

        Assert.All(outcomes, outcome => Assert.Equal("pass", outcome.Result.Status));
    }

    [Fact]
    public void Public_dependencies_resolve_from_the_intended_public_source()
    {
        using var corpus = PackedCorpus.Create();
        var publicDependency = corpus.Pack("PublicSource/PublicSource.csproj");
        var package = corpus.Pack("PublicDependency/PublicDependency.csproj", corpus.OutputPath);
        var publicFeed = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "public-feed"));
        File.Copy(publicDependency, Path.Combine(publicFeed.FullName, Path.GetFileName(publicDependency)));
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.PublicDependency",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = Artifacts(package)
        });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: publicFeed.FullName));

        Assert.Single(outcomes);
        Assert.True(outcomes[0].Result.Status == "pass", outcomes[0].Diagnostic);
    }

    [Fact]
    public void Transitive_public_dependencies_outside_allowlist_resolve_from_the_public_source()
    {
        using var corpus = PackedCorpus.Create();
        var publicLeaf = corpus.Pack("PublicSource/PublicSource.csproj");
        var publicMiddleOriginal = corpus.Pack("Related.Core/Related.Core.csproj");
        var publicMiddle = ArchiveMutator.ReplaceNuspecText(
            publicMiddleOriginal,
            text => text.Replace(
                "</metadata>",
                "<dependencies><group targetFramework=\"net8.0\"><dependency id=\"Fixture.Public.Source\" version=\"[1.0.0]\" /></group></dependencies></metadata>",
                StringComparison.Ordinal),
            "public-middle.nupkg");
        var localOriginal = corpus.Pack("Related.Consumer/Related.Consumer.csproj", corpus.OutputPath);
        var localPackage = ArchiveMutator.ReplaceNuspecText(localOriginal, text => text, "local-app.nupkg");
        var localArtifacts = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "local-artifacts"));
        File.Copy(localPackage, Path.Combine(localArtifacts.FullName, "Fixture.Related.Consumer.1.0.0.nupkg"));

        var publicFeed = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "transitive-public-feed"));
        File.Copy(publicMiddle, Path.Combine(publicFeed.FullName, "Fixture.Related.Core.1.0.0.nupkg"));
        File.Copy(publicLeaf, Path.Combine(publicFeed.FullName, "Fixture.Public.Source.1.0.0.nupkg"));

        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Related.Consumer",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = new List<string> { "Fixture.Related.Consumer.1.0.0.nupkg" }
        });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            localArtifacts.FullName,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: publicFeed.FullName));

        Assert.Single(outcomes);
        Assert.Equal("pass", outcomes[0].Result.Status);
    }

    [Fact]
    public void Unavailable_public_dependency_is_reported_as_infrastructure_error()
    {
        using var corpus = PackedCorpus.Create();
        corpus.Pack("PublicSource/PublicSource.csproj");
        var package = corpus.Pack("PublicDependency/PublicDependency.csproj", corpus.OutputPath);
        var artifactsPath = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "unavailable-public-artifacts"));
        File.Copy(package, Path.Combine(artifactsPath.FullName, Path.GetFileName(package)));
        var unavailablePublicFeed = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "unavailable-public-feed"));
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.PublicDependency",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = Artifacts(package)
        });

        var report = CheckRunner.Run(
            config,
            artifactsPath.FullName,
            corpus.RepositoryRoot,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: unavailablePublicFeed.FullName));

        Assert.Equal("error", report.Status);
        Assert.Equal(2, report.ExitCode);
        var failure = Assert.Single(report.Failures, failure => failure.CheckId == "consumer-rehearsal");
        Assert.Contains("source", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NU1101", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(corpus.Root.FullName, failure.Message, StringComparison.OrdinalIgnoreCase);
        testOutput.WriteLine($"UNAVAILABLE_SOURCE status={report.Status} exitCode={report.ExitCode} message={failure.Message}");
    }

    [Fact]
    public void Warning_only_bad_image_diagnostic_is_a_readiness_failure()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var artifactsPath = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "warning-only-artifacts"));
        File.Copy(package, Path.Combine(artifactsPath.FullName, Path.GetFileName(package)));
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Standard",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = Artifacts(package)
        });
        static Task<ProcessResult> WarningOnlyBadImage(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string?> environment,
            TimeSpan timeout) => Task.FromResult(new ProcessResult(
                Started: true,
                ExitCode: 0,
                TimedOut: false,
                StandardOutput: string.Empty,
                StandardError: "warning MSB3246: bad image in C:\\Users\\test user\\NuGetReady\\bad.dll; metadata is invalid"));

        var report = CheckRunner.Run(
            config,
            artifactsPath.FullName,
            corpus.RepositoryRoot,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(ProcessRunner: WarningOnlyBadImage));

        Assert.Equal("fail", report.Status);
        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure =>
            failure.CheckId == "consumer-rehearsal" &&
            failure.Message.Contains("MSB3246", StringComparison.Ordinal));
        Assert.DoesNotContain("C:\\Users\\test user", report.Failures.Single(failure => failure.CheckId == "consumer-rehearsal").Message, StringComparison.OrdinalIgnoreCase);
        testOutput.WriteLine($"WARNING_ONLY_BAD_IMAGE status={report.Status} exitCode={report.ExitCode} marker=MSB3246");
    }

    [Fact]
    public void Public_substitute_cannot_satisfy_a_missing_local_feed()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var publicFeed = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "public-feed"));
        File.Copy(package, Path.Combine(publicFeed.FullName, Path.GetFileName(package)));
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Standard",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = Artifacts(package)
        });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(IncludeLocalFeed: false, PublicFeedPath: publicFeed.FullName));

        Assert.Single(outcomes);
        Assert.Equal("error", outcomes[0].Result.Status);
        Assert.True(
            outcomes[0].Diagnostic.Contains("NU1101", StringComparison.OrdinalIgnoreCase) ||
            outcomes[0].Diagnostic.Contains("NU1100", StringComparison.OrdinalIgnoreCase) ||
            outcomes[0].Diagnostic.Contains("Unable to resolve", StringComparison.OrdinalIgnoreCase) ||
            outcomes[0].Diagnostic.Contains("Unable to find package", StringComparison.OrdinalIgnoreCase),
            outcomes[0].Diagnostic);
    }

    [Fact]
    public void Fallback_cache_cannot_replace_the_supplied_artifact_or_local_feed()
    {
        using var corpus = PackedCorpus.Create();
        var goodPackage = corpus.Pack("Standard/Standard.csproj");
        var failingPackage = ArchiveMutator.AddEntry(
            goodPackage,
            "buildTransitive/Fixture.Standard.targets",
            System.Text.Encoding.UTF8.GetBytes("<Project><Target Name=\"NuGetReadyFallbackRegression\" BeforeTargets=\"Build\"><Error Text=\"local artifact was restored\" /></Target></Project>"),
            "Fixture.Standard.failing.nupkg");
        var localArtifacts = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "fallback-local-artifacts"));
        File.Copy(failingPackage, Path.Combine(localArtifacts.FullName, "Fixture.Standard.1.0.0.nupkg"));

        var fallbackRoot = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "fallback-packages"));
        var fallbackPackage = Directory.CreateDirectory(Path.Combine(fallbackRoot.FullName, "fixture.standard", "1.0.0"));
        ZipFile.OpenRead(goodPackage).ExtractToDirectory(fallbackPackage.FullName);
        using var environment = new EnvironmentVariableScope("NUGET_FALLBACK_PACKAGES", fallbackRoot.FullName);

        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Standard",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = new List<string> { "Fixture.Standard.1.0.0.nupkg" }
        });

        var controlledFeedOutcome = ConsumerRehearsal.RunDetailed(
            config,
            localArtifacts.FullName,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "empty-public-feed")).FullName));
        Assert.Equal("fail", controlledFeedOutcome.Single().Result.Status);

        var missingLocalFeedOutcome = ConsumerRehearsal.RunDetailed(
            config,
            localArtifacts.FullName,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(
                IncludeLocalFeed: false,
                PublicFeedPath: Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "empty-public-feed-2")).FullName));
        Assert.Equal("error", missingLocalFeedOutcome.Single().Result.Status);
    }

    [Fact]
    public void Child_diagnostics_are_structured_and_do_not_leak_credentials_or_timings()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Standard",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = Artifacts(package)
        });

        static Task<ProcessResult> FailingProcess(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string?> environment,
            TimeSpan timeout) => Task.FromResult(new ProcessResult(
                Started: true,
                ExitCode: 1,
                TimedOut: false,
                StandardOutput: string.Empty,
                StandardError: "Authorization: Bearer super-secret elapsed 17ms"));

        var first = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(ProcessRunner: FailingProcess));
        var second = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(ProcessRunner: FailingProcess));

        Assert.Equal(first.Single().Result, second.Single().Result);
        Assert.DoesNotContain("super-secret", first.Single().Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("17ms", first.Single().Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", first.Single().Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Package_under_test_matching_a_public_pattern_cannot_use_that_source()
    {
        using var corpus = PackedCorpus.Create();
        var package = ArchiveMutator.ReplaceNuspecText(
            corpus.Pack("Standard/Standard.csproj"),
            text => text.Replace("<id>Fixture.Standard</id>", "<id>System.Fixture.Standard</id>", StringComparison.Ordinal),
            "system-fixture.nupkg");
        var publicFeed = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "public-pattern-feed"));
        File.Copy(package, Path.Combine(publicFeed.FullName, Path.GetFileName(package)));
        var config = Config(new PackageExpectation
        {
            Id = "System.Fixture.Standard",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = Artifacts(package)
        });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(IncludeLocalFeed: false, PublicFeedPath: publicFeed.FullName));

        Assert.Single(outcomes);
        Assert.Equal("error", outcomes[0].Result.Status);
    }

    [Fact]
    public void Preexisting_cached_copy_is_rejected_before_restore()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Standard",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = Artifacts(package)
        });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(IncludeLocalFeed: false, SeedPackageCache: true));

        Assert.Single(outcomes);
        Assert.Equal("error", outcomes[0].Result.Status);
        Assert.Contains("cached substitution", outcomes[0].Result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Corrupted_library_asset_for_each_declared_target_framework_fails_the_check()
    {
        using var corpus = PackedCorpus.Create();
        var standard = corpus.Pack("Standard/Standard.csproj");
        var multiTarget = corpus.Pack("MultiTarget/MultiTarget.csproj");
        var buildAssets = corpus.Pack("BuildAssets/BuildAssets.csproj");
        var relatedCore = corpus.Pack("Related.Core/Related.Core.csproj");
        var relatedConsumer = corpus.Pack("Related.Consumer/Related.Consumer.csproj", corpus.OutputPath);
        var publicSource = corpus.Pack("PublicSource/PublicSource.csproj");
        var publicDependency = corpus.Pack("PublicDependency/PublicDependency.csproj", corpus.OutputPath);
        var publicFeed = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "corruption-public-feed"));
        foreach (var packagePath in new[] { publicSource, publicDependency, relatedCore, relatedConsumer, standard, multiTarget, buildAssets })
        {
            File.Copy(packagePath, Path.Combine(publicFeed.FullName, Path.GetFileName(packagePath)));
        }
        var fixtures = new List<(string Id, string Kind, string PackagePath, List<(string Id, string Kind, string PackagePath)> Dependencies)>
        {
            ("Fixture.Standard", "library", standard, new()),
            ("Fixture.MultiTarget", "multiTargetLibrary", multiTarget, new()),
            ("Fixture.BuildAssets", "library", buildAssets, new()),
            ("Fixture.Related.Core", "library", relatedCore, new()),
            ("Fixture.Related.Consumer", "library", relatedConsumer, new() { ("Fixture.Related.Core", "library", relatedCore) }),
            ("Fixture.Public.Source", "library", publicSource, new()),
            ("Fixture.PublicDependency", "library", publicDependency, new() { ("Fixture.Public.Source", "library", publicSource) })
        };

        foreach (var fixture in fixtures)
        {
            using var archive = ZipFile.OpenRead(fixture.PackagePath);
            var libraryAssets = archive.Entries
                .Where(entry => entry.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
                .Where(entry => entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.FullName)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.NotEmpty(libraryAssets);
            foreach (var libraryAsset in libraryAssets)
            {
                var artifact = $"{fixture.Id}.1.0.0.nupkg";
                var corruptedArtifact = $"corrupted-{fixture.Id}-{libraryAsset.Replace('/', '-')}.nupkg";
                var corrupted = ArchiveMutator.ReplaceEntry(fixture.PackagePath, libraryAsset, new byte[64], corruptedArtifact);
                var caseRoot = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "corrupted", fixture.Id, libraryAsset.Replace('/', '-')));
                File.Copy(corrupted, Path.Combine(caseRoot.FullName, artifact));
                var expectations = new List<PackageExpectation>
                {
                    new()
                    {
                        Id = fixture.Id,
                        Kind = fixture.Kind,
                        Version = "1.0.0",
                        Artifacts = new List<string> { artifact }
                    }
                };
                foreach (var dependency in fixture.Dependencies)
                {
                    var dependencyArtifact = Path.GetFileName(dependency.PackagePath);
                    File.Copy(dependency.PackagePath, Path.Combine(caseRoot.FullName, dependencyArtifact));
                    expectations.Add(new PackageExpectation
                    {
                        Id = dependency.Id,
                        Kind = dependency.Kind,
                        Version = "1.0.0",
                        Artifacts = new List<string> { dependencyArtifact }
                    });
                }

                var report = CheckRunner.Run(
                    Config(expectations.ToArray()),
                    caseRoot.FullName,
                    corpus.RepositoryRoot,
                    TimeSpan.FromMinutes(2),
                    new ConsumerRehearsalOptions(PublicFeedPath: publicFeed.FullName));

                Assert.NotEqual("pass", report.Status);
                testOutput.WriteLine($"{fixture.Id} asset={libraryAsset} status={report.Status} exitCode={report.ExitCode}");
                Assert.True(report.ExitCode == 1, $"fixture={fixture.Id}; asset={libraryAsset}; status={report.Status}; exitCode={report.ExitCode}; failures={string.Join(" | ", report.Failures.Select(failure => failure.Message))}");
            }
        }
    }

    private static NuGetReadyConfig Config(params PackageExpectation[] packages)
    {
        return new NuGetReadyConfig { SchemaVersion = 1, Packages = packages.ToList() };
    }

    private static List<string> Artifacts(string package)
    {
        return new List<string> { Path.GetFileName(package) };
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string name;
        private readonly string? original;

        public EnvironmentVariableScope(string name, string value)
        {
            this.name = name;
            original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }
}
