namespace KeelMatrix.NuGetReady.Tests;

public sealed class WorkflowPolicyTests
{
    [Fact]
    public void Valid_trusted_publishing_workflow_has_no_policy_findings()
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
                env:
                  KEELMATRIX_NO_TELEMETRY: '1'
                steps:
                  - run: dotnet nugetready check --artifacts artifacts/packages
                  - uses: NuGet/login@v1
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Empty(findings);
    }

    [Fact]
    public void Short_lived_trusted_publishing_credential_is_allowed()
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
                env:
                  KEELMATRIX_NO_TELEMETRY: '1'
                steps:
                  - run: dotnet nugetready check --artifacts artifacts/release
                  - uses: NuGet/login@v1
                    id: nuget-login
                    with:
                      user: dmitriyzen
                  - shell: pwsh
                    env:
                      NUGET_TEMP_CREDENTIAL: ${{ steps.nuget-login.outputs[format('NUGET_{0}', 'API_KEY')] }}
                    run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg -k $env:NUGET_TEMP_CREDENTIAL
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Empty(findings);
    }

    [Fact]
    public void Authentication_before_validation_before_publication_is_allowed()
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
                env:
                  KEELMATRIX_NO_TELEMETRY: '1'
                steps:
                  - uses: NuGet/login@v1
                  - run: dotnet nugetready check --artifacts artifacts/packages
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Empty(findings);
    }

    [Fact]
    public void A_secret_api_key_in_a_supported_env_scope_is_blocking()
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
                env:
                  KEELMATRIX_NO_TELEMETRY: '1'
                  NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
                steps:
                  - run: dotnet nugetready check --artifacts artifacts/packages
                  - uses: NuGet/login@v1
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg -k $env:NUGET_API_KEY
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("long-lived", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Echoed_validation_text_does_not_prove_artifact_validation()
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
                env:
                  KEELMATRIX_NO_TELEMETRY: '1'
                steps:
                  - run: echo "dotnet nugetready check --artifacts artifacts/packages"
                  - uses: NuGet/login@v1
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("artifact validation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Disabled_validation_step_does_not_prove_artifact_validation()
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
                env:
                  KEELMATRIX_NO_TELEMETRY: '1'
                steps:
                  - if: false
                    run: dotnet nugetready check --artifacts artifacts/packages
                  - uses: NuGet/login@v1
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("artifact validation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Workflow_level_oidc_permission_is_inherited_by_a_job_without_permissions()
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

        Assert.DoesNotContain(findings, finding => finding.Message.Contains("id-token", StringComparison.OrdinalIgnoreCase));
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
    public void Authentication_after_publication_does_not_prove_the_publication_path()
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
                env:
                  KEELMATRIX_NO_TELEMETRY: '1'
                steps:
                  - run: dotnet nugetready check --artifacts artifacts/packages
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
                  - uses: NuGet/login@v1
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Conditional_authentication_does_not_prove_an_executed_authentication_step()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow.Replace(
            "- uses: NuGet/login@v1",
            "- if: github.ref_type == 'tag'\n              uses: NuGet/login@v1",
            StringComparison.Ordinal));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unconstrained_tag_pattern_is_not_a_version_gate()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow.Replace("v*.*.*", "*", StringComparison.Ordinal));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("versioned tag", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_additional_branch_trigger_requires_a_supported_tag_condition()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow.Replace(
            "tags: [\"v*.*.*\"]",
            "branches: [\"main\"]\n            tags: [\"v*.*.*\"]",
            StringComparison.Ordinal));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("branch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Branches_ignore_also_indicates_a_branch_trigger_when_tags_are_configured()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow.Replace(
            "tags: [\"v*.*.*\"]",
            "tags: [\"v*.*.*\"]\n            branches-ignore: [\"main\"]",
            StringComparison.Ordinal));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("branch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tags_ignore_is_not_a_versioned_tag_gate()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow.Replace(
            "tags: [\"v*.*.*\"]",
            "tags-ignore: [\"v0.*\"]",
            StringComparison.Ordinal));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("versioned tag", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unsupported_job_condition_is_reported_as_unproven()
    {
        var workflow = ReleaseWorkflow.Replace("\r\n", "\n", StringComparison.Ordinal).Replace(
            "publish:",
            "publish:\n    if: needs.validate.result == 'success'",
            StringComparison.Ordinal);
        using var repository = WorkflowRepository.Create("release.yml", workflow);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsWarning && finding.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
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

        var finding = Assert.Single(findings, finding => finding.IsWarning && finding.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
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
    public void Scheduled_script_publication_is_a_warning_not_not_applicable()
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

        Assert.Equal("warn", report.Status);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal("warn", report.Checks.Single(check => check.Id == "workflow-policy").Status);
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

        Assert.Contains(findings, finding => finding.IsWarning && finding.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
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
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow.Replace(
            "id-token: write",
            "contents: read",
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

        var finding = Assert.Single(findings, finding => finding.IsWarning && finding.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("composite", finding.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(findings, finding => finding.Message.Contains("does not contain an executable package publication step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unsupported_reusable_workflow_is_a_warning_without_a_pass_or_blocking_exit()
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

        Assert.True(
            report.Status == "warn",
            $"status={report.Status}; exitCode={report.ExitCode}; failures={string.Join(" | ", report.Failures.Select(failure => failure.Message))}");
        Assert.Equal(0, report.ExitCode);
        Assert.Equal("warn", report.Checks.Single(check => check.Id == "workflow-policy").Status);
        Assert.DoesNotContain(report.Failures, failure => failure.Message.Contains("does not contain an executable package publication step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_oidc_permission_is_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow.Replace("id-token: write", "contents: read"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("id-token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Overbroad_oidc_permissions_are_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow.Replace("contents: read", "contents: write"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("broader", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Long_lived_nuget_api_key_architecture_is_blocking()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow + "\n          - run: dotnet nuget push --api-key ${{ secrets.NUGET_API_KEY }}\n");

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("long-lived", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("secrets['NUGET_API_KEY']")]
    [InlineData("secrets[\"NUGET_API_KEY\"]")]
    [InlineData("secrets[NUGET_API_KEY]")]
    public void Bracket_secret_references_are_blocking(string secretReference)
    {
        using var repository = WorkflowRepository.Create(
            "release.yml",
            ReleaseWorkflow + "\n          - run: dotnet nuget push --api-key ${{ " + secretReference + " }}\n");

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("long-lived", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Broad_package_wildcard_is_reported_as_a_warning()
    {
        using var repository = WorkflowRepository.Create("release.yml", ReleaseWorkflow.Replace("artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg", "artifacts/*.nupkg"));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        var finding = Assert.Single(findings);
        Assert.True(finding.IsWarning);
        Assert.False(finding.IsError);
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
    public void Inline_write_all_is_rejected_but_contents_write_in_a_separate_release_job_is_allowed()
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

        Assert.Contains(findings, finding => finding.Message.Contains("broader", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(findings, finding => finding.Message.Contains("contents", StringComparison.OrdinalIgnoreCase) && finding.Message.Contains("broader", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Artifact_validation_must_execute_before_publication()
    {
        using var repository = WorkflowRepository.Create("release.yml", """
            name: release
            on:
              push:
                tags: ["v*.*.*"]
            permissions:
              contents: read
            env:
              KEELMATRIX_NO_TELEMETRY: '1'
            jobs:
              publish:
                permissions:
                  id-token: write
                  contents: read
                steps:
                  - uses: NuGet/login@v1
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
                  - run: dotnet nugetready check --artifacts artifacts/packages
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("before publication", StringComparison.OrdinalIgnoreCase));
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
        using var repository = WorkflowRepository.Create("release.yml", """
            name: release
            on:
              push:
                tags: ["v*.*.*"]
            env:
              KEELMATRIX_NO_TELEMETRY: '1'
            permissions:
              contents: read
            jobs:
              publish:
                permissions:
                  id-token: write
                  contents: read
                env:
                  KEELMATRIX_NO_TELEMETRY: '0'
                steps:
                  - run: dotnet nugetready check --artifacts artifacts/packages
                  - uses: NuGet/login@v1
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
            """);

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.Message.Contains("telemetry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_opt_out_confined_to_an_unrelated_step_does_not_suppress_release_telemetry()
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
                  - name: unrelated setup
                    env:
                      KEELMATRIX_NO_TELEMETRY: '1'
                    run: echo setup
                  - run: dotnet nugetready check --artifacts artifacts/packages
                  - uses: NuGet/login@v1
                  - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
            """);

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

        var finding = Assert.Single(findings, finding => finding.IsWarning && finding.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
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
          publish:
            permissions:
              id-token: write
              contents: read
            env:
              KEELMATRIX_NO_TELEMETRY: '1'
            steps:
              - run: dotnet nugetready check --artifacts artifacts/packages
              - uses: NuGet/login@v1
              - run: dotnet nuget push artifacts/KeelMatrix.NuGetReady.1.0.0.nupkg
        """;

    private static void AssertLimitedUnproven(WorkflowInspectionResult inspection)
    {
        Assert.True(
            inspection.Evaluated,
            $"EVALUATED={inspection.Evaluated}; FINDINGS={(inspection.Failures.Count == 0 ? "none" : string.Join(" | ", inspection.Failures.Select(failure => failure.Message)))}");
        Assert.Contains(inspection.Failures, failure => failure.IsWarning && failure.Message.Contains("unproven", StringComparison.OrdinalIgnoreCase));
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
