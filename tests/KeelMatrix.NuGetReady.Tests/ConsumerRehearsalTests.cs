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

    [Theory]
    [InlineData("net8.0", "Runnable")]
    [InlineData("netstandard2.0", "BuildOnly")]
    [InlineData("net48", "BuildOnly")]
    [InlineData("net481", "BuildOnly")]
    [InlineData("net8.0-windows", "Runnable")]
    [InlineData("net8.0-android", "Unsupported")]
    public void Framework_support_model_is_explicit_and_framework_aware(string framework, string expectedName)
    {
        var expected = Enum.Parse<ConsumerRehearsal.ConsumerTargetFrameworkSupport>(expectedName);
        if (framework is "net48" or "net481")
        {
            expected = OperatingSystem.IsWindows()
                ? ConsumerRehearsal.ConsumerTargetFrameworkSupport.BuildOnly
                : ConsumerRehearsal.ConsumerTargetFrameworkSupport.Unsupported;
        }

        if (framework == "net8.0-windows" && !OperatingSystem.IsWindows())
        {
            expected = ConsumerRehearsal.ConsumerTargetFrameworkSupport.Unsupported;
        }

        Assert.Equal(expected, ConsumerRehearsal.GetConsumerTargetFrameworkSupport(framework));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void Unconfirmed_cleanup_is_always_an_infrastructure_error(int exitCode, bool timedOut)
    {
        var outcome = ConsumerRehearsal.ClassifyProcessResult(
            new ProcessResult(true, exitCode, timedOut, string.Empty, string.Empty, CleanupConfirmed: false),
            ConsumerRehearsal.ProcessPhase.Build);

        Assert.False(outcome.Passed);
        Assert.True(outcome.IsError);
        Assert.Contains("unproven", outcome.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Public_cli_cancellation_returns_a_structured_error_report_and_exit_two()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        File.Delete(Path.ChangeExtension(package, ".snupkg"));
        var configPath = Path.Combine(corpus.Root.FullName, "nugetready.json");
        File.WriteAllText(configPath, $$"""
            {
              "schemaVersion": 1,
              "packages": [
                {
                  "id": "Fixture.Standard",
                  "kind": "library",
                  "version": "1.0.0",
                  "artifacts": ["{{Path.GetFileName(package)}}"]
                }
              ]
            }
            """);

        using var telemetryOptOut = new EnvironmentVariableScope("KEELMATRIX_NO_TELEMETRY", "1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = NuGetReadyApplication.Run(
                ["check", "--config", configPath, "--artifacts", corpus.OutputPath, "--format", "json"],
                new NuGetReadyTelemetry(),
                cancellation.Token);

            Assert.True(exitCode == 2, $"exitCode={exitCode}; report={output}");
            using var report = System.Text.Json.JsonDocument.Parse(output.ToString());
            Assert.Equal("error", report.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, report.RootElement.GetProperty("exitCode").GetInt32());
            var checks = report.RootElement.GetProperty("checks");
            Assert.Equal(CheckContract.Order.Length, checks.GetArrayLength());
            Assert.Equal("error", checks.EnumerateArray().Single(check => check.GetProperty("id").GetString() == "consumer-rehearsal").GetProperty("status").GetString());
            Assert.All(
                checks.EnumerateArray().Where(check => check.GetProperty("id").GetString() != "consumer-rehearsal"),
                check => Assert.Equal("not-run", check.GetProperty("status").GetString()));
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [Fact]
    public void Obsolete_first_public_type_is_not_selected_for_the_consumer_probe()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Documentation/Documentation.csproj");
        var outcomes = ConsumerRehearsal.RunDetailed(
            Config(new PackageExpectation
            {
                Id = "Fixture.Documentation",
                Kind = "library",
                Version = "1.0.0",
                Artifacts = Artifacts(package)
            }),
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath));

        Assert.Single(outcomes);
        Assert.Equal("pass", outcomes[0].Result.Status);
    }

    [Theory]
    [InlineData("net48")]
    [InlineData("net481")]
    public void Framework_aware_consumer_rehearsal_does_not_treat_net_framework_as_modern_dotnet(string framework)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var frameworkPackage = ArchiveMutator.ReplaceEntryPaths(
            package,
            path => path.Replace("lib/net8.0/", $"lib/{framework}/", StringComparison.OrdinalIgnoreCase),
            $"Fixture.Standard.{framework}.nupkg");
        var artifacts = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, framework));
        File.Copy(frameworkPackage, Path.Combine(artifacts.FullName, "Fixture.Standard.1.0.0.nupkg"));

        var outcomes = ConsumerRehearsal.RunDetailed(
            Config(new PackageExpectation
            {
                Id = "Fixture.Standard",
                Kind = "library",
                Version = "1.0.0",
                Artifacts = ["Fixture.Standard.1.0.0.nupkg"]
            }),
            artifacts.FullName,
            TimeSpan.FromSeconds(30),
            new ConsumerRehearsalOptions(PublicFeedPath: Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, $"{framework}-feed")).FullName));

        Assert.Single(outcomes);
        Assert.Equal("pass", outcomes[0].Result.Status);
    }

    [Fact]
    public void Consumer_rehearsal_is_not_influenced_by_custom_or_user_extension_imports()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var importRoot = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "ambient-msbuild-imports"));
        var before = Path.Combine(importRoot.FullName, "before.targets");
        var after = Path.Combine(importRoot.FullName, "after.targets");
        var userExtension = Path.Combine(importRoot.FullName, "Current", "Imports", "Microsoft.Common.props", "ImportBefore", "ambient.targets");
        Directory.CreateDirectory(Path.GetDirectoryName(userExtension)!);
        var failureTarget = "<Project><Target Name=\"AmbientImportMustNotRun\" BeforeTargets=\"CoreCompile\"><Error Text=\"ambient MSBuild import executed\" /></Target></Project>";
        File.WriteAllText(before, failureTarget);
        File.WriteAllText(after, failureTarget);
        File.WriteAllText(userExtension, failureTarget);

        using var beforeScope = new EnvironmentVariableScope("CustomBeforeMicrosoftCommonTargets", before);
        using var afterScope = new EnvironmentVariableScope("CustomAfterMicrosoftCommonTargets", after);
        using var userScope = new EnvironmentVariableScope("MSBuildUserExtensionsPath", importRoot.FullName);
        var outcomes = ConsumerRehearsal.RunDetailed(
            Config(new PackageExpectation
            {
                Id = "Fixture.Standard",
                Kind = "library",
                Version = "1.0.0",
                Artifacts = Artifacts(package)
            }),
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath));

        Assert.Equal("pass", outcomes.Single().Result.Status);
        Assert.DoesNotContain("ambient MSBuild import", outcomes.Single().Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unavailable_sdk_is_an_infrastructure_error_before_package_failure_classification()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        static Task<ProcessResult> MissingSdk(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string?> environment,
            TimeSpan timeout) => Task.FromResult(new ProcessResult(
                Started: true,
                ExitCode: 1,
                TimedOut: false,
                StandardOutput: string.Empty,
                StandardError: "No .NET SDKs were found.",
                CleanupConfirmed: true));

        var outcomes = ConsumerRehearsal.RunDetailed(
            Config(new PackageExpectation
            {
                Id = "Fixture.Standard",
                Kind = "library",
                Version = "1.0.0",
                Artifacts = Artifacts(package)
            }),
            corpus.OutputPath,
            TimeSpan.FromSeconds(30),
            new ConsumerRehearsalOptions(ProcessRunner: MissingSdk));

        Assert.Equal("error", outcomes.Single().Result.Status);
        Assert.Contains("infrastructure", outcomes.Single().Diagnostic, StringComparison.OrdinalIgnoreCase);
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
        Assert.All(outcomes.Where(outcome => outcome.Result.PackageId != "Fixture.Tool"), outcome => Assert.Equal("pass", outcome.Result.Status));
        Assert.Equal("pass", outcomes.Single(outcome => outcome.Result.PackageId == "Fixture.Tool").Result.Status);
        Assert.Contains("net8.0", outcomes.Single(outcome => outcome.Result.PackageId == "Fixture.MultiTarget").Result.Message, StringComparison.Ordinal);
        Assert.Contains("netstandard2.1", outcomes.Single(outcome => outcome.Result.PackageId == "Fixture.MultiTarget").Result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("consumer-api:", outcomes.Single(outcome => outcome.Result.PackageId == "Fixture.Standard").Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_install_requires_the_package_provenance_sidecar()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Tool/Tool.csproj");
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Tool",
            Kind = "dotnetTool",
            Version = "1.0.0",
            Artifacts = Artifacts(package),
            Command = "fixture-tool",
            Smoke = new List<string> { "--help" }
        });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(
                PublicFeedPath: Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "empty-public-feed")).FullName,
                ProcessRunner: AfterToolInstall((workingDirectory, packageDirectory, _) =>
                {
                    File.Delete(Directory.EnumerateFiles(packageDirectory, "*.nupkg.sha512", SearchOption.TopDirectoryOnly).Single());
                })));

        Assert.Single(outcomes);
        Assert.NotEqual("pass", outcomes[0].Result.Status);
        Assert.Contains("provenance sidecar", outcomes[0].Result.Message + outcomes[0].Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tool_rehearsal_rejects_a_command_that_does_not_match_the_installed_tool_metadata()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Tool/Tool.csproj");
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Tool",
            Kind = "dotnetTool",
            Version = "1.0.0",
            Artifacts = Artifacts(package),
            Command = "not-the-tool",
            Smoke = new List<string> { "--help" }
        });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "empty-public-feed")).FullName));

        Assert.Single(outcomes);
        Assert.NotEqual("pass", outcomes[0].Result.Status);
        Assert.Contains("command", outcomes[0].Result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tool_rehearsal_rejects_a_new_launch_file_after_provenance_validation()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Tool/Tool.csproj");
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Tool",
            Kind = "dotnetTool",
            Version = "1.0.0",
            Artifacts = Artifacts(package),
            Command = "fixture-tool",
            Smoke = new List<string> { "--help" }
        });

        var previousSnapshot = ConsumerRehearsal.BeforeToolLaunchSnapshotForTests;
        ConsumerRehearsal.BeforeToolLaunchSnapshotForTests = toolDirectory =>
            File.WriteAllText(Path.Combine(toolDirectory, "Fixture.Tool.Dependency.dll"), "pinned dependency");

        try
        {
            var outcomes = ConsumerRehearsal.RunDetailed(
                config,
                corpus.OutputPath,
                TimeSpan.FromMinutes(2),
                new ConsumerRehearsalOptions(PublicFeedPath: Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "empty-public-feed")).FullName));

            Assert.Equal("error", outcomes.Single().Result.Status);
            Assert.Contains("pinned", outcomes.Single().Result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ConsumerRehearsal.BeforeToolLaunchSnapshotForTests = previousSnapshot;
        }
    }

    [Fact]
    public void Tool_rehearsal_rejects_an_in_place_apphost_edit_after_snapshot()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Tool/Tool.csproj");
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Tool",
            Kind = "dotnetTool",
            Version = "1.0.0",
            Artifacts = Artifacts(package),
            Command = "fixture-tool",
            Smoke = new List<string> { "--help" }
        });

        var previous = ConsumerRehearsal.BeforeToolLaunchForTests;
        var replacementBlocked = false;
        ConsumerRehearsal.BeforeToolLaunchForTests = executable =>
        {
            try
            {
                using var apphost = new FileStream(executable, FileMode.Create, FileAccess.Write, FileShare.None);
                apphost.WriteByte(0);
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                replacementBlocked = true;
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                replacementBlocked = true;
            }
        };

        try
        {
            var outcomes = ConsumerRehearsal.RunDetailed(
                config,
                corpus.OutputPath,
                TimeSpan.FromMinutes(2),
                new ConsumerRehearsalOptions(PublicFeedPath: Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "empty-public-feed")).FullName));

            if (OperatingSystem.IsWindows() && replacementBlocked)
            {
                Assert.Equal("pass", outcomes.Single().Result.Status);
            }
            else
            {
                Assert.Equal("error", outcomes.Single().Result.Status);
                Assert.Contains("changed", outcomes.Single().Result.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            ConsumerRehearsal.BeforeToolLaunchForTests = previous;
        }
    }

    [Fact]
    public void Tool_rehearsal_does_not_launch_a_rebound_reparse_child()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Tool/Tool.csproj");
        var failingPackage = corpus.Pack("ToolFailure/ToolFailure.csproj");
        var linkTargetProbe = Path.Combine(corpus.Root.FullName, "link-target-probe");
        var linkProbe = Path.Combine(corpus.Root.FullName, "link-probe");
        File.WriteAllText(linkTargetProbe, "probe");
        try
        {
            File.CreateSymbolicLink(linkProbe, linkTargetProbe);
            File.Delete(linkProbe);
        }
        catch (Exception linkException) when (linkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Tool",
            Kind = "dotnetTool",
            Version = "1.0.0",
            Artifacts = Artifacts(package),
            Command = "fixture-tool",
            Smoke = new List<string> { "--help" }
        });

        var rebound = false;
        var replacementBlocked = false;
        var previous = ConsumerRehearsal.BeforeToolLaunchForTests;
        ConsumerRehearsal.BeforeToolLaunchForTests = executable =>
        {
            var outside = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "outside-tool-sentinel"));
            using var archive = ZipFile.OpenRead(failingPackage);
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("tools/net8.0/any/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entry.Name)))
            {
                var targetName = Path.GetFileName(entry.FullName)
                    .Replace("fixture-tool-failure", "fixture-tool", StringComparison.OrdinalIgnoreCase)
                    .Replace("Fixture.ToolFailure", "Fixture.Tool", StringComparison.OrdinalIgnoreCase);
                using var source = entry.Open();
                using var target = new FileStream(Path.Combine(outside.FullName, targetName), FileMode.Create, FileAccess.Write, FileShare.None);
                source.CopyTo(target);
            }

            var outsideExecutable = Path.Combine(outside.FullName, Path.GetFileName(executable));
            try
            {
                File.Delete(executable);
                File.CreateSymbolicLink(executable, outsideExecutable);
                rebound = true;
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                replacementBlocked = true;
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                replacementBlocked = true;
            }
        };

        try
        {
            var outcomes = ConsumerRehearsal.RunDetailed(
                config,
                corpus.OutputPath,
                TimeSpan.FromMinutes(2),
                new ConsumerRehearsalOptions(PublicFeedPath: Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "empty-public-feed")).FullName));

            Assert.True(rebound || replacementBlocked, "The reparse-child rebind probe did not run.");
            if (OperatingSystem.IsWindows())
            {
                if (replacementBlocked)
                {
                    Assert.Equal("pass", outcomes.Single().Result.Status);
                }
                else
                {
                    Assert.Equal("error", outcomes.Single().Result.Status);
                    Assert.Contains("changed", outcomes.Single().Result.Message, StringComparison.OrdinalIgnoreCase);
                }
            }
            else
            {
                Assert.Equal("error", outcomes.Single().Result.Status);
                Assert.Contains("changed", outcomes.Single().Result.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            ConsumerRehearsal.BeforeToolLaunchForTests = previous;
        }
    }

    [Fact]
    public void Tool_rehearsal_accepts_the_platform_executable_extension_boundary()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Tool/Tool.csproj");
        var command = OperatingSystem.IsWindows() ? "fixture-tool.exe" : "fixture-tool";
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Tool",
            Kind = "dotnetTool",
            Version = "1.0.0",
            Artifacts = Artifacts(package),
            Command = command,
            Smoke = new List<string> { "--help" }
        });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "empty-public-feed")).FullName));

        Assert.Single(outcomes);
        Assert.Equal("pass", outcomes[0].Result.Status);
    }

    [Fact]
    public void Windows_tool_rehearsal_accepts_an_explicit_exe_command_name()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Tool/Tool.csproj");
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Tool",
            Kind = "dotnetTool",
            Version = "1.0.0",
            Artifacts = Artifacts(package),
            Command = "fixture-tool.exe",
            Smoke = new List<string> { "--help" }
        });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "empty-public-feed")).FullName));

        Assert.Single(outcomes);
        Assert.Equal("pass", outcomes[0].Result.Status);
    }

    [Fact]
    public void Tool_install_rejects_changed_non_dll_payload_even_when_the_smoke_command_still_succeeds()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Tool/Tool.csproj");
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Tool",
            Kind = "dotnetTool",
            Version = "1.0.0",
            Artifacts = Artifacts(package),
            Command = "fixture-tool",
            Smoke = new List<string> { "--help" }
        });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(
                PublicFeedPath: Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "empty-public-feed")).FullName,
                ProcessRunner: AfterToolInstall((workingDirectory, packageDirectory, environment) =>
                {
                    var executable = Path.Combine(workingDirectory, "tool", OperatingSystem.IsWindows() ? "fixture-tool.exe" : "fixture-tool");
                    var smoke = BoundedProcess.RunAsync(executable, ["--help"], workingDirectory, environment, TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
                    Assert.True(smoke.Started && !smoke.TimedOut && smoke.ExitCode == 0, $"The mutated tool smoke command did not succeed: {smoke.StandardError}");
                    var runtimeConfig = Directory.EnumerateFiles(packageDirectory, "*.runtimeconfig.json", SearchOption.AllDirectories).Single();
                    File.AppendAllText(runtimeConfig, Environment.NewLine);
                })));

        Assert.Single(outcomes);
        Assert.NotEqual("pass", outcomes[0].Result.Status);
        Assert.True(
            (outcomes[0].Result.Message + outcomes[0].Diagnostic).Contains("contents", StringComparison.OrdinalIgnoreCase),
            outcomes[0].Result.Message + " " + outcomes[0].Diagnostic);
    }

    [Fact]
    public void Tool_install_derives_a_non_net8_asset_layout_from_the_package()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Tool/Tool.csproj");
        var nonNet8Package = ArchiveMutator.ReplaceEntryPaths(
            package,
            path => path.Replace("tools/net8.0/any/", "tools/net7.0/any/", StringComparison.OrdinalIgnoreCase),
            "Fixture.Tool.non-net8.nupkg");
        var nonNet8Artifacts = Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "non-net8-artifacts"));
        var nonNet8Artifact = Path.Combine(nonNet8Artifacts.FullName, "Fixture.Tool.1.0.0.nupkg");
        File.Copy(nonNet8Package, nonNet8Artifact);
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Tool",
            Kind = "dotnetTool",
            Version = "1.0.0",
            Artifacts = Artifacts(nonNet8Artifact),
            Command = "fixture-tool",
            Smoke = new List<string> { "--help" }
        });

        var outcomes = ConsumerRehearsal.RunDetailed(
            config,
            nonNet8Artifacts.FullName,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(
                PublicFeedPath: Directory.CreateDirectory(Path.Combine(corpus.Root.FullName, "empty-public-feed")).FullName));

        Assert.Single(outcomes);
        Assert.Equal("pass", outcomes[0].Result.Status);
    }

    [Fact]
    public void Duplicate_primary_archives_for_one_identity_never_pass_consumer_rehearsal()
    {
        using var corpus = PackedCorpus.Create();
        var goodPackage = corpus.Pack("Standard/Standard.csproj");
        File.Delete(Path.ChangeExtension(goodPackage, ".snupkg"));
        var brokenPackage = ArchiveMutator.ReplaceEntry(
            goodPackage,
            "lib/net8.0/Fixture.Standard.dll",
            new byte[64],
            "Fixture.Standard.alternate.nupkg");
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Standard",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = new List<string>
            {
                Path.GetFileName(goodPackage),
                Path.GetFileName(brokenPackage)
            }
        });

        var report = CheckRunner.Run(
            config,
            corpus.OutputPath,
            corpus.Root.FullName,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath));

        Assert.NotEqual("pass", report.Status);
        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.Message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Packaged_library_with_xml_documentation_rehearses_and_rejects_a_substituted_assembly()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Documentation/Documentation.csproj");
        using (var archive = ZipFile.OpenRead(package))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName.Equals("lib/net8.0/Documentation.xml", StringComparison.OrdinalIgnoreCase));
        }

        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Documentation",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = Artifacts(package)
        });

        var intact = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath));

        Assert.Single(intact);
        Assert.Equal("pass", intact[0].Result.Status);

        var corrupted = ArchiveMutator.ReplaceEntry(
            package,
            "lib/net8.0/Documentation.dll",
            new byte[64],
            "Fixture.Documentation.corrupted.nupkg");
        var corruptedConfig = Config(new PackageExpectation
        {
            Id = "Fixture.Documentation",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = Artifacts(corrupted)
        });

        var substituted = ConsumerRehearsal.RunDetailed(
            corruptedConfig,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath));

        Assert.Single(substituted);
        Assert.NotEqual("pass", substituted[0].Result.Status);
    }

    [Fact]
    public void Package_version_cache_provenance_requires_sidecar_archive_and_payload_match()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Documentation/Documentation.csproj");
        var config = Config(new PackageExpectation
        {
            Id = "Fixture.Documentation",
            Kind = "library",
            Version = "1.0.0",
            Artifacts = Artifacts(package)
        });

        var intact = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath));
        var missingSidecar = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(
                PublicFeedPath: corpus.OutputPath,
                ProcessRunner: AfterRestore((_, cachedArchive) => File.Delete(cachedArchive + ".sha512"))));
        var tamperedCachedPackage = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(
                PublicFeedPath: corpus.OutputPath,
                ProcessRunner: AfterRestore((_, cachedArchive) => File.AppendAllText(cachedArchive, "tampered"))));
        var missingCachedXml = ConsumerRehearsal.RunDetailed(
            config,
            corpus.OutputPath,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(
                PublicFeedPath: corpus.OutputPath,
                ProcessRunner: AfterRestore((cachePackage, _) => File.Delete(Path.Combine(cachePackage, "lib", "net8.0", "Documentation.xml")))));

        testOutput.WriteLine($"intact: {intact.Single().Result.Status.ToUpperInvariant()}");
        testOutput.WriteLine($"missing-sidecar: {missingSidecar.Single().Result.Status.ToUpperInvariant()}");
        testOutput.WriteLine($"tampered-cached-nupkg: {tamperedCachedPackage.Single().Result.Status.ToUpperInvariant()}");
        testOutput.WriteLine($"missing-cached-xml: {missingCachedXml.Single().Result.Status.ToUpperInvariant()}");

        Assert.Equal("pass", intact.Single().Result.Status);
        Assert.NotEqual("pass", missingSidecar.Single().Result.Status);
        Assert.Contains("provenance sidecar", missingSidecar.Single().Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(corpus.Root.FullName, missingSidecar.Single().Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("pass", tamperedCachedPackage.Single().Result.Status);
        Assert.NotEqual("pass", missingCachedXml.Single().Result.Status);
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
        var unavailablePublicFeed = Path.Combine(corpus.Root.FullName, "unavailable-public-feed");
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
            corpus.Root.FullName,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: unavailablePublicFeed));

        Assert.Equal("error", report.Status);
        Assert.Equal(2, report.ExitCode);
        var failure = Assert.Single(report.Failures, failure => failure.CheckId == "consumer-rehearsal");
        Assert.Contains("infrastructure", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NU1101", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(corpus.Root.FullName, failure.Message, StringComparison.OrdinalIgnoreCase);
        testOutput.WriteLine($"UNAVAILABLE_SOURCE status={report.Status} exitCode={report.ExitCode} message={failure.Message}");
    }

    [Fact]
    public void Warning_only_bad_image_diagnostic_does_not_change_a_successful_process_result()
    {
        var outcome = ConsumerRehearsal.ClassifyProcessResult(new ProcessResult(
                Started: true,
                ExitCode: 0,
                TimedOut: false,
                StandardOutput: string.Empty,
                StandardError: "warning MSB3246: bad image in a diagnostic example; metadata is invalid",
                CleanupConfirmed: true),
            ConsumerRehearsal.ProcessPhase.Build);

        Assert.True(outcome.Passed);
        Assert.False(outcome.IsError);
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
        Assert.Contains("infrastructure", outcomes[0].Diagnostic, StringComparison.OrdinalIgnoreCase);
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
            StandardError: "Authorization: Bearer super-secret elapsed 17ms",
            CleanupConfirmed: true));

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
                    corpus.Root.FullName,
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

    private static ConsumerProcessRunner AfterRestore(Action<string, string> mutateCache)
    {
        return async (fileName, arguments, workingDirectory, environment, timeout) =>
        {
            var result = await BoundedProcess.RunAsync(fileName, arguments, workingDirectory, environment, timeout);
            if (arguments.Count > 0 && arguments[0].Equals("restore", StringComparison.OrdinalIgnoreCase))
            {
                var cachePath = environment["NUGET_PACKAGES"]!;
                var packageRoot = Directory.EnumerateDirectories(cachePath)
                    .Single(path => Path.GetFileName(path).Equals("Fixture.Documentation", StringComparison.OrdinalIgnoreCase));
                var packageCache = Directory.EnumerateDirectories(packageRoot)
                    .Single(path => Path.GetFileName(path).Equals("1.0.0", StringComparison.OrdinalIgnoreCase));
                var cachedArchive = Directory.EnumerateFiles(packageCache, "*.nupkg", SearchOption.TopDirectoryOnly)
                    .Single(path => !path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase));
                Assert.True(File.Exists(cachedArchive + ".sha512"), "The restore harness did not produce the package-specific provenance sidecar.");
                mutateCache(packageCache, cachedArchive);
            }

            return result;
        };
    }

    private static ConsumerProcessRunner AfterToolInstall(Action<string, string, IReadOnlyDictionary<string, string?>> mutateTool)
    {
        return async (fileName, arguments, workingDirectory, environment, timeout) =>
        {
            var result = await BoundedProcess.RunAsync(fileName, arguments, workingDirectory, environment, timeout);
            if (arguments.Count > 1 && arguments[0].Equals("tool", StringComparison.OrdinalIgnoreCase) && arguments[1].Equals("install", StringComparison.OrdinalIgnoreCase))
            {
                var toolRoot = Path.Combine(workingDirectory, "tool");
                var packageDirectory = Directory.EnumerateDirectories(toolRoot, "1.0.0", SearchOption.AllDirectories)
                    .Single(path => Directory.EnumerateFiles(path, "*.nuspec", SearchOption.TopDirectoryOnly).Any());
                mutateTool(workingDirectory, packageDirectory, environment);
            }

            return result;
        };
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
