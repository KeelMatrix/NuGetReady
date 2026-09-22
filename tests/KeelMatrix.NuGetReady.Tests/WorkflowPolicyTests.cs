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
