using System.Text.RegularExpressions;

namespace KeelMatrix.NuGetReady;

internal static partial class WorkflowPolicyInspector
{
    private const int MaxWorkflowBytes = 512 * 1024;

    public static IReadOnlyList<Failure> Inspect(string repositoryPath)
    {
        var workflowDirectory = Path.Combine(repositoryPath, ".github", "workflows");
        if (!Directory.Exists(workflowDirectory))
        {
            return Array.Empty<Failure>();
        }

        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(workflowDirectory, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (IOException)
        {
            return new[] { new Failure("workflow-policy", "Release workflow files could not be enumerated.", true) };
        }
        catch (UnauthorizedAccessException)
        {
            return new[] { new Failure("workflow-policy", "Release workflow files could not be enumerated.", true) };
        }

        var failures = new List<Failure>();
        foreach (var path in paths)
        {
            string content;
            try
            {
                var file = new FileInfo(path);
                if (file.Length > MaxWorkflowBytes)
                {
                    failures.Add(new Failure("workflow-policy", "A release workflow file is too large to inspect safely.", true));
                    continue;
                }

                content = File.ReadAllText(path);
            }
            catch (IOException)
            {
                failures.Add(new Failure("workflow-policy", "A release workflow file could not be inspected.", true));
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                failures.Add(new Failure("workflow-policy", "A release workflow file could not be inspected.", true));
                continue;
            }

            if (!LooksLikeReleaseWorkflow(path, content))
            {
                continue;
            }

            if (LongLivedNuGetCredentialRegex().IsMatch(content))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow contains a long-lived NuGet API-key publication path."));
            }

            if (!TagTriggerRegex().IsMatch(content))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow is not gated by a versioned tag trigger."));
            }

            if (!IdTokenPermissionRegex().IsMatch(content))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow does not grant the required id-token write permission for Trusted Publishing."));
            }

            if (OverbroadPermissionRegex().IsMatch(content))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow grants permissions broader than the OIDC publication path requires."));
            }

            if (!ExactArtifactCheckRegex().IsMatch(content))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow does not validate the exact expected artifact set before publication."));
            }

            if (BroadPackageWildcardRegex().IsMatch(content))
            {
                failures.Add(new Failure(
                    "workflow-policy",
                    "Release workflow publishes from a broad package wildcard; use the exact expected artifact set.",
                    IsWarning: true));
            }
        }

        return failures
            .OrderBy(failure => failure.Message, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool LooksLikeReleaseWorkflow(string path, string content)
    {
        return Path.GetFileName(path).Contains("release", StringComparison.OrdinalIgnoreCase) ||
               PublishStepRegex().IsMatch(content) ||
               TrustedPublishingRegex().IsMatch(content);
    }

    [GeneratedRegex("(?i)(NUGET[._-]?API[._-]?KEY|NUGETAPIKEY|--api-key|--ApiKey)")]
    private static partial Regex LongLivedNuGetCredentialRegex();

    [GeneratedRegex("(?ms)^\\s*on\\s*:\\s*.*?^\\s*push\\s*:\\s*.*?^\\s*tags\\s*:", RegexOptions.Multiline)]
    private static partial Regex TagTriggerRegex();

    [GeneratedRegex("(?im)^\\s*id-token\\s*:\\s*write\\s*$")]
    private static partial Regex IdTokenPermissionRegex();

    [GeneratedRegex("(?im)^\\s*(permissions\\s*:\\s*write-all|actions|contents|packages|pull-requests|issues)\\s*:\\s*write\\s*$")]
    private static partial Regex OverbroadPermissionRegex();

    [GeneratedRegex("(?i)(nugetready\\s+check|expected.{0,40}artifact|artifact.{0,40}(set|contract)|verify-package-contract)")]
    private static partial Regex ExactArtifactCheckRegex();

    [GeneratedRegex("(?i)(?:[\\w./-]+/)?(?:\\*\\*?/)?\\*\\.s?nupkg|(?:[\\w./-]+/)?\\*\\.s?nupkg")]
    private static partial Regex BroadPackageWildcardRegex();

    [GeneratedRegex("(?i)(dotnet\\s+nuget\\s+push|nuget/login@|trusted.?publishing)")]
    private static partial Regex PublishStepRegex();

    [GeneratedRegex("(?i)(nuget/login@|trusted.?publishing)")]
    private static partial Regex TrustedPublishingRegex();
}
