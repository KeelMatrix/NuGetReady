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
                "- run: dotnet build -t:Publish\n      - id: nuget-login")
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
    public void A_skipped_validation_dependency_path_is_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "  validate:\n    runs-on: ubuntu-latest",
            "  validate:\n    if: false\n    runs-on: ubuntu-latest"));

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

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
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
            "run: dotnet run --project src/KeelMatrix.NuGetReady/KeelMatrix.NuGetReady.csproj --configuration Release --no-build --no-restore -- check --config nugetready.json --artifacts artifacts/release --format json",
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
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow.Replace("v*.*.*", "*", StringComparison.Ordinal));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_additional_branch_trigger_requires_a_supported_tag_condition()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "tags: [\"v*.*.*\"]",
            "branches: [\"main\"]\n    tags: [\"v*.*.*\"]"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("branch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Branches_ignore_also_indicates_a_branch_trigger_when_tags_are_configured()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "tags: [\"v*.*.*\"]",
            "tags: [\"v*.*.*\"]\n    branches-ignore: [\"main\"]"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("branch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tags_ignore_is_not_a_versioned_tag_gate()
    {
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            "tags: [\"v*.*.*\"]",
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
                uses: ./.github/workflows/reusable-build.yml
            """);
        repository.WriteWorkflow("reusable-build.yml", """
            name: reusable build
            on:
              workflow_call:
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

    [Fact]
    public void Ordinary_remote_action_is_limited_unproven()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
            jobs:
              build:
                steps:
                  - uses: owner/repository/action@v1
            """);

        var inspection = WorkflowPolicyInspector.InspectDetailed(repository.Root.FullName);

        AssertLimitedUnproven(inspection);
        Assert.Contains(inspection.Failures, failure => failure.Message.Contains("remote action", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Allowlisted_ci_actions_keep_ci_quiet()
    {
        using var repository = WorkflowRepository.Create("ci.yml", """
            name: continuous integration
            on:
              push:
                branches: [main]
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
        const string validation = "- name: Validate exact artifacts\n        shell: pwsh\n        run: dotnet run --project src/KeelMatrix.NuGetReady/KeelMatrix.NuGetReady.csproj --configuration Release --no-build --no-restore -- check --config nugetready.json --artifacts artifacts/release --format json";
        const string upload = "- name: Upload exact artifacts\n        uses: actions/upload-artifact@v4\n        with:\n          name: validated-release-artifacts\n          path: |\n            artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg\n            artifacts/release/KeelMatrix.NuGetReady.1.0.0.snupkg\n          if-no-files-found: error";
        using var repository = WorkflowRepository.Create("release.yml", Mutate(
            ReleaseWorkflow,
            $"{validation}\n      {upload}",
            $"{upload}\n      {validation}"));

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
        using var repository = WorkflowRepository.Create("ci.yml", $"""
            name: continuous integration
            on:
              pull_request:
            jobs:
              build:
                steps:
                  - run: {command}
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

    private const string ReleaseWorkflow = """
        name: release
        on:
          push:
            tags: ["v*.*.*"]
        permissions:
          contents: read
        jobs:
          validate:
            runs-on: ubuntu-latest
            timeout-minutes: 30
            env:
              KEELMATRIX_NO_TELEMETRY: '1'
              DOTNET_CLI_TELEMETRY_OPTOUT: '1'
            steps:
              - name: Validate exact artifacts
                shell: pwsh
                run: dotnet run --project src/KeelMatrix.NuGetReady/KeelMatrix.NuGetReady.csproj --configuration Release --no-build --no-restore -- check --config nugetready.json --artifacts artifacts/release --format json
              - name: Upload exact artifacts
                uses: actions/upload-artifact@v4
                with:
                  name: validated-release-artifacts
                  path: |
                    artifacts/release/KeelMatrix.NuGetReady.1.0.0.nupkg
                    artifacts/release/KeelMatrix.NuGetReady.1.0.0.snupkg
                  if-no-files-found: error
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
