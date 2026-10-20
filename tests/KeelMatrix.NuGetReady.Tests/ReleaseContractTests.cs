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
    public void Website_manifest_and_source_tags_must_agree()
    {
        using var repository = SyntheticReleaseRepository.Create();
        var manifestPath = Path.Combine(repository.Root.FullName, "keelmatrix.website.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("public-product", "internal-package", StringComparison.Ordinal));

        var result = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sentinel tags do not agree", FlattenOutput(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Website_manifest_must_match_the_complete_packable_source_package_set()
    {
        using var repository = SyntheticReleaseRepository.Create();
        var extraProjectDirectory = Directory.CreateDirectory(Path.Combine(repository.Root.FullName, "src", "NuGetReady.Component"));
        File.WriteAllText(Path.Combine(extraProjectDirectory.FullName, "NuGetReady.Component.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <PackageId>KeelMatrix.NuGetReady.Component</PackageId>
              </PropertyGroup>
            </Project>
            """);

        var result = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("package set must exactly match", FlattenOutput(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicitly_non_packable_source_projects_do_not_enter_the_manifest_package_set()
    {
        using var repository = SyntheticReleaseRepository.Create();
        var projectDirectory = Directory.CreateDirectory(Path.Combine(repository.Root.FullName, "src", "NuGetReady.Internal"));
        File.WriteAllText(Path.Combine(projectDirectory.FullName, "NuGetReady.Internal.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsPackable>false</IsPackable>
                <PackageId>KeelMatrix.NuGetReady.Internal</PackageId>
              </PropertyGroup>
            </Project>
            """);

        var result = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Website_manifest_must_have_exactly_one_primary_package()
    {
        using var repository = SyntheticReleaseRepository.Create();
        var manifestPath = Path.Combine(repository.Root.FullName, "keelmatrix.website.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("primary", "component", StringComparison.Ordinal));
        ReplaceProjectProperty(repository, "PackageTags", "nuget;keelmatrix-public-product;keelmatrix-component");

        var result = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("exactly one primary", FlattenOutput(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Website_manifest_must_reject_duplicate_primary_packages()
    {
        using var repository = SyntheticReleaseRepository.Create();
        var extraProjectDirectory = Directory.CreateDirectory(Path.Combine(repository.Root.FullName, "src", "NuGetReady.Component"));
        File.WriteAllText(Path.Combine(extraProjectDirectory.FullName, "NuGetReady.Component.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsPackable>true</IsPackable>
                <PackageId>KeelMatrix.NuGetReady.Component</PackageId>
                <PackageTags>nuget;keelmatrix-public-product;keelmatrix-primary</PackageTags>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(repository.Root.FullName, "keelmatrix.website.json"), """
            {
              "schemaVersion": 1,
              "packages": {
                "KeelMatrix.NuGetReady": { "visibility": "public-product", "role": "primary" },
                "KeelMatrix.NuGetReady.Component": { "visibility": "public-product", "role": "primary" }
              }
            }
            """);

        var result = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("exactly one primary package; found 2", FlattenOutput(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Website_manifest_must_not_name_a_nonexistent_package()
    {
        using var repository = SyntheticReleaseRepository.Create();
        File.WriteAllText(Path.Combine(repository.Root.FullName, "keelmatrix.website.json"), """
            {
              "schemaVersion": 1,
              "packages": {
                "KeelMatrix.NuGetReady": { "visibility": "public-product", "role": "primary" },
                "KeelMatrix.NuGetReady.Missing": { "visibility": "public-product", "role": "component" }
              }
            }
            """);

        var result = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not match a packable project", FlattenOutput(result), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"packages\":{", "not valid JSON")]
    [InlineData("[]", "must contain a JSON object")]
    [InlineData("{\"schemaVersion\":1,\"packages\":{\"KeelMatrix.NuGetReady\":{\"visibility\":\"public-product\",\"role\":\"primary\"}},\"description\":\"copy\"}", "must contain only schemaVersion and packages")]
    [InlineData("{\"schemaVersion\":2,\"packages\":{\"KeelMatrix.NuGetReady\":{\"visibility\":\"public-product\",\"role\":\"primary\"}}}", "supports only schemaVersion 1")]
    [InlineData("{\"schemaVersion\":\"1\",\"packages\":{\"KeelMatrix.NuGetReady\":{\"visibility\":\"public-product\",\"role\":\"primary\"}}}", "schemaVersion must be the integer 1")]
    [InlineData("{\"schemaVersion\":1,\"packages\":{\"KeelMatrix.NuGetReady\":{\"visibility\":\"public-product\",\"role\":\"primary\",\"description\":\"copy\"}}}", "contain only visibility and role")]
    [InlineData("{\"schemaVersion\":1,\"packages\":{\"KeelMatrix.NuGetReady\":{\"visibility\":\"Public-Product\",\"role\":\"primary\"}}}", "visibility for 'KeelMatrix.NuGetReady'")]
    [InlineData("{\"schemaVersion\":1,\"packages\":{\"KeelMatrix.NuGetReady\":{\"visibility\":\"public-product\",\"role\":\"Primary\"}}}", "role for 'KeelMatrix.NuGetReady'")]
    public void Invalid_website_manifest_schemas_are_rejected(string manifest, string expectedMessage)
    {
        using var repository = SyntheticReleaseRepository.Create();
        File.WriteAllText(Path.Combine(repository.Root.FullName, "keelmatrix.website.json"), manifest);

        var result = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedMessage, FlattenOutput(result), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PackageTags", "nuget;keelmatrix-primary", "exactly one website visibility sentinel")]
    [InlineData("PackageTags", "nuget;keelmatrix-public-product;keelmatrix-internal-package;keelmatrix-primary", "exactly one website visibility sentinel")]
    [InlineData("PackageTags", "nuget;keelmatrix-public-product;keelmatrix-component", "do not agree")]
    [InlineData("PackageTags", "nuget;KeelMatrix-public-product;keelmatrix-primary", "do not agree")]
    [InlineData("PackageTags", "nuget;keelmatrix-public-product;KeelMatrix-primary", "do not agree")]
    public void Invalid_website_sentinel_tag_sets_are_rejected(string propertyName, string value, string expectedMessage)
    {
        using var repository = SyntheticReleaseRepository.Create();
        ReplaceProjectProperty(repository, propertyName, value);

        var result = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedMessage, FlattenOutput(result), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PackageProjectUrl", "https://github.com/KeelMatrix/NuGetReady#readme", "RepositoryUrl and PackageProjectUrl")]
    [InlineData("RepositoryUrl", "https://github.com/KeelMatrix/Other", "RepositoryUrl and PackageProjectUrl")]
    [InlineData("Description", "A different package description.", "approved product description")]
    [InlineData("PackageType", "Library", ".NET tool")]
    [InlineData("PackageReadmeFile", "missing.md", "does not exist")]
    public void Inaccurate_shipping_package_metadata_is_rejected(string propertyName, string value, string expectedMessage)
    {
        using var repository = SyntheticReleaseRepository.Create();
        ReplaceProjectProperty(repository, propertyName, value);

        var result = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedMessage, FlattenOutput(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Package_readme_must_be_packed_at_the_package_root()
    {
        using var repository = SyntheticReleaseRepository.Create();
        var projectPath = Path.Combine(repository.Root.FullName, "src", "KeelMatrix.NuGetReady", "KeelMatrix.NuGetReady.csproj");
        File.WriteAllText(projectPath, File.ReadAllText(projectPath).Replace(
            "    <None Include=\"README.md\" Pack=\"true\" PackagePath=\"README.md\" />",
            string.Empty,
            StringComparison.Ordinal));

        var result = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("README must be packed at the package root", FlattenOutput(result), StringComparison.OrdinalIgnoreCase);
    }

    private static string FlattenOutput(ProcessResult result)
    {
        var flattened = new StringBuilder();
        var output = result.StandardOutput + Environment.NewLine + result.StandardError;
        foreach (var line in output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalizedLine = line.Trim().TrimStart('|').Trim();
            if (flattened.Length > 0)
            {
                flattened.Append(' ');
            }

            flattened.Append(normalizedLine);
        }

        return flattened.ToString();
    }

    private static void ReplaceProjectProperty(SyntheticReleaseRepository repository, string propertyName, string value)
    {
        var projectPath = Path.Combine(repository.Root.FullName, "src", "KeelMatrix.NuGetReady", "KeelMatrix.NuGetReady.csproj");
        var project = File.ReadAllText(projectPath);
        var openTag = $"<{propertyName}>";
        var closeTag = $"</{propertyName}>";
        var start = project.IndexOf(openTag, StringComparison.Ordinal);
        var end = project.IndexOf(closeTag, start, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            throw new Xunit.Sdk.XunitException($"Synthetic project does not declare {propertyName}.");
        }

        File.WriteAllText(projectPath, project[..(start + openTag.Length)] + value + project[end..]);
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
    public void Invalid_calendar_release_date_is_rejected()
    {
        using var repository = SyntheticReleaseRepository.Create(
            SyntheticReleaseRepository.FinalizedChangelog.Replace("2026-09-16", "2026-99-99", StringComparison.Ordinal));

        var result = repository.Validate(mode: "Tag", tagVersion: "0.1.0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("date", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_target_release_sections_are_rejected()
    {
        using var repository = SyntheticReleaseRepository.Create(
            SyntheticReleaseRepository.FinalizedChangelog + """

            ## [0.1.0] - 2026-09-17

            ### Added

            - Duplicate release entry.
            """);

        var result = repository.Validate(mode: "Tag", tagVersion: "0.1.0");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void Empty_target_release_is_rejected()
    {
        using var repository = SyntheticReleaseRepository.Create(
            SyntheticReleaseRepository.FinalizedChangelog.Replace(
                "### Added\n\n- Initial package capabilities.\n- Release documentation.",
                "### Added",
                StringComparison.Ordinal));

        var result = repository.Validate(mode: "Tag", tagVersion: "0.1.0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Added section", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fenced_example_release_heading_does_not_satisfy_tag_mode()
    {
        using var repository = SyntheticReleaseRepository.Create("""
            # Changelog

            ## [Unreleased]

            ### Added

            - Initial package capabilities.

            ```markdown
            ## [0.1.0] - 2026-09-16

            ### Added

            - Example only.
            ```
            """);

        var result = repository.Validate(mode: "Tag", tagVersion: "0.1.0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not contain", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tag_mode_ignores_comments_indented_code_and_unequal_fence_closers()
    {
        using var repository = SyntheticReleaseRepository.Create("""
            # Changelog

            ## [Unreleased]

            ### Added

            - Initial package capabilities.

            ## [0.1.0] - 2026-09-16

            <!--
            ### Fixed
            - Comment-only fake release evidence.
            -->

            ### Added

            - Initial package capabilities.

                ### Fixed
                - Indented code is not a release category.

            ````markdown
            ### Fixed
            - The three-backtick line does not close a four-backtick fence.
            ```
            ### Fixed
            - Still inside the unequal fence.
            ````
            """);

        var result = repository.Validate(mode: "Tag", tagVersion: "0.1.0");

        Assert.True(result.ExitCode == 0, result.StandardOutput + Environment.NewLine + result.StandardError);
    }

    [Fact]
    public void Pre_release_rejects_a_bullet_hidden_by_an_inline_comment()
    {
        using var repository = SyntheticReleaseRepository.Create("""
            # Changelog

            ## [Unreleased]

            ### Added

            - <!-- hidden release evidence -->
            """);

        var result = repository.Validate(mode: "PreRelease", tagVersion: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("CHANGELOG.md must contain", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tag_mode_does_not_close_a_fence_with_a_non_whitespace_tail()
    {
        using var repository = SyntheticReleaseRepository.Create("""
            # Changelog

            ## [Unreleased]

            ### Added

            - Initial package capabilities.

            ```markdown
            ## [0.1.0] - 2026-09-16

            ### Added

            - Fake fenced release.
            ``` trailing text

            ## [0.1.0] - 2026-09-16

            ### Added

            - Initial package capabilities.
            """);

        var result = repository.Validate(mode: "Tag", tagVersion: "0.1.0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not contain", result.StandardError, StringComparison.OrdinalIgnoreCase);
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

        Assert.True(tagResult.ExitCode == 0, tagResult.StandardOutput + Environment.NewLine + tagResult.StandardError);
        Assert.True(prereleaseResult.ExitCode == 0, prereleaseResult.StandardOutput + Environment.NewLine + prereleaseResult.StandardError);
        Assert.Contains("Release contract passed", tagResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Current_repository_release_contract_matches_the_execution_context()
    {
        var root = SyntheticReleaseRepository.FindRepositoryRoot();
        var tagMode = string.Equals(Environment.GetEnvironmentVariable("GITHUB_REF_TYPE"), "tag", StringComparison.Ordinal);
        var tagVersion = tagMode ? Environment.GetEnvironmentVariable("GITHUB_REF_NAME") : null;
        var result = RunValidator(
            Path.Combine(root, "scripts", "validate-release-contract.ps1"),
            root,
            tagMode ? "Tag" : "PreRelease",
            "0.1.0",
            tagVersion);

        Assert.True(
            result.ExitCode == 0,
            $"Release contract validation failed.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");
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
            var buildDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "build"));
            var repositoryBuildDirectory = Path.Combine(FindRepositoryRoot(), "build");
            foreach (var buildFile in new[] { "Invoke-NestedPwsh.ps1", "Test-NestedPwshLaunch.ps1" })
            {
                File.Copy(Path.Combine(repositoryBuildDirectory, buildFile), Path.Combine(buildDirectory.FullName, buildFile));
            }

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
            File.WriteAllText(Path.Combine(root.FullName, "keelmatrix.website.json"), """
                {
                  "schemaVersion": 1,
                  "packages": {
                    "KeelMatrix.NuGetReady": {
                      "visibility": "public-product",
                      "role": "primary"
                    }
                  }
                }
                """, Encoding.UTF8);

            File.WriteAllText(Path.Combine(projectDirectory.FullName, "KeelMatrix.NuGetReady.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <IsPackable>true</IsPackable>
                    <PackAsTool>true</PackAsTool>
                    <ToolCommandName>nugetready</ToolCommandName>
                    <PackageId>KeelMatrix.NuGetReady</PackageId>
                    <PackageType>DotnetTool</PackageType>
                    <Authors>KeelMatrix</Authors>
                    <Description>Rehearse a NuGet release from the exact built artifacts and prove clean isolated consumer installation before publishing.</Description>
                    <PackageTags>nuget;keelmatrix-public-product;keelmatrix-primary</PackageTags>
                    <PackageProjectUrl>https://github.com/KeelMatrix/NuGetReady</PackageProjectUrl>
                    <RepositoryUrl>https://github.com/KeelMatrix/NuGetReady</RepositoryUrl>
                    <RepositoryType>git</RepositoryType>
                    <PublishRepositoryUrl>true</PublishRepositoryUrl>
                    <PackageReadmeFile>README.md</PackageReadmeFile>
                    <PackageReleaseNotes>Initial release.</PackageReleaseNotes>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="KeelMatrix.Telemetry" />
                    <None Include="README.md" Pack="true" PackagePath="README.md" />
                  </ItemGroup>
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
