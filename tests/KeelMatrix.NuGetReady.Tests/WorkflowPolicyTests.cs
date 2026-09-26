namespace KeelMatrix.NuGetReady.Tests;

public sealed class WorkflowPolicyTests
{
    [Fact]
    public void Valid_trusted_publishing_workflow_has_no_policy_findings()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Empty(findings);
    }

    [Fact]
    public void Short_lived_trusted_publishing_credential_is_allowed()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Empty(findings);
    }

    [Fact]
    public void Customer_workflow_with_a_different_literal_publisher_username_is_supported()
    {
        using var repository = WorkflowRepository.Create(
            "release.yml",
            Mutate(ReleaseWorkflow, "user: dmitriyzen", "user: customer-maintainer"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("user: ''")]
    [InlineData(null)]
    [InlineData("user: ${{ vars.NUGET_USERNAME }}")]
    public void Missing_empty_or_dynamic_publisher_username_is_unproven(string? loginInput)
    {
        using var repository = WorkflowRepository.Create(
            "release.yml",
            loginInput is null
                ? Mutate(ReleaseWorkflow, "          user: dmitriyzen\n", string.Empty)
                : Mutate(ReleaseWorkflow, "user: dmitriyzen", loginInput));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding =>
            finding.IsError && finding.Message.Contains("literal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Explicit_expected_publisher_username_rejects_a_mismatch()
    {
        using var repository = WorkflowRepository.Create(
            "release.yml",
            Mutate(ReleaseWorkflow, "user: dmitriyzen", "user: customer-maintainer"));
        repository.WriteFile(
            "nugetready.json",
            File.ReadAllText(Path.Combine(repository.Root.FullName, "nugetready.json"))
                .Replace("\"packages\":", "\"workflowPolicy\": { \"expectedNuGetUsername\": \"other-maintainer\" },\n  \"packages\":", StringComparison.Ordinal));

        var configPath = Path.Combine(repository.Root.FullName, "nugetready.json");
        var findings = WorkflowPolicyInspector.Inspect(
            repository.Root.FullName,
            ConfigurationLoader.Load(configPath),
            configPath);

        Assert.Contains(findings, finding =>
            finding.Message.Contains("ExpectedNuGetUsername", StringComparison.Ordinal));
    }

    [Fact]
    public void Broad_version_tag_without_release_identity_binding_is_blocking()
    {
        using var repository = WorkflowRepository.Create(
            "release.yml",
            ReleaseWorkflow.Replace("v1.0.0", "v*.*.*", StringComparison.Ordinal));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding =>
            finding.Message.Contains("release version", StringComparison.OrdinalIgnoreCase) ||
            finding.Message.Contains("release identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Exact_tag_for_a_different_configured_version_is_blocking()
    {
        using var repository = WorkflowRepository.Create(
            "release.yml",
            ReleaseWorkflow.Replace("v1.0.0", "v9.9.9", StringComparison.Ordinal));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding =>
            finding.Message.Contains("v9.9.9", StringComparison.Ordinal) &&
            finding.Message.Contains("1.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Configured_artifact_filename_for_a_different_version_is_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow);
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new PackageExpectation
                {
                    Id = "KeelMatrix.NuGetReady",
                    Kind = "dotnetTool",
                    Version = "1.0.0",
                    Artifacts = ["KeelMatrix.NuGetReady.9.9.9.nupkg", "KeelMatrix.NuGetReady.1.0.0.snupkg"]
                }
            ]
        };

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName, config);

        Assert.Contains(findings, finding =>
            finding.Message.Contains("artifact filenames", StringComparison.OrdinalIgnoreCase) &&
            finding.Message.Contains("1.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsupported_publication_relevant_mutations_never_remain_pass()
    {
        var mutations = new Dictionary<string, string>
        {
            ["arbitrary executable step"] = Mutate(
                ReleaseWorkflow,
                "- id: nuget-login",
                "- run: echo unexpected\n      - id: nuget-login"),
            ["local composite action"] = Mutate(
                ReleaseWorkflow,
                "uses: actions/download-artifact@v4",
                "uses: ./.github/actions/download"),
            ["unknown remote action"] = Mutate(
                ReleaseWorkflow,
                "uses: NuGet/login@v1",
                "uses: example/opaque-login@v1"),
            ["conditional authentication"] = Mutate(
                ReleaseWorkflow,
                "- id: nuget-login\n        uses: NuGet/login@v1",
                "- id: nuget-login\n        if: github.ref_type == 'tag'\n        uses: NuGet/login@v1"),
            ["step permissions"] = Mutate(
                ReleaseWorkflow,
                "- id: nuget-login\n        uses: NuGet/login@v1",
                "- id: nuget-login\n        permissions: {}\n        uses: NuGet/login@v1"),
            ["command wrapper"] = Mutate(
                ReleaseWorkflow,
                "run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key \"$env:NUGET_API_KEY\"",
                "run: bash -c 'dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg'"),
            ["executable suffix variant"] = Mutate(
                ReleaseWorkflow,
                "run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key \"$env:NUGET_API_KEY\"",
                "run: dotnet.exe nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg"),
            ["leading-dot executable variant"] = Mutate(
                ReleaseWorkflow,
                "run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key \"$env:NUGET_API_KEY\"",
                "run: ./dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg"),
            ["dynamic invocation"] = Mutate(
                ReleaseWorkflow,
                "run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key \"$env:NUGET_API_KEY\"",
                "run: '& $env:PUBLISH_COMMAND artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg'"),
            ["process launcher"] = Mutate(
                ReleaseWorkflow,
                "run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key \"$env:NUGET_API_KEY\"",
                "run: Start-Process dotnet -ArgumentList 'nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg'"),
            ["generated script execution"] = Mutate(
                ReleaseWorkflow,
                "run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key \"$env:NUGET_API_KEY\"",
                "run: Set-Content generated.ps1 'dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg'; ./generated.ps1"),
            ["git alias publication"] = Mutate(
                ReleaseWorkflow,
                "run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key \"$env:NUGET_API_KEY\"",
                "run: git -c alias.ship='!dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg' ship"),
            ["custom MSBuild import"] = Mutate(
                ReleaseWorkflow,
                "run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key \"$env:NUGET_API_KEY\"",
                "run: dotnet build -p:CustomAfterMicrosoftCommonTargets=publish.targets"),
            ["repository script"] = Mutate(
                ReleaseWorkflow,
                "run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key \"$env:NUGET_API_KEY\"",
                "run: ./scripts/publish.ps1"),
            ["path replacement"] = Mutate(
                ReleaseWorkflow,
                "      DOTNET_CLI_TELEMETRY_OPTOUT: '1'\n    steps:\n      - uses: actions/download-artifact@v4",
                "      DOTNET_CLI_TELEMETRY_OPTOUT: '1'\n      PATH: ./tools\n    steps:\n      - uses: actions/download-artifact@v4"),
            ["build in credential job"] = Mutate(
                ReleaseWorkflow,
                "- id: nuget-login",
                "- run: dotnet build -t:Publish\n      - id: nuget-login"),
            ["unmodeled acquisition resolver"] = Mutate(
                ReleaseWorkflow,
                "\"rollForward\": \"latestPatch\"",
                "\"rollForward\": \"latestMajor\""),
            ["acquisition outside fixed resolver directory"] = Mutate(
                ReleaseWorkflow,
                "working-directory: /tmp/nugetready-acquisition",
                "working-directory: /tmp"),
            ["mutable ref checkout"] = Mutate(
                ReleaseWorkflow,
                "ref: ${{ github.sha }}",
                "ref: ${{ github.ref }}")
        };

        foreach (var mutation in mutations)
        {
            using var repository = WorkflowRepository.Create("release.yml", mutation.Value);

            var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

            Assert.True(
                findings.Any(finding => finding.IsError),
                $"Mutation '{mutation.Key}' unexpectedly remained certifiable: {string.Join(" | ", findings.Select(finding => finding.Message))}");
        }
    }

    [Fact]
    public void Unmodeled_global_json_sdk_resolver_property_is_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow);
        repository.WriteFile("global.json", """
            {
              "sdk": {
                "version": "8.0.425",
                "rollForward": "latestPatch",
                "allowPrerelease": false,
                "paths": [ ".repo-sdk" ]
              }
            }
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding =>
            finding.IsError &&
            finding.Message.Contains("global.json", StringComparison.OrdinalIgnoreCase) &&
            finding.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("global.json", "GLOBAL.JSON")]
    [InlineData("NuGet.config", "nuget.config")]
    [InlineData("nugetready.json", "NuGetReady.json")]
    [InlineData("Example.sln", "example.sln")]
    [InlineData("src", "Src")]
    [InlineData("src/Example/Example.csproj", "src/Example/example.csproj")]
    [InlineData(".github", ".GITHUB")]
    [InlineData(".github/workflows", ".github/Workflows")]
    [InlineData(".github/workflows/release.yml", ".github/workflows/release.YML")]
    public void Release_control_paths_require_exact_cross_platform_casing(string original, string replacement)
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow);
        repository.RenamePath(original, replacement);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding =>
            finding.IsError &&
            finding.Message.Contains("casing", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("validated-release-artifacts", "different-artifact")]
    [InlineData("path: artifacts/release", "path: artifacts/unvalidated")]
    [InlineData("needs: validate", "needs: missing-validation")]
    public void Dynamic_or_mismatched_artifact_continuity_is_blocking(string original, string replacement)
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(ReleaseWorkflow, original, replacement));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError &&
            (finding.Message.Contains("artifact", StringComparison.OrdinalIgnoreCase) ||
             finding.Message.Contains("dependency", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Artifact_continuity_uses_the_active_configuration_not_an_unrelated_root_file()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow);
        var activeConfig = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new PackageExpectation
                {
                    Id = "Different.Package",
                    Kind = "library",
                    Version = "1.0.0",
                    Artifacts = ["Different.Package.1.0.0.nupkg", "Different.Package.1.0.0.snupkg"]
                }
            ]
        };

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName, activeConfig);

        Assert.Contains(findings, finding => finding.Message.Contains("exact configured primary package", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Alternate_active_configuration_cannot_diverge_from_the_root_release_validator_config()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow);
        var rootConfigPath = Path.Combine(repository.Root.FullName, "nugetready.json");
        var alternateConfigPath = Path.Combine(repository.Root.FullName, "review-config.json");
        var originalConfig = File.ReadAllText(rootConfigPath);
        repository.WriteFile("review-config.json", originalConfig);
        repository.WriteFile("nugetready.json", originalConfig.Replace("1.0.0", "9.9.9", StringComparison.Ordinal));
        var activeConfig = ConfigurationLoader.Load(alternateConfigPath);

        var inspection = WorkflowPolicyInspector.InspectDetailed(
            repository.Root.FullName,
            activeConfig,
            alternateConfigPath);

        Assert.Contains(inspection.Failures, finding =>
            finding.IsError &&
            finding.Message.Contains("repository-root nugetready.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Supplied_configuration_without_path_identity_cannot_certify_the_release_profile()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow);
        var activeConfig = ConfigurationLoader.Load(Path.Combine(repository.Root.FullName, "nugetready.json"));

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName, activeConfig);

        Assert.Contains(inspection.Failures, finding =>
            finding.IsError &&
            finding.Message.Contains("path identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_skipped_validation_dependency_path_is_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "  validate:\n    needs: produce\n    runs-on: ubuntu-latest",
            "  validate:\n    needs: produce\n    if: false\n    runs-on: ubuntu-latest"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("validation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Yaml_aliases_and_merge_keys_on_the_publication_path_are_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "  publish:\n    needs: validate",
            "  template: &publish-template\n    runs-on: ubuntu-latest\n  publish:\n    <<: *publish-template\n    needs: validate"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Duplicate_yaml_keys_on_the_publication_path_are_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "  publish:\n    needs: validate",
            "  publish:\n    needs: validate\n    needs: validate"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("could not be parsed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validation_inside_the_credential_bearing_job_is_rejected()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "- uses: actions/download-artifact@v4",
            "- run: dotnet nugetready check --artifacts artifacts/release\n      - uses: actions/download-artifact@v4"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("credential-bearing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_secret_api_key_in_a_supported_env_scope_is_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "DOTNET_CLI_TELEMETRY_OPTOUT: '1'\n    steps:",
            "DOTNET_CLI_TELEMETRY_OPTOUT: '1'\n      LEGACY_KEY: ${{ secrets.NUGET_API_KEY }}\n    steps:"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("long-lived", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Echoed_validation_text_does_not_prove_artifact_validation()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            $"run: {ValidationCommand}",
            "run: echo dotnet nugetready check --artifacts artifacts/release"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("validation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Disabled_validation_step_does_not_prove_artifact_validation()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "- name: Validate exact artifacts\n        shell: pwsh",
            "- name: Validate exact artifacts\n        if: false\n        shell: pwsh"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("validation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Workflow_level_oidc_permission_is_not_accepted_for_the_publish_job()
    {
        using var repository = WorkflowRepository.Create("release.yml", """
            name: release
            on:
              push:
                tags: ["v*.*.*"]
            permissions:
              id-token: write
              contents: read
            jobs:
              publish:
                env:
                  KEELMATRIX_NO_TELEMETRY: '1'
                steps:
                  - run: dotnet nugetready check --artifacts artifacts/packages
                  - uses: NuGet/login@v1
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("permissions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Explicitly_empty_job_permissions_do_not_inherit_workflow_oidc_permission()
    {
        using var repository = WorkflowRepository.Create("release.yml", """
            name: release
            on:
              push:
                tags: ["v*.*.*"]
            permissions:
              id-token: write
              contents: read
            jobs:
              publish:
                permissions: {}
                env:
                  KEELMATRIX_NO_TELEMETRY: '1'
                steps:
                  - run: dotnet nugetready check --artifacts artifacts/packages
                  - uses: NuGet/login@v1
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("id-token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Quoted_yaml_keys_do_not_hide_a_release_workflow()
    {
        using var repository = WorkflowRepository.Create("release.yml", """
            "name": "Release"
            "on":
              "push":
                "tags": ["v*.*.*"]
            "permissions": { "contents": "read" }
            "jobs":
              "publish":
                "runs-on": "ubuntu-latest"
                "steps":
                  - "uses": "NuGet/login@v1"
                  - "run": "dotnet nuget push artifacts/Example.1.0.0.nupkg"
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("id-token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Authentication_after_publication_does_not_prove_the_publication_path()
    {
        const string login = "- id: nuget-login\n        uses: NuGet/login@v1\n        with:\n          user: dmitriyzen";
        const string push = "- shell: pwsh\n        env:\n          NUGET_API_KEY: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}\n        run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key \"$env:NUGET_API_KEY\"";
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            $"{login}\n      {push}",
            $"{push}\n      {login}"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Conditional_authentication_does_not_prove_an_executed_authentication_step()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "- id: nuget-login\n        uses: NuGet/login@v1",
            "- id: nuget-login\n        if: github.ref_type == 'tag'\n        uses: NuGet/login@v1"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unconstrained_tag_pattern_is_not_a_version_gate()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow.Replace("v1.0.0", "*", StringComparison.Ordinal));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_additional_branch_trigger_requires_a_supported_tag_condition()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "tags: [\"v1.0.0\"]",
            "branches: [\"main\"]\n    tags: [\"v1.0.0\"]"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("branch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Branches_ignore_also_indicates_a_branch_trigger_when_tags_are_configured()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "tags: [\"v1.0.0\"]",
            "tags: [\"v1.0.0\"]\n    branches-ignore: [\"main\"]"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("branch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tags_ignore_is_not_a_versioned_tag_gate()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "tags: [\"v1.0.0\"]",
            "tags-ignore: [\"v0.*\"]"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unsupported_job_condition_is_reported_as_unproven()
    {
        var workflow = ReleaseWorkflow.Replace("\r\n", "\n", StringComparison.Ordinal).Replace(
            "  publish:",
            "  publish:\n    if: needs.validate.result == 'success'",
            StringComparison.Ordinal);
        using var repository = WorkflowRepository.Create("release.yml", workflow);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reusable_workflow_job_is_reported_as_limited_unproven_instead_of_missing_publication()
    {
        using var repository = WorkflowRepository.Create("release.yml", """
            name: release
            on:
              push:
                tags: ["v*.*.*"]
            jobs:
              publish:
                uses: ./.github/workflows/reusable-publish.yml
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        var finding = Assert.Single(findings, finding => finding.IsError && finding.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("reusable", finding.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(findings, finding => finding.Message.Contains("does not contain an executable package publication step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Non_release_ci_reusable_build_job_is_not_classified_as_release()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: ci
            on:
              push:
                branches: ["main"]
            jobs:
              build:
                permissions: {}
                uses: ./.github/workflows/reusable-build.yml
            """);
        repository.WriteWorkflow("reusable-build.yml", """
            name: reusable build
            on:
              workflow_call:
            permissions: {}
            jobs:
              build:
                steps:
                  - run: dotnet build
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Empty(findings);
    }

    [Fact]
    public void Scheduled_workflow_with_a_local_publication_script_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: maintenance
            on:
              schedule:
                - cron: "0 0 * * *"
            jobs:
              maintenance:
                steps:
                  - run: pwsh scripts/publish.ps1
            """);
        repository.WriteFile("scripts/publish.ps1", "dotnet nuget push artifacts/package.nupkg");

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Workflow_run_with_a_local_publication_script_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: maintenance
            on:
              workflow_run:
                workflows: [build]
                types: [completed]
            jobs:
              maintenance:
                steps:
                  - run: bash tools/publish.sh
            """);
        repository.WriteFile("tools/publish.sh", "nuget push artifacts/package.nupkg");

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Branch_workflow_with_a_local_publication_script_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: ./release.sh
            """);
        repository.WriteFile("release.sh", "gh release create v1.0.0");

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Matrix_workflow_with_a_local_publication_script_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                strategy:
                  matrix:
                    os: [ubuntu-latest, windows-latest]
                steps:
                  - run: sh tools/publish.sh
            """);
        repository.WriteFile("tools/publish.sh", "NuGet.exe push artifacts/package.nupkg");

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Ordinary_ci_with_a_local_composite_publication_action_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - uses: ./.github/actions/build
            """);
        repository.WriteFile(".github/actions/build/action.yml", """
            name: build
            description: Build the package
            runs:
              using: composite
              steps:
                - shell: bash
                  run: dotnet nuget push artifacts/package.nupkg
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Resolvable_local_script_without_publication_keeps_ci_quiet()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              pull_request:
            permissions: {}
            jobs:
              build:
                steps:
                  - run: bash scripts/build.sh
            """);
        repository.WriteFile("scripts/build.sh", "dotnet build --no-restore");

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Fact]
    public void Resolvable_local_composite_without_publication_keeps_ci_quiet()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            permissions: {}
            jobs:
              build:
                steps:
                  - uses: ./.github/actions/build
            """);
        repository.WriteFile(".github/actions/build/action.yaml", """
            name: build
            description: Build the package
            runs:
              using: composite
              steps:
                - shell: bash
                  run: dotnet build --no-restore
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Fact]
    public void Nested_unix_script_publication_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: bash scripts/outer.sh
            """);
        repository.WriteFile("scripts/outer.sh", "bash scripts/inner.sh");
        repository.WriteFile("scripts/inner.sh", "dotnet nuget push artifacts/package.nupkg");

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Nested_powershell_script_publication_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: pwsh -File scripts/outer.ps1
            """);
        repository.WriteFile("scripts/outer.ps1", "pwsh -File scripts/inner.ps1");
        repository.WriteFile("scripts/inner.ps1", "dotnet nuget push artifacts/package.nupkg");

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Fully_resolved_nested_nonpublishing_scripts_keep_ci_quiet()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              pull_request:
            permissions: {}
            jobs:
              build:
                steps:
                  - run: bash scripts/outer.sh
            """);
        repository.WriteFile("scripts/outer.sh", "bash scripts/inner.sh");
        repository.WriteFile("scripts/inner.sh", "dotnet build --no-restore");

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Fact]
    public void Indirect_script_depth_limit_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: bash scripts/level0.sh
            """);
        for (var level = 0; level <= 8; level++)
        {
            repository.WriteFile(
                $"scripts/level{level}.sh",
                level == 8 ? "dotnet build --no-restore" : $"bash scripts/level{level + 1}.sh");
        }

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Theory]
    [InlineData("bash -c './scripts/publish.sh'")]
    [InlineData("bash -c 'dotnet nuget push artifacts/package.nupkg'")]
    [InlineData("powershell -Command \"& ./scripts/publish.ps1\"")]
    [InlineData("powershell -Command \"dotnet nuget push artifacts/package.nupkg\"")]
    [InlineData("pwsh -File \"scripts/publish.ps1\"")]
    [InlineData("pwsh -File $env:SCRIPT_PATH")]
    [InlineData("bash ${{ inputs.script }}")]
    [InlineData("bash \"$SCRIPT_PATH\"")]
    public void Wrapped_or_derived_script_paths_are_limited_unproven(string command)
    {
        using var repository = WorkflowRepository.Create("ci.yml", $"""
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: {command}
            """);
        repository.WriteFile("scripts/publish.sh", "dotnet nuget push artifacts/package.nupkg");
        repository.WriteFile("scripts/publish.ps1", "dotnet nuget push artifacts/package.nupkg");

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Theory]
    [InlineData("permissions: {}")]
    [InlineData("permissions:\n  contents: read")]
    public void Unknown_action_in_credential_free_read_only_ci_stays_outside_release_policy(string permissions)
    {
        using var repository = WorkflowRepository.Create("ci.yml", $$"""
            name: continuous integration
            on:
              push:
                branches: [main]
            {{permissions}}
            jobs:
              build:
                steps:
                  - uses: owner/repository/action@v1
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Theory]
    [InlineData("env", "NUGET_API_KEY", "literal-credential")]
    [InlineData("env", "NUGET_API_KEY", "${{ vars.NUGET_API_KEY }}")]
    [InlineData("with", "api-key", "literal-credential")]
    [InlineData("with", "api-key", "${{ vars.NUGET_API_KEY }}")]
    public void Unknown_action_with_credential_shaped_binding_is_blocking(
        string bindingMap,
        string bindingName,
        string bindingValue)
    {
        using var repository = WorkflowRepository.Create("ci.yml", $"""
            name: continuous integration
            on:
              pull_request:
            permissions:
              contents: read
            jobs:
              build:
                steps:
                  - uses: owner/repository/action@v1
                    {bindingMap}:
                      {bindingName}: {bindingValue}
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
        Assert.Contains(inspection.Failures, failure => failure.Message.Contains("remote action", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Credential_shaped_binding_probes_block_the_complete_readiness_result()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new PackageExpectation
                {
                    Id = "Fixture.Standard",
                    Kind = "library",
                    Version = "1.0.0",
                    Artifacts = [Path.GetFileName(package), Path.GetFileName(Path.ChangeExtension(package, ".snupkg"))]
                }
            ]
        };
        var releaseWorkflow = Mutate(
            ReleaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.nupkg",
            Path.GetFileName(package));
        releaseWorkflow = Mutate(
            releaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.snupkg",
            Path.GetFileName(Path.ChangeExtension(package, ".snupkg")));
        var probes = new[]
        {
            (Name: "literal environment", Map: "env", Binding: "NUGET_API_KEY", Value: "literal-credential"),
            (Name: "vars environment", Map: "env", Binding: "NUGET_API_KEY", Value: "${{ vars.NUGET_API_KEY }}"),
            (Name: "literal action input", Map: "with", Binding: "api-key", Value: "literal-credential"),
            (Name: "vars action input", Map: "with", Binding: "api-key", Value: "${{ vars.NUGET_API_KEY }}"),
            (Name: "camel-case NuGet API key", Map: "with", Binding: "NuGetApiKey", Value: "literal-credential"),
            (Name: "separator-free NuGet API key", Map: "with", Binding: "nugetapikey", Value: "literal-credential"),
            (Name: "camel-case API key", Map: "with", Binding: "ApiKey", Value: "literal-credential"),
            (Name: "camel-case access token", Map: "with", Binding: "AccessToken", Value: "literal-credential"),
            (Name: "neutral vars member binding", Map: "with", Binding: "note", Value: "${{ vars.NUGET_API_KEY }}"),
            (Name: "inputs member control", Map: "with", Binding: "nuget-api-key", Value: "${{ inputs.nuget_api_key }}"),
            (Name: "literal access-token control", Map: "with", Binding: "access-token", Value: "literal-credential"),
            (Name: "trimmed quoted API key control", Map: "with", Binding: "\" API_KEY \"", Value: "literal-credential")
        };

        foreach (var probe in probes)
        {
            using var repository = WorkflowRepository.Create("release.yml", releaseWorkflow);
            repository.WriteWorkflow("ci.yml", $"""
                name: continuous integration
                on:
                  pull_request:
                permissions:
                  contents: read
                jobs:
                  build:
                    steps:
                      - uses: owner/repository/action@v1
                        {probe.Map}:
                          {probe.Binding}: {probe.Value}
                """);
            var configPath = Path.Combine(repository.Root.FullName, "nugetready.json");
            repository.WriteFile("nugetready.json", System.Text.Json.JsonSerializer.Serialize(config));

            var report = CheckRunner.Run(
                config,
                corpus.OutputPath,
                repository.Root.FullName,
                TimeSpan.FromMinutes(2),
                new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath),
                configPath);

            Assert.True(
                report.Status == "error" && report.ExitCode == 2,
                $"Probe '{probe.Name}' returned status={report.Status}, exitCode={report.ExitCode}.");
            Assert.Equal("error", report.Checks.Single(check => check.Id == "workflow-policy").Status);
            Assert.Contains(report.Failures, failure => failure.IsError && failure.Message.Contains("remote action", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Remaining_credential_boundary_probes_block_the_complete_readiness_result()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var symbolPackage = Path.ChangeExtension(package, ".snupkg");
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new PackageExpectation
                {
                    Id = "Fixture.Standard",
                    Kind = "library",
                    Version = "1.0.0",
                    Artifacts = [Path.GetFileName(package), Path.GetFileName(symbolPackage)]
                }
            ]
        };
        var releaseWorkflow = Mutate(
            ReleaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.nupkg",
            Path.GetFileName(package));
        releaseWorkflow = Mutate(
            releaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.snupkg",
            Path.GetFileName(symbolPackage));
        var probes = new[]
        {
            (Name: "dynamic member selector", Binding: "note", Value: "${{ vars[inputs.member_name] }}"),
            (Name: "period-separated environment key", Binding: "Nuget.Api.Key", Value: "literal-credential")
        };
        var outcomes = new List<string>();

        foreach (var probe in probes)
        {
            using var repository = WorkflowRepository.Create("release.yml", releaseWorkflow);
            repository.WriteWorkflow("ci.yml", $$"""
                name: continuous integration
                on:
                  pull_request:
                permissions:
                  contents: read
                jobs:
                  build:
                    steps:
                      - uses: owner/repository/action@v1
                        env:
                          {{probe.Binding}}: {{probe.Value}}
                """);
            var configPath = Path.Combine(repository.Root.FullName, "nugetready.json");
            repository.WriteFile("nugetready.json", System.Text.Json.JsonSerializer.Serialize(config));

            var report = CheckRunner.Run(
                config,
                corpus.OutputPath,
                repository.Root.FullName,
                TimeSpan.FromMinutes(2),
                new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath),
                configPath);
            var workflowPolicyStatus = report.Checks.Single(check => check.Id == "workflow-policy").Status;

            outcomes.Add($"{probe.Name}: overall={report.Status}; workflow-policy={workflowPolicyStatus}; exit={report.ExitCode}");
        }

        Console.WriteLine(string.Join(Environment.NewLine, outcomes));
        Assert.True(
            outcomes.All(outcome => outcome.Contains("overall=error; workflow-policy=error; exit=2", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, outcomes));
    }

    [Theory]
    [InlineData("note", "${{ vars['ARTIFACT.PATH'] }}")]
    [InlineData("ApiKeyPath", "artifacts/key.txt")]
    [InlineData("Nuget.Api.Key.Path", "artifacts/key.txt")]
    public void Credential_boundary_negative_controls_preserve_the_complete_readiness_result(
        string bindingName,
        string bindingValue)
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var symbolPackage = Path.ChangeExtension(package, ".snupkg");
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new PackageExpectation
                {
                    Id = "Fixture.Standard",
                    Kind = "library",
                    Version = "1.0.0",
                    Artifacts = [Path.GetFileName(package), Path.GetFileName(symbolPackage)]
                }
            ]
        };
        var releaseWorkflow = Mutate(
            ReleaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.nupkg",
            Path.GetFileName(package));
        releaseWorkflow = Mutate(
            releaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.snupkg",
            Path.GetFileName(symbolPackage));
        using var repository = WorkflowRepository.Create("release.yml", releaseWorkflow);
        repository.WriteWorkflow("ci.yml", $$"""
            name: continuous integration
            on:
              pull_request:
            permissions:
              contents: read
            jobs:
              build:
                steps:
                  - uses: owner/repository/action@v1
                    env:
                      {{bindingName}}: {{bindingValue}}
            """);
        var configPath = Path.Combine(repository.Root.FullName, "nugetready.json");
        repository.WriteFile("nugetready.json", System.Text.Json.JsonSerializer.Serialize(config));

        var report = CheckRunner.Run(
            config,
            corpus.OutputPath,
            repository.Root.FullName,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath),
            configPath);
        var workflowPolicyStatus = report.Checks.Single(check => check.Id == "workflow-policy").Status;

        Console.WriteLine($"{bindingName}={bindingValue}: overall={report.Status}; workflow-policy={workflowPolicyStatus}; exit={report.ExitCode}");
        Assert.True(
            report.Status == "pass" && workflowPolicyStatus == "pass" && report.ExitCode == 0,
            $"overall={report.Status}; workflow-policy={workflowPolicyStatus}; exit={report.ExitCode}; failures={string.Join(" | ", report.Failures.Select(failure => failure.Message))}");
    }

    [Theory]
    [InlineData("api-key-path", "artifacts/key.txt")]
    [InlineData("ApiKeyPath", "artifacts/key.txt")]
    [InlineData("cache-path", ".secrets/cache")]
    [InlineData("note", "documentation-secrets-reference")]
    [InlineData("note", "NUGET_API_KEY")]
    [InlineData("note", "vars.NUGET_API_KEY")]
    [InlineData("note", "${{ vars['ARTIFACT.PATH'] }}")]
    [InlineData("note", "${{ vars.ApiKeyPath }}")]
    [InlineData("note", "${{ vars['Nuget.Api.Key.Path'] }}")]
    public void Safe_credential_boundary_probes_preserve_a_valid_release_result(
        string bindingName,
        string bindingValue)
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var symbolPackage = Path.ChangeExtension(package, ".snupkg");
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new PackageExpectation
                {
                    Id = "Fixture.Standard",
                    Kind = "library",
                    Version = "1.0.0",
                    Artifacts = [Path.GetFileName(package), Path.GetFileName(symbolPackage)]
                }
            ]
        };
        var workflow = Mutate(
            ReleaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.nupkg",
            Path.GetFileName(package));
        workflow = Mutate(
            workflow,
            "KeelMatrix.NuGetReady.1.0.0.snupkg",
            Path.GetFileName(symbolPackage));
        using var repository = WorkflowRepository.Create("release.yml", workflow);
        repository.WriteWorkflow("ci.yml", $$"""
            name: continuous integration
            on:
              pull_request:
            permissions: {}
            jobs:
              build:
                steps:
                  - uses: owner/repository/action@v1
                    with:
                      {{bindingName}}: {{bindingValue}}
            """);
        var configPath = Path.Combine(repository.Root.FullName, "nugetready.json");
        repository.WriteFile("nugetready.json", System.Text.Json.JsonSerializer.Serialize(config));

        var report = CheckRunner.Run(
            config,
            corpus.OutputPath,
            repository.Root.FullName,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath),
            configPath);

        Assert.True(
            report.Status == "pass" && report.ExitCode == 0,
            $"Binding '{bindingName}: {bindingValue}' returned status={report.Status}, exitCode={report.ExitCode}; failures={string.Join(" | ", report.Failures.Select(failure => failure.Message))}");
        Assert.Equal("pass", report.Checks.Single(check => check.Id == "workflow-policy").Status);
    }

    [Theory]
    [InlineData("NUGET_API_KEY")]
    [InlineData("NuGetApiKey")]
    [InlineData("nugetapikey")]
    [InlineData("nuget.api.key")]
    [InlineData("NUGET API KEY")]
    [InlineData("nuget/api-key")]
    [InlineData("API_KEY")]
    [InlineData("ApiKey")]
    [InlineData("api-key")]
    [InlineData("ACCESS_TOKEN")]
    [InlineData("AccessToken")]
    [InlineData("authorization")]
    [InlineData("PASSWORD")]
    [InlineData("secret")]
    [InlineData("CREDENTIAL")]
    [InlineData("\" API_KEY \"")]
    public void Bounded_credential_binding_name_family_enters_publication_policy(string bindingName)
    {
        using var repository = WorkflowRepository.Create("ci.yml", $"""
            name: continuous integration
            on:
              pull_request:
            permissions:
              contents: read
            jobs:
              build:
                steps:
                  - uses: owner/repository/action@v1
                    with:
                      {bindingName}: literal-credential
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Theory]
    [InlineData("${{ vars.NUGET_API_KEY }}")]
    [InlineData("${{ vars.NuGetApiKey }}")]
    [InlineData("${{ vars['Nuget.Api.Key'] }}")]
    [InlineData("${{ inputs.ApiKey }}")]
    [InlineData("${{ env.AccessToken }}")]
    [InlineData("prefix-${{ vars['api-key'] }}-suffix")]
    [InlineData("${{ inputs[\"ACCESS_TOKEN\"] }}")]
    [InlineData("${{ format('}}') || vars.ApiKey }}")]
    public void Credential_shaped_member_reference_enters_publication_policy(string value)
    {
        using var repository = WorkflowRepository.Create("ci.yml", $$"""
            name: continuous integration
            on:
              pull_request:
            permissions: {}
            jobs:
              build:
                steps:
                  - uses: owner/repository/action@v1
                    with:
                      note: {{value}}
            """);

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Theory]
    [InlineData("${{ vars[inputs.member_name] }}")]
    [InlineData("${{ inputs[vars.member_name] }}")]
    [InlineData("${{ env[inputs.member_name] }}")]
    [InlineData("${{ vars[format('{0}', inputs.member_name)] }}")]
    [InlineData("${{ vars }}")]
    [InlineData("${{ inputs. }}")]
    [InlineData("${{ env.SAFE_VALUE.other }}")]
    [InlineData("${{ vars['SAFE_VALUE'][inputs.index] }}")]
    public void Unresolvable_credential_member_selectors_enter_publication_policy(string value)
    {
        using var repository = WorkflowRepository.Create("ci.yml", $$"""
            name: continuous integration
            on:
              pull_request:
            permissions: {}
            jobs:
              build:
                steps:
                  - uses: owner/repository/action@v1
                    with:
                      note: {{value}}
            """);

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Theory]
    [InlineData("${{ vars.NUGET_API_KEY")]
    [InlineData("${{ vars.NUGET_API_KEY }")]
    [InlineData("${{ vars['NUGET_API_KEY] }}")]
    [InlineData("${{ vars['NUGET_API_KEY'")]
    [InlineData("${{ vars[inputs.member_name]")]
    [InlineData("${{ inputs.ApiKey")]
    [InlineData("${{ env.ApiKey")]
    [InlineData("${{ secrets.ApiKey")]
    [InlineData("${{ vars['ARTIFACT.PATH']")]
    [InlineData("${{ matrix.release_channel")]
    [InlineData("${{ ${{ vars.NUGET_API_KEY }}")]
    [InlineData("${{${{ vars.NUGET_API_KEY }}}}")]
    [InlineData("${{ vars.SAFE_VALUE }}-${{ inputs.ApiKey")]
    public void Malformed_expression_framing_is_unsupported_unproven(string value)
    {
        using var repository = WorkflowRepository.Create("ci.yml", $$"""
            name: continuous integration
            on:
              push:
                branches: [main]
            permissions:
              contents: read
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - name: Read-only action
                    uses: actions/checkout@v6
                    with:
                      note: >-
                        {{value}}
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
        Assert.Contains(
            inspection.Failures,
            failure => failure.IsError &&
                       failure.Message.Contains("malformed/incomplete expression framing", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("workflow-env")]
    [InlineData("workflow-run-name")]
    [InlineData("workflow-concurrency")]
    [InlineData("job-env")]
    [InlineData("job-condition")]
    [InlineData("job-name")]
    [InlineData("job-concurrency")]
    [InlineData("job-continue-on-error")]
    [InlineData("job-strategy")]
    [InlineData("job-defaults-run")]
    [InlineData("job-output")]
    [InlineData("job-environment-scalar")]
    [InlineData("job-environment-url")]
    [InlineData("container-scalar")]
    [InlineData("container-image")]
    [InlineData("container-credentials")]
    [InlineData("container-env")]
    [InlineData("service-image")]
    [InlineData("service-credentials")]
    [InlineData("service-env")]
    [InlineData("job-runs-on")]
    [InlineData("job-timeout")]
    [InlineData("step-env")]
    [InlineData("step-name")]
    [InlineData("action-with")]
    [InlineData("reusable-with")]
    [InlineData("reusable-secrets")]
    [InlineData("workflow-call-input-default")]
    [InlineData("workflow-call-output-value")]
    [InlineData("step-run")]
    [InlineData("step-condition")]
    [InlineData("step-shell")]
    [InlineData("step-working-directory")]
    [InlineData("step-timeout")]
    [InlineData("step-continue-on-error")]
    public void Malformed_expression_framing_is_blocking_across_evaluated_scalar_surfaces(string surface)
    {
        const string malformedExpression = "${{ vars.ARTIFACT_PATH";
        var workflow = surface switch
        {
            "workflow-env" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                env:
                  PROBE: '{{malformedExpression}}'
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "workflow-run-name" => $$"""
                name: continuous integration
                run-name: '{{malformedExpression}}'
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "workflow-concurrency" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                concurrency:
                  group: '{{malformedExpression}}'
                  cancel-in-progress: false
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-env" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    env:
                      PROBE: '{{malformedExpression}}'
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-condition" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    if: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-name" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    name: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-concurrency" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    concurrency: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-continue-on-error" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    continue-on-error: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-strategy" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    strategy:
                      fail-fast: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-defaults-run" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    defaults:
                      run:
                        working-directory: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-output" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    outputs:
                      probe: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-environment-scalar" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    environment: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-environment-url" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    environment:
                      name: probe
                      url: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "container-scalar" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    container: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "container-image" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    container:
                      image: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "container-credentials" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    container:
                      image: ubuntu:latest
                      credentials:
                        username: probe
                        password: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "container-env" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    container:
                      image: ubuntu:latest
                      env:
                        PROBE: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "service-image" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    services:
                      redis:
                        image: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "service-credentials" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    services:
                      redis:
                        image: redis:latest
                        credentials:
                          username: probe
                          password: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "service-env" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    services:
                      redis:
                        image: redis:latest
                        env:
                          PROBE: '{{malformedExpression}}'
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-runs-on" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: '{{malformedExpression}}'
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-timeout" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    timeout-minutes: '{{malformedExpression}}'
                    steps:
                      - uses: actions/checkout@v6
                """,
            "step-env" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                        env:
                          PROBE: '{{malformedExpression}}'
                """,
            "step-name" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - name: '{{malformedExpression}}'
                        uses: actions/checkout@v6
                """,
            "action-with" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                        with:
                          note: '{{malformedExpression}}'
                """,
            "reusable-with" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    uses: owner/repository/.github/workflows/reusable.yml@v1
                    with:
                      note: '{{malformedExpression}}'
                """,
            "reusable-secrets" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    uses: owner/repository/.github/workflows/reusable.yml@v1
                    secrets:
                      PROBE: '{{malformedExpression}}'
                """,
            "workflow-call-input-default" => $$"""
                name: continuous integration
                on:
                  workflow_call:
                    inputs:
                      channel:
                        description: Channel
                        default: '{{malformedExpression}}'
                        required: false
                        type: string
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "workflow-call-output-value" => $$"""
                name: continuous integration
                on:
                  workflow_call:
                    outputs:
                      probe:
                        description: Probe
                        value: '{{malformedExpression}}'
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    outputs:
                      probe: safe
                    steps:
                      - uses: actions/checkout@v6
                """,
            "step-run" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - run: echo '{{malformedExpression}}'
                """,
            "step-condition" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - if: '{{malformedExpression}}'
                        uses: actions/checkout@v6
                """,
            "step-shell" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - shell: '{{malformedExpression}}'
                        run: echo probe
                """,
            "step-working-directory" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - working-directory: '{{malformedExpression}}'
                        run: echo probe
                """,
            "step-timeout" => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - timeout-minutes: '{{malformedExpression}}'
                        run: echo probe
                """,
            _ => $$"""
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - continue-on-error: '{{malformedExpression}}'
                        run: echo probe
                """
        };
        using var repository = WorkflowRepository.Create("ci.yml", workflow);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
        Assert.Contains(
            inspection.Failures,
            failure => failure.IsError &&
                       failure.Message.Contains("malformed/incomplete expression framing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Malformed_expression_framing_in_composite_action_inputs_is_unsupported_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on: push
            permissions:
              contents: read
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: ./.github/actions/adversarial-probe
            """);
        repository.WriteFile(".github/actions/adversarial-probe/action.yml", """
            name: Adversarial probe
            description: Test fixture
            runs:
              using: composite
              steps:
                - uses: actions/checkout@v6
                  with:
                    fetch-depth: '${{ matrix.release_channel'
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
        Assert.Contains(
            inspection.Failures,
            failure => failure.IsError &&
                       failure.Message.Contains("malformed/incomplete expression framing", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("ordinary-literal")]
    [InlineData("${{ vars['ARTIFACT.PATH'] }}")]
    [InlineData("${{ vars.ApiKeyPath }}")]
    [InlineData("${{ vars['Nuget.Api.Key.Path'] }}")]
    [InlineData("${{ format('${{') }}")]
    public void Complete_noncredential_expressions_and_literals_stay_outside_release_policy(string value)
    {
        using var repository = WorkflowRepository.Create("ci.yml", $$"""
            name: continuous integration
            on:
              push:
                branches: [main]
            permissions:
              contents: read
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v6
                    with:
                      note: {{value}}
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Theory]
    [InlineData("workflow-concurrency")]
    [InlineData("job-scalars")]
    [InlineData("job-objects")]
    [InlineData("workflow-call")]
    public void Complete_noncredential_expressions_stay_outside_release_policy_across_structural_families(string family)
    {
        var workflow = family switch
        {
            "workflow-concurrency" => """
                name: continuous integration
                on: push
                permissions:
                  contents: read
                concurrency:
                  group: ${{ github.ref_name }}
                  cancel-in-progress: ${{ github.ref_type == 'branch' }}
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-scalars" => """
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    name: ${{ matrix.display_name }}
                    concurrency: ${{ matrix.concurrency_group }}
                    continue-on-error: ${{ matrix.experimental }}
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "job-objects" => """
                name: continuous integration
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    strategy:
                      fail-fast: ${{ github.ref_type == 'branch' }}
                    defaults:
                      run:
                        working-directory: ${{ github.workspace }}
                    outputs:
                      probe: ${{ steps.probe.outputs.value }}
                    container:
                      image: ${{ vars.CONTAINER_IMAGE }}
                      credentials:
                        username: ${{ vars.CONTAINER_USER }}
                        password: ${{ vars.CONTAINER_PASSWORD }}
                      env:
                        PROBE: ${{ github.ref_name }}
                    services:
                      redis:
                        image: ${{ vars.SERVICE_IMAGE }}
                        credentials:
                          username: ${{ vars.SERVICE_USER }}
                          password: ${{ vars.SERVICE_PASSWORD }}
                        env:
                          PROBE: ${{ github.ref_name }}
                    runs-on: ubuntu-latest
                    steps:
                      - id: probe
                        run: echo probe
                """,
            _ => """
                name: continuous integration
                on:
                  workflow_call:
                    inputs:
                      channel:
                        default: ${{ vars.DEFAULT_CHANNEL }}
                        required: false
                        type: string
                    outputs:
                      probe:
                        value: ${{ vars.OUTPUT_VALUE }}
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """
        };
        using var repository = WorkflowRepository.Create("ci.yml", workflow);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Fact]
    public void Complete_noncredential_expression_in_composite_action_input_stays_outside_release_policy()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on: push
            permissions:
              contents: read
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: ./.github/actions/read-only
            """);
        repository.WriteFile(".github/actions/read-only/action.yml", """
            name: Read-only action
            description: Test fixture
            runs:
              using: composite
              steps:
                - uses: actions/checkout@v6
                  with:
                    ref: ${{ github.ref_name }}
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Fact]
    public void Malformed_expression_framing_family_blocks_an_otherwise_valid_release_rehearsal()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var symbolPackage = Path.ChangeExtension(package, ".snupkg");
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new PackageExpectation
                {
                    Id = "Fixture.Standard",
                    Kind = "library",
                    Version = "1.0.0",
                    Artifacts = [Path.GetFileName(package), Path.GetFileName(symbolPackage)]
                }
            ]
        };
        var releaseWorkflow = Mutate(
            ReleaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.nupkg",
            Path.GetFileName(package));
        releaseWorkflow = Mutate(
            releaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.snupkg",
            Path.GetFileName(symbolPackage));
        var cases = new[]
        {
            (Name: "selector_unclosed_expression", Value: "${{ vars.NUGET_API_KEY"),
            (Name: "single_closing_brace", Value: "${{ vars.NUGET_API_KEY }"),
            (Name: "unterminated_quoted_member", Value: "${{ vars['NUGET_API_KEY] }}"),
            (Name: "incomplete_bracket_selector", Value: "${{ vars['NUGET_API_KEY'"),
            (Name: "incomplete_dynamic_selector", Value: "${{ vars[inputs.member_name]"),
            (Name: "inputs_root", Value: "${{ inputs.ApiKey"),
            (Name: "env_root", Value: "${{ env.ApiKey"),
            (Name: "secrets_root", Value: "${{ secrets.ApiKey"),
            (Name: "static_nonbounded_member", Value: "${{ vars['ARTIFACT.PATH']"),
            (Name: "unknown_root", Value: "${{ matrix.release_channel"),
            (Name: "nested_opener", Value: "${{ ${{ vars.NUGET_API_KEY }}"),
            (Name: "doubled_opener", Value: "${{${{ vars.NUGET_API_KEY }}}}"),
            (Name: "complete_then_unclosed", Value: "${{ vars.SAFE_VALUE }}-${{ inputs.ApiKey")
        };

        foreach (var testCase in cases)
        {
            var ciWorkflow = testCase.Name == "selector_unclosed_expression"
                ? """
                    name: CI Probe
                    on:
                      push:
                        branches: [main]
                    permissions:
                      contents: read
                    jobs:
                      build:
                        runs-on: ubuntu-latest
                        steps:
                          - name: Read-only action
                            uses: actions/checkout@v6
                            env:
                              PROBE: '${{ vars.NUGET_API_KEY'
                    """
                : $$"""
                    name: CI Probe
                    on:
                      push:
                        branches: [main]
                    permissions:
                      contents: read
                    jobs:
                      build:
                        runs-on: ubuntu-latest
                        steps:
                          - name: Read-only action
                            uses: actions/checkout@v6
                            env:
                              PROBE: >-
                                {{testCase.Value}}
                    """;
            using var repository = WorkflowRepository.Create("release.yml", releaseWorkflow);
            repository.WriteWorkflow("ci.yml", ciWorkflow);
            var configPath = Path.Combine(repository.Root.FullName, "nugetready.json");
            repository.WriteFile("nugetready.json", System.Text.Json.JsonSerializer.Serialize(config));

            var report = CheckRunner.Run(
                config,
                corpus.OutputPath,
                repository.Root.FullName,
                TimeSpan.FromMinutes(2),
                new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath),
                configPath);
            var workflowPolicyStatus = report.Checks.Single(check => check.Id == "workflow-policy").Status;

            Console.WriteLine($"case={testCase.Name} overall={report.Status}; workflow-policy={workflowPolicyStatus}; exit={report.ExitCode}");
            Assert.True(
                report.Status == "error" && workflowPolicyStatus == "error" && report.ExitCode == 2,
                $"case={testCase.Name} overall={report.Status}; workflow-policy={workflowPolicyStatus}; exit={report.ExitCode}; failures={string.Join(" | ", report.Failures.Select(failure => failure.Message))}");
        }
    }

    [Fact]
    public void Runs_on_scalar_sequence_and_mapping_expression_framing_controls_complete_readiness_result()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var symbolPackage = Path.ChangeExtension(package, ".snupkg");
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new PackageExpectation
                {
                    Id = "Fixture.Standard",
                    Kind = "library",
                    Version = "1.0.0",
                    Artifacts = [Path.GetFileName(package), Path.GetFileName(symbolPackage)]
                }
            ]
        };
        var releaseWorkflow = Mutate(
            ReleaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.nupkg",
            Path.GetFileName(package));
        releaseWorkflow = Mutate(
            releaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.snupkg",
            Path.GetFileName(symbolPackage));
        const string ciWorkflow = """
            name: CI Probe
            on:
              push:
                branches: [main]
            permissions:
              contents: read
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v6
            """;
        var cases = new[]
        {
            (
                Name: "scalar-malformed",
                RunsOn: "runs-on: '${{ matrix.channel'",
                ExpectedStatus: "error",
                ExpectedExitCode: 2),
            (
                Name: "scalar-complete",
                RunsOn: "runs-on: '${{ matrix.channel }}'",
                ExpectedStatus: "pass",
                ExpectedExitCode: 0),
            (
                Name: "sequence-malformed",
                RunsOn: """
                    runs-on:
                      - self-hosted
                      - '${{ matrix.channel'
                    """,
                ExpectedStatus: "error",
                ExpectedExitCode: 2),
            (
                Name: "sequence-complete",
                RunsOn: """
                    runs-on:
                      - self-hosted
                      - '${{ matrix.channel }}'
                    """,
                ExpectedStatus: "pass",
                ExpectedExitCode: 0),
            (
                Name: "mapping-malformed-nested-label",
                RunsOn: """
                    runs-on:
                      group: '${{ vars.RUNNER_GROUP }}'
                      labels:
                        - self-hosted
                        - '${{ matrix.channel'
                    """,
                ExpectedStatus: "error",
                ExpectedExitCode: 2),
            (
                Name: "mapping-complete",
                RunsOn: """
                    runs-on:
                      group: '${{ vars.RUNNER_GROUP }}'
                      labels:
                        - self-hosted
                        - '${{ matrix.channel }}'
                    """,
                ExpectedStatus: "pass",
                ExpectedExitCode: 0)
        };
        var outcomes = new List<string>();

        foreach (var testCase in cases)
        {
            using var repository = WorkflowRepository.Create("release.yml", releaseWorkflow);
            repository.WriteWorkflow(
                "ci.yml",
                Mutate(
                    ciWorkflow,
                    "runs-on: ubuntu-latest",
                    testCase.RunsOn.Replace("\n", "\n    ", StringComparison.Ordinal)));
            var configPath = Path.Combine(repository.Root.FullName, "nugetready.json");
            repository.WriteFile("nugetready.json", System.Text.Json.JsonSerializer.Serialize(config));

            var report = CheckRunner.Run(
                config,
                corpus.OutputPath,
                repository.Root.FullName,
                TimeSpan.FromMinutes(2),
                new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath),
                configPath);
            var workflowPolicyStatus = report.Checks.Single(check => check.Id == "workflow-policy").Status;

            outcomes.Add(
                $"{testCase.Name}: overall={report.Status}; workflow-policy={workflowPolicyStatus}; exit={report.ExitCode}; " +
                $"expected={testCase.ExpectedStatus}/{testCase.ExpectedExitCode}");
        }

        Console.WriteLine(string.Join(Environment.NewLine, outcomes));
        Assert.True(
            outcomes.All(outcome =>
                outcome.Contains("overall=error; workflow-policy=error; exit=2; expected=error/2", StringComparison.Ordinal) ||
                outcome.Contains("overall=pass; workflow-policy=pass; exit=0; expected=pass/0", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, outcomes));
    }

    [Theory]
    [InlineData("branches", "${{feature")]
    [InlineData("tags", "${{feature")]
    [InlineData("tags", "docs-*")]
    public void Static_nonrelease_filters_do_not_block_a_valid_release_rehearsal(string filter, string pattern)
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var symbolPackage = Path.ChangeExtension(package, ".snupkg");
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new PackageExpectation
                {
                    Id = "Fixture.Standard",
                    Kind = "library",
                    Version = "1.0.0",
                    Artifacts = [Path.GetFileName(package), Path.GetFileName(symbolPackage)]
                }
            ]
        };
        var releaseWorkflow = Mutate(
            ReleaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.nupkg",
            Path.GetFileName(package));
        releaseWorkflow = Mutate(
            releaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.snupkg",
            Path.GetFileName(symbolPackage));
        using var repository = WorkflowRepository.Create("release.yml", releaseWorkflow);
        repository.WriteWorkflow("ci.yml", $$"""
            name: CI Probe
            on:
              push:
                {{filter}}:
                  - '{{pattern}}'
            permissions:
              contents: read
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - name: Read-only action
                    uses: actions/checkout@v6
            """);
        var configPath = Path.Combine(repository.Root.FullName, "nugetready.json");
        repository.WriteFile("nugetready.json", System.Text.Json.JsonSerializer.Serialize(config));

        var report = CheckRunner.Run(
            config,
            corpus.OutputPath,
            repository.Root.FullName,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath),
            configPath);
        var workflowPolicyStatus = report.Checks.Single(check => check.Id == "workflow-policy").Status;

        Console.WriteLine($"case=static_{filter}_{pattern} expected=pass/pass/0 overall={report.Status} workflow-policy={workflowPolicyStatus} exit={report.ExitCode}");
        Assert.True(
            report.Status == "pass" && workflowPolicyStatus == "pass" && report.ExitCode == 0,
            $"overall={report.Status}; workflow-policy={workflowPolicyStatus}; exit={report.ExitCode}; failures={string.Join(" | ", report.Failures.Select(failure => failure.Message))}");
    }

    [Theory]
    [InlineData("workflow-name")]
    [InlineData("push-path")]
    [InlineData("push-tag-ignore")]
    [InlineData("workflow-default")]
    public void Static_workflow_data_with_unmatched_opener_stays_outside_release_policy(string surface)
    {
        var workflow = surface switch
        {
            "workflow-name" => """
                name: 'CI ${{feature'
                on: push
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "push-path" => """
                name: CI Probe
                on:
                  push:
                    paths:
                      - '${{feature'
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            "push-tag-ignore" => """
                name: CI Probe
                on:
                  push:
                    tags-ignore:
                      - '${{feature'
                permissions:
                  contents: read
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """,
            _ => """
                name: CI Probe
                on: push
                permissions:
                  contents: read
                defaults:
                  run:
                    working-directory: '${{feature'
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v6
                """
        };
        using var repository = WorkflowRepository.Create("ci.yml", workflow);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Fact]
    public void Static_tag_pattern_with_unmatched_opener_is_not_classified_as_expression_framing()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: CI Probe
            on:
              push:
                tags:
                  - '${{feature'
            permissions:
              contents: read
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v6
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.DoesNotContain(
            inspection.Failures,
            failure => failure.Message.Contains(
                "malformed/incomplete expression framing",
                StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(".secrets/cache")]
    [InlineData("documentation-secrets-reference")]
    [InlineData("secrets")]
    [InlineData("${{ format('documentation-secrets-reference') }}")]
    [InlineData("${{ 'secrets.NUGET_API_KEY' }}")]
    public void Literal_secrets_text_keeps_read_only_ci_outside_release_policy(string value)
    {
        using var repository = WorkflowRepository.Create("ci.yml", $$"""
            name: continuous integration
            on:
              pull_request:
            permissions: {}
            jobs:
              build:
                steps:
                  - uses: owner/repository/action@v1
                    with:
                      note: {{value}}
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Theory]
    [InlineData("workflow-to-job", true)]
    [InlineData("workflow-to-job", false)]
    [InlineData("workflow-to-step", true)]
    [InlineData("workflow-to-step", false)]
    [InlineData("job-to-step", true)]
    [InlineData("job-to-step", false)]
    public void Effective_environment_overrides_determine_credential_capability(
        string overridePath,
        bool credentialInOuterScope)
    {
        const string credential = "${{ secrets.READ_ONLY_INPUT }}";
        const string literal = "ordinary-literal";
        var outerValue = credentialInOuterScope ? credential : literal;
        var innerValue = credentialInOuterScope ? literal : credential;
        var workflow = overridePath switch
        {
            "workflow-to-job" => $$"""
                name: continuous integration
                on:
                  pull_request:
                permissions: {}
                env:
                  NEUTRAL_VALUE: {{outerValue}}
                jobs:
                  build:
                    env:
                      NEUTRAL_VALUE: {{innerValue}}
                    steps:
                      - uses: owner/repository/action@v1
                """,
            "workflow-to-step" => $$"""
                name: continuous integration
                on:
                  pull_request:
                permissions: {}
                env:
                  NEUTRAL_VALUE: {{outerValue}}
                jobs:
                  build:
                    steps:
                      - uses: owner/repository/action@v1
                        env:
                          NEUTRAL_VALUE: {{innerValue}}
                """,
            _ => $$"""
                name: continuous integration
                on:
                  pull_request:
                permissions: {}
                jobs:
                  build:
                    env:
                      NEUTRAL_VALUE: {{outerValue}}
                    steps:
                      - uses: owner/repository/action@v1
                        env:
                          NEUTRAL_VALUE: {{innerValue}}
                """
        };
        using var repository = WorkflowRepository.Create("ci.yml", workflow);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        if (credentialInOuterScope)
        {
            Assert.False(inspection.Evaluated);
            Assert.Empty(inspection.Failures);
        }
        else
        {
            AssertLimitedUnproven(inspection);
        }
    }

    [Fact]
    public void Effective_environment_override_probes_update_the_complete_readiness_result()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var symbolPackage = Path.ChangeExtension(package, ".snupkg");
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new PackageExpectation
                {
                    Id = "Fixture.Standard",
                    Kind = "library",
                    Version = "1.0.0",
                    Artifacts = [Path.GetFileName(package), Path.GetFileName(symbolPackage)]
                }
            ]
        };
        var releaseWorkflow = Mutate(
            ReleaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.nupkg",
            Path.GetFileName(package));
        releaseWorkflow = Mutate(
            releaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.snupkg",
            Path.GetFileName(symbolPackage));
        var probes = new[]
        {
            (OverridePath: "workflow-to-job", CredentialInOuterScope: true),
            (OverridePath: "workflow-to-job", CredentialInOuterScope: false),
            (OverridePath: "workflow-to-step", CredentialInOuterScope: true),
            (OverridePath: "workflow-to-step", CredentialInOuterScope: false),
            (OverridePath: "job-to-step", CredentialInOuterScope: true),
            (OverridePath: "job-to-step", CredentialInOuterScope: false)
        };

        foreach (var probe in probes)
        {
            const string credential = "${{ secrets.READ_ONLY_INPUT }}";
            const string literal = "ordinary-literal";
            var outerValue = probe.CredentialInOuterScope ? credential : literal;
            var innerValue = probe.CredentialInOuterScope ? literal : credential;
            var ciWorkflow = probe.OverridePath switch
            {
                "workflow-to-job" => $$"""
                    name: continuous integration
                    on:
                      pull_request:
                    permissions: {}
                    env:
                      NEUTRAL_VALUE: {{outerValue}}
                    jobs:
                      build:
                        env:
                          NEUTRAL_VALUE: {{innerValue}}
                        steps:
                          - uses: owner/repository/action@v1
                    """,
                "workflow-to-step" => $$"""
                    name: continuous integration
                    on:
                      pull_request:
                    permissions: {}
                    env:
                      NEUTRAL_VALUE: {{outerValue}}
                    jobs:
                      build:
                        steps:
                          - uses: owner/repository/action@v1
                            env:
                              NEUTRAL_VALUE: {{innerValue}}
                    """,
                _ => $$"""
                    name: continuous integration
                    on:
                      pull_request:
                    permissions: {}
                    jobs:
                      build:
                        env:
                          NEUTRAL_VALUE: {{outerValue}}
                        steps:
                          - uses: owner/repository/action@v1
                            env:
                              NEUTRAL_VALUE: {{innerValue}}
                    """
            };
            using var repository = WorkflowRepository.Create("release.yml", releaseWorkflow);
            repository.WriteWorkflow("ci.yml", ciWorkflow);
            var configPath = Path.Combine(repository.Root.FullName, "nugetready.json");
            repository.WriteFile("nugetready.json", System.Text.Json.JsonSerializer.Serialize(config));

            var report = CheckRunner.Run(
                config,
                corpus.OutputPath,
                repository.Root.FullName,
                TimeSpan.FromMinutes(2),
                new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath),
                configPath);
            var expectedStatus = probe.CredentialInOuterScope ? "pass" : "error";
            var expectedExitCode = probe.CredentialInOuterScope ? 0 : 2;

            Assert.True(
                report.Status == expectedStatus && report.ExitCode == expectedExitCode,
                $"Probe '{probe.OverridePath}' with credentialInOuterScope={probe.CredentialInOuterScope} returned status={report.Status}, exitCode={report.ExitCode}; failures={string.Join(" | ", report.Failures.Select(failure => failure.Message))}");
            Assert.Equal(expectedStatus, report.Checks.Single(check => check.Id == "workflow-policy").Status);
        }
    }

    [Theory]
    [InlineData("${{ vars.ApiKeyPath }}")]
    [InlineData("${{ inputs.AccessTokenizer }}")]
    [InlineData("${{ env.CredentialHelper }}")]
    [InlineData("${{ vars['NuGetApiKeyFile'] }}")]
    [InlineData("${{ vars['Nuget.Api.Key.Path'] }}")]
    [InlineData("${{ format('vars.NUGET_API_KEY') }}")]
    public void Credential_member_near_misses_keep_read_only_ci_outside_release_policy(string value)
    {
        using var repository = WorkflowRepository.Create("ci.yml", $$"""
            name: continuous integration
            on:
              pull_request:
            permissions: {}
            jobs:
              build:
                steps:
                  - uses: owner/repository/action@v1
                    with:
                      note: {{value}}
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Fact]
    public void Inherited_job_credential_remains_effective_when_any_active_step_does_not_override_it()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              pull_request:
            permissions: {}
            jobs:
              build:
                env:
                  NEUTRAL_VALUE: ${{ secrets.READ_ONLY_INPUT }}
                steps:
                  - uses: owner/repository/action@v1
                    env:
                      NEUTRAL_VALUE: ordinary-literal
                  - uses: owner/repository/another-action@v1
            """);

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Theory]
    [InlineData("workflow")]
    [InlineData("job")]
    [InlineData("step")]
    public void Credential_shaped_environment_names_are_detected_at_each_scope(string scope)
    {
        var workflow = scope switch
        {
            "workflow" => """
                name: continuous integration
                on:
                  pull_request:
                permissions: {}
                env:
                  ACCESS_TOKEN: literal-credential
                jobs:
                  build:
                    steps:
                      - uses: owner/repository/action@v1
                """,
            "job" => """
                name: continuous integration
                on:
                  pull_request:
                permissions: {}
                jobs:
                  build:
                    env:
                      ACCESS_TOKEN: literal-credential
                    steps:
                      - uses: owner/repository/action@v1
                """,
            _ => """
                name: continuous integration
                on:
                  pull_request:
                permissions: {}
                jobs:
                  build:
                    steps:
                      - uses: owner/repository/action@v1
                        env:
                          ACCESS_TOKEN: literal-credential
                """
        };
        using var repository = WorkflowRepository.Create("ci.yml", workflow);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Theory]
    [InlineData("with", "literal-credential")]
    [InlineData("with", "${{ vars.ACCESS_TOKEN }}")]
    [InlineData("secrets", "literal-credential")]
    [InlineData("secrets", "${{ vars.ACCESS_TOKEN }}")]
    public void Credential_shaped_reusable_workflow_bindings_are_blocking(string bindingMap, string bindingValue)
    {
        using var repository = WorkflowRepository.Create("ci.yml", $"""
            name: continuous integration
            on:
              pull_request:
            permissions:
              contents: read
            jobs:
              build:
                uses: owner/repository/.github/workflows/build.yml@v1
                {bindingMap}:
                  ACCESS_TOKEN: {bindingValue}
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
        Assert.Contains(inspection.Failures, failure => failure.Message.Contains("reusable workflow", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Credential_name_near_misses_keep_read_only_ci_outside_release_policy()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              pull_request:
            permissions:
              contents: read
            jobs:
              build:
                steps:
                  - uses: owner/repository/action@v1
                    with:
                      nuget-api-key-file: artifacts/key.txt
                      api-key-path: artifacts/key.txt
                      ApiKeyPath: artifacts/key.txt
                      access-tokenizer: enabled
                      authorization-policy: read-only
                      passwordless: enabled
                      secret-sauce: none
                      credential-helper: disabled
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Theory]
    [InlineData("id-token: write", null)]
    [InlineData("contents: write", null)]
    [InlineData(null, "PUBLISH_TOKEN: ${{ secrets.NUGET_API_KEY }}")]
    public void Unknown_action_with_publication_capability_is_blocking(string? permission, string? environment)
    {
        var permissionYaml = permission is null
            ? "permissions: {}"
            : $"permissions:\n  {permission}";
        var environmentYaml = environment is null
            ? string.Empty
            : $"    env:\n      {environment}\n";
        using var repository = WorkflowRepository.Create("ci.yml", $$"""
            name: continuous integration
            on:
              pull_request:
            {{permissionYaml}}
            jobs:
              build:
            {{environmentYaml}}    steps:
                  - uses: owner/repository/action@v1
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
        Assert.Contains(inspection.Failures, failure => failure.Message.Contains("remote action", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_reusable_workflow_receiving_a_secret_is_blocking()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              pull_request:
            permissions: {}
            jobs:
              build:
                uses: owner/repository/.github/workflows/build.yml@v1
                with:
                  token: ${{ secrets.BUILD_TOKEN }}
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
        Assert.Contains(inspection.Failures, failure => failure.Message.Contains("reusable workflow", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_action_on_an_artifact_path_to_publication_is_blocking()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: package publication
            on:
              push:
                branches: [main]
            permissions: {}
            jobs:
              produce:
                steps:
                  - uses: owner/repository/action@v1
                  - uses: actions/upload-artifact@v4
                    with:
                      name: release-package
                      path: artifacts/package.nupkg
              publish:
                permissions:
                  contents: write
                steps:
                  - uses: actions/download-artifact@v4
                    with:
                      name: release-package
                      path: artifacts
                  - run: dotnet nuget push artifacts/package.nupkg
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
        Assert.Contains(inspection.Failures, failure => failure.Message.Contains("remote action", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_action_on_a_dependency_path_to_publication_is_blocking()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: package publication
            on:
              push:
                branches: [main]
            permissions: {}
            jobs:
              prepare:
                steps:
                  - uses: owner/repository/action@v1
              publish:
                needs: prepare
                permissions:
                  contents: write
                steps:
                  - run: dotnet nuget push artifacts/package.nupkg
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
        Assert.Contains(inspection.Failures, failure => failure.Message.Contains("remote action", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unrelated_unknown_ci_does_not_change_a_valid_release_workflow()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow);
        repository.WriteWorkflow("ci.yml", """
            name: continuous integration
            on:
              pull_request:
            permissions: {}
            jobs:
              build:
                steps:
                  - uses: owner/repository/action@v1
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Empty(findings);
    }

    [Fact]
    public void Allowlisted_ci_actions_keep_ci_quiet()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            permissions: {}
            jobs:
              build:
                steps:
                  - uses: actions/checkout@v6
                  - uses: actions/setup-dotnet@v5
                  - uses: actions/upload-artifact@v4
                  - uses: actions/download-artifact@v4
                  - uses: NuGet/login@v1
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Fact]
    public void Local_composite_action_with_a_referenced_publication_script_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - uses: ./.github/actions/build
            """);
        repository.WriteFile(".github/actions/build/action.yml", """
            name: build
            description: Build the package
            runs:
              using: composite
              steps:
                - shell: pwsh
                  run: pwsh scripts/publish.ps1
            """);
        repository.WriteFile("scripts/publish.ps1", "gh release create v1.0.0");

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Missing_local_composite_metadata_is_limited_unproven_instead_of_quiet()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - uses: ./.github/actions/build
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Remote_publication_action_is_limited_unproven_instead_of_quiet()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - uses: owner/publish-action@v1
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Missing_local_script_is_limited_unproven_instead_of_quiet()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: bash scripts/missing.sh
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Scheduled_script_publication_is_a_blocking_error_not_not_applicable()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: nightly maintenance
            on:
              schedule:
                - cron: "0 0 * * *"
            jobs:
              maintenance:
                steps:
                  - run: pwsh scripts/publish.ps1
            """);
        repository.WriteFile("scripts/publish.ps1", "dotnet nuget push artifacts/package.nupkg");

        var report = CheckRunner.Run(
            new NuGetReadyConfig
            {
                SchemaVersion = 1,
                Packages =
                [
                    new PackageExpectation
                    {
                        Id = "Fixture.Standard",
                        Kind = "library",
                        Version = "1.0.0",
                        Artifacts = [Path.GetFileName(package), Path.GetFileName(Path.ChangeExtension(package, ".snupkg"))]
                    }
                ]
            },
            corpus.OutputPath,
            repository.Root.FullName,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath));

        Assert.Equal("error", report.Status);
        Assert.Equal(2, report.ExitCode);
        Assert.Equal("error", report.Checks.Single(check => check.Id == "workflow-policy").Status);
        Assert.DoesNotContain(report.Failures, failure => failure.Message.Contains("not-applicable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Publish_workflow_filename_marks_a_reusable_target_as_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("publish.yml", """
            name: ci
            on:
              push:
                tags: ["v*.*.*"]
            jobs:
              build:
                uses: ./.github/workflows/reusable-build.yml
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(findings, finding => finding.Message.Contains("does not contain an executable package publication step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ship_named_workflow_is_reported_as_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ship.yml", """
            name: ship
            on:
              push:
                branches: ["main"]
            jobs:
              build:
                steps:
                  - run: dotnet build
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Opaque_reusable_target_is_reported_as_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: ci
            on:
              push:
                branches: ["main"]
            jobs:
              build:
                uses: ${{ inputs.workflow }}
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Remote_reusable_target_is_reported_as_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: ci
            on:
              push:
                branches: ["main"]
            jobs:
              build:
                uses: owner/repository/.github/workflows/build.yml@main
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Publication_named_workflow_call_input_is_reported_as_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("reusable.yml", """
            name: reusable
            on:
              workflow_call:
                inputs:
                  publish:
                    required: false
                    type: boolean
            jobs:
              build:
                steps:
                  - run: dotnet build
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Theory]
    [InlineData("release.yml", """
        name: release event
        on:
          release:
            types: [published]
        jobs:
          build:
            steps:
              - run: dotnet build
        """)]
    [InlineData("dispatch.yml", """
        name: dispatch
        on:
          workflow_dispatch:
            inputs:
              publish:
                required: false
                type: boolean
        jobs:
          build:
            steps:
              - run: dotnet build
        """)]
    public void Publication_shaped_trigger_without_visible_push_is_reported_as_limited_unproven(string fileName, string workflow)
    {
        using var repository = WorkflowRepository.Create(fileName, workflow);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Uninspectable_job_steps_are_reported_as_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: ci
            on:
              push:
                branches: ["main"]
            jobs:
              build:
                steps: ${{ inputs.steps }}
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Unsupported_step_key_is_reported_as_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: ci
            on:
              push:
                branches: ["main"]
            jobs:
              build:
                steps:
                  - run: dotnet build
                    unsupported-key: true
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
    }

    [Fact]
    public void Deterministic_release_violation_remains_fail_closed()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        var workflow = Mutate(
            ReleaseWorkflow,
            "KeelMatrix.NuGetReady.1.0.0.nupkg",
            Path.GetFileName(package));
        workflow = Mutate(
            workflow,
            "KeelMatrix.NuGetReady.1.0.0.snupkg",
            Path.GetFileName(Path.ChangeExtension(package, ".snupkg")));
        using var repository = WorkflowRepository.Create("release.yml", workflow.Replace(
            "id-token: write",
            "id-token: read",
            StringComparison.Ordinal));
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new PackageExpectation
                {
                    Id = "Fixture.Standard",
                    Kind = "library",
                    Version = "1.0.0",
                    Artifacts = [Path.GetFileName(package), Path.GetFileName(Path.ChangeExtension(package, ".snupkg"))]
                }
            ]
        };
        var configPath = Path.Combine(repository.Root.FullName, "nugetready.json");
        repository.WriteFile("nugetready.json", System.Text.Json.JsonSerializer.Serialize(config));

        var report = CheckRunner.Run(
            config,
            corpus.OutputPath,
            repository.Root.FullName,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath),
            configPath);

        Assert.Equal("fail", report.Status);
        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.Message.Contains("id-token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Composite_action_publication_path_is_reported_as_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("release.yml", """
            name: release
            on:
              push:
                tags: ["v*.*.*"]
            jobs:
              publish:
                steps:
                  - uses: ./.github/actions/publish
            """);
        var actionDirectory = Directory.CreateDirectory(Path.Combine(repository.Root.FullName, ".github", "actions", "publish"));
        File.WriteAllText(Path.Combine(actionDirectory.FullName, "action.yml"), """
            name: publish
            description: Publish the package
            runs:
              using: composite
              steps:
                - shell: pwsh
                  run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        var finding = Assert.Single(findings, finding => finding.IsError && finding.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("composite", finding.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(findings, finding => finding.Message.Contains("does not contain an executable package publication step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unsupported_reusable_workflow_is_a_blocking_error()
    {
        using var corpus = PackedCorpus.Create();
        var package = corpus.Pack("Standard/Standard.csproj");
        using var repository = WorkflowRepository.Create("release.yml", """
            name: release
            on:
              push:
                tags: ["v*.*.*"]
            jobs:
              publish:
                uses: ./.github/workflows/reusable-publish.yml
            """);

        var report = CheckRunner.Run(
            new NuGetReadyConfig
            {
                SchemaVersion = 1,
                Packages =
                [
                    new PackageExpectation
                    {
                        Id = "Fixture.Standard",
                        Kind = "library",
                        Version = "1.0.0",
                        Artifacts = [Path.GetFileName(package), Path.GetFileName(Path.ChangeExtension(package, ".snupkg"))]
                    }
                ]
            },
            corpus.OutputPath,
            repository.Root.FullName,
            TimeSpan.FromMinutes(2),
            new ConsumerRehearsalOptions(PublicFeedPath: corpus.OutputPath));

        Assert.Equal("error", report.Status);
        Assert.Equal(2, report.ExitCode);
        Assert.Equal("error", report.Checks.Single(check => check.Id == "workflow-policy").Status);
        Assert.Contains(report.Failures, failure => failure.IsError && failure.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Failures, failure => failure.Message.Contains("does not contain an executable package publication step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_oidc_permission_is_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(ReleaseWorkflow, "id-token: write", "id-token: read"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("id-token", StringComparison.OrdinalIgnoreCase) || finding.Message.Contains("permissions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Overbroad_oidc_permissions_are_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "id-token: write\n      contents: read",
            "id-token: write\n      contents: write"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("permissions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Long_lived_nuget_api_key_architecture_is_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "- shell: pwsh\n        env:\n          NUGET_API_KEY: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}",
            "- run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --api-key ${{ secrets.NUGET_API_KEY }}\n      - shell: pwsh\n        env:\n          NUGET_API_KEY: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("long-lived", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("secrets['NUGET_API_KEY']")]
    [InlineData("secrets[\"NUGET_API_KEY\"]")]
    [InlineData("secrets[NUGET_API_KEY]")]
    [InlineData("secrets.RELEASE_CREDENTIAL")]
    [InlineData("secrets['PUBLISH_KEY']")]
    public void Bracket_secret_references_are_blocking(string secretReference)
    {
        using var repository = WorkflowRepository.Create(
            "release.yml",
            Mutate(
                ReleaseWorkflow,
                "- shell: pwsh\n        env:\n          NUGET_API_KEY: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}",
                "- run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --api-key ${{ " + secretReference + " }}\n      - shell: pwsh\n        env:\n          NUGET_API_KEY: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("long-lived", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Broad_package_wildcard_is_a_deterministic_failure()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg",
            "dotnet nuget push artifacts/release/*.nupkg"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => !finding.IsWarning && !finding.IsError && finding.Message.Contains("exact", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Comments_and_step_names_do_not_prove_authentication_or_artifact_validation()
    {
        using var repository = WorkflowRepository.Create("release.yml", """
            name: release
            on:
              push:
                tags: ["v*.*.*"]
            permissions:
              contents: read
            jobs:
              publish:
                permissions:
                  id-token: write
                  contents: read
                steps:
                  # Trusted Publishing and expected artifacts are mentioned here.
                  - name: Trusted Publishing
                    run: echo "not authentication"
                  - name: Validate expected artifacts
                    run: echo "not validation"
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.NotEmpty(findings);
    }

    [Fact]
    public void Write_all_and_an_additional_github_release_job_are_not_certified()
    {
        using var repository = WorkflowRepository.Create("release.yml", """
            name: release
            on:
              push:
                tags: ["v*.*.*"]
            jobs:
              publish:
                permissions: write-all # broad permissions
                env:
                  KEELMATRIX_NO_TELEMETRY: '1'
                steps:
                  - run: dotnet nugetready check --artifacts artifacts/packages
                  - uses: NuGet/login@v1
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
              release:
                permissions:
                  contents: write
                steps:
                  - run: gh release create "${{ github.ref_name }}"
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError || finding.Message.Contains("permissions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Artifact_validation_must_execute_before_publication()
    {
        const string install = $"- name: Install the pinned NuGetReady tool\n        shell: pwsh\n        working-directory: /tmp/nugetready-acquisition\n        run: {ToolInstallCommand}";
        const string checkout = "- name: Check out source at the triggering commit\n        uses: actions/checkout@v6\n        with:\n          fetch-depth: 0\n          ref: ${{ github.sha }}\n          persist-credentials: false";
        const string validation = $"- name: Validate exact artifacts\n        shell: pwsh\n        run: {ValidationCommand}";
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            $"{install}\n      {checkout}\n      {validation}",
            $"{validation}\n      {checkout}\n      {install}"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("validation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Release_workflow_must_suppress_product_telemetry()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow.Replace(
            "KEELMATRIX_NO_TELEMETRY",
            "OTHER_ENVIRONMENT_VARIABLE",
            StringComparison.Ordinal));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("telemetry", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("KEELMATRIX_NO_TELEMETRY", "keelmatrix_no_telemetry")]
    [InlineData("DOTNET_CLI_TELEMETRY_OPTOUT", "dotnet_cli_telemetry_optout")]
    [InlineData("NUGET_PACKAGES", "nuget_packages")]
    public void Required_runtime_environment_names_are_case_sensitive(string canonicalName, string caseVariant)
    {
        using var repository = WorkflowRepository.Create(
            "release.yml",
            Mutate(ReleaseWorkflow, canonicalName, caseVariant));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError);
    }

    [Fact]
    public void Publish_credential_environment_name_is_case_sensitive()
    {
        using var repository = WorkflowRepository.Create(
            "release.yml",
            Mutate(
                ReleaseWorkflow,
                "NUGET_API_KEY: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}",
                "nuget_api_key: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError);
    }

    [Fact]
    public void Case_variant_environment_override_is_not_merged_as_a_linux_binding()
    {
        using var repository = WorkflowRepository.Create(
            "release.yml",
            Mutate(
                ReleaseWorkflow,
                "NUGET_PACKAGES: /tmp/nugetready-packages\n",
                "NUGET_PACKAGES: /tmp/nugetready-packages\n              nuget_packages: /tmp/nugetready-packages\n"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError);
    }

    [Theory]
    [InlineData("dotnet restore Example.sln --configfile NuGet.config --nologo -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -p:TreatWarningsAsErrors=true", "'dotnet' restore Example.sln --configfile NuGet.config --nologo -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -p:TreatWarningsAsErrors=true")]
    [InlineData("dotnet restore Example.sln --configfile NuGet.config --nologo -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -p:TreatWarningsAsErrors=true", "dotnet restore ' Example.sln ' --configfile NuGet.config --nologo -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -p:TreatWarningsAsErrors=true")]
    [InlineData("dotnet format Example.sln --verify-no-changes --no-restore --verbosity minimal", "'dotnet' format Example.sln --verify-no-changes --no-restore --verbosity minimal")]
    [InlineData("dotnet build Example.sln --configuration Release --no-restore --nologo -p:UseSharedCompilation=false", "dotnet build ' Example.sln ' --configuration Release --no-restore --nologo -p:UseSharedCompilation=false")]
    [InlineData("dotnet test Example.sln --configuration Release --no-build --no-restore --nologo --logger \"console;verbosity=minimal\"", "dotnet test Example.sln --configuration Release --no-build --no-restore --nologo --logger \"console;verbosity=minimal\" \"\"")]
    [InlineData("dotnet pack src/Example/Example.csproj --configuration Release --no-build --no-restore --include-symbols -p:SymbolPackageFormat=snupkg -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryTargets=false --output artifacts/release --nologo -p:UseSharedCompilation=false", "dotnet pack (src/Example/Example.csproj) --configuration Release --no-build --no-restore --include-symbols -p:SymbolPackageFormat=snupkg -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryTargets=false --output artifacts/release --nologo -p:UseSharedCompilation=false")]
    [InlineData(ToolInstallCommand, "'dotnet' tool install KeelMatrix.NuGetReady --version 0.1.0 --tool-path /tmp/nugetready-tool --source https://api.nuget.org/v3/index.json --no-cache --verbosity minimal")]
    [InlineData(ValidationCommand, "'/tmp/nugetready-tool/nugetready' check --config nugetready.json --artifacts /tmp/nugetready-artifacts --format json")]
    public void Unsupported_command_quoting_or_empty_arguments_never_remain_certifiable(string command, string replacement)
    {
        using var repository = WorkflowRepository.Create(
            "release.yml",
            ReplaceRunWithBlockScalar(ReleaseWorkflow, command, replacement));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("profile", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("automation.yml", "on: [push")]
    [InlineData("automation.yml", "name: first\nname: second")]
    [InlineData("automation.yml", "- not-a-workflow\n- still-not-a-workflow")]
    [InlineData("automation.yml", "name: first\n---\nname: second")]
    public void Invalid_workflow_input_is_blocking_even_with_a_neutral_filename(string fileName, string invalidContent)
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow);
        File.Delete(Path.Combine(repository.Root.FullName, ".github", "workflows", "release.yml"));
        repository.WriteWorkflow(fileName, invalidContent);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.True(inspection.Evaluated);
        var failure = Assert.Single(inspection.Failures);
        Assert.True(failure.IsError);
        Assert.Contains($".github/workflows/{fileName}", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("second", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("automation.yml")]
    [InlineData("broken-release.yml")]
    public void Invalid_workflow_input_is_blocking_beside_a_valid_release_workflow(string fileName)
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow);
        repository.WriteWorkflow(fileName, "on: [push");

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.True(inspection.Evaluated);
        Assert.Contains(inspection.Failures, failure =>
            failure.IsError &&
            failure.Message.Contains($".github/workflows/{fileName}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("dotnet build KeelMatrix.NuGetReady.sln -t:Publish")]
    [InlineData("dotnet msbuild publish.proj -t:Publish")]
    [InlineData("dotnet tool run arbitrary-tool")]
    [InlineData("git -c alias.ship=!dotnet-nuget-push ship")]
    [InlineData("pwsh -Command ./scripts/generated.ps1")]
    [InlineData("Set-Content generated.ps1 'Write-Output generated'")]
    [InlineData("echo additional-command")]
    public void Unsupported_validation_job_execution_never_remains_certifiable(string command)
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            $"      - name: Validate exact artifacts\n        shell: pwsh\n        run: {ValidationCommand}",
            $"      - name: Unsupported validation execution\n        shell: pwsh\n        run: {command}\n      - name: Validate exact artifacts\n        shell: pwsh\n        run: {ValidationCommand}"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("validation", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(
        "dotnet build Example.sln --configuration Release --no-restore --nologo -p:UseSharedCompilation=false",
        "dotnet build Example.sln -t:Publish")]
    [InlineData(
        "dotnet build Example.sln --configuration Release --no-restore --nologo -p:UseSharedCompilation=false",
        "git -c alias.ship=!dotnet-nuget-push ship")]
    [InlineData(
        ToolInstallCommand,
        "dotnet tool run arbitrary-tool")]
    [InlineData(
        "dotnet pack src/Example/Example.csproj --configuration Release --no-build --no-restore --include-symbols -p:SymbolPackageFormat=snupkg -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryTargets=false --output artifacts/release --nologo -p:UseSharedCompilation=false",
        "dotnet pack src/Example/Example.csproj --configuration Release -p:CustomAfterMicrosoftCommonTargets=publish.targets")]
    [InlineData(
        "dotnet restore Example.sln --configfile NuGet.config --nologo -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -p:TreatWarningsAsErrors=true",
        "dotnet restore Example.sln -p:CustomAfterMicrosoftCommonTargets=publish.targets")]
    [InlineData(
        ValidationCommand,
        "nugetready check --config nugetready.json --artifacts artifacts/release --format json")]
    public void Mutating_a_supported_validation_command_never_remains_certifiable(string original, string replacement)
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(ReleaseWorkflow, original, replacement));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("profile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Candidate_artifact_feed_cannot_replace_the_source_exclusive_published_tool()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "--source https://api.nuget.org/v3/index.json",
            "--source /tmp/nugetready-artifacts"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("source-exclusive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_second_tool_source_cannot_race_the_source_exclusive_acquisition()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "--source https://api.nuget.org/v3/index.json --no-cache",
            "--source https://api.nuget.org/v3/index.json --add-source /tmp/nugetready-artifacts --no-cache"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("source-exclusive", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("GITHUB_PATH")]
    [InlineData("GITHUB_ENV")]
    public void Repository_controlled_runner_channels_cannot_share_a_job_with_validation(string channel)
    {
        const string upload = "- name: Upload exact artifacts\n        uses: actions/upload-artifact@v4";
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            upload,
            $"- name: Install before the runner boundary\n        shell: pwsh\n        run: {ToolInstallCommand}\n      - name: Validate before the runner boundary\n        shell: pwsh\n        run: {ValidationCommand}\n      {upload}"));
        repository.WriteFile("Directory.Build.targets", $"""
            <Project>
              <Target Name="PersistRunnerState" BeforeTargets="Build">
                <WriteLinesToFile File="$({channel})" Lines="$(MSBuildThisFileDirectory)adversarial-bin" />
              </Target>
            </Project>
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("artifact-producer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Additional_tool_acquisition_source_is_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow);
        repository.WriteFile("NuGet.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
                <add key="other" value="https://packages.example.invalid/v3/index.json" protocolVersion="3" />
              </packageSources>
            </configuration>
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("NuGet.config", StringComparison.Ordinal));
    }

    [Fact]
    public void Job_telemetry_opt_out_overrides_workflow_opt_out_when_disabled()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "      contents: read\n    env:\n      KEELMATRIX_NO_TELEMETRY: '1'\n      DOTNET_CLI_TELEMETRY_OPTOUT: '1'",
            "      contents: read\n    env:\n      KEELMATRIX_NO_TELEMETRY: '0'\n      DOTNET_CLI_TELEMETRY_OPTOUT: '0'"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("telemetry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_opt_out_confined_to_an_unrelated_step_does_not_suppress_release_telemetry()
    {
        var workflow = Mutate(
            ReleaseWorkflow,
            "    env:\n      KEELMATRIX_NO_TELEMETRY: '1'\n      DOTNET_CLI_TELEMETRY_OPTOUT: '1'\n    steps:\n      - uses: actions/download-artifact@v4",
            "    steps:\n      - uses: actions/download-artifact@v4\n        env:\n          KEELMATRIX_NO_TELEMETRY: '1'\n          DOTNET_CLI_TELEMETRY_OPTOUT: '1'");
        using var repository = WorkflowRepository.Create("release.yml", workflow);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("telemetry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Publish_named_reusable_workflow_is_reported_as_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("publish.yml", """
            name: publish
            on:
              push:
                tags: ["v*.*.*"]
            jobs:
              publish:
                uses: ./.github/workflows/reusable-publish.yml
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        var finding = Assert.Single(findings, finding => finding.IsError && finding.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("reusable", finding.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(findings, finding => finding.Message.Contains("does not contain an executable package publication step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Repository_root_is_anchored_above_a_build_directory_config()
    {
        var root = Directory.CreateTempSubdirectory("nugetready-root-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, ".git"));
            var configDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "build"));
            var configPath = Path.Combine(configDirectory.FullName, "nugetready.json");
            File.WriteAllText(configPath, "{}");

            Assert.Equal(root.FullName, RepositoryLocator.FindRoot(configPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("dotnet restore")]
    [InlineData("dotnet build")]
    [InlineData("dotnet test")]
    [InlineData("dotnet pack")]
    [InlineData("dotnet format")]
    [InlineData("dotnet list package")]
    [InlineData("dotnet tool list")]
    [InlineData("nugetready check --config nugetready.json --artifacts artifacts --format json")]
    [InlineData("git status --short")]
    [InlineData("echo ready")]
    [InlineData("printf 'ready\\n'")]
    [InlineData("mkdir -p artifacts")]
    public void Safe_command_allowlist_keeps_ordinary_ci_quiet(string command)
    {
        using var repository = WorkflowRepository.Create("ci.yml", $$"""
            name: continuous integration
            on:
              pull_request:
            permissions: {}
            jobs:
              build:
                steps:
                  - run: {{command}}
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Theory]
    [InlineData("oidc", "dotnet build")]
    [InlineData("oidc", "dotnet test")]
    [InlineData("oidc", "dotnet tool list")]
    [InlineData("secret", "dotnet build")]
    [InlineData("secret", "dotnet test")]
    [InlineData("secret", "dotnet tool list")]
    [InlineData("dynamic-secret", "dotnet build")]
    [InlineData("whole-secret-context", "dotnet build")]
    [InlineData("nested-whole-secret-context", "dotnet build")]
    [InlineData("packages-write", "dotnet build")]
    [InlineData("write-all", "dotnet test")]
    public void Capability_bearing_workflows_cannot_bypass_closed_world_evaluation(
        string capability,
        string command)
    {
        var capabilityYaml = capability switch
        {
            "oidc" => "    permissions:\n      id-token: write",
            "packages-write" => "    permissions:\n      packages: write",
            "write-all" => "    permissions: write-all",
            "dynamic-secret" => "    env:\n      PUBLISH_TOKEN: ${{ secrets[format('{0}', 'NUGET_API_KEY')] }}",
            "whole-secret-context" => "    env:\n      ALL_SECRETS: ${{ toJSON(secrets) }}",
            "nested-whole-secret-context" => "    env:\n      PUBLISH_TOKEN: ${{ fromJSON(toJSON(secrets))['NUGET_API_KEY'] }}",
            _ => "    env:\n      PUBLISH_TOKEN: ${{ secrets['NUGET_API_KEY'] }}"
        };
        using var repository = WorkflowRepository.Create("ci.yml", $$"""
            name: continuous integration
            on:
              pull_request:
            permissions: {}
            jobs:
              build:
                runs-on: ubuntu-latest
            {{capabilityYaml}}
                steps:
                  - run: {{command}}
            """);

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Fact]
    public void Every_supported_write_permission_enters_closed_world_evaluation_at_workflow_and_job_scope()
    {
        foreach (var permission in WritableGitHubPermissions)
        {
            foreach (var scope in new[] { "workflow", "job" })
            {
                var workflowPermissions = scope == "workflow"
                    ? $"permissions:\n  {permission}: write"
                    : "permissions: {}";
                var jobPermissions = scope == "job"
                    ? $"    permissions:\n      {permission}: write\n"
                    : string.Empty;
                using var repository = WorkflowRepository.Create("ci.yml", $$"""
                    name: continuous integration
                    on:
                      pull_request:
                    {{workflowPermissions}}
                    jobs:
                      build:
                        runs-on: ubuntu-latest
                    {{jobPermissions}}    steps:
                          - run: dotnet build
                    """);

                var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

                Assert.True(
                    inspection.Evaluated && inspection.Failures.Any(failure => failure.IsError),
                    $"Permission '{permission}: write' at {scope} scope unexpectedly bypassed closed-world evaluation.");
            }
        }
    }

    [Fact]
    public void Unknown_permission_names_and_values_enter_closed_world_evaluation()
    {
        var cases = new[]
        {
            (Name: "unknown workflow permission", WorkflowPermissions: "future-permission: read", JobPermissions: string.Empty),
            (Name: "unknown job permission", WorkflowPermissions: "{}", JobPermissions: "future-permission: read"),
            (Name: "internal sentinel spelling as a mapping key", WorkflowPermissions: "__all__: read-all", JobPermissions: string.Empty),
            (Name: "mis-cased permission name", WorkflowPermissions: "Contents: read", JobPermissions: string.Empty),
            (Name: "unknown workflow value", WorkflowPermissions: "contents: future", JobPermissions: string.Empty),
            (Name: "unknown job value", WorkflowPermissions: "{}", JobPermissions: "contents: future"),
            (Name: "uninspectable workflow permissions", WorkflowPermissions: "${{ vars.PERMISSIONS }}", JobPermissions: string.Empty),
            (Name: "uninspectable job permissions", WorkflowPermissions: "{}", JobPermissions: "${{ vars.PERMISSIONS }}"),
            (Name: "invalid id-token read value", WorkflowPermissions: "id-token: read", JobPermissions: string.Empty),
            (Name: "invalid vulnerability-alerts write value", WorkflowPermissions: "{}", JobPermissions: "vulnerability-alerts: write")
        };

        foreach (var testCase in cases)
        {
            var workflowPermissions = testCase.WorkflowPermissions == "{}"
                ? "permissions: {}"
                : $"permissions:\n  {testCase.WorkflowPermissions}";
            var jobPermissions = string.IsNullOrEmpty(testCase.JobPermissions)
                ? string.Empty
                : $"    permissions:\n      {testCase.JobPermissions}\n";
            using var repository = WorkflowRepository.Create("ci.yml", $$"""
                name: continuous integration
                on:
                  pull_request:
                {{workflowPermissions}}
                jobs:
                  build:
                    runs-on: ubuntu-latest
                {{jobPermissions}}    steps:
                      - run: dotnet build
                """);

            var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

            Assert.True(
                inspection.Evaluated && inspection.Failures.Any(failure => failure.IsError),
                $"Case '{testCase.Name}' unexpectedly bypassed closed-world evaluation.");
        }
    }

    [Fact]
    public void Producer_and_validator_reject_every_supported_write_permission()
    {
        foreach (var job in new[] { "produce", "validate" })
        {
            foreach (var permission in WritableGitHubPermissions)
            {
                var workflow = Mutate(
                    ReleaseWorkflow,
                    $"  {job}:",
                    $"  {job}:\n    permissions:\n      {permission}: write");
                using var repository = WorkflowRepository.Create("release.yml", workflow);

                var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

                Assert.True(
                    findings.Any(finding => !finding.IsError && finding.Message.Contains("write capability", StringComparison.OrdinalIgnoreCase)),
                    $"Job '{job}' unexpectedly accepted '{permission}: write': {string.Join(" | ", findings.Select(finding => finding.Message))}");
            }
        }
    }

    [Fact]
    public void Producer_and_validator_treat_unknown_permissions_as_unproven()
    {
        foreach (var job in new[] { "produce", "validate" })
        {
            foreach (var permission in new[] { "future-permission: read", "contents: future" })
            {
                var workflow = Mutate(
                    ReleaseWorkflow,
                    $"  {job}:",
                    $"  {job}:\n    permissions:\n      {permission}");
                using var repository = WorkflowRepository.Create("release.yml", workflow);

                Assert.Contains(
                    WorkflowPolicyInspector.Inspect(repository.Root.FullName),
                    finding => finding.IsError && finding.Message.Contains("permission", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void Omitted_permissions_with_github_token_are_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              pull_request:
            jobs:
              build:
                runs-on: ubuntu-latest
                env:
                  GH_TOKEN: ${{ github.token }}
                steps:
                  - run: dotnet build
            """);

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Fact]
    public void Executable_job_with_omitted_permissions_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              pull_request:
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: dotnet build
            """);

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Fact]
    public void Explicit_read_only_permissions_keep_github_token_ci_quiet()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              pull_request:
            permissions:
              contents: read
            jobs:
              build:
                runs-on: ubuntu-latest
                env:
                  GH_TOKEN: ${{ github.token }}
                steps:
                  - run: dotnet build
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Fact]
    public void Explicit_empty_job_permissions_remove_inherited_oidc_capability()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              pull_request:
            permissions:
              id-token: write
            jobs:
              build:
                permissions: {}
                steps:
                  - run: dotnet build
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        Assert.False(inspection.Evaluated);
        Assert.Empty(inspection.Failures);
    }

    [Fact]
    public void Opaque_task_runner_with_a_real_make_target_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: make publish
            """);
        repository.WriteFile("Makefile", "publish:\n\tdotnet nuget push artifacts/package.nupkg\n");

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Fact]
    public void Alias_definition_and_invocation_are_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: |
                      alias ship='dotnet nuget push artifacts/package.nupkg'
                      ship
            """);

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Fact]
    public void Msbuild_publish_target_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: dotnet msbuild publish.csproj -t:Publish
            """);
        repository.WriteFile("publish.csproj", "<Project />");

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Fact]
    public void Container_build_indirection_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: docker build -f Dockerfile .
            """);
        repository.WriteFile("Dockerfile", "FROM mcr.microsoft.com/dotnet/sdk:8.0\nRUN dotnet nuget push artifacts/package.nupkg\n");

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Fact]
    public void Shell_function_definition_and_invocation_are_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: |
                      ship() { dotnet nuget push artifacts/package.nupkg; }
                      ship
            """);

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Fact]
    public void Generated_configuration_that_is_later_invoked_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: |
                      printf 'publish: dotnet nuget push artifacts/package.nupkg\n' > generated.config
                      config-runner generated.config
            """);
        repository.WriteFile("generated.config", "publish: dotnet nuget push artifacts/package.nupkg\n");

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Fact]
    public void Composite_action_invoking_a_task_runner_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - uses: ./.github/actions/publish
            """);
        repository.WriteFile(".github/actions/publish/action.yml", """
            name: publish
            description: Publish the package
            runs:
              using: composite
              steps:
                - shell: bash
                  run: make publish
            """);
        repository.WriteFile("Makefile", "publish:\n\tdotnet nuget push artifacts/package.nupkg\n");

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    [Fact]
    public void Inline_control_block_cannot_hide_an_opaque_runner()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - run: if ($true) { make publish }
            """);
        repository.WriteFile("Makefile", "publish:\n\tdotnet nuget push artifacts/package.nupkg\n");

        AssertLimitedUnproven(WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName));
    }

    private const string ToolInstallCommand = "dotnet tool install KeelMatrix.NuGetReady --version 0.1.0 --tool-path /tmp/nugetready-tool --source https://api.nuget.org/v3/index.json --no-cache --verbosity minimal";
    private const string ValidationCommand = "/tmp/nugetready-tool/nugetready check --config nugetready.json --artifacts /tmp/nugetready-artifacts --format json";

    private static readonly string[] WritableGitHubPermissions =
    [
        "actions",
        "artifact-metadata",
        "attestations",
        "checks",
        "code-quality",
        "contents",
        "deployments",
        "discussions",
        "id-token",
        "issues",
        "packages",
        "pages",
        "pull-requests",
        "security-events",
        "statuses"
    ];

    private const string ReleaseWorkflow = """
        name: release
        on:
          push:
            tags: ["v1.0.0"]
        permissions:
          contents: read
        jobs:
          produce:
            runs-on: ubuntu-latest
            timeout-minutes: 30
            env:
              KEELMATRIX_NO_TELEMETRY: '1'
              DOTNET_CLI_TELEMETRY_OPTOUT: '1'
            steps:
              - name: Check out source at the triggering commit
                uses: actions/checkout@v6
                with:
                  fetch-depth: 0
                  ref: ${{ github.sha }}
                  persist-credentials: false
              - name: Set up .NET SDK
                uses: actions/setup-dotnet@v5
                with:
                  global-json-file: global.json
              - name: Restore from controlled sources and audit dependencies
                shell: pwsh
                run: dotnet restore Example.sln --configfile NuGet.config --nologo -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -p:TreatWarningsAsErrors=true
              - name: Verify format
                shell: pwsh
                run: dotnet format Example.sln --verify-no-changes --no-restore --verbosity minimal
              - name: Build Release
                shell: pwsh
                run: dotnet build Example.sln --configuration Release --no-restore --nologo -p:UseSharedCompilation=false
              - name: Run release tests
                shell: pwsh
                run: dotnet test Example.sln --configuration Release --no-build --no-restore --nologo --logger "console;verbosity=minimal"
              - name: Pack the exact release artifacts
                shell: pwsh
                run: dotnet pack src/Example/Example.csproj --configuration Release --no-build --no-restore --include-symbols -p:SymbolPackageFormat=snupkg -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryTargets=false --output artifacts/release --nologo -p:UseSharedCompilation=false
              - name: Upload exact artifacts
                uses: actions/upload-artifact@v4
                with:
                  name: validated-release-artifacts
                  path: |
                    artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg
                    artifacts/release/KeelMatrix.NuGetReady.1.0.0.snupkg
                  if-no-files-found: error
          validate:
            needs: produce
            runs-on: ubuntu-latest
            timeout-minutes: 10
            env:
              KEELMATRIX_NO_TELEMETRY: '1'
              DOTNET_CLI_TELEMETRY_OPTOUT: '1'
              NUGET_PACKAGES: /tmp/nugetready-packages
            steps:
              - name: Create the fixed validator SDK resolver
                shell: pwsh
                run: |
                  New-Item -ItemType Directory -Path /tmp/nugetready-acquisition -Force | Out-Null
                  @'
                  {
                    "sdk": {
                      "version": "8.0.425",
                      "rollForward": "latestPatch",
                      "allowPrerelease": false
                    }
                  }
                  '@ | Set-Content -LiteralPath /tmp/nugetready-acquisition/global.json -Encoding utf8NoBOM
              - name: Set up .NET SDK
                uses: actions/setup-dotnet@v5
                with:
                  dotnet-version: '8.0.425'
              - name: Download immutable artifacts
                uses: actions/download-artifact@v4
                with:
                  name: validated-release-artifacts
                  path: /tmp/nugetready-artifacts
              - name: Install the pinned NuGetReady tool
                shell: pwsh
                working-directory: /tmp/nugetready-acquisition
                run: dotnet tool install KeelMatrix.NuGetReady --version 0.1.0 --tool-path /tmp/nugetready-tool --source https://api.nuget.org/v3/index.json --no-cache --verbosity minimal
              - name: Check out source at the triggering commit
                uses: actions/checkout@v6
                with:
                  fetch-depth: 0
                  ref: ${{ github.sha }}
                  persist-credentials: false
              - name: Validate exact artifacts
                shell: pwsh
                run: /tmp/nugetready-tool/nugetready check --config nugetready.json --artifacts /tmp/nugetready-artifacts --format json
          publish:
            needs: validate
            runs-on: ubuntu-latest
            timeout-minutes: 10
            permissions:
              id-token: write
              contents: read
            env:
              KEELMATRIX_NO_TELEMETRY: '1'
              DOTNET_CLI_TELEMETRY_OPTOUT: '1'
            steps:
              - uses: actions/download-artifact@v4
                with:
                  name: validated-release-artifacts
                  path: artifacts/release
              - id: nuget-login
                uses: NuGet/login@v1
                with:
                  user: dmitriyzen
              - shell: pwsh
                env:
                  NUGET_API_KEY: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}
                run: dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key "$env:NUGET_API_KEY"
        """;

    private static string Mutate(string workflow, string original, string replacement)
    {
        workflow = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(original, workflow, StringComparison.Ordinal);
        return workflow.Replace(original, replacement, StringComparison.Ordinal);
    }

    private static string ReplaceRunWithBlockScalar(string workflow, string command, string replacement)
    {
        workflow = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
        var originalLine = workflow
            .Split('\n')
            .Single(line => line.Trim().Equals($"run: {command}", StringComparison.Ordinal));
        var indentation = originalLine[..(originalLine.Length - originalLine.TrimStart().Length)];
        return workflow.Replace(
            originalLine,
            $"{indentation}run: |\n{indentation}  {replacement}",
            StringComparison.Ordinal);
    }

    private static void AssertLimitedUnproven(WorkflowInspectionResult inspection)
    {
        Assert.True(
            inspection.Evaluated,
            $"EVALUATED={inspection.Evaluated}; FINDINGS={(inspection.Failures.Count == 0 ? "none" : string.Join(" | ", inspection.Failures.Select(failure => failure.Message)))}");
        Assert.Contains(inspection.Failures, failure => failure.IsError && failure.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class WorkflowRepository : IDisposable
{
    private WorkflowRepository(DirectoryInfo root)
    {
        Root = root;
    }

    public DirectoryInfo Root { get; }

    public static WorkflowRepository Create(string fileName, string content)
    {
        var root = Directory.CreateTempSubdirectory("nugetready-workflow-");
        var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".github", "workflows"));
        File.WriteAllText(Path.Combine(directory.FullName, fileName), content);
        File.WriteAllText(Path.Combine(root.FullName, "NuGet.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(root.FullName, "nugetready.json"), """
            {
              "schemaVersion": 1,
              "packages": [
                {
                  "id": "KeelMatrix.NuGetReady",
                  "kind": "dotnetTool",
                  "version": "1.0.0",
                  "artifacts": [
                    "KeelMatrix.NuGetReady.1.0.0.nupkg",
                    "KeelMatrix.NuGetReady.1.0.0.snupkg"
                  ],
                  "command": "nugetready",
                  "smoke": ["--help"]
                }
              ]
            }
            """);
        File.WriteAllText(Path.Combine(root.FullName, "global.json"), """
            {
              "sdk": {
                "version": "8.0.425",
                "rollForward": "latestPatch",
                "allowPrerelease": false
              }
            }
            """);
        File.WriteAllText(Path.Combine(root.FullName, "Example.sln"), string.Empty);
        var projectDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "Example"));
        File.WriteAllText(Path.Combine(projectDirectory.FullName, "Example.csproj"), "<Project />");
        return new WorkflowRepository(root);
    }

    public void WriteWorkflow(string fileName, string content)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Root.FullName, ".github", "workflows"));
        File.WriteAllText(Path.Combine(directory.FullName, fileName), content);
    }

    public void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(Root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void RenamePath(string originalRelativePath, string replacementRelativePath)
    {
        var original = Path.Combine(Root.FullName, originalRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var replacement = Path.Combine(Root.FullName, replacementRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var temporary = Path.Combine(Root.FullName, $"case-rename-{Guid.NewGuid():N}");

        if (Directory.Exists(original))
        {
            Directory.Move(original, temporary);
            Directory.CreateDirectory(Path.GetDirectoryName(replacement)!);
            Directory.Move(temporary, replacement);
        }
        else
        {
            File.Move(original, temporary);
            Directory.CreateDirectory(Path.GetDirectoryName(replacement)!);
            File.Move(temporary, replacement);
        }
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
    }
}
