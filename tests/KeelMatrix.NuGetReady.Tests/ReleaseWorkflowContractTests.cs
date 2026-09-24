using System.Text.RegularExpressions;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class ReleaseWorkflowContractTests
{
    [Fact]
    public void Current_release_workflow_has_no_policy_findings()
    {
        var root = FindRepositoryRoot();

        Assert.Empty(WorkflowPolicyInspector.Inspect(root));
    }

    [Fact]
    public void Release_workflow_publishes_symbols_exactly_once()
    {
        var root = FindRepositoryRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "release.yml");
        var workflow = File.ReadAllText(workflowPath);
        var pushLines = workflow
            .Split('\n')
            .Where(line => line.Contains("dotnet nuget push", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Single(pushLines);
        Assert.DoesNotContain("--skip-duplicate", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".snupkg", pushLines[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet nuget push artifacts/release/KeelMatrix.NuGetReady.0.1.0.nupkg", pushLines[0], StringComparison.Ordinal);
        Assert.Contains("--api-key \"$env:NUGET_API_KEY\"", pushLines[0], StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("dotnet\\s+nuget\\s+push[^\\r\\n]*\\.snupkg", RegexOptions.IgnoreCase), workflow);
    }

    [Fact]
    public void Release_workflow_isolates_production_validation_and_publication()
    {
        var root = FindRepositoryRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "release.yml");
        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("persist-credentials: false", workflow, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(workflow, "ref: \\$\\{\\{ github\\.sha \\}\\}", RegexOptions.CultureInvariant).Count);
        Assert.DoesNotContain("github.ref }}", workflow, StringComparison.Ordinal);
        Assert.Contains("produce:", workflow, StringComparison.Ordinal);
        Assert.Contains("validate:\n    name: Validate the immutable release artifacts\n    needs: produce", workflow, StringComparison.Ordinal);
        Assert.Contains("publish:\n    name: Publish the exact validated package\n    needs: validate", workflow, StringComparison.Ordinal);
        Assert.Contains("<add key=\"candidate\" value=\"/tmp/nugetready-artifacts\" />", workflow, StringComparison.Ordinal);
        Assert.Contains("<package pattern=\"KeelMatrix.NuGetReady\" />", workflow, StringComparison.Ordinal);
        Assert.Contains("<packageSource key=\"nuget.org\">", workflow, StringComparison.Ordinal);
        Assert.Contains("<package pattern=\"*\" />", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet tool install KeelMatrix.NuGetReady --version 0.1.0 --tool-path /tmp/nugetready-tool --configfile /tmp/nugetready-tool.config --no-cache --verbosity minimal", workflow, StringComparison.Ordinal);
        Assert.Contains("/tmp/nugetready-tool/nugetready check --config nugetready.json --artifacts /tmp/nugetready-artifacts --format json", workflow, StringComparison.Ordinal);
        Assert.True(
            workflow.IndexOf("dotnet tool install KeelMatrix.NuGetReady", StringComparison.Ordinal) <
            workflow.LastIndexOf("uses: actions/checkout@v6", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(workflow, "uses: actions/upload-artifact@v4", RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Collection(
            Regex.Matches(workflow, "uses: actions/download-artifact@v4", RegexOptions.CultureInvariant).Cast<Match>(),
            _ => { },
            _ => { });
        Assert.DoesNotContain("dotnet run --project", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--add-source", workflow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<package pattern=\"KeelMatrix.NuGetReady\" />", "<package pattern=\"*\" />")]
    [InlineData("<add key=\"candidate\" value=\"/tmp/nugetready-artifacts\" />", "<add key=\"candidate\" value=\"https://packages.example.invalid/v3/index.json\" />")]
    [InlineData("--configfile /tmp/nugetready-tool.config", "--add-source /tmp/nugetready-artifacts")]
    public void Candidate_tool_source_mapping_is_exact_or_blocking(string original, string replacement)
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        Assert.Contains(original, workflow, StringComparison.Ordinal);

        using var repository = WorkflowRepository.Create(
            "release.yml",
            workflow.Replace(original, replacement, StringComparison.Ordinal));
        repository.WriteFile("nugetready.json", File.ReadAllText(Path.Combine(root, "nugetready.json")));

        var findings = WorkflowPolicyInspector.Inspect(repository.Root.FullName);

        Assert.Contains(findings, finding => finding.IsError && finding.Message.Contains("source-exclusive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ci_installed_tool_rehearses_corpus_outside_repository()
    {
        var root = FindRepositoryRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "ci.yml");
        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("$corpus = Join-Path $runRoot 'corpus'", workflow, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -Path (Join-Path $root 'artifacts/corpus/*.nupkg') -Destination $corpus", workflow, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -Path (Join-Path $root 'artifacts/corpus/*.snupkg') -Destination $corpus", workflow, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath (Join-Path $root 'artifacts/corpus/nugetready.json') -Destination $corpus", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("$corpusConfig = Join-Path $root 'artifacts/corpus/nugetready.json'", workflow, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
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
}
