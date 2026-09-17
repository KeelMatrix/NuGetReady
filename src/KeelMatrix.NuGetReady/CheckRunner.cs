namespace KeelMatrix.NuGetReady;

internal static class CheckRunner
{
    private static readonly string[] CheckOrder =
    {
        "artifact-set",
        "archive-metadata",
        "archive-layout",
        "dependency-groups",
        "dependency-coherence",
        "archive-security",
        "archive-parse",
        "workflow-policy",
        "consumer-rehearsal"
    };

    public static ReadinessReport Run(NuGetReadyConfig config, string artifactsPath)
    {
        return RunCore(config, artifactsPath, repositoryPath: null, timeout: null);
    }

    public static ReadinessReport Run(
        NuGetReadyConfig config,
        string artifactsPath,
        string repositoryPath,
        TimeSpan timeout,
        ConsumerRehearsalOptions? rehearsalOptions = null)
    {
        return RunCore(config, artifactsPath, repositoryPath, timeout, rehearsalOptions);
    }

    private static ReadinessReport RunCore(
        NuGetReadyConfig config,
        string artifactsPath,
        string? repositoryPath,
        TimeSpan? timeout,
        ConsumerRehearsalOptions? rehearsalOptions = null)
    {
        if (!Directory.Exists(artifactsPath))
        {
            return ErrorReport("Artifact directory was not found.");
        }

        var failures = CheckOrder.ToDictionary(id => id, _ => new List<Failure>(), StringComparer.Ordinal);
        var actualArtifacts = EnumerateArtifacts(artifactsPath, failures["artifact-set"]);
        var expectedArtifacts = config.Packages!.SelectMany(package => package.Artifacts!).ToArray();
        var expectations = config.Packages!
            .SelectMany(package => package.Artifacts!.Select(artifact => (package, artifact)))
            .ToDictionary(item => item.artifact, item => item.package, StringComparer.OrdinalIgnoreCase);

        foreach (var expected in expectedArtifacts)
        {
            if (!actualArtifacts.TryGetValue(expected, out var actualPaths))
            {
                failures["artifact-set"].Add(new Failure("artifact-set", $"Expected artifact '{expected}' was not found."));
                continue;
            }

            if (actualPaths.Count != 1 || actualPaths[0].Contains('/', StringComparison.Ordinal))
            {
                failures["artifact-set"].Add(new Failure("artifact-set", $"Expected artifact '{expected}' is duplicate or not directly in the artifact directory."));
            }
        }

        foreach (var actual in actualArtifacts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!expectations.ContainsKey(actual.Key))
            {
                failures["artifact-set"].Add(new Failure("artifact-set", $"Unintended artifact '{actual.Key}' was found."));
            }
        }

        foreach (var expected in expectedArtifacts.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.Ordinal))
        {
            if (!actualArtifacts.TryGetValue(expected, out var paths) || paths.Count != 1 || paths[0].Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            var package = expectations[expected];
            var absolutePath = Path.Combine(artifactsPath, paths[0].Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var inspectionFailures = ArchiveInspector.Inspect(
                    absolutePath,
                    package,
                    expected.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase));
                foreach (var failure in inspectionFailures)
                {
                    failures[failure.CheckId].Add(failure);
                }
            }
            catch (NuGetReadyInputException exception)
            {
                failures["archive-parse"].Add(new Failure("archive-parse", exception.Message, true));
            }
            catch (Exception)
            {
                failures["archive-parse"].Add(new Failure("archive-parse", "A package archive could not be parsed as a NuGet archive.", true));
            }
        }

        foreach (var failure in DependencyCoherence.Inspect(config, artifactsPath))
        {
            failures["dependency-coherence"].Add(failure);
        }

        if (repositoryPath is not null)
        {
            foreach (var failure in WorkflowPolicyInspector.Inspect(repositoryPath))
            {
                failures["workflow-policy"].Add(failure);
            }
        }

        var rehearsals = Array.Empty<RehearsalResult>();
        var blockingArchiveFailure = failures.Values.SelectMany(items => items).Any(failure => !failure.IsWarning);
        if (repositoryPath is not null && timeout is not null && !blockingArchiveFailure)
        {
            var detailedRehearsals = ConsumerRehearsal.RunDetailed(config, artifactsPath, timeout.Value, rehearsalOptions);
            rehearsals = detailedRehearsals.Select(outcome => outcome.Result).ToArray();
            foreach (var outcome in detailedRehearsals)
            {
                if (!outcome.Result.Status.Equals("pass", StringComparison.Ordinal))
                {
                    failures["consumer-rehearsal"].Add(new Failure(
                        "consumer-rehearsal",
                        $"{outcome.Result.PackageId}: {outcome.Result.Message}{FormatDiagnostic(outcome.Diagnostic)}",
                        outcome.Result.IsError));
                }
            }
        }

        return BuildReport(expectedArtifacts.Length, actualArtifacts.Values.Sum(paths => paths.Count), failures, rehearsals);
    }

    private static string FormatDiagnostic(string diagnostic)
    {
        return string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $" Diagnostic: {diagnostic}";
    }

    private static Dictionary<string, List<string>> EnumerateArtifacts(string root, List<Failure> failures)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
                .ToArray();
        }
        catch (IOException)
        {
            failures.Add(new Failure("artifact-set", "Artifact directory could not be enumerated.", true));
            return result;
        }
        catch (UnauthorizedAccessException)
        {
            failures.Add(new Failure("artifact-set", "Artifact directory could not be enumerated.", true));
            return result;
        }

        foreach (var file in files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ThenBy(file => file, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (!result.TryGetValue(name, out var paths))
            {
                paths = new List<string>();
                result[name] = paths;
            }

            paths.Add(file);
        }

        foreach (var pair in result.Where(pair => pair.Value.Count > 1).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            failures.Add(new Failure("artifact-set", $"Artifact '{pair.Key}' is ambiguous because multiple files have that name."));
        }

        return result;
    }

    private static ReadinessReport ErrorReport(string message)
    {
        var failure = new Failure("artifact-set", message, true);
        return BuildReport(0, 0, new Dictionary<string, List<Failure>>(StringComparer.Ordinal)
        {
            ["artifact-set"] = new List<Failure> { failure }
        }, Array.Empty<RehearsalResult>());
    }

    private static ReadinessReport BuildReport(
        int expectedCount,
        int foundCount,
        Dictionary<string, List<Failure>> failures,
        IReadOnlyList<RehearsalResult> rehearsals)
    {
        var checks = CheckOrder
            .Select(id => new CheckResult(id, failures.TryGetValue(id, out var checkFailures) ? checkFailures : Array.Empty<Failure>()))
            .ToArray();
        var allFailures = checks
            .SelectMany(check => check.Failures)
            .OrderBy(failure => failure.CheckId, StringComparer.Ordinal)
            .ThenBy(failure => failure.Message, StringComparer.Ordinal)
            .ToArray();
        var hasError = allFailures.Any(failure => failure.IsError);
        var hasFailure = allFailures.Any(failure => !failure.IsWarning);
        var hasWarning = allFailures.Any(failure => failure.IsWarning);

        return new ReadinessReport
        {
            Status = hasError ? "error" : hasFailure ? "fail" : hasWarning ? "warn" : "pass",
            ExitCode = hasError ? 2 : hasFailure ? 1 : 0,
            ExpectedArtifacts = expectedCount,
            FoundArtifacts = foundCount,
            Checks = checks,
            Failures = allFailures,
            Rehearsals = rehearsals
        };
    }
}
