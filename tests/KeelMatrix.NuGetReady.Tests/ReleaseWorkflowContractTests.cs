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
