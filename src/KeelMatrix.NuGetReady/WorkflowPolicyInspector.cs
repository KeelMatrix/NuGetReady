namespace KeelMatrix.NuGetReady;

internal sealed record WorkflowInspectionResult(IReadOnlyList<Failure> Failures, bool Evaluated);

internal static class WorkflowPolicyInspector
{
    private const int MaxWorkflowBytes = 512 * 1024;

    public static IReadOnlyList<Failure> Inspect(string repositoryPath)
    {
        return InspectDetailed(repositoryPath).Failures;
    }

    public static WorkflowInspectionResult InspectDetailed(string repositoryPath)
    {
        var workflowDirectory = Path.Combine(repositoryPath, ".github", "workflows");
        if (!Directory.Exists(workflowDirectory))
        {
            return new WorkflowInspectionResult(Array.Empty<Failure>(), false);
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
            return new WorkflowInspectionResult(new[] { new Failure("workflow-policy", "Release workflow files could not be enumerated.", true) }, true);
        }
        catch (UnauthorizedAccessException)
        {
            return new WorkflowInspectionResult(new[] { new Failure("workflow-policy", "Release workflow files could not be enumerated.", true) }, true);
        }

        var failures = new List<Failure>();
        var evaluated = false;
        foreach (var path in paths)
        {
            string content;
            try
            {
                var file = new FileInfo(path);
                if (file.Length > MaxWorkflowBytes)
                {
                    failures.Add(new Failure("workflow-policy", "A release workflow file is too large to inspect safely.", true));
                    evaluated = true;
                    continue;
                }

                content = File.ReadAllText(path);
            }
            catch (IOException)
            {
                failures.Add(new Failure("workflow-policy", "A release workflow file could not be inspected.", true));
                evaluated = true;
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                failures.Add(new Failure("workflow-policy", "A release workflow file could not be inspected.", true));
                evaluated = true;
                continue;
            }

            var workflow = SupportedYaml.Parse(content);
            if (!LooksLikeReleaseWorkflow(path, workflow))
            {
                continue;
            }

            evaluated = true;
            InspectWorkflow(workflow, failures);
        }

        return new WorkflowInspectionResult(failures.OrderBy(failure => failure.Message, StringComparer.Ordinal).ToArray(), evaluated);
    }

    private static bool LooksLikeReleaseWorkflow(string path, WorkflowDocument workflow)
    {
        return Path.GetFileName(path).Contains("release", StringComparison.OrdinalIgnoreCase) ||
               workflow.Jobs.Any(job => job.Steps.Any(IsPublishStep));
    }

    private static void InspectWorkflow(WorkflowDocument workflow, List<Failure> failures)
    {
        if (!workflow.HasVersionedTagTrigger)
        {
            failures.Add(new Failure("workflow-policy", "Release workflow is not gated by a versioned tag trigger."));
        }

        if (IsWriteAll(workflow.Permissions))
        {
            failures.Add(new Failure("workflow-policy", "Release workflow grants permissions broader than the OIDC publication path requires."));
        }

        var publishingJobs = workflow.Jobs.Where(job => job.Steps.Any(IsPublishStep)).ToArray();
        if (publishingJobs.Length == 0)
        {
            failures.Add(new Failure("workflow-policy", "Release workflow does not contain an executable package publication step."));
            return;
        }

        foreach (var job in workflow.Jobs)
        {
            if (IsWriteAll(job.Permissions))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow grants permissions broader than the OIDC publication path requires."));
            }
        }

        foreach (var job in publishingJobs)
        {
            if (!IsWritePermission(job.Permissions, "id-token"))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow does not grant the required id-token write permission for Trusted Publishing in the publishing job."));
            }

            if (!job.Steps.Any(IsTrustedPublishingStep))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow does not execute Trusted Publishing authentication in the publishing job."));
            }

            if (job.Permissions.Any(permission =>
                    permission.Key.Equals("contents", StringComparison.OrdinalIgnoreCase) && permission.Value.Equals("write", StringComparison.OrdinalIgnoreCase)) ||
                job.Permissions.Any(permission => permission.Key is "actions" or "packages" or "pull-requests" or "issues" &&
                                                   permission.Value.Equals("write", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow grants permissions broader than the OIDC publication path requires."));
            }

            var steps = job.Steps.ToArray();
            var pushIndex = Array.FindIndex(steps, IsPublishStep);
            var validationIndex = Array.FindIndex(steps, IsArtifactValidationStep);
            if (validationIndex < 0)
            {
                var dependencyHasValidation = job.DependsOn.Any(dependency => workflow.Jobs
                    .FirstOrDefault(candidate => candidate.Id.Equals(dependency, StringComparison.OrdinalIgnoreCase))?
                    .Steps.Any(IsArtifactValidationStep) == true);
                if (!dependencyHasValidation)
                {
                    failures.Add(new Failure("workflow-policy", "Release workflow does not prove exact artifact validation before publication.", IsWarning: true));
                }
            }
            else if (validationIndex > pushIndex)
            {
                failures.Add(new Failure("workflow-policy", "Release workflow validates the exact artifact set after publication instead of before publication."));
            }

            if (job.Steps.Any(step => ContainsLongLivedCredential(step.Run)))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow contains a long-lived NuGet API-key publication path."));
            }

            if (!HasTelemetrySuppression(workflow, job))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow does not suppress product and CLI telemetry."));
            }

            if (job.Steps.Any(step => IsPublishStep(step) && ContainsPackageWildcard(step.Run)))
            {
                failures.Add(new Failure(
                    "workflow-policy",
                    "Release workflow publishes from a broad package wildcard; use the exact expected artifact set.",
                    IsWarning: true));
            }
        }
    }

    private static bool IsPublishStep(WorkflowStep step)
    {
        return step.Uses?.Contains("nuget/login@", StringComparison.OrdinalIgnoreCase) == true ||
               step.Run?.Contains("dotnet nuget push", StringComparison.OrdinalIgnoreCase) == true ||
               step.Run?.Contains("nuget push", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsTrustedPublishingStep(WorkflowStep step)
    {
        return step.Uses?.Contains("nuget/login@", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsArtifactValidationStep(WorkflowStep step)
    {
        return step.Run?.Contains("nugetready check", StringComparison.OrdinalIgnoreCase) == true ||
               step.Run?.Contains("inspect-package.ps1", StringComparison.OrdinalIgnoreCase) == true;
    }
    private static bool ContainsLongLivedCredential(string? run)
    {
        return run is not null &&
               (run.Contains("secrets.NUGET_API_KEY", StringComparison.OrdinalIgnoreCase) ||
                run.Contains("secrets.NUGET-API-KEY", StringComparison.OrdinalIgnoreCase) ||
                run.Contains("secrets.NUGETAPIKEY", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsPackageWildcard(string? run)
    {
        return run is not null && (run.Contains("*.nupkg", StringComparison.OrdinalIgnoreCase) || run.Contains("*.snupkg", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWriteAll(Dictionary<string, string> permissions)
    {
        return permissions.TryGetValue("__all__", out var value) && value.Equals("write-all", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWritePermission(Dictionary<string, string> permissions, string name)
    {
        return permissions.TryGetValue(name, out var value) && value.Equals("write", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasTelemetrySuppression(WorkflowDocument workflow, WorkflowJob job)
    {
        return HasTelemetrySuppression(workflow.Environment) || HasTelemetrySuppression(job.Environment) || job.Steps.Any(step => HasTelemetrySuppression(step.Environment));
    }

    private static bool HasTelemetrySuppression(IReadOnlyDictionary<string, string> environment)
    {
        return environment.Any(pair =>
            (pair.Key.Equals("KEELMATRIX_NO_TELEMETRY", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("DOTNET_CLI_TELEMETRY_OPTOUT", StringComparison.OrdinalIgnoreCase)) &&
            (pair.Value.Equals("1", StringComparison.OrdinalIgnoreCase) || pair.Value.Equals("true", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class WorkflowDocument
    {
        public bool HasVersionedTagTrigger { get; set; }
        public Dictionary<string, string> Permissions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<WorkflowJob> Jobs { get; } = new();
    }

    private sealed class WorkflowJob
    {
        public WorkflowJob(string id) => Id = id;
        public string Id { get; }
        public HashSet<string> DependsOn { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Permissions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<WorkflowStep> Steps { get; } = new();
    }

    private sealed class WorkflowStep
    {
        public string? Name { get; set; }
        public string? Uses { get; set; }
        public string? Run { get; set; }
        public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static class SupportedYaml
    {
        public static WorkflowDocument Parse(string content)
        {
            var workflow = new WorkflowDocument();
            var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            var inOn = false;
            var inPush = false;
            var inJobs = false;
            WorkflowJob? currentJob = null;
            WorkflowStep? currentStep = null;
            Dictionary<string, string>? activeMap = null;
            var activeMapIndent = -1;
            var activeStepMap = false;
            WorkflowStep? blockRunStep = null;
            var blockRunIndent = -1;

            foreach (var rawLine in lines)
            {
                var line = RemoveComment(rawLine);
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("---", StringComparison.Ordinal))
                {
                    continue;
                }

                var indent = line.Length - line.TrimStart(' ').Length;
                var trimmed = line.Trim();
                if (blockRunStep is not null && indent > blockRunIndent)
                {
                    blockRunStep.Run = string.IsNullOrWhiteSpace(blockRunStep.Run)
                        ? trimmed
                        : blockRunStep.Run + "\n" + trimmed;
                    continue;
                }

                if (blockRunStep is not null && indent <= blockRunIndent)
                {
                    blockRunStep = null;
                    blockRunIndent = -1;
                }
                if (activeMap is not null && indent <= activeMapIndent)
                {
                    activeMap = null;
                    activeMapIndent = -1;
                }

                if (activeMap is not null && indent > activeMapIndent && TryParseKeyValue(trimmed, out var mapKey, out var mapValue))
                {
                    activeMap[mapKey] = Unquote(mapValue);
                    continue;
                }

                if (indent == 0 && TryParseKeyValue(trimmed, out var topKey, out var topValue))
                {
                    inOn = topKey.Equals("on", StringComparison.OrdinalIgnoreCase);
                    inJobs = topKey.Equals("jobs", StringComparison.OrdinalIgnoreCase);
                    inPush = false;
                    currentJob = null;
                    currentStep = null;
                    activeStepMap = false;
                    if (topKey.Equals("permissions", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseInlineMap(topValue, workflow.Permissions);
                        if (string.IsNullOrWhiteSpace(topValue))
                        {
                            activeMap = workflow.Permissions;
                            activeMapIndent = indent;
                        }
                    }
                    else if (topKey.Equals("env", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseInlineMap(topValue, workflow.Environment);
                        if (string.IsNullOrWhiteSpace(topValue))
                        {
                            activeMap = workflow.Environment;
                            activeMapIndent = indent;
                        }
                    }
                    else if (topKey.Equals("on", StringComparison.OrdinalIgnoreCase) && topValue.Contains("push", StringComparison.OrdinalIgnoreCase) && topValue.Contains("tags", StringComparison.OrdinalIgnoreCase))
                    {
                        workflow.HasVersionedTagTrigger = true;
                    }

                    continue;
                }

                if (inOn && indent == 2 && TryParseKeyValue(trimmed, out var triggerKey, out var triggerValue))
                {
                    inPush = triggerKey.Equals("push", StringComparison.OrdinalIgnoreCase);
                    if (inPush && triggerValue.Contains("tags", StringComparison.OrdinalIgnoreCase))
                    {
                        workflow.HasVersionedTagTrigger = true;
                    }

                    continue;
                }

                if (inOn && inPush && indent >= 4 && trimmed.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                {
                    workflow.HasVersionedTagTrigger = true;
                    continue;
                }

                if (inJobs && indent == 2 && TryParseKeyValue(trimmed, out var jobId, out _))
                {
                    currentJob = new WorkflowJob(jobId);
                    workflow.Jobs.Add(currentJob);
                    currentStep = null;
                    activeStepMap = false;
                    continue;
                }

                if (currentJob is null)
                {
                    continue;
                }

                if (indent == 4 && TryParseKeyValue(trimmed, out var jobKey, out var jobValue))
                {
                    activeStepMap = false;
                    if (jobKey.Equals("permissions", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseInlineMap(jobValue, currentJob.Permissions);
                        if (string.IsNullOrWhiteSpace(jobValue))
                        {
                            activeMap = currentJob.Permissions;
                            activeMapIndent = indent;
                        }
                    }
                    else if (jobKey.Equals("env", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseInlineMap(jobValue, currentJob.Environment);
                        if (string.IsNullOrWhiteSpace(jobValue))
                        {
                            activeMap = currentJob.Environment;
                            activeMapIndent = indent;
                        }
                    }
                    else if (jobKey.Equals("steps", StringComparison.OrdinalIgnoreCase))
                    {
                        currentStep = null;
                    }
                    else if (jobKey.Equals("needs", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var dependency in ParseSequence(jobValue))
                        {
                            currentJob.DependsOn.Add(dependency);
                        }
                    }

                    continue;
                }

                if (indent >= 6 && trimmed.StartsWith('-'))
                {
                    currentStep = new WorkflowStep();
                    currentJob.Steps.Add(currentStep);
                    activeStepMap = false;
                    var inlineStep = trimmed[1..].Trim();
                    if (TryParseKeyValue(inlineStep, out var stepKey, out var stepValue))
                    {
                        AssignStepValue(currentStep, stepKey, stepValue);
                        if (stepKey.Equals("run", StringComparison.OrdinalIgnoreCase) && (stepValue == "|" || stepValue == ">"))
                        {
                            blockRunStep = currentStep;
                            blockRunIndent = indent;
                        }
                    }

                    continue;
                }

                if (currentStep is not null && indent >= 8 && TryParseKeyValue(trimmed, out var key, out var value))
                {
                    if (key.Equals("env", StringComparison.OrdinalIgnoreCase))
                    {
                        activeMap = currentStep.Environment;
                        activeMapIndent = indent;
                        activeStepMap = true;
                    }
                    else
                    {
                        AssignStepValue(currentStep, key, value);
                        if (key.Equals("run", StringComparison.OrdinalIgnoreCase) && (value == "|" || value == ">"))
                        {
                            blockRunStep = currentStep;
                            blockRunIndent = indent;
                        }
                    }

                    continue;
                }

                if (activeStepMap && currentStep is not null && indent > 8 && TryParseKeyValue(trimmed, out var envKey, out var envValue))
                {
                    currentStep.Environment[envKey] = Unquote(envValue);
                }
            }

            return workflow;
        }

        private static void AssignStepValue(WorkflowStep step, string key, string value)
        {
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                step.Name = Unquote(value);
            }
            else if (key.Equals("uses", StringComparison.OrdinalIgnoreCase))
            {
                step.Uses = Unquote(value);
            }
            else if (key.Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                step.Run = Unquote(value);
            }
        }

        private static void ParseInlineMap(string value, Dictionary<string, string> destination)
        {
            var text = value.Trim();
            if (text.Equals("write-all", StringComparison.OrdinalIgnoreCase))
            {
                destination["__all__"] = "write-all";
                return;
            }

            if (text.StartsWith('{') && text.EndsWith('}'))
            {
                foreach (var pair in text[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (TryParseKeyValue(pair, out var key, out var pairValue))
                    {
                        destination[key] = Unquote(pairValue);
                    }
                }
            }
        }

        private static IEnumerable<string> ParseSequence(string value)
        {
            var text = value.Trim();
            if (text.StartsWith('[') && text.EndsWith(']'))
            {
                return text[1..^1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Unquote)
                    .Where(item => item.Length > 0);
            }

            return string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : new[] { Unquote(text) };
        }

        private static bool TryParseKeyValue(string value, out string key, out string remainder)
        {
            var separator = value.IndexOf(':');
            if (separator <= 0)
            {
                key = string.Empty;
                remainder = string.Empty;
                return false;
            }

            key = value[..separator].Trim();
            remainder = value[(separator + 1)..].Trim();
            return key.Length > 0;
        }

        private static string RemoveComment(string line)
        {
            var singleQuoted = false;
            var doubleQuoted = false;
            for (var index = 0; index < line.Length; index++)
            {
                var character = line[index];
                if (character == '\'' && !doubleQuoted)
                {
                    singleQuoted = !singleQuoted;
                }
                else if (character == '"' && !singleQuoted)
                {
                    doubleQuoted = !doubleQuoted;
                }
                else if (character == '#' && !singleQuoted && !doubleQuoted && (index == 0 || char.IsWhiteSpace(line[index - 1])))
                {
                    return line[..index];
                }
            }

            return line;
        }

        private static string Unquote(string value)
        {
            return value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"'))
                ? value[1..^1]
                : value;
        }
    }
}
