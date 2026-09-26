using System.IO.Compression;
using System.Text.Json;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class ArchiveContractTests
{
    [Fact]
    public void Valid_library_archive_passes_the_archive_contract()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("Example.Core.1.2.3.nupkg", "Example.Core", "1.2.3");

        var report = CheckRunner.Run(Config("Example.Core", "library", "1.2.3", "Example.Core.1.2.3.nupkg"), fixture.ArtifactsPath);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal("pass", report.Status);
        Assert.All(report.Checks.Where(check => check.Id is not "workflow-policy" and not "consumer-rehearsal"), check => Assert.Equal("pass", check.Status));
        Assert.Equal("not-applicable", report.Checks.Single(check => check.Id == "workflow-policy").Status);
        Assert.Equal("not-applicable", report.Checks.Single(check => check.Id == "consumer-rehearsal").Status);
    }

    [Fact]
    public void Missing_archive_metadata_is_a_blocking_readiness_failure()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage(
            "Example.Core.1.2.3.nupkg",
            "Example.Core",
            "1.2.3",
            includeReadme: false,
            includeIcon: false,
            includeLicense: false);

        var report = CheckRunner.Run(Config("Example.Core", "library", "1.2.3", "Example.Core.1.2.3.nupkg"), fixture.ArtifactsPath);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.Message.Contains("README", StringComparison.Ordinal));
        Assert.Contains(report.Failures, failure => failure.Message.Contains("icon", StringComparison.Ordinal));
        Assert.Contains(report.Failures, failure => failure.Message.Contains("license", StringComparison.Ordinal));
    }

    [Fact]
    public void Multi_package_archive_failures_identify_the_defective_configured_artifact_in_text_and_json()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("Example.Good.1.2.3.nupkg", "Example.Good", "1.2.3");
        fixture.AddPackage("Example.Bad.1.2.3.nupkg", "Example.Bad", "1.2.3", includeReadme: false);
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new()
                {
                    Id = "Example.Good",
                    Kind = "library",
                    Version = "1.2.3",
                    Artifacts = ["Example.Good.1.2.3.nupkg"]
                },
                new()
                {
                    Id = "Example.Bad",
                    Kind = "library",
                    Version = "1.2.3",
                    Artifacts = ["Example.Bad.1.2.3.nupkg"]
                }
            ]
        };

        var report = CheckRunner.Run(config, fixture.ArtifactsPath);
        var text = ReportWriter.RenderText(report);
        var json = ReportWriter.RenderJson(report);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains("Example.Bad", text, StringComparison.Ordinal);
        Assert.Contains("1.2.3", text, StringComparison.Ordinal);
        Assert.Contains("Example.Bad.1.2.3.nupkg", text, StringComparison.Ordinal);
        Assert.Contains("expectation 'Example.Bad'", text, StringComparison.Ordinal);
        Assert.Contains("\"packageId\": \"Example.Bad\"", json, StringComparison.Ordinal);
        Assert.Contains("\"packageVersion\": \"1.2.3\"", json, StringComparison.Ordinal);
        Assert.Contains("\"artifactFileName\": \"Example.Bad.1.2.3.nupkg\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expectationName\": \"Example.Bad\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root.FullName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Root.FullName, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wrong_identity_or_version_is_reported()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("Example.Core.1.2.3.nupkg", "Different.Core", "1.2.4");

        var report = CheckRunner.Run(Config("Example.Core", "library", "1.2.3", "Example.Core.1.2.3.nupkg"), fixture.ArtifactsPath);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.Message.Contains("identity", StringComparison.Ordinal));
        Assert.Contains(report.Failures, failure => failure.Message.Contains("version", StringComparison.Ordinal));
    }

    [Fact]
    public void Nuspec_identity_mismatch_prevents_a_workflow_policy_pass()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("Example.Core.1.2.3.nupkg", "Different.Core", "1.2.4");
        using var repository = WorkflowRepository.Create("release.yml", """
            name: release
            on:
              push:
                tags: ["v1.2.3"]
            permissions: {}
            jobs:
              publish:
                steps:
                  - run: dotnet nuget push artifacts/Example.Core.1.2.3.nupkg
            """);

        var report = CheckRunner.Run(
            Config("Example.Core", "library", "1.2.3", "Example.Core.1.2.3.nupkg"),
            fixture.ArtifactsPath,
            repository.Root.FullName,
            TimeSpan.FromSeconds(1));

        Assert.Equal("fail", report.Checks.Single(check => check.Id == "archive-metadata").Status);
        Assert.Equal("not-run", report.Checks.Single(check => check.Id == "workflow-policy").Status);
    }

    [Fact]
    public void Dependency_group_must_match_package_assets()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("Example.Core.1.2.3.nupkg", "Example.Core", "1.2.3", dependencyFramework: "net7.0");

        var report = CheckRunner.Run(Config("Example.Core", "library", "1.2.3", "Example.Core.1.2.3.nupkg"), fixture.ArtifactsPath);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.CheckId == "dependency-groups");
    }

    [Fact]
    public void Multi_target_library_archive_passes_with_each_declared_framework()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("Example.Multi.1.2.3.nupkg", "Example.Multi", "1.2.3", multiTarget: true);

        var report = CheckRunner.Run(Config("Example.Multi", "multiTargetLibrary", "1.2.3", "Example.Multi.1.2.3.nupkg"), fixture.ArtifactsPath);

        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void Sensitive_internal_archive_entries_fail_closed()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("Example.Core.1.2.3.nupkg", "Example.Core", "1.2.3", includeSensitiveFile: true);

        var report = CheckRunner.Run(Config("Example.Core", "library", "1.2.3", "Example.Core.1.2.3.nupkg"), fixture.ArtifactsPath);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.CheckId == "archive-security");
    }

    [Fact]
    public void Root_and_nested_sensitive_files_fail_without_rejecting_user_secrets_assembly()
    {
        using var fixture = PackageFixture.Create();
        var sensitiveEntries = new[]
        {
            ".env",
            ".env.local",
            "local-telemetry.json",
            "nested/local-telemetry.json",
            "nested/AGENTS.md",
            "nested/keelmatrix.telemetry.json"
        };

        foreach (var entry in sensitiveEntries)
        {
            var original = fixture.AddPackage($"sensitive-{entry.Replace('/', '-').Replace('.', '-')}.nupkg", "Example.Core", "1.2.3");
            var package = ArchiveMutator.AddEntry(
                original,
                entry,
                new byte[] { 1 },
                $"sensitive-{entry.Replace('/', '-').Replace('.', '-')}-mutated.nupkg");
            File.Delete(original);

            var report = CheckRunner.Run(
                Config("Example.Core", "library", "1.2.3", Path.GetFileName(package)),
                fixture.ArtifactsPath);

            Assert.Contains(report.Failures, failure => failure.CheckId == "archive-security");
            File.Delete(package);
        }

        var originalAssemblyPackage = fixture.AddPackage("user-secrets-assembly.nupkg", "Example.Core", "1.2.3");
        var assemblyPackage = ArchiveMutator.AddEntry(
            originalAssemblyPackage,
            "tools/net8.0/any/Microsoft.Extensions.Configuration.UserSecrets.dll",
            new byte[] { 1 },
            "user-secrets-assembly-mutated.nupkg");
        File.Delete(originalAssemblyPackage);

        var validReport = CheckRunner.Run(
            Config("Example.Core", "library", "1.2.3", Path.GetFileName(assemblyPackage)),
            fixture.ArtifactsPath);

        Assert.DoesNotContain(validReport.Failures, failure => failure.CheckId == "archive-security");
    }

    [Fact]
    public void Tool_and_symbol_archives_pass_with_their_declared_layouts()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Tool/Tool.csproj");
        var symbols = Path.Combine(corpus.OutputPath, "Fixture.Tool.1.0.0.snupkg");

        var report = CheckRunner.Run(
            ConfigWithCommand(
                "Fixture.Tool",
                "dotnetTool",
                "1.0.0",
                new[] { Path.GetFileName(package), Path.GetFileName(symbols) },
                "fixture-tool"),
            corpus.OutputPath);

        Assert.True(report.ExitCode == 0, string.Join(" | ", report.Failures.Select(failure => failure.Message)));
    }

    [Fact]
    public void Symbol_archive_without_a_portable_pdb_fails()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("example-tool.1.2.3.nupkg", "example-tool", "1.2.3", kind: "dotnetTool");
        fixture.AddSymbols("example-tool.1.2.3.snupkg", "example-tool", "1.2.3", includePdb: false);

        var report = CheckRunner.Run(
            Config("example-tool", "dotnetTool", "1.2.3", "example-tool.1.2.3.nupkg", "example-tool.1.2.3.snupkg"),
            fixture.ArtifactsPath);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.Message.Contains("symbol file", StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_or_mismatched_symbols_and_missing_license_file_fail()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("example-tool.1.2.3.nupkg", "example-tool", "1.2.3", kind: "dotnetTool");
        fixture.AddSymbols("example-tool.1.2.3.snupkg", "example-tool", "1.2.3");

        var malformedReport = CheckRunner.Run(
            Config("example-tool", "dotnetTool", "1.2.3", "example-tool.1.2.3.nupkg", "example-tool.1.2.3.snupkg"),
            fixture.ArtifactsPath);

        Assert.Contains(malformedReport.Failures, failure => failure.CheckId == "archive-layout");
        File.Delete(Path.Combine(fixture.ArtifactsPath, "example-tool.1.2.3.nupkg"));
        File.Delete(Path.Combine(fixture.ArtifactsPath, "example-tool.1.2.3.snupkg"));

        var originalPackage = fixture.AddPackage("file-license.nupkg", "Example.Core", "1.2.3");
        var packageWithFileLicense = ArchiveMutator.ReplaceNuspecText(
            originalPackage,
            text => text.Replace(
                "<license type=\"expression\">MIT</license>",
                "<license type=\"file\">LICENSE.txt</license>",
                StringComparison.Ordinal),
            "file-license-mutated.nupkg");
        File.Delete(originalPackage);

        var missingLicenseReport = CheckRunner.Run(
            Config("Example.Core", "library", "1.2.3", Path.GetFileName(packageWithFileLicense)),
            fixture.ArtifactsPath);

        Assert.Contains(missingLicenseReport.Failures, failure => failure.CheckId == "archive-metadata");
    }

    [Fact]
    public void Symbol_layout_must_match_an_assembly_in_the_main_package()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var symbols = Path.Combine(corpus.OutputPath, "Fixture.Standard.1.0.0.snupkg");
        byte[] pdb;
        using (var archive = ZipFile.OpenRead(symbols))
        {
            var entry = archive.Entries.Single(entry => entry.FullName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            pdb = buffer.ToArray();
        }

        var intermediate = ArchiveMutator.RemoveEntry(symbols, "lib/net8.0/Fixture.Standard.pdb", "mismatched-symbols.nupkg");
        var mismatched = ArchiveMutator.AddEntry(intermediate, "lib/net8.0/OtherAssembly.pdb", pdb, "mismatched-symbols-final.snupkg");
        File.Delete(symbols);
        File.Delete(intermediate);

        var report = CheckRunner.Run(
            Config("Fixture.Standard", "library", "1.0.0", Path.GetFileName(package), "mismatched-symbols-final.snupkg"),
            corpus.OutputPath);

        Assert.Contains(report.Failures, failure => failure.CheckId == "archive-layout" && failure.Message.Contains("matching", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Foreign_valid_portable_pdb_at_the_expected_path_fails_identity_validation()
    {
        using var corpus = PackedCorpus.Create();
        var toolPackage = corpus.Pack("Tool/Tool.csproj");
        corpus.Pack("Standard/Standard.csproj");
        var toolSymbols = Path.Combine(corpus.OutputPath, "Fixture.Tool.1.0.0.snupkg");
        var standardSymbols = Path.Combine(corpus.OutputPath, "Fixture.Standard.1.0.0.snupkg");
        byte[] foreignPdb;
        using (var archive = ZipFile.OpenRead(standardSymbols))
        {
            var entry = archive.Entries.Single(entry => entry.FullName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            foreignPdb = buffer.ToArray();
        }
        File.Delete(Path.Combine(corpus.OutputPath, "Fixture.Standard.1.0.0.nupkg"));
        File.Delete(standardSymbols);

        var substitutedSymbols = ArchiveMutator.ReplaceEntry(
            toolSymbols,
            "tools/net8.0/any/Fixture.Tool.pdb",
            foreignPdb,
            "foreign-pdb.snupkg");
        File.Delete(toolSymbols);

        var report = CheckRunner.Run(
            ConfigWithCommand(
                "Fixture.Tool",
                "dotnetTool",
                "1.0.0",
                new[] { Path.GetFileName(toolPackage), Path.GetFileName(substitutedSymbols) },
                "fixture-tool"),
            corpus.OutputPath);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure =>
            failure.CheckId == "archive-layout" &&
            failure.Message.Contains("identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Deterministic_rebuild_symbols_match_the_same_assembly()
    {
        using var first = PackedCorpus.Create();
        using var rebuild = PackedCorpus.Create();
        var firstPackage = first.Pack("Tool/Tool.csproj");
        var firstSymbols = Path.Combine(first.OutputPath, "Fixture.Tool.1.0.0.snupkg");
        rebuild.Pack("Tool/Tool.csproj");
        var rebuildSymbols = Path.Combine(rebuild.OutputPath, "Fixture.Tool.1.0.0.snupkg");
        var copiedSymbols = Path.Combine(first.OutputPath, "deterministic-rebuild.snupkg");
        File.Copy(rebuildSymbols, copiedSymbols);
        File.Delete(firstSymbols);

        var report = CheckRunner.Run(
            ConfigWithCommand(
                "Fixture.Tool",
                "dotnetTool",
                "1.0.0",
                new[] { Path.GetFileName(firstPackage), Path.GetFileName(copiedSymbols) },
                "fixture-tool"),
            first.OutputPath);

        Assert.Equal(0, report.ExitCode);
        Assert.DoesNotContain(report.Failures, failure => failure.CheckId == "archive-layout");
    }

    [Fact]
    public void Blocking_archive_failure_marks_consumer_rehearsal_as_not_run()
    {
        using var fixture = PackageFixture.Create();
        File.WriteAllText(Path.Combine(fixture.ArtifactsPath, "Example.Core.1.2.3.nupkg"), "not a zip");
        using var repository = WorkflowRepository.Create("ci.yml", "name: CI");

        var report = CheckRunner.Run(
            Config("Example.Core", "library", "1.2.3", "Example.Core.1.2.3.nupkg"),
            fixture.ArtifactsPath,
            repository.Root.FullName,
            TimeSpan.FromSeconds(1));

        Assert.Equal("not-run", report.Checks.Single(check => check.Id == "consumer-rehearsal").Status);
    }

    [Fact]
    public void Tool_command_must_match_the_configured_command()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("example-tool.1.2.3.nupkg", "example-tool", "1.2.3", kind: "dotnetTool");

        var report = CheckRunner.Run(
            ConfigWithCommand("example-tool", "dotnetTool", "1.2.3", new[] { "example-tool.1.2.3.nupkg" }, "different-command"),
            fixture.ArtifactsPath);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.Message.Contains("configured command", StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_expected_archive_is_an_infrastructure_error()
    {
        using var fixture = PackageFixture.Create();
        File.WriteAllText(Path.Combine(fixture.ArtifactsPath, "Example.Core.1.2.3.nupkg"), "not a zip");

        var report = CheckRunner.Run(Config("Example.Core", "library", "1.2.3", "Example.Core.1.2.3.nupkg"), fixture.ArtifactsPath);

        Assert.Equal(2, report.ExitCode);
        Assert.Equal("error", report.Status);
        Assert.Contains(report.Failures, failure => failure.CheckId == "archive-parse" && failure.IsError);
        Assert.Equal("error", report.Checks.Single(check => check.Id == "archive-parse").Status);
        var parseFailure = Assert.Single(report.Failures, failure => failure.CheckId == "archive-parse");
        Assert.Equal("Example.Core", parseFailure.PackageId);
        Assert.Equal("1.2.3", parseFailure.PackageVersion);
        Assert.Equal("Example.Core.1.2.3.nupkg", parseFailure.ArtifactFileName);
        Assert.Equal("Example.Core", parseFailure.ExpectationName);
        Assert.All(
            report.Checks.Where(check => check.Id is "archive-metadata" or "archive-layout" or "dependency-groups" or "dependency-coherence" or "archive-security"),
            check => Assert.Equal("not-run", check.Status));
    }

    [Fact]
    public void Archive_entry_limit_is_rejected_before_consumer_execution()
    {
        using var fixture = PackageFixture.Create();
        var original = fixture.AddPackage("Example.Core.1.2.3.nupkg", "Example.Core", "1.2.3");
        var oversized = ArchiveMutator.AddGeneratedEntries(
            original,
            ArchiveInspectionLimits.MaxEntryCount,
            "Example.Core.too-many-entries.nupkg");
        File.Delete(original);
        using var repository = WorkflowRepository.Create("ci.yml", "name: CI");

        var report = CheckRunner.Run(
            Config("Example.Core", "library", "1.2.3", Path.GetFileName(oversized)),
            fixture.ArtifactsPath,
            repository.Root.FullName,
            TimeSpan.FromSeconds(1));

        Assert.Equal(2, report.ExitCode);
        Assert.Equal("error", report.Status);
        Assert.Contains(report.Failures, failure => failure.CheckId == "archive-parse" && failure.IsError);
        Assert.Equal("not-run", report.Checks.Single(check => check.Id == "consumer-rehearsal").Status);
    }

    [Fact]
    public void Text_and_json_reports_are_byte_stable()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("Example.Core.1.2.3.nupkg", "Example.Core", "1.2.3");
        var config = Config("Example.Core", "library", "1.2.3", "Example.Core.1.2.3.nupkg");
        var first = CheckRunner.Run(config, fixture.ArtifactsPath);
        var second = CheckRunner.Run(config, fixture.ArtifactsPath);

        Assert.Equal(ReportWriter.RenderText(first), ReportWriter.RenderText(second));
        Assert.Equal(ReportWriter.RenderJson(first), ReportWriter.RenderJson(second));
        using var json = JsonDocument.Parse(ReportWriter.RenderJson(first));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.DoesNotContain(fixture.Root.FullName, ReportWriter.RenderJson(first), StringComparison.OrdinalIgnoreCase);
    }

    private static NuGetReadyConfig Config(string id, string kind, string version, params string[] artifacts)
    {
        return ConfigWithCommand(id, kind, version, artifacts, null);
    }

    private static NuGetReadyConfig ConfigWithCommand(string id, string kind, string version, string[] artifacts, string? command)
    {
        return new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages = new List<PackageExpectation>
            {
                new()
                {
                    Id = id,
                    Kind = kind,
                    Version = version,
                    Artifacts = artifacts.ToList(),
                    Command = command
                }
            }
        };
    }
}
