using System.Text.RegularExpressions;

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
            InspectWorkflow(repositoryPath, workflow, failures);
        }

        return new WorkflowInspectionResult(failures.OrderBy(failure => failure.Message, StringComparer.Ordinal).ToArray(), evaluated);
    }

    private static bool LooksLikeReleaseWorkflow(string path, WorkflowDocument workflow)
    {
        return Path.GetFileName(path).Contains("release", StringComparison.OrdinalIgnoreCase) ||
               workflow.Jobs.Any(job => job.Steps.Any(IsPublishStep));
    }

    private static void InspectWorkflow(string repositoryPath, WorkflowDocument workflow, List<Failure> failures)
    {
        if (!workflow.HasVersionedTagTrigger || workflow.HasUnsupportedTagPattern)
        {
            failures.Add(new Failure("workflow-policy", "Release workflow is not gated by a versioned tag trigger."));
        }

        var unsupportedPublicationPath = InspectUnsupportedPublicationPaths(repositoryPath, workflow, failures);
        var publishingJobs = workflow.Jobs.Where(job => job.Steps.Any(IsPublishStep)).ToArray();
        if (publishingJobs.Length == 0)
        {
            if (!unsupportedPublicationPath)
            {
                failures.Add(new Failure("workflow-policy", "Release workflow does not contain an executable package publication step."));
            }

            return;
        }

        foreach (var job in publishingJobs)
        {
            var effectivePermissions = job.Permissions.Count == 0 ? workflow.Permissions : job.Permissions;
            if (IsWriteAll(effectivePermissions) || HasOverbroadPublicationPermission(effectivePermissions))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow grants permissions broader than the OIDC publication path requires."));
            }

            if (!IsWritePermission(effectivePermissions, "id-token"))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow does not grant the required id-token write permission for Trusted Publishing in the publishing job."));
            }

            if (!job.Steps.Any(IsTrustedPublishingStep))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow does not execute Trusted Publishing authentication in the publishing job."));
            }

            var conditionScope = GetPublicationConditionScope(job.Condition);
            if (conditionScope == PublicationConditionScope.Unproven)
            {
                failures.Add(new Failure(
                    "workflow-policy",
                    "Release workflow tag gating for the publication job is limited/unproven for its supported condition shape.",
                    IsWarning: true));
            }
            else if (!IsTagGatedPublication(workflow, conditionScope))
            {
                failures.Add(new Failure(
                    "workflow-policy",
                    workflow.HasPushBranchTrigger
                        ? "Release workflow has an additional branch-triggered publication path; publication must be restricted to version tags."
                        : "Release workflow has a publication path that is not restricted to version tags."));
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

            if (ContainsLongLivedCredential(workflow.Environment) ||
                ContainsLongLivedCredential(job.Environment) ||
                job.Steps.Any(step => ContainsLongLivedCredential(step.Run) || ContainsLongLivedCredential(step.Environment)))
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

    private static bool InspectUnsupportedPublicationPaths(
        string repositoryPath,
        WorkflowDocument workflow,
        List<Failure> failures)
    {
        var foundUnsupportedPath = false;
        foreach (var job in workflow.Jobs)
        {
            if (!IsExplicitlyDisabled(job.Condition) && !string.IsNullOrWhiteSpace(job.Uses))
            {
                failures.Add(new Failure(
                    "workflow-policy",
                    "Release workflow contains an unsupported reusable workflow job; package publication policy is limited/unproven.",
                    IsWarning: true));
                foundUnsupportedPath = true;
            }

            foreach (var step in job.Steps)
            {
                if (IsExplicitlyDisabled(step.Condition) || !IsUnsupportedCompositePublicationPath(repositoryPath, step.Uses))
                {
                    continue;
                }

                failures.Add(new Failure(
                    "workflow-policy",
                    "Release workflow uses a composite action publication path; package publication policy is limited/unproven.",
                    IsWarning: true));
                foundUnsupportedPath = true;
            }
        }

        return foundUnsupportedPath;
    }

    private static bool IsUnsupportedCompositePublicationPath(string repositoryPath, string? uses)
    {
        if (!IsLocalActionReference(uses))
        {
            return false;
        }

        string actionDirectory;
        try
        {
            var repositoryRoot = Path.GetFullPath(repositoryPath);
            actionDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, uses![2..].Replace('/', Path.DirectorySeparatorChar)));
            if (!actionDirectory.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        foreach (var metadataName in new[] { "action.yml", "action.yaml" })
        {
            var metadataPath = Path.Combine(actionDirectory, metadataName);
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            try
            {
                var file = new FileInfo(metadataPath);
                if (file.Length > MaxWorkflowBytes)
                {
                    return true;
                }

                var content = File.ReadAllText(metadataPath);
                return IsCompositeAction(content) &&
                    (ContainsExecutableCommand(content, "nuget push") ||
                     Regex.IsMatch(content, @"^\s*run\s*:\s*(?:\|\s*)?(?:&\s*)?(?:dotnet\s+)?nuget\s+push(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline));
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCompositeAction(string content)
    {
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("using:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["using:".Length..].Trim().Trim('\'', '"'))
            .Any(value => value.Equals("composite", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLocalActionReference(string? uses)
    {
        return uses is not null && (uses.StartsWith("./", StringComparison.Ordinal) || uses.StartsWith(".\\", StringComparison.Ordinal));
    }

    private static bool IsPublishStep(WorkflowStep step)
    {
        return !IsExplicitlyDisabled(step.Condition) && ContainsExecutableCommand(step.Run, "nuget push");
    }

    private static bool IsTrustedPublishingStep(WorkflowStep step)
    {
        return !IsExplicitlyDisabled(step.Condition) &&
               step.Uses?.Contains("nuget/login@", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsArtifactValidationStep(WorkflowStep step)
    {
        return IsDefinitelyEnabled(step.Condition) &&
               (ContainsExecutableCommand(step.Run, "nugetready check") || ContainsExecutableValidationScript(step.Run));
    }

    private static bool ContainsLongLivedCredential(string? run)
    {
        return run is not null && Regex.IsMatch(run, @"secrets\s*\.\s*NUGET(?:_|-)?API(?:_|-)?KEY\b", RegexOptions.IgnoreCase);
    }

    private static bool ContainsLongLivedCredential(IReadOnlyDictionary<string, string> environment)
    {
        return environment.Any(pair => ContainsLongLivedCredential(pair.Value));
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

    private static bool HasOverbroadPublicationPermission(Dictionary<string, string> permissions)
    {
        return permissions.Any(permission =>
            (permission.Key.Equals("contents", StringComparison.OrdinalIgnoreCase) ||
             permission.Key is "actions" or "packages" or "pull-requests" or "issues") &&
            permission.Value.Equals("write", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTagGatedPublication(WorkflowDocument workflow, PublicationConditionScope conditionScope)
    {
        return conditionScope switch
        {
            PublicationConditionScope.TagOnly => true,
            PublicationConditionScope.PushOnly => !workflow.HasPushBranchTrigger,
            PublicationConditionScope.Unconditional => !workflow.HasPushBranchTrigger && !workflow.HasOtherTrigger,
            _ => false
        };
    }

    private static PublicationConditionScope GetPublicationConditionScope(string? condition)
    {
        var normalized = NormalizeCondition(condition);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return PublicationConditionScope.Unconditional;
        }

        if (IsExactCondition(normalized, @"github\.event_name\s*==\s*['""]push['""]"))
        {
            return PublicationConditionScope.PushOnly;
        }

        if (IsExactCondition(normalized, @"github\.ref_type\s*==\s*['""]tag['""]") ||
            IsExactCondition(normalized, @"startsWith\s*\(\s*github\.ref\s*,\s*['""]refs/tags/v['""]\s*\)"))
        {
            return PublicationConditionScope.TagOnly;
        }

        if (Regex.IsMatch(
                normalized,
                @"^github\.event_name\s*==\s*['""]push['""]\s*&&\s*(?:github\.ref_type\s*==\s*['""]tag['""]|startsWith\s*\(\s*github\.ref\s*,\s*['""]refs/tags/v['""]\s*\))$",
                RegexOptions.IgnoreCase))
        {
            return PublicationConditionScope.TagOnly;
        }

        return PublicationConditionScope.Unproven;
    }

    private static bool IsExactCondition(string condition, string pattern)
    {
        return Regex.IsMatch(condition, $"^{pattern}$", RegexOptions.IgnoreCase);
    }

    private static string NormalizeCondition(string? condition)
    {
        var normalized = condition?.Trim() ?? string.Empty;
        if (normalized.StartsWith("${{", StringComparison.Ordinal) && normalized.EndsWith("}}", StringComparison.Ordinal))
        {
            normalized = normalized[3..^2].Trim();
        }

        return normalized;
    }

    private static bool IsExplicitlyDisabled(string? condition)
    {
        return NormalizeCondition(condition).Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefinitelyEnabled(string? condition)
    {
        var normalized = NormalizeCondition(condition);
        return string.IsNullOrWhiteSpace(normalized) || normalized.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsExecutableCommand(string? run, string command)
    {
        if (string.IsNullOrWhiteSpace(run))
        {
            return false;
        }

        foreach (var rawLine in run.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || IsOutputOnlyCommand(line))
            {
                continue;
            }

            if (Regex.IsMatch(line, $@"^(?:&\s*)?(?:dotnet\s+)?{Regex.Escape(command)}(?:\s|$)", RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsExecutableValidationScript(string? run)
    {
        if (string.IsNullOrWhiteSpace(run))
        {
            return false;
        }

        foreach (var rawLine in run.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || IsOutputOnlyCommand(line))
            {
                continue;
            }

            if (Regex.IsMatch(line, @"^(?:&\s*)?(?:pwsh|powershell)\b.*(?:-File\s+)?[^\r\n]*inspect-package\.ps1\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(line, @"^(?:&\s*)?(?:\.\/?|\.\\)?scripts[/\\]inspect-package\.ps1\b", RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOutputOnlyCommand(string line)
    {
        return Regex.IsMatch(line, @"^(?:echo|printf|write-output|write-host)\b", RegexOptions.IgnoreCase);
    }

    private enum PublicationConditionScope
    {
        Unconditional,
        PushOnly,
        TagOnly,
        Unproven
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
        public bool HasUnsupportedTagPattern { get; set; }
        public bool HasPushBranchTrigger { get; set; }
        public bool HasOtherTrigger { get; set; }
        public Dictionary<string, string> Permissions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<WorkflowJob> Jobs { get; } = new();
    }

    private sealed class WorkflowJob
    {
        public WorkflowJob(string id) => Id = id;
        public string Id { get; }
        public string? Uses { get; set; }
        public string? Condition { get; set; }
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
        public string? Condition { get; set; }
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
            string? activeTriggerOption = null;
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
                    activeTriggerOption = null;
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
                    else if (topKey.Equals("on", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var trigger in ParseSequence(topValue))
                        {
                            if (!trigger.Equals("push", StringComparison.OrdinalIgnoreCase))
                            {
                                workflow.HasOtherTrigger = true;
                            }
                        }
                    }

                    continue;
                }

                if (inOn && indent == 2 && TryParseKeyValue(trimmed, out var triggerKey, out var triggerValue))
                {
                    inPush = triggerKey.Equals("push", StringComparison.OrdinalIgnoreCase);
                    activeTriggerOption = null;
                    if (inPush)
                    {
                        ParseTriggerOptions(triggerValue, workflow);
                    }
                    else
                    {
                        workflow.HasOtherTrigger = true;
                    }

                    continue;
                }

                if (inOn && inPush && indent >= 4 && trimmed.StartsWith('-') && activeTriggerOption is not null)
                {
                    if (activeTriggerOption.Equals("tags", StringComparison.OrdinalIgnoreCase))
                    {
                        AddTagPatterns(workflow, new[] { Unquote(trimmed[1..].Trim()) });
                    }

                    continue;
                }

                if (inOn && inPush && indent >= 4 && TryParseKeyValue(trimmed, out var triggerOption, out var triggerOptionValue))
                {
                    if (triggerOption.Equals("tags", StringComparison.OrdinalIgnoreCase))
                    {
                        activeTriggerOption = triggerOption;
                        AddTagPatterns(workflow, ParseSequence(triggerOptionValue));
                    }
                    else if (triggerOption.Equals("branches", StringComparison.OrdinalIgnoreCase))
                    {
                        activeTriggerOption = triggerOption;
                        workflow.HasPushBranchTrigger = true;
                    }
                    else
                    {
                        activeTriggerOption = null;
                    }

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
                    else if (jobKey.Equals("if", StringComparison.OrdinalIgnoreCase))
                    {
                        currentJob.Condition = Unquote(jobValue);
                    }
                    else if (jobKey.Equals("uses", StringComparison.OrdinalIgnoreCase))
                    {
                        currentJob.Uses = Unquote(jobValue);
                    }

                    continue;
                }

                if (indent >= 6 && trimmed.StartsWith('-'))
                {
                    activeMap = null;
                    activeMapIndent = -1;
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
            else if (key.Equals("if", StringComparison.OrdinalIgnoreCase))
            {
                step.Condition = Unquote(value);
            }
        }

        private static void ParseTriggerOptions(string value, WorkflowDocument workflow)
        {
            var text = value.Trim();
            if (!text.StartsWith('{') || !text.EndsWith('}'))
            {
                return;
            }

            foreach (var pair in text[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParseKeyValue(pair, out var key, out var optionValue))
                {
                    continue;
                }

                if (key.Equals("tags", StringComparison.OrdinalIgnoreCase))
                {
                    AddTagPatterns(workflow, ParseSequence(optionValue));
                }
                else if (key.Equals("branches", StringComparison.OrdinalIgnoreCase))
                {
                    workflow.HasPushBranchTrigger = true;
                }
            }
        }

        private static void AddTagPatterns(WorkflowDocument workflow, IEnumerable<string> patterns)
        {
            var values = patterns.ToArray();
            foreach (var pattern in values)
            {
                if (pattern.Trim().Equals("v*.*.*", StringComparison.OrdinalIgnoreCase))
                {
                    workflow.HasVersionedTagTrigger = true;
                }
                else
                {
                    workflow.HasUnsupportedTagPattern = true;
                }
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
