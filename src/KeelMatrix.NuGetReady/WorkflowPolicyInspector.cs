using System.Text;
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
            if (!LooksLikeReleaseWorkflow(repositoryPath, path, workflow))
            {
                continue;
            }

            evaluated = true;
            InspectWorkflow(repositoryPath, path, workflow, failures);
        }

        return new WorkflowInspectionResult(failures.OrderBy(failure => failure.Message, StringComparer.Ordinal).ToArray(), evaluated);
    }

    private static bool LooksLikeReleaseWorkflow(string repositoryPath, string path, WorkflowDocument workflow)
    {
        if (workflow.HasPublicationShapedTrigger ||
            workflow.HasPublicationInput ||
            workflow.HasUninspectableStructure ||
            workflow.Jobs.Any(job => IsDirectPublicationJob(job) ||
                                     job.Steps.Any(IsPublicationRelevantStep)))
        {
            return true;
        }

        return workflow.Jobs.Any(job => IsReleaseLikePublicationJob(repositoryPath, path, workflow, job));
    }

    private static bool IsReleaseLikePublicationJob(string repositoryPath, string path, WorkflowDocument workflow, WorkflowJob job)
    {
        if (IsExplicitlyDisabled(job.Condition))
        {
            return false;
        }

        if (job.HasUninspectableStructure || job.Steps.Any(step => step.HasUninspectableStructure))
        {
            return true;
        }

        if (job.Steps.Any(step => HasIndirectPublicationPath(repositoryPath, step)))
        {
            return true;
        }

        if (HasReleasePublicationSignal(Path.GetFileNameWithoutExtension(path)) ||
               HasReleasePublicationSignal(workflow.Name) ||
               HasReleasePublicationSignal(job.Id) ||
               HasReleasePublicationSignal(job.Name) ||
               HasReleasePublicationSignal(job.Uses))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(job.Uses))
        {
            return false;
        }

        return !TryProveLocalReusableWorkflow(repositoryPath, job.Uses, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static bool HasReleasePublicationSignal(string? value)
    {
        return value is not null && Regex.IsMatch(
            value,
            @"(?:^|[-_./\s])(?:release|publish|publishing|nuget|deploy|ship|push)(?:$|[-_./\s])",
            RegexOptions.IgnoreCase);
    }

    private static bool IsDirectPublicationJob(WorkflowJob job)
    {
        return !IsExplicitlyDisabled(job.Condition) && job.Steps.Any(IsPublishStep);
    }

    private static bool TryProveLocalReusableWorkflow(
        string repositoryPath,
        string uses,
        HashSet<string> visitedPaths)
    {
        if (!IsLocalWorkflowReference(uses) || ContainsExpression(uses))
        {
            return false;
        }

        string workflowPath;
        try
        {
            var repositoryRoot = Path.GetFullPath(repositoryPath);
            workflowPath = Path.GetFullPath(Path.Combine(
                repositoryRoot,
                uses[2..].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            if (!workflowPath.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                (!workflowPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) &&
                 !workflowPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!visitedPaths.Add(workflowPath))
        {
            return false;
        }

        try
        {
            var file = new FileInfo(workflowPath);
            if (!file.Exists || file.Length > MaxWorkflowBytes)
            {
                return false;
            }

            var workflow = SupportedYaml.Parse(File.ReadAllText(workflowPath));
            if (workflow.Jobs.Count == 0 ||
                workflow.HasPublicationShapedTrigger ||
                workflow.HasPublicationInput ||
                workflow.HasUninspectableStructure ||
                workflow.Jobs.Any(job => IsDirectPublicationJob(job)) ||
                HasReleasePublicationSignal(Path.GetFileNameWithoutExtension(workflowPath)) ||
                HasReleasePublicationSignal(workflow.Name))
            {
                return false;
            }

            foreach (var job in workflow.Jobs)
            {
                if (IsExplicitlyDisabled(job.Condition))
                {
                    continue;
                }

                if (job.HasUninspectableStructure ||
                    job.Steps.Any(step => step.HasUninspectableStructure) ||
                    HasReleasePublicationSignal(job.Id) ||
                    HasReleasePublicationSignal(job.Name) ||
                    HasReleasePublicationSignal(job.Uses))
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(job.Uses) &&
                    !TryProveLocalReusableWorkflow(repositoryPath, job.Uses, visitedPaths))
                {
                    return false;
                }
            }

            visitedPaths.Remove(workflowPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ContainsExpression(string? value)
    {
        return value?.Contains("${{", StringComparison.Ordinal) == true;
    }

    private static void InspectWorkflow(string repositoryPath, string path, WorkflowDocument workflow, List<Failure> failures)
    {
        var publishingJobs = workflow.Jobs.Where(job => job.Steps.Any(IsPublishStep)).ToArray();
        if (!workflow.HasVersionedTagTrigger || workflow.HasUnsupportedTagPattern)
        {
            if (publishingJobs.Length > 0)
            {
                failures.Add(new Failure("workflow-policy", "Release workflow is not gated by a versioned tag trigger."));
            }
        }

        var unsupportedPublicationPath = InspectUnsupportedPublicationPaths(repositoryPath, workflow, failures);
        var hasPublicationRelevantStep = workflow.Jobs.Any(job => job.Steps.Any(IsPublicationRelevantStep));
        var hasReleaseVocabulary = HasReleasePublicationSignal(Path.GetFileNameWithoutExtension(path)) ||
                                    HasReleasePublicationSignal(workflow.Name) ||
                                    workflow.Jobs.Any(job =>
                                        !IsExplicitlyDisabled(job.Condition) &&
                                         (HasReleasePublicationSignal(job.Id) ||
                                          HasReleasePublicationSignal(job.Name) ||
                                          HasReleasePublicationSignal(job.Uses)));
        var limitedUnprovenShape = workflow.HasPublicationShapedTrigger ||
            workflow.HasPublicationInput ||
            workflow.HasUninspectableStructure ||
                                    workflow.Jobs.Any(job => job.HasUninspectableStructure ||
                                                            job.Steps.Any(step => step.HasUninspectableStructure)) ||
                                    (publishingJobs.Length == 0 && (hasReleaseVocabulary || hasPublicationRelevantStep));
        if (limitedUnprovenShape && !unsupportedPublicationPath)
        {
            failures.Add(new Failure(
                "workflow-policy",
                "Workflow publication policy is limited/unproven because a release-shaped trigger or unsupported structure could not be inspected safely.",
                IsWarning: true));
        }

        if (publishingJobs.Length == 0)
        {
            if (!unsupportedPublicationPath && !limitedUnprovenShape)
            {
                failures.Add(new Failure("workflow-policy", "Release workflow does not contain an executable package publication step."));
            }

            return;
        }

        foreach (var job in publishingJobs)
        {
            var effectivePermissions = job.PermissionsSpecified ? job.Permissions : workflow.Permissions;
            if (IsWriteAll(effectivePermissions) || HasOverbroadPublicationPermission(effectivePermissions))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow grants permissions broader than the OIDC publication path requires."));
            }

            if (!IsWritePermission(effectivePermissions, "id-token"))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow does not grant the required id-token write permission for Trusted Publishing in the publishing job."));
            }

            var steps = job.Steps.ToArray();
            var pushIndices = steps
                .Select((step, index) => (step, index))
                .Where(item => IsPublishStep(item.step))
                .Select(item => item.index)
                .ToArray();
            var authenticationIndex = Array.FindIndex(steps, IsTrustedPublishingStep);
            if (authenticationIndex < 0 || pushIndices.Any(index => authenticationIndex > index))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow does not execute Trusted Publishing authentication before publication in the publishing job."));
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
                if (IsExplicitlyDisabled(step.Condition))
                {
                    continue;
                }

                var indirectPath = InspectIndirectPublicationPath(repositoryPath, step);
                if (indirectPath is null || indirectPath == IndirectPublicationPath.ProvenNonPublishing)
                {
                    continue;
                }

                var message = IsLocalActionReference(step.Uses)
                    ? "Release workflow uses a composite action publication path; package publication policy is limited/unproven."
                    : !string.IsNullOrWhiteSpace(step.Uses)
                        ? "Release workflow uses an opaque publication action; package publication policy is limited/unproven."
                        : "Release workflow uses a referenced script publication path; package publication policy is limited/unproven.";
                failures.Add(new Failure("workflow-policy", message, IsWarning: true));
                foundUnsupportedPath = true;
            }
        }

        return foundUnsupportedPath;
    }

    private static bool HasIndirectPublicationPath(string repositoryPath, WorkflowStep step)
    {
        var result = InspectIndirectPublicationPath(repositoryPath, step);
        return result is not null && result != IndirectPublicationPath.ProvenNonPublishing;
    }

    private static IndirectPublicationPath? InspectIndirectPublicationPath(string repositoryPath, WorkflowStep step)
    {
        if (IsExplicitlyDisabled(step.Condition))
        {
            return null;
        }

        var scriptReferences = FindReferencedScripts(step.Run);
        if (scriptReferences.Count > 0)
        {
            var scriptResult = InspectReferencedScripts(repositoryPath, step, scriptReferences);
            if (scriptResult != IndirectPublicationPath.ProvenNonPublishing)
            {
                return scriptResult;
            }
        }

        if (IsLocalActionReference(step.Uses))
        {
            return InspectLocalCompositeAction(repositoryPath, step.Uses!, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        if (IsPotentiallyPublicationAction(step))
        {
            return IndirectPublicationPath.Unknown;
        }

        return null;
    }

    private static IndirectPublicationPath InspectReferencedScripts(
        string repositoryPath,
        WorkflowStep step,
        IReadOnlyList<string> scriptReferences)
    {
        var result = IndirectPublicationPath.ProvenNonPublishing;
        foreach (var reference in scriptReferences)
        {
            var scriptResult = InspectReferencedScript(repositoryPath, step, reference);
            if (scriptResult == IndirectPublicationPath.Publication)
            {
                return scriptResult;
            }

            if (scriptResult == IndirectPublicationPath.Unknown)
            {
                result = scriptResult;
            }
        }

        return result;
    }

    private static IndirectPublicationPath InspectReferencedScript(
        string repositoryPath,
        WorkflowStep step,
        string reference)
    {
        string scriptPath;
        try
        {
            var repositoryRoot = Path.GetFullPath(repositoryPath);
            var workingDirectory = ResolveWorkingDirectory(repositoryRoot, step.WorkingDirectory);
            if (workingDirectory is null || ContainsExpression(reference))
            {
                return IndirectPublicationPath.Unknown;
            }

            scriptPath = Path.GetFullPath(Path.Combine(
                workingDirectory,
                reference.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            if (!IsWithinRepository(repositoryRoot, scriptPath))
            {
                return IndirectPublicationPath.Unknown;
            }
        }
        catch (ArgumentException)
        {
            return IndirectPublicationPath.Unknown;
        }

        try
        {
            var file = new FileInfo(scriptPath);
            if (!file.Exists || file.Length > MaxWorkflowBytes)
            {
                return IndirectPublicationPath.Unknown;
            }

            return ContainsExecutablePublicationCommand(File.ReadAllText(scriptPath))
                ? IndirectPublicationPath.Publication
                : IndirectPublicationPath.ProvenNonPublishing;
        }
        catch (IOException)
        {
            return IndirectPublicationPath.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return IndirectPublicationPath.Unknown;
        }
    }

    private static IndirectPublicationPath InspectLocalCompositeAction(
        string repositoryPath,
        string uses,
        HashSet<string> visitedPaths)
    {
        string actionDirectory;
        try
        {
            var repositoryRoot = Path.GetFullPath(repositoryPath);
            actionDirectory = Path.GetFullPath(Path.Combine(
                repositoryRoot,
                uses[2..].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            if (!IsWithinRepository(repositoryRoot, actionDirectory) || !visitedPaths.Add(actionDirectory))
            {
                return IndirectPublicationPath.Unknown;
            }
        }
        catch (ArgumentException)
        {
            return IndirectPublicationPath.Unknown;
        }

        string? metadataPath = null;
        foreach (var metadataName in new[] { "action.yml", "action.yaml" })
        {
            var candidate = Path.Combine(actionDirectory, metadataName);
            if (File.Exists(candidate))
            {
                metadataPath = candidate;
                break;
            }
        }

        if (metadataPath is null)
        {
            return IndirectPublicationPath.Unknown;
        }

        try
        {
            var file = new FileInfo(metadataPath);
            if (file.Length > MaxWorkflowBytes)
            {
                return IndirectPublicationPath.Unknown;
            }

            var action = SupportedYaml.ParseCompositeAction(File.ReadAllText(metadataPath));
            if (!action.IsComposite || !action.HasInspectableSteps)
            {
                return IndirectPublicationPath.Unknown;
            }

            var result = IndirectPublicationPath.ProvenNonPublishing;
            foreach (var step in action.Steps)
            {
                if (IsExplicitlyDisabled(step.Condition))
                {
                    continue;
                }

                if (ContainsExecutablePublicationCommand(step.Run))
                {
                    return IndirectPublicationPath.Publication;
                }

                var nested = IsLocalActionReference(step.Uses)
                    ? IndirectPublicationPath.Unknown
                    : InspectIndirectPublicationPath(repositoryPath, step);
                if (nested == IndirectPublicationPath.Publication)
                {
                    return nested.Value;
                }

                if (nested == IndirectPublicationPath.Unknown)
                {
                    result = IndirectPublicationPath.Unknown;
                }
            }

            return result;
        }
        catch (IOException)
        {
            return IndirectPublicationPath.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return IndirectPublicationPath.Unknown;
        }
    }

    private static bool IsWithinRepository(string repositoryRoot, string candidate)
    {
        return candidate.Equals(repositoryRoot, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveWorkingDirectory(string repositoryRoot, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || ContainsExpression(workingDirectory))
        {
            return repositoryRoot;
        }

        try
        {
            var resolved = Path.GetFullPath(Path.Combine(
                repositoryRoot,
                workingDirectory.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            return IsWithinRepository(repositoryRoot, resolved) ? resolved : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsPotentiallyPublicationAction(WorkflowStep step)
    {
        return !IsLocalActionReference(step.Uses) &&
               !string.IsNullOrWhiteSpace(step.Uses) &&
               !(step.Uses?.StartsWith("NuGet/login@", StringComparison.OrdinalIgnoreCase) ?? false) &&
               HasReleasePublicationSignal(step.Uses);
    }

    private enum IndirectPublicationPath
    {
        ProvenNonPublishing,
        Publication,
        Unknown
    }

    private static bool IsLocalActionReference(string? uses)
    {
        return uses is not null && (uses.StartsWith("./", StringComparison.Ordinal) || uses.StartsWith(".\\", StringComparison.Ordinal));
    }

    private static bool IsLocalWorkflowReference(string? uses)
    {
        return uses is not null && IsLocalActionReference(uses) &&
               (uses.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                uses.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPublishStep(WorkflowStep step)
    {
        return !IsExplicitlyDisabled(step.Condition) && ContainsExecutablePackagePublicationCommand(step.Run);
    }

    private static bool IsPublicationRelevantStep(WorkflowStep step)
    {
        return !IsExplicitlyDisabled(step.Condition) &&
               (ContainsExecutablePackagePublicationCommand(step.Run) || ContainsExecutableReleasePublicationCommand(step.Run));
    }

    private static bool IsTrustedPublishingStep(WorkflowStep step)
    {
        return IsDefinitelyEnabled(step.Condition) &&
               step.Uses?.Contains("nuget/login@", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsArtifactValidationStep(WorkflowStep step)
    {
        return IsDefinitelyEnabled(step.Condition) &&
               (ContainsExecutableCommand(step.Run, "nugetready check") || ContainsExecutableValidationScript(step.Run));
    }

    private static bool ContainsLongLivedCredential(string? run)
    {
        return run is not null && Regex.IsMatch(
            run,
            @"secrets\s*(?:\.\s*NUGET(?:_|-)?API(?:_|-)?KEY\b|\[\s*['""]?\s*NUGET(?:_|-)?API(?:_|-)?KEY\s*['""]?\s*\])",
            RegexOptions.IgnoreCase);
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

    private static bool ContainsExecutablePublicationCommand(string? content)
    {
        return ContainsExecutablePackagePublicationCommand(content) ||
               ContainsExecutableReleasePublicationCommand(content);
    }

    private static bool ContainsExecutablePackagePublicationCommand(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || IsOutputOnlyCommand(line))
            {
                continue;
            }

            if (Regex.IsMatch(
                    line,
                    @"^(?:&\s*)?(?:(?:dotnet\s+)?nuget(?:\.exe)?\s+push)(?:\s|$)",
                    RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsExecutableReleasePublicationCommand(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || IsOutputOnlyCommand(line))
            {
                continue;
            }

            if (Regex.IsMatch(line, @"^(?:&\s*)?gh(?:\.exe)?\s+release\s+create(?:\s|$)", RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> FindReferencedScripts(string? run)
    {
        if (string.IsNullOrWhiteSpace(run))
        {
            return Array.Empty<string>();
        }

        var references = new List<string>();
        foreach (var rawLine in run.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || IsOutputOnlyCommand(line))
            {
                continue;
            }

            var tokens = TokenizeCommandLine(line);
            if (tokens.Length == 0)
            {
                continue;
            }

            var command = tokens[0].TrimStart('&');
            if (IsScriptInterpreter(command))
            {
                for (var index = 1; index < tokens.Length; index++)
                {
                    var token = tokens[index];
                    if (token.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("-Command", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("--command", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (token.Equals("-File", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("-f", StringComparison.OrdinalIgnoreCase))
                    {
                        var foundReference = false;
                        for (var referenceIndex = index + 1; referenceIndex < tokens.Length; referenceIndex++)
                        {
                            if (IsScriptPathToken(tokens[referenceIndex]))
                            {
                                references.Add(tokens[referenceIndex]);
                                foundReference = true;
                                break;
                            }
                        }

                        if (!foundReference)
                        {
                            references.Add(index + 1 < tokens.Length ? tokens[index + 1] : string.Empty);
                        }

                        break;
                    }

                    if (!token.StartsWith('-') &&
                        IsScriptPathToken(token))
                    {
                        references.Add(token);
                        break;
                    }
                }

                continue;
            }

            if (IsScriptPathToken(command))
            {
                references.Add(command);
            }
        }

        return references;
    }

    private static bool IsScriptInterpreter(string command)
    {
        return command.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("sh", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("dash", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("zsh", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsScriptPathToken(string token)
    {
        var normalized = token.Trim().Trim(';', '|', '&');
        return normalized.StartsWith("./", StringComparison.Ordinal) ||
               normalized.StartsWith(".\\", StringComparison.Ordinal) ||
               normalized.StartsWith("../", StringComparison.Ordinal) ||
               normalized.StartsWith("..\\", StringComparison.Ordinal) ||
               normalized.StartsWith('/') ||
               normalized.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("scripts\\", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("tools\\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains('/', StringComparison.Ordinal) &&
               (normalized.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".bash", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) ||
               normalized.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".bash", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] TokenizeCommandLine(string line)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        var quote = '\0';
        foreach (var character in line)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    token.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (token.Length > 0)
                {
                    var cleaned = CleanCommandToken(token.ToString());
                    if (cleaned.Length > 0)
                    {
                        tokens.Add(cleaned);
                    }

                    token.Clear();
                }
            }
            else
            {
                token.Append(character);
            }
        }

        if (token.Length > 0)
        {
            var cleaned = CleanCommandToken(token.ToString());
            if (cleaned.Length > 0)
            {
                tokens.Add(cleaned);
            }
        }

        return tokens.ToArray();
    }

    private static string CleanCommandToken(string token)
    {
        return token.Trim('`', ',', ';', '|', '&', '(', ')');
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
        var inheritedEnvironment = new Dictionary<string, string>(workflow.Environment, StringComparer.OrdinalIgnoreCase);
        ApplyEnvironment(inheritedEnvironment, job.Environment);

        var relevantSteps = job.Steps
            .Where(IsTelemetryRelevantStep)
            .ToArray();
        if (relevantSteps.Length == 0)
        {
            return false;
        }

        return relevantSteps.All(step =>
        {
            var effectiveEnvironment = new Dictionary<string, string>(inheritedEnvironment, StringComparer.OrdinalIgnoreCase);
            ApplyEnvironment(effectiveEnvironment, step.Environment);
            return HasTelemetrySuppression(effectiveEnvironment);
        });
    }

    private static bool IsTelemetryRelevantStep(WorkflowStep step)
    {
        return !IsExplicitlyDisabled(step.Condition) &&
               (ContainsExecutableCommand(step.Run, "dotnet") ||
                ContainsExecutableCommand(step.Run, "nugetready") ||
                ContainsExecutableCommand(step.Run, "nuget"));
    }

    private static void ApplyEnvironment(Dictionary<string, string> destination, IReadOnlyDictionary<string, string> source)
    {
        foreach (var pair in source)
        {
            destination[pair.Key] = pair.Value;
        }
    }

    private static bool HasTelemetrySuppression(IReadOnlyDictionary<string, string> environment)
    {
        return environment.Any(pair =>
            (pair.Key.Equals("KEELMATRIX_NO_TELEMETRY", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("DOTNET_CLI_TELEMETRY_OPTOUT", StringComparison.OrdinalIgnoreCase)) &&
            (pair.Value.Equals("1", StringComparison.OrdinalIgnoreCase) || pair.Value.Equals("true", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class CompositeActionDocument
    {
        public bool IsComposite { get; set; }
        public bool HasInspectableSteps { get; set; }
        public List<WorkflowStep> Steps { get; } = new();
    }

    private sealed class WorkflowDocument
    {
        public string? Name { get; set; }
        public bool HasPublicationShapedTrigger { get; set; }
        public bool HasPublicationInput { get; set; }
        public bool HasUninspectableStructure { get; set; }
        public bool HasVersionedTagTrigger { get; set; }
        public bool HasUnsupportedTagPattern { get; set; }
        public bool HasPushBranchTrigger { get; set; }
        public bool HasOtherTrigger { get; set; }
        public bool PermissionsSpecified { get; set; }
        public Dictionary<string, string> Permissions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<WorkflowJob> Jobs { get; } = new();
    }

    private sealed class WorkflowJob
    {
        public WorkflowJob(string id) => Id = id;
        public string Id { get; }
        public string? Name { get; set; }
        public string? Uses { get; set; }
        public string? Condition { get; set; }
        public bool HasUninspectableStructure { get; set; }
        public bool PermissionsSpecified { get; set; }
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
        public string? WorkingDirectory { get; set; }
        public bool HasUninspectableStructure { get; set; }
        public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> With { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static class SupportedYaml
    {
        public static CompositeActionDocument ParseCompositeAction(string content)
        {
            var action = new CompositeActionDocument();
            var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            var inRuns = false;
            var inSteps = false;
            var blockRunStep = (WorkflowStep?)null;
            var blockRunIndent = -1;
            WorkflowStep? currentStep = null;

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

                if (indent == 0 && TryParseKeyValue(trimmed, out var topKey, out _))
                {
                    inRuns = topKey.Equals("runs", StringComparison.OrdinalIgnoreCase);
                    inSteps = false;
                    currentStep = null;
                    continue;
                }

                if (inRuns && indent == 2 && TryParseKeyValue(trimmed, out var runsKey, out var runsValue))
                {
                    if (runsKey.Equals("using", StringComparison.OrdinalIgnoreCase))
                    {
                        action.IsComposite = Unquote(runsValue).Equals("composite", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (runsKey.Equals("steps", StringComparison.OrdinalIgnoreCase))
                    {
                        action.HasInspectableSteps = string.IsNullOrWhiteSpace(runsValue) || runsValue.Equals("[]", StringComparison.Ordinal);
                        inSteps = action.HasInspectableSteps;
                    }

                    continue;
                }

                if (inSteps && indent == 4 && trimmed.StartsWith('-'))
                {
                    currentStep = new WorkflowStep();
                    action.Steps.Add(currentStep);
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

                if (currentStep is not null && indent >= 6 && TryParseKeyValue(trimmed, out var key, out var value))
                {
                    AssignStepValue(currentStep, key, value);
                    if (key.Equals("run", StringComparison.OrdinalIgnoreCase) && (value == "|" || value == ">"))
                    {
                        blockRunStep = currentStep;
                        blockRunIndent = indent;
                    }
                }
            }

            return action;
        }

        public static WorkflowDocument Parse(string content)
        {
            var workflow = new WorkflowDocument();
            var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            var inOn = false;
            var inPush = false;
            var activeTrigger = string.Empty;
            var inTriggerInputs = false;
            var triggerInputsIndent = -1;
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

                if (inTriggerInputs && indent <= triggerInputsIndent)
                {
                    inTriggerInputs = false;
                    triggerInputsIndent = -1;
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
                    activeTrigger = string.Empty;
                    inTriggerInputs = false;
                    triggerInputsIndent = -1;
                    activeTriggerOption = null;
                    currentJob = null;
                    currentStep = null;
                    activeStepMap = false;
                    if (topKey.Equals("permissions", StringComparison.OrdinalIgnoreCase))
                    {
                        workflow.PermissionsSpecified = true;
                        ParseInlineMap(topValue, workflow.Permissions);
                        if (string.IsNullOrWhiteSpace(topValue))
                        {
                            activeMap = workflow.Permissions;
                            activeMapIndent = indent;
                        }
                    }
                    else if (topKey.Equals("name", StringComparison.OrdinalIgnoreCase))
                    {
                        workflow.Name = Unquote(topValue);
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
                    activeTrigger = triggerKey;
                    activeTriggerOption = null;
                    if (triggerKey.Equals("release", StringComparison.OrdinalIgnoreCase))
                    {
                        workflow.HasPublicationShapedTrigger = true;
                    }
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

                if (inOn && !inPush && indent == 4 &&
                    activeTrigger is "workflow_call" or "workflow_dispatch" &&
                    TryParseKeyValue(trimmed, out var triggerOptionKey, out _))
                {
                    inTriggerInputs = triggerOptionKey.Equals("inputs", StringComparison.OrdinalIgnoreCase);
                    triggerInputsIndent = inTriggerInputs ? indent : -1;
                    continue;
                }

                if (inOn && !inPush && inTriggerInputs && indent == triggerInputsIndent + 2 &&
                    TryParseKeyValue(trimmed, out var inputName, out _))
                {
                    if (HasReleasePublicationSignal(inputName))
                    {
                        workflow.HasPublicationInput = true;
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
                    else if (triggerOption.Equals("branches-ignore", StringComparison.OrdinalIgnoreCase))
                    {
                        activeTriggerOption = triggerOption;
                        workflow.HasPushBranchTrigger = true;
                    }
                    else if (triggerOption.Equals("tags-ignore", StringComparison.OrdinalIgnoreCase))
                    {
                        activeTriggerOption = triggerOption;
                        workflow.HasUnsupportedTagPattern = true;
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
                        currentJob.PermissionsSpecified = true;
                        ParseInlineMap(jobValue, currentJob.Permissions);
                        if (string.IsNullOrWhiteSpace(jobValue))
                        {
                            activeMap = currentJob.Permissions;
                            activeMapIndent = indent;
                        }
                    }
                    else if (jobKey.Equals("name", StringComparison.OrdinalIgnoreCase))
                    {
                        currentJob.Name = Unquote(jobValue);
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
                        if (!string.IsNullOrWhiteSpace(jobValue) && !jobValue.Equals("[]", StringComparison.Ordinal))
                        {
                            currentJob.HasUninspectableStructure = true;
                            workflow.HasUninspectableStructure = true;
                        }

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
                        if (string.IsNullOrWhiteSpace(currentJob.Uses))
                        {
                            currentJob.HasUninspectableStructure = true;
                            workflow.HasUninspectableStructure = true;
                        }
                    }
                    else if (!IsSupportedJobKey(jobKey))
                    {
                        currentJob.HasUninspectableStructure = true;
                        workflow.HasUninspectableStructure = true;
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
                    else if (key.Equals("with", StringComparison.OrdinalIgnoreCase))
                    {
                        AssignStepValue(currentStep, key, value);
                        activeMap = currentStep.With;
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

        private static bool IsSupportedJobKey(string key)
        {
            return key.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("uses", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("if", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("permissions", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("env", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("needs", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("steps", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("runs-on", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("strategy", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("continue-on-error", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("timeout-minutes", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("container", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("services", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("outputs", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("defaults", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("concurrency", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("with", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("secrets", StringComparison.OrdinalIgnoreCase);
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
            else if (key.Equals("working-directory", StringComparison.OrdinalIgnoreCase))
            {
                step.WorkingDirectory = Unquote(value);
            }
            else if (!IsSupportedStepKey(key))
            {
                step.HasUninspectableStructure = true;
            }
        }

        private static bool IsSupportedStepKey(string key)
        {
            return key.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("uses", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("run", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("if", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("env", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("with", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("shell", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("working-directory", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("continue-on-error", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("timeout-minutes", StringComparison.OrdinalIgnoreCase);
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
                else if (key.Equals("branches-ignore", StringComparison.OrdinalIgnoreCase))
                {
                    workflow.HasPushBranchTrigger = true;
                }
                else if (key.Equals("tags-ignore", StringComparison.OrdinalIgnoreCase))
                {
                    workflow.HasUnsupportedTagPattern = true;
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
