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
        Assert.All(report.Checks, check => Assert.Equal("pass", check.Status));
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
    public void Tool_and_symbol_archives_pass_with_their_declared_layouts()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("example-tool.1.2.3.nupkg", "example-tool", "1.2.3", kind: "dotnetTool");
        fixture.AddSymbols("example-tool.1.2.3.snupkg", "example-tool", "1.2.3");

        var report = CheckRunner.Run(
            ConfigWithCommand(
                "example-tool",
                "dotnetTool",
                "1.2.3",
                new[] { "example-tool.1.2.3.nupkg", "example-tool.1.2.3.snupkg" },
                "example-tool"),
            fixture.ArtifactsPath);

        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void Symbol_archive_without_a_portable_pdb_fails()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddSymbols("example-tool.1.2.3.snupkg", "example-tool", "1.2.3", includePdb: false);

        var report = CheckRunner.Run(
            Config("example-tool", "dotnetTool", "1.2.3", "example-tool.1.2.3.snupkg"),
            fixture.ArtifactsPath);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.Message.Contains("symbol file", StringComparison.Ordinal));
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
