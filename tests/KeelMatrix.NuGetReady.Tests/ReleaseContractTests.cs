using System.Text;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class ReleaseContractTests
{
    [Fact]
    public void Wrong_tag_version_is_rejected()
    {
        using var repository = SyntheticReleaseRepository.Create();

        var result = repository.Validate(mode: "Tag", tagVersion: "0.1.1");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Tag version", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_changelog_with_only_an_unreleased_section_is_rejected_in_tag_mode()
    {
        using var repository = SyntheticReleaseRepository.Create("""
            # Changelog

            ## [Unreleased]

            ### Added

            - Initial package capabilities.
            """);

        var result = repository.Validate(mode: "Tag", tagVersion: "0.1.0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("CHANGELOG.md", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_fixed_section_in_a_first_release_is_rejected()
    {
        using var repository = SyntheticReleaseRepository.Create(SyntheticReleaseRepository.FinalizedChangelog.Replace(
            "### Added\n\n- Initial package capabilities.",
            "### Added\n\n- Initial package capabilities.\n\n### Fixed\n\n- Fixed a release issue."));

        var result = repository.Validate(mode: "Tag", tagVersion: "0.1.0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("only an Added section", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Planned_and_not_yet_published_wording_are_rejected()
    {
        foreach (var wording in new[] { "Planned", "not yet published" })
        {
            using var repository = SyntheticReleaseRepository.Create(SyntheticReleaseRepository.FinalizedChangelog.Replace(
                "- Initial package capabilities.",
                $"- Initial package capabilities. This release is {wording}."));

            var result = repository.Validate(mode: "Tag", tagVersion: "0.1.0");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("pre-release wording", result.StandardError, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_mismatched_install_command_is_rejected()
    {
        using var repository = SyntheticReleaseRepository.Create(readme: """
            # KeelMatrix NuGetReady

            ## Install

            ```bash
            dotnet tool install --global Another.Tool
            ```
            """);

        var result = repository.Validate(mode: "Tag", tagVersion: "0.1.0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Install command does not install", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mismatched_package_metadata_is_rejected()
    {
        using var repository = SyntheticReleaseRepository.Create(buildVersion: "0.2.0");

        var result = repository.Validate(mode: "Tag", tagVersion: "0.1.0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("package version", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_planned_synthetic_changelog_is_accepted_in_prerelease_mode_but_rejected_in_tag_mode()
    {
        using var repository = SyntheticReleaseRepository.Create(SyntheticReleaseRepository.PlannedChangelog);

        var tagResult = repository.Validate(mode: "Tag", tagVersion: "v0.1.0");
        var prereleaseResult = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.NotEqual(0, tagResult.ExitCode);
        Assert.Contains("[Unreleased]", tagResult.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, prereleaseResult.ExitCode);
    }

    [Fact]
    public void A_finalized_synthetic_release_tree_is_accepted_in_both_validation_modes()
    {
        using var repository = SyntheticReleaseRepository.Create();

        var tagResult = repository.Validate(mode: "Tag", tagVersion: "v0.1.0");
        var prereleaseResult = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.Equal(0, tagResult.ExitCode);
        Assert.Equal(0, prereleaseResult.ExitCode);
        Assert.Contains("Release contract passed", tagResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessResult RunValidator(string script, string root, string mode, string expectedVersion, string? tagVersion)
    {
        var arguments = new List<string>
        {
            "-NoProfile",
            "-NonInteractive",
            "-File",
            script,
            "-RepositoryRoot",
            root,
            "-Mode",
            mode,
            "-ExpectedVersion",
            expectedVersion
        };
        if (tagVersion is not null)
        {
            arguments.Add("-TagVersion");
            arguments.Add(tagVersion);
        }

        return BoundedProcess.RunAsync(
                "pwsh",
                arguments,
                root,
                new Dictionary<string, string?>
                {
                    ["KEELMATRIX_NO_TELEMETRY"] = "1",
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
                },
                TimeSpan.FromSeconds(30))
            .GetAwaiter()
            .GetResult();
    }

    private sealed class SyntheticReleaseRepository : IDisposable
    {
        private SyntheticReleaseRepository(DirectoryInfo root)
        {
            Root = root;
        }

        public DirectoryInfo Root { get; }

        public static string PlannedChangelog => """
            # Changelog

            ## [Unreleased]

            ### Added

            - Initial package capabilities.
            - Release documentation.

            ## [0.1.0] - 2026-09-16

            ### Added

            - Initial package capabilities.
            """;

        public static string FinalizedChangelog => """
            # Changelog

            ## [Unreleased]

            ## [0.1.0] - 2026-09-16

            ### Added

            - Initial package capabilities.
            - Release documentation.
            """;

        public static SyntheticReleaseRepository Create(
            string? changelog = null,
            string buildVersion = "0.1.0",
            string? readme = null)
        {
            var root = Directory.CreateTempSubdirectory("nugetready-release-contract-");
            var projectDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "KeelMatrix.NuGetReady"));
            File.WriteAllText(Path.Combine(root.FullName, "CHANGELOG.md"), changelog ?? FinalizedChangelog, Encoding.UTF8);
            File.WriteAllText(Path.Combine(root.FullName, "Directory.Build.props"), $"""
                <Project>
                  <PropertyGroup>
                    <Version>{buildVersion}</Version>
                    <PackageVersion>$(Version)</PackageVersion>
                  </PropertyGroup>
                </Project>
                """, Encoding.UTF8);
            File.WriteAllText(Path.Combine(root.FullName, "Directory.Packages.props"), """
                <Project>
                  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="KeelMatrix.Telemetry" Version="[0.1.1]" />
                  </ItemGroup>
                </Project>
                """, Encoding.UTF8);
            File.WriteAllText(Path.Combine(root.FullName, "nugetready.json"), """
                {
                  "schemaVersion": 1,
                  "packages": [{
                    "id": "KeelMatrix.NuGetReady",
                    "kind": "dotnetTool",
                    "version": "0.1.0",
                    "artifacts": ["KeelMatrix.NuGetReady.0.1.0.nupkg", "KeelMatrix.NuGetReady.0.1.0.snupkg"]
                  }]
                }
                """, Encoding.UTF8);

            File.WriteAllText(Path.Combine(projectDirectory.FullName, "KeelMatrix.NuGetReady.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <IsPackable>true</IsPackable>
                    <PackageId>KeelMatrix.NuGetReady</PackageId>
                    <PackageReleaseNotes>Initial release.</PackageReleaseNotes>
                  </PropertyGroup>
                  <ItemGroup><PackageReference Include="KeelMatrix.Telemetry" /></ItemGroup>
                </Project>
                """, Encoding.UTF8);

            var defaultReadme = """
                # KeelMatrix NuGetReady

                ## Install

                ```bash
                dotnet tool install --global KeelMatrix.NuGetReady
                ```

                KeelMatrix.Telemetry 0.1.1 provides the shared contract.
                """;
            File.WriteAllText(Path.Combine(root.FullName, "README.md"), readme ?? defaultReadme, Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectDirectory.FullName, "README.md"), defaultReadme, Encoding.UTF8);
            return new SyntheticReleaseRepository(root);
        }

        public ProcessResult Validate(string mode, string? tagVersion)
        {
            return RunValidator(
                Path.Combine(FindRepositoryRoot(), "scripts", "validate-release-contract.ps1"),
                Root.FullName,
                mode,
                "0.1.0",
                tagVersion);
        }

        public static string FindRepositoryRoot()
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

        public void Dispose()
        {
            try
            {
                Root.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
