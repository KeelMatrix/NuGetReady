using System.Text.RegularExpressions;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class ReleaseWorkflowContractTests
{
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
        Assert.Contains("$package = \"artifacts/release/KeelMatrix.NuGetReady.$env:RELEASE_VERSION.nupkg\"", workflow, StringComparison.Ordinal);
        Assert.Contains("$package", pushLines[0], StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("dotnet\\s+nuget\\s+push[^\\r\\n]*\\.snupkg", RegexOptions.IgnoreCase), workflow);
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
