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
                tags: ["v*"]
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
                tags: ["v*"]
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
                tags: ["v*"]
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
                tags: ["v*"]
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
            tags: ["v*"]
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
