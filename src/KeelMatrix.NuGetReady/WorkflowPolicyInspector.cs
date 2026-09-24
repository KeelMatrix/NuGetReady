using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KeelMatrix.NuGetReady;

internal sealed record WorkflowInspectionResult(IReadOnlyList<Failure> Failures, bool Evaluated);

internal static class WorkflowPolicyInspector
{
    private const int MaxWorkflowBytes = 512 * 1024;
    private const int MaxIndirectPathDepth = 8;
    private const long MaxIndirectPathBytes = 2 * 1024 * 1024;
    private const string SupportedToolVersion = "0.1.0";
    private const string InstalledValidationCommand = "./.nugetready/nugetready check --config nugetready.json --artifacts artifacts/release --format json";
    private static readonly string[] InstalledValidationTokens = TokenizeCommandLine(InstalledValidationCommand);
    private static readonly string[] ExactPackArguments =
    {
        "--configuration", "Release", "--no-build", "--no-restore", "--include-symbols",
        "-p:SymbolPackageFormat=snupkg", "-p:ImportDirectoryBuildTargets=false",
        "-p:ImportDirectoryTargets=false", "--output", "artifacts/release", "--nologo",
        "-p:UseSharedCompilation=false"
    };

    public static IReadOnlyList<Failure> Inspect(string repositoryPath, NuGetReadyConfig? config = null)
    {
        return InspectDetailed(repositoryPath, config).Failures;
    }

    public static WorkflowInspectionResult InspectDetailed(string repositoryPath, NuGetReadyConfig? config = null)
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
            InspectWorkflow(repositoryPath, path, workflow, config, failures);
        }

        return new WorkflowInspectionResult(failures.OrderBy(failure => failure.Message, StringComparer.Ordinal).ToArray(), evaluated);
    }

    private static bool LooksLikeReleaseWorkflow(string repositoryPath, string path, WorkflowDocument workflow)
    {
        if (workflow.HasPublicationShapedTrigger ||
            workflow.HasPublicationInput ||
            workflow.HasUninspectableStructure ||
            workflow.Jobs.Any(job => IsDirectPublicationJob(job) ||
                                     job.Steps.Any(IsPublicationRelevantStep) ||
                                     job.Steps.Any(step => HasIndirectPublicationPath(repositoryPath, step))))
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

                if (job.Steps.Any(step => HasIndirectPublicationPath(repositoryPath, step)))
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

    private static void InspectWorkflow(
        string repositoryPath,
        string path,
        WorkflowDocument workflow,
        NuGetReadyConfig? config,
        List<Failure> failures)
    {
        var indirectPathContext = new IndirectInspectionContext();
        var unsupportedPublicationPath = InspectUnsupportedPublicationPaths(repositoryPath, workflow, failures, indirectPathContext);
        var publishingJobs = workflow.Jobs.Where(job => job.Steps.Any(IsPublishStep)).ToArray();
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
            failures.Add(Unsupported(
                "Workflow publication policy is unsupported/unproven because a publication-relevant YAML node is outside the supported release profile."));
        }

        if (publishingJobs.Length == 0)
        {
            if (!unsupportedPublicationPath && !limitedUnprovenShape)
            {
                failures.Add(Unsupported("Release workflow does not contain the directly recognizable package publication step required by the supported release profile."));
            }

            return;
        }

        if (publishingJobs.Length != 1)
        {
            failures.Add(Unsupported("The supported release profile requires exactly one credential-bearing NuGet publish job."));
            return;
        }

        InspectSupportedReleaseProfile(repositoryPath, workflow, publishingJobs[0], config, failures);
    }

    private static void InspectSupportedReleaseProfile(
        string repositoryPath,
        WorkflowDocument workflow,
        WorkflowJob publishJob,
        NuGetReadyConfig? config,
        List<Failure> failures)
    {
        if (!workflow.HasVersionedTagTrigger || workflow.HasUnsupportedTagPattern)
        {
            failures.Add(new Failure("workflow-policy", "Release workflow is not gated exclusively by the supported version-tag pattern v*.*.*."));
        }

        if (workflow.HasPushBranchTrigger)
        {
            failures.Add(new Failure("workflow-policy", "Release workflow has an additional branch-triggered publication path; publication must be restricted to version tags."));
        }

        if (workflow.HasOtherTrigger || workflow.HasPublicationShapedTrigger || workflow.HasPublicationInput)
        {
            failures.Add(Unsupported("The supported release profile permits only an on.push.tags version-tag trigger; additional triggers are unproven."));
        }

        if (!HasExactPermissions(workflow.Permissions, ("contents", "read")))
        {
            failures.Add(new Failure("workflow-policy", "The supported release profile requires workflow permissions to be exactly contents: read."));
        }

        if (workflow.PresentKeys.Any(key => key is not ("name" or "on" or "permissions" or "env" or "jobs")))
        {
            failures.Add(Unsupported("The release workflow uses top-level execution controls outside the supported release profile."));
        }

        if (workflow.Environment.Count != 0)
        {
            failures.Add(Unsupported("The supported release profile does not permit workflow-level environment variables."));
        }

        if (!TryLoadExpectedArtifacts(repositoryPath, config, failures, out var expectedArtifacts, out var primaryArtifact))
        {
            return;
        }

        if (!IsDefinitelyEnabled(publishJob.Condition))
        {
            failures.Add(Unsupported("The credential-bearing publish job uses an unsupported/unproven condition; default successful-needs execution is required."));
        }

        if (publishJob.HasUninspectableStructure ||
            publishJob.PresentKeys.Any(key => key is not ("name" or "runs-on" or "timeout-minutes" or "needs" or "permissions" or "env" or "steps")))
        {
            failures.Add(Unsupported("The credential-bearing publish job contains an unknown or unsupported/unproven job node."));
        }

        if (!string.Equals(publishJob.RunsOn, "ubuntu-latest", StringComparison.Ordinal))
        {
            failures.Add(Unsupported("The supported publish job must run on the literal ubuntu-latest runner."));
        }

        if (!string.Equals(publishJob.TimeoutMinutes, "10", StringComparison.Ordinal))
        {
            failures.Add(Unsupported("The supported publish job timeout-minutes value must be the literal 10."));
        }

        if (!publishJob.PermissionsSpecified || !HasExactPermissions(publishJob.Permissions, ("contents", "read"), ("id-token", "write")))
        {
            failures.Add(new Failure("workflow-policy", "The publish job permissions must be exactly contents: read and id-token: write."));
        }

        if (!HasSupportedJobEnvironment(publishJob.Environment))
        {
            failures.Add(Unsupported("The publish job environment contains a credential, command-resolution, or unsupported variable or value."));
        }

        if (publishJob.DependsOn.Count != 1)
        {
            failures.Add(Unsupported("The publish job must have exactly one required validation-job dependency."));
            return;
        }

        var validationJob = workflow.Jobs.SingleOrDefault(candidate => publishJob.DependsOn.Contains(candidate.Id));
        if (validationJob is null)
        {
            failures.Add(Unsupported("The publish job validation dependency could not be resolved."));
            return;
        }

        if (workflow.Jobs.Count != 2 || workflow.Jobs.Any(job => job != publishJob && job != validationJob))
        {
            failures.Add(Unsupported("The 0.1.0 supported release profile contains exactly one validation job and one NuGet publish job; additional jobs are unproven."));
        }

        InspectValidationJob(repositoryPath, workflow, validationJob, expectedArtifacts, failures);
        InspectCredentialBearingJob(publishJob, primaryArtifact, failures);

        if (ContainsLongLivedCredential(workflow.Environment) ||
            workflow.Jobs.Any(job => ContainsLongLivedCredential(job.Environment) ||
                                     job.Steps.Any(step => ContainsLongLivedCredential(step.Run) ||
                                                           ContainsLongLivedCredential(step.Environment) ||
                                                           ContainsLongLivedCredential(step.With))))
        {
            failures.Add(new Failure("workflow-policy", "Release workflow contains a long-lived NuGet API-key publication path."));
        }

        if (!HasTelemetrySuppression(workflow, validationJob) || !HasTelemetrySuppression(workflow, publishJob))
        {
            failures.Add(new Failure("workflow-policy", "Release workflow does not suppress product and CLI telemetry for validation and publication execution."));
        }
    }

    private static void InspectValidationJob(
        string repositoryPath,
        WorkflowDocument workflow,
        WorkflowJob validationJob,
        IReadOnlyList<string> expectedArtifacts,
        List<Failure> failures)
    {
        if (!IsDefinitelyEnabled(validationJob.Condition) || !string.IsNullOrWhiteSpace(validationJob.Uses))
        {
            failures.Add(Unsupported("The validation dependency must be an unconditional local job."));
        }

        if (validationJob.DependsOn.Count != 0 || !string.Equals(validationJob.RunsOn, "ubuntu-latest", StringComparison.Ordinal))
        {
            failures.Add(Unsupported("The validation job must be a root job on the literal ubuntu-latest runner."));
        }

        if (!string.Equals(validationJob.TimeoutMinutes, "30", StringComparison.Ordinal))
        {
            failures.Add(Unsupported("The supported validation job timeout-minutes value must be the literal 30."));
        }


        if (!HasSupportedJobEnvironment(validationJob.Environment))
        {
            failures.Add(Unsupported("The validation job environment contains a credential, command-resolution, or unsupported variable or value."));
        }

        if (validationJob.HasUninspectableStructure ||
            validationJob.PresentKeys.Any(key => key is not ("name" or "runs-on" or "timeout-minutes" or "permissions" or "env" or "steps")))
        {
            failures.Add(Unsupported("The validation job contains an unknown or unsupported job node."));
        }

        var effectivePermissions = validationJob.PermissionsSpecified ? validationJob.Permissions : workflow.Permissions;
        if (IsWritePermission(effectivePermissions, "id-token") || IsWriteAll(effectivePermissions) || HasOverbroadPublicationPermission(effectivePermissions))
        {
            failures.Add(new Failure("workflow-policy", "The validation job must not receive an OIDC token or repository write capability."));
        }

        if (ContainsLongLivedCredential(validationJob.Environment) ||
            validationJob.Steps.Any(step => ContainsLongLivedCredential(step.Run) ||
                                               ContainsLongLivedCredential(step.Environment) ||
                                               ContainsLongLivedCredential(step.With)))
        {
            failures.Add(new Failure("workflow-policy", "The validation job must not receive a long-lived NuGet publishing credential."));
        }

        if (!HasSupportedNuGetConfiguration(repositoryPath))
        {
            failures.Add(Unsupported("The supported validation profile requires NuGet.config to contain only the canonical NuGet.org v3 source."));
        }

        if (!HasExactValidationSequence(validationJob.Steps, expectedArtifacts))
        {
            failures.Add(Unsupported("The validation job must use the exact ordered closed command profile: checkout, SDK setup, restore, format, build, test, pack, pinned tool-path install, installed NuGetReady check, and exact artifact upload."));
        }
    }

    private static void InspectCredentialBearingJob(
        WorkflowJob publishJob,
        string primaryArtifact,
        List<Failure> failures)
    {
        var steps = publishJob.Steps;
        var authenticationIndex = steps.FindIndex(IsTrustedPublishingStep);
        var publicationIndex = steps.FindIndex(IsPublishStep);
        if (authenticationIndex < 0 || publicationIndex < 0 || authenticationIndex > publicationIndex)
        {
            failures.Add(new Failure("workflow-policy", "Release workflow does not execute unconditional Trusted Publishing authentication before publication in the publishing job."));
        }

        if (steps.Count != 3)
        {
            failures.Add(Unsupported("The credential-bearing publish job must contain exactly download, Trusted Publishing login, and direct push steps."));
            return;
        }

        var download = steps[0];
        if (!UsesExactly(download, "actions/download-artifact@v4") || !IsSimpleActionStep(download, allowId: false) ||
            !HasExactInputs(download.With, ("name", "validated-release-artifacts"), ("path", "artifacts/release")))
        {
            failures.Add(Unsupported("The first publish step must download the exact validated artifact identity to artifacts/release."));
        }

        var login = steps[1];
        if (!UsesExactly(login, "NuGet/login@v1") || !IsSimpleActionStep(login, allowId: true) ||
            !string.Equals(login.Id, "nuget-login", StringComparison.Ordinal) ||
            !HasExactInputs(login.With, ("user", "dmitriyzen")))
        {
            failures.Add(Unsupported("The second publish step must be the unconditional NuGet/login@v1 Trusted Publishing login with id nuget-login."));
        }

        var push = steps[2];
        var expectedCommand = $"dotnet nuget push artifacts/release/{primaryArtifact} --source https://api.nuget.org/v3/index.json --api-key \"$env:NUGET_API_KEY\"";
        var hasSupportedPushStructure = IsDefinitelyEnabled(push.Condition) && !push.HasUninspectableStructure &&
            string.Equals(push.Shell, "pwsh", StringComparison.Ordinal) &&
            HasExactInputs(push.Environment, ("NUGET_API_KEY", "${{ steps.nuget-login.outputs.NUGET_API_KEY }}")) &&
            push.PresentKeys.All(key => key is "name" or "shell" or "env" or "run");
        if (!hasSupportedPushStructure)
        {
            failures.Add(Unsupported("The final publish step must be the supported direct dotnet nuget push of the exact primary package with only the temporary login output."));
        }
        else if (ContainsPackageWildcard(push.Run))
        {
            failures.Add(new Failure("workflow-policy", "Release workflow publishes from a broad package wildcard; use the exact expected artifact."));
        }
        else if (!string.Equals(push.Run?.Trim(), expectedCommand, StringComparison.Ordinal))
        {
            if (IsRecognizableLiteralPush(push.Run))
            {
                failures.Add(new Failure("workflow-policy", "Release workflow does not push the exact configured primary package with the supported arguments."));
            }
            else
            {
                failures.Add(Unsupported("The final publish command is outside the directly recognizable dotnet nuget push language."));
            }
        }
    }

    private static bool IsRecognizableLiteralPush(string? run)
    {
        return run is not null && !ContainsExpression(run) && Regex.IsMatch(
            run.Trim(),
            "^dotnet\\s+nuget\\s+push\\s+[^\\s$%*?]+\\s+--source\\s+https://api\\.nuget\\.org/v3/index\\.json\\s+--api-key\\s+\"\\$env:NUGET_API_KEY\"$",
            RegexOptions.CultureInvariant);
    }

    private static bool TryLoadExpectedArtifacts(
        string repositoryPath,
        NuGetReadyConfig? suppliedConfig,
        List<Failure> failures,
        out IReadOnlyList<string> artifacts,
        out string primaryArtifact)
    {
        artifacts = Array.Empty<string>();
        primaryArtifact = string.Empty;
        try
        {
            var config = suppliedConfig ?? ConfigurationLoader.Load(Path.Combine(repositoryPath, "nugetready.json"));
            var packages = config.Packages!;
            if (packages.Count != 1)
            {
                failures.Add(Unsupported("The 0.1.0 supported release profile requires exactly one configured package."));
                return false;
            }

            artifacts = packages[0].Artifacts!.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            primaryArtifact = artifacts.Single(value => value.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) &&
                                                         !value.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase));
            return true;
        }
        catch (NuGetReadyInputException)
        {
            failures.Add(Unsupported("The exact release artifact set could not be resolved from nugetready.json."));
            return false;
        }
        catch (NuGetReadyInfrastructureException)
        {
            failures.Add(Unsupported("The exact release artifact set could not be resolved from nugetready.json."));
            return false;
        }
    }

    private static Failure Unsupported(string message)
    {
        return new Failure("workflow-policy", message, IsError: true);
    }

    private static bool HasExactPermissions(Dictionary<string, string> actual, params (string Name, string Value)[] expected)
    {
        return HasExactInputs(actual, expected);
    }

    private static bool HasExactInputs(Dictionary<string, string> actual, params (string Name, string Value)[] expected)
    {
        return actual.Count == expected.Length && expected.All(item =>
            actual.TryGetValue(item.Name, out var value) && string.Equals(value.Trim(), item.Value, StringComparison.Ordinal));
    }

    private static bool HasSupportedJobEnvironment(Dictionary<string, string> environment)
    {
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["KEELMATRIX_NO_TELEMETRY"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["NUGET_XMLDOC_MODE"] = "skip",
            ["MSBUILDDISABLENODEREUSE"] = "1"
        };
        return environment.Count is >= 2 and <= 5 &&
               environment.All(pair => allowed.TryGetValue(pair.Key, out var value) &&
                                       string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase)) &&
               IsEnabled(environment, "KEELMATRIX_NO_TELEMETRY") &&
               IsEnabled(environment, "DOTNET_CLI_TELEMETRY_OPTOUT");
    }

    private static bool UsesExactly(WorkflowStep step, string action)
    {
        return string.Equals(step.Uses, action, StringComparison.Ordinal);
    }

    private static bool HasExactValidationSequence(
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyList<string> expectedArtifacts)
    {
        if (steps.Count != 10 ||
            !IsExactCheckoutStep(steps[0]) ||
            !IsExactSetupDotNetStep(steps[1]) ||
            !TryMatchRestoreStep(steps[2], out var solutionPath) ||
            !IsExactFormatStep(steps[3], solutionPath) ||
            !IsExactBuildStep(steps[4], solutionPath) ||
            !IsExactTestStep(steps[5], solutionPath) ||
            !IsExactPackStep(steps[6]) ||
            !IsExactToolInstallStep(steps[7]) ||
            !IsExactProfileValidationStep(steps[8]) ||
            !IsExactUploadStep(steps[9], expectedArtifacts))
        {
            return false;
        }

        return true;
    }

    private static bool IsExactCheckoutStep(WorkflowStep step)
    {
        return UsesExactly(step, "actions/checkout@v6") &&
               IsSimpleActionStep(step, allowId: false) &&
               HasExactInputs(
                   step.With,
                   ("fetch-depth", "0"),
                   ("ref", "${{ github.ref }}"),
                   ("persist-credentials", "false"));
    }

    private static bool IsExactSetupDotNetStep(WorkflowStep step)
    {
        return UsesExactly(step, "actions/setup-dotnet@v5") &&
               IsSimpleActionStep(step, allowId: false) &&
               HasExactInputs(step.With, ("global-json-file", "global.json"));
    }

    private static bool TryMatchRestoreStep(WorkflowStep step, out string solutionPath)
    {
        solutionPath = string.Empty;
        if (!TryGetDirectCommandTokens(step, out var tokens) ||
            tokens.Length != 9 ||
            !tokens[0].Equals("dotnet", StringComparison.Ordinal) ||
            !tokens[1].Equals("restore", StringComparison.Ordinal) ||
            !IsLiteralRepositoryPath(tokens[2], "sln", "slnx") ||
            !tokens[3].Equals("--configfile", StringComparison.Ordinal) ||
            !tokens[4].Equals("NuGet.config", StringComparison.Ordinal) ||
            !tokens[5].Equals("--nologo", StringComparison.Ordinal) ||
            !tokens[6].Equals("-p:NuGetAuditMode=all", StringComparison.Ordinal) ||
            !tokens[7].Equals("-p:NuGetAuditLevel=low", StringComparison.Ordinal) ||
            !tokens[8].Equals("-p:TreatWarningsAsErrors=true", StringComparison.Ordinal))
        {
            return false;
        }

        solutionPath = tokens[2];
        return true;
    }

    private static bool IsExactFormatStep(WorkflowStep step, string solutionPath)
    {
        return HasExactDirectCommand(
            step,
            "dotnet", "format", solutionPath, "--verify-no-changes", "--no-restore", "--verbosity", "minimal");
    }

    private static bool IsExactBuildStep(WorkflowStep step, string solutionPath)
    {
        return HasExactDirectCommand(
            step,
            "dotnet", "build", solutionPath, "--configuration", "Release", "--no-restore", "--nologo", "-p:UseSharedCompilation=false");
    }

    private static bool IsExactTestStep(WorkflowStep step, string solutionPath)
    {
        return HasExactDirectCommand(
            step,
            "dotnet", "test", solutionPath, "--configuration", "Release", "--no-build", "--no-restore", "--nologo", "--logger", "console;verbosity=minimal");
    }

    private static bool IsExactPackStep(WorkflowStep step)
    {
        if (!TryGetDirectCommandTokens(step, out var tokens) ||
            tokens.Length != 15 ||
            !tokens[0].Equals("dotnet", StringComparison.Ordinal) ||
            !tokens[1].Equals("pack", StringComparison.Ordinal) ||
            !IsLiteralRepositoryPath(tokens[2], "csproj"))
        {
            return false;
        }

        return tokens.Skip(3).SequenceEqual(ExactPackArguments, StringComparer.Ordinal);
    }

    private static bool IsExactToolInstallStep(WorkflowStep step)
    {
        return HasExactDirectCommand(
            step,
            "dotnet", "tool", "install", "KeelMatrix.NuGetReady", "--version", SupportedToolVersion,
            "--tool-path", ".nugetready", "--configfile", "NuGet.config", "--add-source", "artifacts/release",
            "--no-cache", "--verbosity", "minimal");
    }

    private static bool IsExactUploadStep(WorkflowStep step, IReadOnlyList<string> expectedArtifacts)
    {
        return UsesExactly(step, "actions/upload-artifact@v4") &&
               IsSimpleActionStep(step, allowId: false) &&
               HasExactInputs(
                   step.With,
                   ("name", "validated-release-artifacts"),
                   ("path", JoinArtifactPaths(expectedArtifacts)),
                   ("if-no-files-found", "error"));
    }

    private static bool IsSimpleActionStep(WorkflowStep step, bool allowId)
    {
        return IsDefinitelyEnabled(step.Condition) &&
               !step.HasUninspectableStructure &&
               string.IsNullOrWhiteSpace(step.Run) &&
               step.Environment.Count == 0 &&
               step.PresentKeys.All(key => key is "name" or "uses" or "with" || allowId && key == "id");
    }

    private static bool IsExactProfileValidationStep(WorkflowStep step)
    {
        return IsDefinitelyEnabled(step.Condition) &&
               string.Equals(step.Shell, "pwsh", StringComparison.Ordinal) &&
               string.Equals(step.Run?.Trim(), InstalledValidationCommand, StringComparison.Ordinal) &&
               step.Environment.Count == 0 &&
               step.PresentKeys.All(key => key is "name" or "shell" or "run");
    }

    private static bool TryGetDirectCommandTokens(WorkflowStep step, out string[] tokens)
    {
        tokens = Array.Empty<string>();
        if (!IsDefinitelyEnabled(step.Condition) ||
            step.HasUninspectableStructure ||
            !string.Equals(step.Shell, "pwsh", StringComparison.Ordinal) ||
            step.Environment.Count != 0 ||
            step.PresentKeys.Any(key => key is not ("name" or "shell" or "run")) ||
            string.IsNullOrWhiteSpace(step.Run))
        {
            return false;
        }

        var command = step.Run.Trim();
        if (HasUnquotedCommandSeparator(command) ||
            command.Contains("${{", StringComparison.Ordinal) ||
            Regex.IsMatch(command, @"(?<!\\)\$[A-Za-z_]|%[A-Za-z_][A-Za-z0-9_]*%", RegexOptions.CultureInvariant))
        {
            return false;
        }

        tokens = TokenizeCommandLine(command);
        return tokens.Length > 0;
    }

    private static bool HasUnquotedCommandSeparator(string command)
    {
        var quote = '\0';
        foreach (var character in command)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character is '\r' or '\n' or ';' or '|' or '&' or '`')
            {
                return true;
            }
        }

        return quote != '\0';
    }

    private static bool HasExactDirectCommand(WorkflowStep step, params string[] expectedTokens)
    {
        return TryGetDirectCommandTokens(step, out var tokens) &&
               tokens.SequenceEqual(expectedTokens, StringComparer.Ordinal);
    }

    private static bool IsLiteralRepositoryPath(string value, params string[] extensions)
    {
        if (Path.IsPathRooted(value) || value.Contains('\\', StringComparison.Ordinal) || ContainsExpression(value))
        {
            return false;
        }

        var segments = value.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
                                    !Regex.IsMatch(segment, "^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)))
        {
            return false;
        }

        return extensions.Any(extension => value.EndsWith($".{extension}", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasSupportedNuGetConfiguration(string repositoryPath)
    {
        try
        {
            var path = Path.Combine(repositoryPath, "NuGet.config");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 32 * 1024)
            {
                return false;
            }

            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
            using var reader = XmlReader.Create(path, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;
            if (root is null || root.Name != "configuration" || root.Attributes().Any())
            {
                return false;
            }

            var rootElements = root.Elements().ToArray();
            if (rootElements.Length != 1 || rootElements[0].Name != "packageSources" || rootElements[0].Attributes().Any())
            {
                return false;
            }

            var sources = rootElements[0].Elements().ToArray();
            if (sources.Length != 2 || sources[0].Name != "clear" || sources[0].HasAttributes || sources[0].HasElements ||
                sources[1].Name != "add" || sources[1].HasElements ||
                sources[1].Attributes().Any(attribute => !string.IsNullOrEmpty(attribute.Name.NamespaceName)))
            {
                return false;
            }

            var attributes = sources[1].Attributes().ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value, StringComparer.Ordinal);
            return attributes.Count == 3 &&
                   attributes.TryGetValue("key", out var key) && key == "nuget.org" &&
                   attributes.TryGetValue("value", out var value) && value == "https://api.nuget.org/v3/index.json" &&
                   attributes.TryGetValue("protocolVersion", out var protocol) && protocol == "3";
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (XmlException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string JoinArtifactPaths(IReadOnlyList<string> expectedArtifacts)
    {
        return string.Join('\n', expectedArtifacts.Select(artifact => $"artifacts/release/{artifact}"));
    }

    private static bool InspectUnsupportedPublicationPaths(
        string repositoryPath,
        WorkflowDocument workflow,
        List<Failure> failures,
        IndirectInspectionContext context)
    {
        var foundUnsupportedPath = false;
        foreach (var job in workflow.Jobs)
        {
            if (!IsExplicitlyDisabled(job.Condition) && !string.IsNullOrWhiteSpace(job.Uses))
            {
                failures.Add(new Failure(
                    "workflow-policy",
                    "Release workflow contains an unsupported reusable workflow job; package publication policy is unsupported/unproven.",
                    IsError: true));
                foundUnsupportedPath = true;
            }

            foreach (var step in job.Steps)
            {
                if (IsExplicitlyDisabled(step.Condition))
                {
                    continue;
                }

                var indirectPath = InspectIndirectPublicationPath(repositoryPath, step, context);
                if (indirectPath is null || indirectPath == IndirectPublicationPath.ProvenNonPublishing)
                {
                    continue;
                }

                var message = IsLocalActionReference(step.Uses)
                    ? "Release workflow uses a composite action publication path; package publication policy is unsupported/unproven."
                    : IsUnallowlistedRemoteAction(step)
                        ? "Release workflow uses an unallowlisted remote action; package publication policy is unsupported/unproven."
                    : !string.IsNullOrWhiteSpace(step.Uses)
                        ? "Release workflow uses an opaque publication action; package publication policy is unsupported/unproven."
                        : "Release workflow uses a referenced script publication path; package publication policy is unsupported/unproven.";
                failures.Add(Unsupported(message));
                foundUnsupportedPath = true;
            }
        }

        return foundUnsupportedPath;
    }

    private static bool HasIndirectPublicationPath(string repositoryPath, WorkflowStep step)
    {
        var result = InspectIndirectPublicationPath(repositoryPath, step, new IndirectInspectionContext());
        return result is not null && result != IndirectPublicationPath.ProvenNonPublishing;
    }

    private static IndirectPublicationPath? InspectIndirectPublicationPath(
        string repositoryPath,
        WorkflowStep step,
        IndirectInspectionContext context,
        int depth = 0)
    {
        if (IsExplicitlyDisabled(step.Condition))
        {
            return null;
        }

        if (depth > MaxIndirectPathDepth)
        {
            return IndirectPublicationPath.Unknown;
        }

        string repositoryRoot;
        string? workingDirectory;
        try
        {
            repositoryRoot = Path.GetFullPath(repositoryPath);
            workingDirectory = ResolveWorkingDirectory(repositoryRoot, step.WorkingDirectory);
        }
        catch (ArgumentException)
        {
            return IndirectPublicationPath.Unknown;
        }

        var result = ContainsExecutablePublicationCommand(step.Run) || ContainsExecutableReleasePublicationCommand(step.Run)
            ? null
            : InspectCommandContent(repositoryRoot, workingDirectory, step.Run, context, depth);
        if (result == IndirectPublicationPath.Publication)
        {
            return result;
        }

        if (IsLocalActionReference(step.Uses))
        {
            var compositeResult = InspectLocalCompositeAction(repositoryRoot, step.Uses!, context, depth + 1);
            if (compositeResult == IndirectPublicationPath.Publication)
            {
                return compositeResult;
            }

            if (compositeResult == IndirectPublicationPath.Unknown)
            {
                result = compositeResult;
            }
            else if (compositeResult == IndirectPublicationPath.ProvenNonPublishing && result is null)
            {
                result = compositeResult;
            }
        }

        if (IsUnallowlistedRemoteAction(step))
        {
            return IndirectPublicationPath.Unknown;
        }

        return result;
    }

    private static IndirectPublicationPath InspectReferencedScript(
        string repositoryRoot,
        string workingDirectory,
        string reference,
        IndirectInspectionContext context,
        int depth)
    {
        if (depth > MaxIndirectPathDepth || IsDerivedPathToken(reference))
        {
            return IndirectPublicationPath.Unknown;
        }

        string scriptPath;
        try
        {
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

        if (context.ScriptResults.TryGetValue(scriptPath, out var cachedResult))
        {
            return cachedResult;
        }

        if (!context.ActiveScripts.Add(scriptPath))
        {
            return IndirectPublicationPath.Unknown;
        }

        try
        {
            if (!context.TryReadText(scriptPath, out var content))
            {
                return CacheScriptResult(context, scriptPath, IndirectPublicationPath.Unknown);
            }

            var result = ContainsExecutablePublicationCommand(content)
                ? IndirectPublicationPath.Publication
                : InspectCommandContent(
                    repositoryRoot,
                    workingDirectory,
                    content,
                    context,
                    depth,
                    Path.GetDirectoryName(scriptPath));
            return CacheScriptResult(context, scriptPath, result ?? IndirectPublicationPath.ProvenNonPublishing);
        }
        finally
        {
            context.ActiveScripts.Remove(scriptPath);
        }
    }

    private static IndirectPublicationPath CacheScriptResult(
        IndirectInspectionContext context,
        string scriptPath,
        IndirectPublicationPath result)
    {
        context.ScriptResults[scriptPath] = result;
        return result;
    }

    private static IndirectPublicationPath InspectLocalCompositeAction(
        string repositoryRoot,
        string uses,
        IndirectInspectionContext context,
        int depth)
    {
        if (depth > MaxIndirectPathDepth)
        {
            return IndirectPublicationPath.Unknown;
        }

        string actionDirectory;
        try
        {
            actionDirectory = Path.GetFullPath(Path.Combine(
                repositoryRoot,
                uses[2..].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            if (!IsWithinRepository(repositoryRoot, actionDirectory))
            {
                return IndirectPublicationPath.Unknown;
            }
        }
        catch (ArgumentException)
        {
            return IndirectPublicationPath.Unknown;
        }

        if (context.CompositeResults.TryGetValue(actionDirectory, out var cachedResult))
        {
            return cachedResult;
        }

        if (!context.ActiveComposites.Add(actionDirectory))
        {
            return IndirectPublicationPath.Unknown;
        }

        try
        {
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

            if (metadataPath is null || !context.TryReadText(metadataPath, out var metadata))
            {
                return CacheCompositeResult(context, actionDirectory, IndirectPublicationPath.Unknown);
            }

            var action = SupportedYaml.ParseCompositeAction(metadata);
            if (!action.IsComposite || !action.HasInspectableSteps)
            {
                return CacheCompositeResult(context, actionDirectory, IndirectPublicationPath.Unknown);
            }

            var result = IndirectPublicationPath.ProvenNonPublishing;
            foreach (var step in action.Steps)
            {
                if (IsExplicitlyDisabled(step.Condition))
                {
                    continue;
                }

                var nested = ContainsExecutablePublicationCommand(step.Run) || ContainsExecutableReleasePublicationCommand(step.Run)
                    ? IndirectPublicationPath.Publication
                    : InspectIndirectPublicationPath(repositoryRoot, step, context, depth);
                if (nested == IndirectPublicationPath.Publication)
                {
                    return CacheCompositeResult(context, actionDirectory, nested.Value);
                }

                if (nested == IndirectPublicationPath.Unknown)
                {
                    result = IndirectPublicationPath.Unknown;
                }
            }

            return CacheCompositeResult(context, actionDirectory, result);
        }
        catch (IOException)
        {
            return CacheCompositeResult(context, actionDirectory, IndirectPublicationPath.Unknown);
        }
        catch (UnauthorizedAccessException)
        {
            return CacheCompositeResult(context, actionDirectory, IndirectPublicationPath.Unknown);
        }
        finally
        {
            context.ActiveComposites.Remove(actionDirectory);
        }
    }

    private static IndirectPublicationPath CacheCompositeResult(
        IndirectInspectionContext context,
        string actionDirectory,
        IndirectPublicationPath result)
    {
        context.CompositeResults[actionDirectory] = result;
        return result;
    }

    private static bool IsWithinRepository(string repositoryRoot, string candidate)
    {
        return candidate.Equals(repositoryRoot, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveWorkingDirectory(string repositoryRoot, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return repositoryRoot;
        }

        if (ContainsExpression(workingDirectory) || IsDerivedPathToken(workingDirectory))
        {
            return null;
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

    private static bool IsUnallowlistedRemoteAction(WorkflowStep step)
    {
        return !IsLocalActionReference(step.Uses) &&
               !string.IsNullOrWhiteSpace(step.Uses) &&
               !IsAllowlistedRemoteAction(step.Uses!);
    }

    private static bool IsAllowlistedRemoteAction(string uses)
    {
        var separator = uses.IndexOf('@');
        if (separator <= 0)
        {
            return false;
        }

        var action = uses[..separator];
        return action.Equals("actions/checkout", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("actions/setup-dotnet", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("actions/upload-artifact", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("actions/download-artifact", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("NuGet/login", StringComparison.OrdinalIgnoreCase);
    }

    private enum IndirectPublicationPath
    {
        ProvenNonPublishing,
        Publication,
        Unknown
    }

    private sealed record CommandAnalysis(
        IReadOnlyList<ScriptReference> ScriptReferences,
        IReadOnlyList<string> InnerCommands,
        bool IsUnresolved,
        bool IsSafeCommand)
    {
        public static CommandAnalysis Empty { get; } = new(Array.Empty<ScriptReference>(), Array.Empty<string>(), false, false);

        public static CommandAnalysis Unresolved { get; } = new(Array.Empty<ScriptReference>(), Array.Empty<string>(), true, false);

        public static CommandAnalysis Safe { get; } = new(Array.Empty<ScriptReference>(), Array.Empty<string>(), false, true);
    }

    private sealed record ScriptReference(string Path, bool RelativeToCurrentScript);

    private sealed class IndirectInspectionContext
    {
        public HashSet<string> ActiveScripts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, IndirectPublicationPath> ScriptResults { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> SafePowerShellFunctions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ActiveComposites { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, IndirectPublicationPath> CompositeResults { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long BytesRead { get; private set; }

        public bool TryReadText(string path, out string content)
        {
            content = string.Empty;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > MaxWorkflowBytes || BytesRead + file.Length > MaxIndirectPathBytes)
                {
                    return false;
                }

                content = File.ReadAllText(path);
                BytesRead += file.Length;
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

    private static bool ContainsLongLivedCredential(string? run)
    {
        return run is not null && Regex.IsMatch(
            run,
            @"\bsecrets\s*(?:\.\s*[A-Za-z_][A-Za-z0-9_-]*|\[\s*(?:'[^']+'|""[^""]+""|[A-Za-z_][A-Za-z0-9_-]*)\s*\])",
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

        foreach (var rawLine in SplitCommandSegments(content))
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

        foreach (var rawLine in SplitCommandSegments(content))
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

    private static IndirectPublicationPath? InspectCommandContent(
        string repositoryRoot,
        string? workingDirectory,
        string? content,
        IndirectInspectionContext context,
        int depth,
        string? scriptDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        if (depth > MaxIndirectPathDepth || workingDirectory is null)
        {
            return IndirectPublicationPath.Unknown;
        }

        if (ContainsExecutablePublicationCommand(content))
        {
            return IndirectPublicationPath.Publication;
        }

        var result = IndirectPublicationPath.ProvenNonPublishing;
        var inspected = false;
        var safePowerShellFunctions = FindSafePowerShellFunctions(content, context.SafePowerShellFunctions);
        foreach (var rawLine in SplitCommandSegments(content))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || IsOutputOnlyCommand(line))
            {
                continue;
            }

            var analysis = AnalyzeCommandLine(line, safePowerShellFunctions);
            if (analysis.IsSafeCommand || analysis.IsUnresolved)
            {
                inspected = true;
                if (analysis.IsUnresolved)
                {
                    result = IndirectPublicationPath.Unknown;
                }
            }

            foreach (var innerCommand in analysis.InnerCommands)
            {
                inspected = true;
                var innerResult = InspectCommandContent(repositoryRoot, workingDirectory, innerCommand, context, depth + 1, scriptDirectory);
                if (innerResult == IndirectPublicationPath.Publication)
                {
                    return innerResult;
                }

                if (innerResult == IndirectPublicationPath.Unknown)
                {
                    result = innerResult.Value;
                }
            }

            foreach (var reference in analysis.ScriptReferences)
            {
                inspected = true;
                var referenceDirectory = reference.RelativeToCurrentScript && scriptDirectory is not null
                    ? scriptDirectory
                    : workingDirectory;
                var scriptResult = referenceDirectory is null
                    ? IndirectPublicationPath.Unknown
                    : InspectReferencedScript(repositoryRoot, referenceDirectory, reference.Path, context, depth + 1);
                if (scriptResult == IndirectPublicationPath.Publication)
                {
                    return scriptResult;
                }

                if (scriptResult == IndirectPublicationPath.Unknown)
                {
                    result = scriptResult;
                }

                if (scriptResult == IndirectPublicationPath.ProvenNonPublishing)
                {
                    safePowerShellFunctions = FindSafePowerShellFunctions(content, context.SafePowerShellFunctions);
                }
            }
        }

        if (result == IndirectPublicationPath.ProvenNonPublishing)
        {
            context.SafePowerShellFunctions.UnionWith(safePowerShellFunctions);
        }

        return inspected ? result : null;
    }

    private static List<string> SplitCommandSegments(string content)
    {
        var segments = new List<string>();
        var segment = new StringBuilder();
        var quote = '\0';

        void Flush()
        {
            if (segment.Length > 0)
            {
                segments.Add(segment.ToString());
                segment.Clear();
            }
        }

        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (quote != '\0')
            {
                segment.Append(character);
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                segment.Append(character);
            }
            else if (character == '`' && index + 1 < content.Length && content[index + 1] is '\r' or '\n')
            {
                if (content[index + 1] == '\r' && index + 2 < content.Length && content[index + 2] == '\n')
                {
                    index += 2;
                }
                else
                {
                    index++;
                }

                segment.Append(' ');
            }
            else if (character is '\r' or '\n' or ';')
            {
                Flush();
            }
            else if ((character == '&' && index + 1 < content.Length && content[index + 1] == '&') ||
                     (character == '|' && index + 1 < content.Length && content[index + 1] == '|') ||
                     character == '|')
            {
                Flush();
                index++;
            }
            else
            {
                segment.Append(character);
            }
        }

        Flush();
        return segments;
    }

    private static CommandAnalysis AnalyzeCommandLine(string line, HashSet<string>? safePowerShellFunctions = null)
    {
        var tokens = TokenizeCommandLine(line);
        if (tokens.Length == 0)
        {
            return CommandAnalysis.Empty;
        }

        var commandIndex = tokens[0].Equals("&", StringComparison.Ordinal) ? 1 : 0;
        if (commandIndex >= tokens.Length)
        {
            return CommandAnalysis.Unresolved;
        }

        var command = tokens[commandIndex].TrimStart('&');
        if (IsScriptInterpreter(command))
        {
            for (var index = commandIndex + 1; index < tokens.Length; index++)
            {
                var token = tokens[index];
                if (IsCommandWrapperOption(token))
                {
                    if (index + 1 >= tokens.Length)
                    {
                        return CommandAnalysis.Unresolved;
                    }

                    var innerCommand = string.Join(' ', tokens.Skip(index + 1));
                    return IsDerivedPathToken(innerCommand)
                        ? CommandAnalysis.Unresolved
                        : new CommandAnalysis(Array.Empty<ScriptReference>(), new[] { innerCommand }, false, false);
                }

                if (IsEncodedCommandOption(token))
                {
                    return CommandAnalysis.Unresolved;
                }

                if (token.Equals("-File", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("-f", StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 >= tokens.Length || IsDerivedPathToken(tokens[index + 1]))
                    {
                        return CommandAnalysis.Unresolved;
                    }

                    var reference = tokens[index + 1];
                    return reference.StartsWith('-')
                        ? CommandAnalysis.Unresolved
                         : new CommandAnalysis(new[] { new ScriptReference(reference, false) }, Array.Empty<string>(), false, false);
                }

                if (IsDerivedPathToken(token))
                {
                    return CommandAnalysis.Unresolved;
                }

                if (!token.StartsWith('-') && !token.StartsWith('/'))
                {
                    return new CommandAnalysis(new[] { new ScriptReference(token, false) }, Array.Empty<string>(), false, false);
                }
            }

            return CommandAnalysis.Empty;
        }

        if (command.Equals(".", StringComparison.Ordinal))
        {
            var sourcedScript = Regex.Match(
                line,
                @"^\s*\.\s*\(\s*Join-Path\s+\$PSScriptRoot\s+[""'](?<path>[^""']+)[""']\s*\)",
                RegexOptions.IgnoreCase);
            if (sourcedScript.Success)
            {
                return new CommandAnalysis(
                    new[] { new ScriptReference(sourcedScript.Groups["path"].Value, RelativeToCurrentScript: true) },
                    Array.Empty<string>(),
                    false,
                    false);
            }

            var relativeScript = tokens
                .Skip(commandIndex + 1)
                .FirstOrDefault(token => IsScriptPathToken(token));
            if (tokens.Any(token => token.Equals("$PSScriptRoot", StringComparison.OrdinalIgnoreCase)) &&
                relativeScript is not null)
            {
                return new CommandAnalysis(
                    new[] { new ScriptReference(relativeScript, RelativeToCurrentScript: true) },
                    Array.Empty<string>(),
                    false,
                    false);
            }

            return CommandAnalysis.Unresolved;
        }

        if (IsSafeCommand(command, tokens, commandIndex) ||
            safePowerShellFunctions?.Contains(command) == true ||
            IsSafePowerShellStatement(line, safePowerShellFunctions))
        {
            return CommandAnalysis.Safe;
        }

        if (IsDerivedPathToken(command))
        {
            return CommandAnalysis.Unresolved;
        }

        for (var index = commandIndex + 1; index < tokens.Length; index++)
        {
            var argument = tokens[index];
            if (IsDerivedScriptToken(argument))
            {
                return CommandAnalysis.Unresolved;
            }

        }

        return IsScriptPathToken(command)
            ? new CommandAnalysis(new[] { new ScriptReference(command, false) }, Array.Empty<string>(), false, false)
            : CommandAnalysis.Unresolved;
    }

    private static HashSet<string> FindSafePowerShellFunctions(
        string content,
        HashSet<string>? inheritedSafePowerShellFunctions = null)
    {
        var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
                     content,
                     @"(?im)^\s*function\s+(?<name>[A-Za-z_][A-Za-z0-9-]*)\b[^\r\n{]*\{"))
        {
            var bodyStart = match.Index + match.Length;
            var depth = 1;
            var bodyEnd = bodyStart;
            for (; bodyEnd < content.Length && depth > 0; bodyEnd++)
            {
                if (content[bodyEnd] == '{')
                {
                    depth++;
                }
                else if (content[bodyEnd] == '}')
                {
                    depth--;
                }
            }

            if (depth == 0)
            {
                definitions[match.Groups["name"].Value] = content[bodyStart..(bodyEnd - 1)];
            }
        }

        var safe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (inheritedSafePowerShellFunctions is not null)
        {
            safe.UnionWith(inheritedSafePowerShellFunctions);
        }
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var definition in definitions)
            {
                if (safe.Contains(definition.Key) ||
                    ContainsExecutablePublicationCommand(definition.Value) ||
                    ContainsExecutableReleasePublicationCommand(definition.Value))
                {
                    continue;
                }

                var proven = true;
                foreach (var rawLine in SplitCommandSegments(definition.Value))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith('#') || IsOutputOnlyCommand(line))
                    {
                        continue;
                    }

                    var analysis = AnalyzeCommandLine(line, safe);
                    if (analysis.IsUnresolved)
                    {
                        proven = false;
                        break;
                    }
                }

                if (proven)
                {
                    safe.Add(definition.Key);
                    changed = true;
                }
            }
        }

        return safe;
    }

    private static bool IsSafePowerShellStatement(string line, HashSet<string>? safePowerShellFunctions)
    {
        if (Regex.IsMatch(line, @"^\s*[""']", RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (Regex.IsMatch(line, @"^\s*@\{", RegexOptions.IgnoreCase))
        {
            return !Regex.IsMatch(
                line,
                @"(?i)\b(?:dotnet\s+nuget(?:\.exe)?\s+push|nuget(?:\.exe)?\s+push|gh\s+release\s+create|(?:curl|wget|Invoke-WebRequest|Invoke-RestMethod|Start-Process))\b");
        }

        if (Regex.IsMatch(
                line,
                @"(?i)\b(?:dotnet\s+nuget(?:\.exe)?\s+push|nuget(?:\.exe)?\s+push|gh\s+release\s+create|(?:curl|wget|Invoke-WebRequest|Invoke-RestMethod|Start-Process))\b"))
        {
            return false;
        }

        if (Regex.IsMatch(line, @"^\s*\$[A-Za-z_][A-Za-z0-9_:.-]*(?:\[[^\]]+\])?\s*(?:\+=|-=|=)\s*", RegexOptions.IgnoreCase))
        {
            foreach (Match match in Regex.Matches(
                         line,
                         @"(?:\$\(\s*|=\s*\(\s*|\]\s*\(\s*)(?<command>[A-Za-z_][A-Za-z0-9-]*)\b",
                         RegexOptions.IgnoreCase))
            {
                if (!IsSafeCommand(match.Groups["command"].Value, new[] { match.Groups["command"].Value }, 0) &&
                    safePowerShellFunctions?.Contains(match.Groups["command"].Value) != true)
                {
                    return false;
                }
            }

            return !line.Contains("$({", StringComparison.Ordinal) &&
                   !line.Contains("& ", StringComparison.Ordinal) &&
                   !Regex.IsMatch(line, @"(?i)\b(?:System\.Diagnostics\.Process|Invoke-Expression|Invoke-Command|Start-Job|Start-ThreadJob)\b|::Start\s*\(|\.(?:Start|Invoke|Execute|Publish|Push|Run)\s*\(");
        }

        if (Regex.IsMatch(line, @"^\s*[A-Za-z_][A-Za-z0-9_-]*\s*=\s*", RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (Regex.IsMatch(line, @"^\s*(?:Select-Object|Sort-Object)\b", RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (Regex.IsMatch(line, @"^\s*\$[A-Za-z_][A-Za-z0-9_]*\s*(?:-lt|-le|-gt|-ge|-eq|-ne|\+\+|--|,|\)|\})", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(line, @"^\s*\(\s*\$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(line, @"^\s*\$[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9-]*\s*\(", RegexOptions.IgnoreCase))
        {
            return !Regex.IsMatch(line, @"(?i)\.(?:Start|Invoke|Execute|Publish|Push|Run)\s*\(");
        }

        var structuralStatement = Regex.IsMatch(
            line,
            @"^\s*(?:\[.*\]|Set-StrictMode\b|param\b|function\b|if\b|elseif\b|else\b|foreach\b|for\b|while\b|switch\b|case\b|default\b|try\b|catch\b|finally\b|begin\b|process\b|end\b|throw\b|return\b|continue\b|break\b|\{|\})",
            RegexOptions.IgnoreCase);
        if (!structuralStatement)
        {
            return false;
        }

        foreach (var body in FindInlineBraceBodies(line))
        {
            foreach (var segment in SplitCommandSegments(body))
            {
                var nested = segment.Trim();
                if (nested.Length == 0 || nested.StartsWith('#') || IsOutputOnlyCommand(nested))
                {
                    continue;
                }

                if (AnalyzeCommandLine(nested, safePowerShellFunctions).IsUnresolved)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static List<string> FindInlineBraceBodies(string line)
    {
        var bodies = new List<string>();
        var quote = '\0';
        var depth = 0;
        var bodyStart = -1;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (character == '`' && index + 1 < line.Length)
                {
                    index++;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '{')
            {
                if (depth++ == 0)
                {
                    bodyStart = index + 1;
                }
            }
            else if (character == '}' && depth > 0 && --depth == 0)
            {
                bodies.Add(line[bodyStart..index]);
                bodyStart = -1;
            }
        }

        return bodies;
    }

    private static bool IsSafeCommand(string command, string[] tokens, int commandIndex)
    {
        var normalizedCommand = command.TrimStart('&');
        if (normalizedCommand.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            normalizedCommand.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (commandIndex + 1 >= tokens.Length)
            {
                return false;
            }

            var subcommand = tokens[commandIndex + 1];
            return subcommand.Equals("restore", StringComparison.OrdinalIgnoreCase) ||
                   subcommand.Equals("build", StringComparison.OrdinalIgnoreCase) ||
                   subcommand.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                   subcommand.Equals("pack", StringComparison.OrdinalIgnoreCase) ||
                   subcommand.Equals("format", StringComparison.OrdinalIgnoreCase) ||
                   subcommand.Equals("list", StringComparison.OrdinalIgnoreCase) ||
                   subcommand.Equals("tool", StringComparison.OrdinalIgnoreCase) ||
                   subcommand.Equals("nugetready", StringComparison.OrdinalIgnoreCase) ||
                   (subcommand.Equals("run", StringComparison.OrdinalIgnoreCase) && IsNuGetReadyCheckInvocation(tokens, commandIndex));
        }

        if (normalizedCommand.Equals("nugetready", StringComparison.OrdinalIgnoreCase) ||
            normalizedCommand.Equals("nugetready.exe", StringComparison.OrdinalIgnoreCase))
        {
            return commandIndex + 1 < tokens.Length &&
                   tokens[commandIndex + 1].Equals("check", StringComparison.OrdinalIgnoreCase);
        }

        if (normalizedCommand.Equals("./.nugetready/nugetready", StringComparison.Ordinal))
        {
            return tokens.Skip(commandIndex).SequenceEqual(InstalledValidationTokens, StringComparer.Ordinal);
        }

        return IsSupportedReadOnlyGitCommand(normalizedCommand, tokens, commandIndex) ||
               normalizedCommand.Equals("echo", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("printf", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("cat", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("cp", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("copy", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("mkdir", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("md", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("mv", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("move", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("rm", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("del", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("rmdir", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("touch", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("pwd", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("test", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Get-ChildItem", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Get-Content", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Get-Item", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Join-Path", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Resolve-Path", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Set-Content", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Test-Path", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("New-Item", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Remove-Item", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Copy-Item", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Move-Item", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Get-Location", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Push-Location", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Pop-Location", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("ConvertFrom-Json", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("ConvertTo-Json", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Write-Host", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Write-Output", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Select-String", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Compare-Object", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("ForEach-Object", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Where-Object", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Sort-Object", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Out-Null", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.Equals("Split-Path", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedReadOnlyGitCommand(string command, string[] tokens, int commandIndex)
    {
        if (!command.Equals("git", StringComparison.OrdinalIgnoreCase) &&
            !command.Equals("git.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var argumentCount = tokens.Length - commandIndex - 1;
        return (argumentCount == 1 && tokens[commandIndex + 1].Equals("status", StringComparison.OrdinalIgnoreCase)) ||
               (argumentCount == 2 &&
                tokens[commandIndex + 1].Equals("status", StringComparison.OrdinalIgnoreCase) &&
                tokens[commandIndex + 2].Equals("--short", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNuGetReadyCheckInvocation(string[] tokens, int commandIndex)
    {
        var hasNuGetReadyProject = false;
        var hasCheckCommand = false;
        for (var index = commandIndex + 2; index < tokens.Length; index++)
        {
            if (tokens[index].Contains("KeelMatrix.NuGetReady", StringComparison.OrdinalIgnoreCase) &&
                tokens[index].EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                hasNuGetReadyProject = true;
            }

            if (tokens[index].Equals("check", StringComparison.OrdinalIgnoreCase))
            {
                hasCheckCommand = true;
            }
        }

        return hasNuGetReadyProject && hasCheckCommand;
    }

    private static bool IsCommandWrapperOption(string token)
    {
        return token.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("--command", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("-Command", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("/c", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEncodedCommandOption(string token)
    {
        return token.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("-Encoded", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("-e", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDerivedPathToken(string token)
    {
        return ContainsExpression(token) ||
               Regex.IsMatch(token, @"(?<!\\)\$[A-Za-z_][A-Za-z0-9_]*|%[A-Za-z_][A-Za-z0-9_]*%", RegexOptions.IgnoreCase);
    }

    private static bool IsDerivedScriptToken(string token)
    {
        return IsDerivedPathToken(token) &&
               Regex.IsMatch(token, "(?i)(script[_-]?(?:path|file)|inputs\\.script)");
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
        if (normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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
        return token.Trim().Trim('`', ',', ';', '|', '&', '(', ')').Trim();
    }

    private static bool IsOutputOnlyCommand(string line)
    {
        return Regex.IsMatch(line, @"^(?:echo|printf|write-output|write-host)\b", RegexOptions.IgnoreCase);
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
        return IsEnabled(environment, "KEELMATRIX_NO_TELEMETRY") &&
               IsEnabled(environment, "DOTNET_CLI_TELEMETRY_OPTOUT");
    }

    private static bool IsEnabled(IReadOnlyDictionary<string, string> environment, string name)
    {
        return environment.TryGetValue(name, out var value) &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase));
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
        public HashSet<string> PresentKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<WorkflowJob> Jobs { get; } = new();
    }

    private sealed class WorkflowJob
    {
        public WorkflowJob(string id) => Id = id;
        public string Id { get; }
        public string? Name { get; set; }
        public string? Uses { get; set; }
        public string? Condition { get; set; }
        public string? RunsOn { get; set; }
        public string? TimeoutMinutes { get; set; }
        public bool HasUninspectableStructure { get; set; }
        public bool PermissionsSpecified { get; set; }
        public HashSet<string> PresentKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DependsOn { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Permissions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<WorkflowStep> Steps { get; } = new();
    }

    private sealed class WorkflowStep
    {
        public string? Name { get; set; }
        public string? Id { get; set; }
        public string? Uses { get; set; }
        public string? Run { get; set; }
        public string? Condition { get; set; }
        public string? Shell { get; set; }
        public string? WorkingDirectory { get; set; }
        public bool HasUninspectableStructure { get; set; }
        public bool PermissionsSpecified { get; set; }
        public HashSet<string> PresentKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Permissions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> With { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static class SupportedYaml
    {
        private static readonly HashSet<string> SupportedTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "name", "run-name", "on", "permissions", "env", "defaults", "concurrency", "jobs"
        };

        public static CompositeActionDocument ParseCompositeAction(string content)
        {
            var action = new CompositeActionDocument();
            if (!TryLoadRoot(content, out var root, out var unsupported) || root is null)
            {
                return action;
            }

            if (unsupported || !TryGet(root, "runs", out var runsNode) || runsNode is not YamlMappingNode runs)
            {
                return action;
            }

            action.IsComposite = TryGetScalar(runs, "using", out var usingValue) &&
                usingValue.Equals("composite", StringComparison.OrdinalIgnoreCase);
            if (!TryGet(runs, "steps", out var stepsNode) || stepsNode is not YamlSequenceNode steps)
            {
                return action;
            }

            action.HasInspectableSteps = true;
            foreach (var stepNode in steps.Children)
            {
                if (stepNode is not YamlMappingNode stepMapping)
                {
                    action.HasInspectableSteps = false;
                    continue;
                }

                var step = ParseStep(stepMapping);
                action.Steps.Add(step);
                action.HasInspectableSteps &= !step.HasUninspectableStructure;
            }

            return action;
        }

        public static WorkflowDocument Parse(string content)
        {
            var workflow = new WorkflowDocument();
            if (!TryLoadRoot(content, out var root, out var unsupported) || root is null)
            {
                workflow.HasUninspectableStructure = true;
                return workflow;
            }

            workflow.HasUninspectableStructure = unsupported;
            foreach (var pair in root.Children)
            {
                if (!TryScalar(pair.Key, out var key))
                {
                    workflow.HasUninspectableStructure = true;
                    continue;
                }

                if (!SupportedTopLevelKeys.Contains(key))
                {
                    workflow.HasUninspectableStructure = true;
                    continue;
                }

                if (!workflow.PresentKeys.Add(key))
                {
                    workflow.HasUninspectableStructure = true;
                }

                switch (key.ToLowerInvariant())
                {
                    case "name":
                        if (TryScalar(pair.Value, out var name))
                        {
                            workflow.Name = name;
                        }
                        else
                        {
                            workflow.HasUninspectableStructure = true;
                        }
                        break;
                    case "on":
                        ParseTriggers(pair.Value, workflow);
                        break;
                    case "permissions":
                        workflow.PermissionsSpecified = true;
                        if (!ParseStringMapOrPermission(pair.Value, workflow.Permissions))
                        {
                            workflow.HasUninspectableStructure = true;
                        }
                        break;
                    case "env":
                        if (!ParseStringMap(pair.Value, workflow.Environment))
                        {
                            workflow.HasUninspectableStructure = true;
                        }
                        break;
                    case "jobs":
                        ParseJobs(pair.Value, workflow);
                        break;
                }
            }

            return workflow;
        }

        private static bool TryLoadRoot(string content, out YamlMappingNode? root, out bool unsupported)
        {
            root = null;
            unsupported = false;
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(content));
                if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
                {
                    return false;
                }

                root = mapping;
                unsupported = mapping.AllNodes.Any(node => !node.Anchor.IsEmpty || !node.Tag.IsEmpty) ||
                    mapping.AllNodes.OfType<YamlScalarNode>().Any(node => node.Value == "<<");
                return true;
            }
            catch (YamlException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static void ParseTriggers(YamlNode node, WorkflowDocument workflow)
        {
            if (node is YamlScalarNode scalar)
            {
                var trigger = scalar.Value ?? string.Empty;
                if (trigger.Equals("push", StringComparison.OrdinalIgnoreCase))
                {
                    workflow.HasPushBranchTrigger = true;
                }
                else if (trigger.Length > 0)
                {
                    workflow.HasOtherTrigger = true;
                    workflow.HasPublicationShapedTrigger = trigger.Equals("release", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    workflow.HasUninspectableStructure = true;
                }
                return;
            }

            if (node is YamlSequenceNode triggers)
            {
                foreach (var item in triggers.Children)
                {
                    if (!TryScalar(item, out var trigger))
                    {
                        workflow.HasUninspectableStructure = true;
                    }
                    else if (trigger.Equals("push", StringComparison.OrdinalIgnoreCase))
                    {
                        workflow.HasPushBranchTrigger = true;
                    }
                    else
                    {
                        workflow.HasOtherTrigger = true;
                        workflow.HasPublicationShapedTrigger |= trigger.Equals("release", StringComparison.OrdinalIgnoreCase);
                    }
                }
                return;
            }

            if (node is not YamlMappingNode triggersMap)
            {
                workflow.HasUninspectableStructure = true;
                return;
            }

            foreach (var pair in triggersMap.Children)
            {
                if (!TryScalar(pair.Key, out var trigger))
                {
                    workflow.HasUninspectableStructure = true;
                    continue;
                }

                if (trigger.Equals("push", StringComparison.OrdinalIgnoreCase))
                {
                    ParsePushTrigger(pair.Value, workflow);
                    continue;
                }

                workflow.HasOtherTrigger = true;
                workflow.HasPublicationShapedTrigger |= trigger.Equals("release", StringComparison.OrdinalIgnoreCase);
                if (trigger.Equals("workflow_call", StringComparison.OrdinalIgnoreCase) ||
                    trigger.Equals("workflow_dispatch", StringComparison.OrdinalIgnoreCase))
                {
                    ParseTriggerInputs(pair.Value, workflow);
                }
            }
        }

        private static void ParsePushTrigger(YamlNode node, WorkflowDocument workflow)
        {
            if (node is YamlScalarNode scalar && string.IsNullOrWhiteSpace(scalar.Value))
            {
                workflow.HasPushBranchTrigger = true;
                return;
            }

            if (node is not YamlMappingNode push)
            {
                workflow.HasUninspectableStructure = true;
                return;
            }

            var hasTags = false;
            var hasTagsIgnore = false;
            var hasBranches = false;
            foreach (var pair in push.Children)
            {
                if (!TryScalar(pair.Key, out var key))
                {
                    workflow.HasUninspectableStructure = true;
                    continue;
                }

                switch (key.ToLowerInvariant())
                {
                    case "tags":
                        hasTags = true;
                        if (TryReadScalarSequence(pair.Value, out var tagPatterns))
                        {
                            AddTagPatterns(workflow, tagPatterns);
                        }
                        else
                        {
                            workflow.HasUninspectableStructure = true;
                        }
                        break;
                    case "tags-ignore":
                        hasTagsIgnore = true;
                        workflow.HasUnsupportedTagPattern = true;
                        workflow.HasUninspectableStructure |= !TryReadScalarSequence(pair.Value, out _);
                        break;
                    case "branches":
                    case "branches-ignore":
                        hasBranches = true;
                        workflow.HasUninspectableStructure |= !TryReadScalarSequence(pair.Value, out _);
                        break;
                    case "paths":
                    case "paths-ignore":
                        _ = TryReadScalarSequence(pair.Value, out _);
                        workflow.HasUninspectableStructure = true;
                        break;
                    default:
                        workflow.HasUninspectableStructure = true;
                        break;
                }
            }

            workflow.HasPushBranchTrigger = hasBranches || (!hasTags && !hasTagsIgnore);
        }

        private static void ParseTriggerInputs(YamlNode node, WorkflowDocument workflow)
        {
            if (node is not YamlMappingNode trigger || !TryGet(trigger, "inputs", out var inputsNode) || inputsNode is not YamlMappingNode inputs)
            {
                return;
            }

            foreach (var input in inputs.Children.Keys)
            {
                if (!TryScalar(input, out var inputName))
                {
                    workflow.HasUninspectableStructure = true;
                }
                else if (HasReleasePublicationSignal(inputName))
                {
                    workflow.HasPublicationInput = true;
                }
            }
        }

        private static void ParseJobs(YamlNode node, WorkflowDocument workflow)
        {
            if (node is not YamlMappingNode jobs)
            {
                workflow.HasUninspectableStructure = true;
                return;
            }

            foreach (var pair in jobs.Children)
            {
                if (!TryScalar(pair.Key, out var id) || pair.Value is not YamlMappingNode jobMapping)
                {
                    workflow.HasUninspectableStructure = true;
                    continue;
                }

                var job = new WorkflowJob(id);
                workflow.Jobs.Add(job);
                foreach (var property in jobMapping.Children)
                {
                    if (!TryScalar(property.Key, out var key))
                    {
                        MarkUninspectable(workflow, job);
                        continue;
                    }

                    if (!job.PresentKeys.Add(key))
                    {
                        MarkUninspectable(workflow, job);
                    }

                    switch (key.ToLowerInvariant())
                    {
                        case "name":
                            job.Name = ReadRequiredScalar(property.Value, workflow, job);
                            break;
                        case "uses":
                            job.Uses = ReadRequiredScalar(property.Value, workflow, job);
                            break;
                        case "if":
                            job.Condition = ReadRequiredScalar(property.Value, workflow, job);
                            break;
                        case "runs-on":
                            job.RunsOn = ReadRequiredScalar(property.Value, workflow, job);
                            break;
                        case "timeout-minutes":
                            job.TimeoutMinutes = ReadRequiredScalar(property.Value, workflow, job);
                            break;
                        case "permissions":
                            job.PermissionsSpecified = true;
                            if (!ParseStringMapOrPermission(property.Value, job.Permissions))
                            {
                                MarkUninspectable(workflow, job);
                            }
                            break;
                        case "env":
                            if (!ParseStringMap(property.Value, job.Environment))
                            {
                                MarkUninspectable(workflow, job);
                            }
                            break;
                        case "needs":
                            if (!TryReadScalarSequence(property.Value, out var dependencies))
                            {
                                MarkUninspectable(workflow, job);
                            }
                            foreach (var dependency in dependencies)
                            {
                                job.DependsOn.Add(dependency);
                            }
                            break;
                        case "steps":
                            ParseSteps(property.Value, workflow, job);
                            break;
                        default:
                            if (!IsSupportedJobKey(key))
                            {
                                MarkUninspectable(workflow, job);
                            }
                            break;
                    }
                }
            }
        }

        private static void ParseSteps(YamlNode node, WorkflowDocument workflow, WorkflowJob job)
        {
            if (node is not YamlSequenceNode steps)
            {
                MarkUninspectable(workflow, job);
                return;
            }

            foreach (var nodeItem in steps.Children)
            {
                if (nodeItem is not YamlMappingNode stepMapping)
                {
                    MarkUninspectable(workflow, job);
                    continue;
                }

                var step = ParseStep(stepMapping);
                job.Steps.Add(step);
                if (step.HasUninspectableStructure)
                {
                    MarkUninspectable(workflow, job);
                }
            }
        }

        private static WorkflowStep ParseStep(YamlMappingNode mapping)
        {
            var step = new WorkflowStep();
            foreach (var pair in mapping.Children)
            {
                if (!TryScalar(pair.Key, out var key))
                {
                    step.HasUninspectableStructure = true;
                    continue;
                }

                if (!step.PresentKeys.Add(key))
                {
                    step.HasUninspectableStructure = true;
                }

                switch (key.ToLowerInvariant())
                {
                    case "name":
                        step.Name = ReadStepScalar(pair.Value, step);
                        break;
                    case "id":
                        step.Id = ReadStepScalar(pair.Value, step);
                        break;
                    case "uses":
                        step.Uses = ReadStepScalar(pair.Value, step);
                        break;
                    case "run":
                        step.Run = ReadStepScalar(pair.Value, step);
                        break;
                    case "if":
                        step.Condition = ReadStepScalar(pair.Value, step);
                        break;
                    case "shell":
                        step.Shell = ReadStepScalar(pair.Value, step);
                        break;
                    case "working-directory":
                        step.WorkingDirectory = ReadStepScalar(pair.Value, step);
                        break;
                    case "env":
                        step.HasUninspectableStructure |= !ParseStringMap(pair.Value, step.Environment);
                        break;
                    case "permissions":
                        step.PermissionsSpecified = true;
                        step.HasUninspectableStructure |= !ParseStringMapOrPermission(pair.Value, step.Permissions);
                        break;
                    case "with":
                        step.HasUninspectableStructure |= !ParseStringMap(pair.Value, step.With);
                        break;
                    default:
                        if (!IsSupportedStepKey(key) || pair.Value is not YamlScalarNode)
                        {
                            step.HasUninspectableStructure = true;
                        }
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(step.Run) == string.IsNullOrWhiteSpace(step.Uses))
            {
                step.HasUninspectableStructure = true;
            }

            return step;
        }

        private static string? ReadRequiredScalar(YamlNode node, WorkflowDocument workflow, WorkflowJob job)
        {
            if (TryScalar(node, out var value))
            {
                return value;
            }

            MarkUninspectable(workflow, job);
            return null;
        }

        private static string? ReadStepScalar(YamlNode node, WorkflowStep step)
        {
            if (TryScalar(node, out var value))
            {
                return value;
            }

            step.HasUninspectableStructure = true;
            return null;
        }

        private static bool ParseStringMapOrPermission(YamlNode node, Dictionary<string, string> destination)
        {
            if (node is YamlScalarNode scalar)
            {
                var value = scalar.Value ?? string.Empty;
                if (value is "read-all" or "write-all")
                {
                    destination["__all__"] = value;
                    return true;
                }
                return false;
            }

            return ParseStringMap(node, destination);
        }

        private static bool ParseStringMap(YamlNode node, Dictionary<string, string> destination)
        {
            if (node is not YamlMappingNode mapping)
            {
                return false;
            }

            var valid = true;
            foreach (var pair in mapping.Children)
            {
                if (!TryScalar(pair.Key, out var key) || !TryScalar(pair.Value, out var value) || !destination.TryAdd(key, value))
                {
                    valid = false;
                }
            }
            return valid;
        }

        private static bool TryReadScalarSequence(YamlNode node, out IReadOnlyList<string> values)
        {
            if (TryScalar(node, out var scalar))
            {
                values = new[] { scalar };
                return true;
            }

            if (node is not YamlSequenceNode sequence)
            {
                values = Array.Empty<string>();
                return false;
            }

            var result = new List<string>();
            foreach (var child in sequence.Children)
            {
                if (!TryScalar(child, out var value))
                {
                    values = result;
                    return false;
                }

                result.Add(value);
            }

            values = result;
            return true;
        }

        private static bool TryGet(YamlMappingNode mapping, string name, out YamlNode node)
        {
            foreach (var pair in mapping.Children)
            {
                if (TryScalar(pair.Key, out var key) && key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    node = pair.Value;
                    return true;
                }
            }

            node = null!;
            return false;
        }

        private static bool TryGetScalar(YamlMappingNode mapping, string name, out string value)
        {
            value = string.Empty;
            return TryGet(mapping, name, out var node) && TryScalar(node, out value);
        }

        private static bool TryScalar(YamlNode node, out string value)
        {
            if (node is YamlScalarNode scalar && scalar.Value is not null)
            {
                value = scalar.Value;
                return true;
            }

            value = string.Empty;
            return false;
        }

        private static void MarkUninspectable(WorkflowDocument workflow, WorkflowJob job)
        {
            workflow.HasUninspectableStructure = true;
            job.HasUninspectableStructure = true;
        }

        private static bool IsSupportedJobKey(string key)
        {
            return key.Equals("runs-on", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("strategy", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("continue-on-error", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("timeout-minutes", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("container", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("services", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("outputs", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("defaults", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("concurrency", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("with", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("secrets", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("environment", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSupportedStepKey(string key)
        {
            return key.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("shell", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("continue-on-error", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("timeout-minutes", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddTagPatterns(WorkflowDocument workflow, IEnumerable<string> patterns)
        {
            var values = patterns.ToArray();
            if (values.Length == 0)
            {
                workflow.HasUnsupportedTagPattern = true;
                return;
            }

            foreach (var pattern in values)
            {
                if (pattern.Equals("v*.*.*", StringComparison.OrdinalIgnoreCase))
                {
                    workflow.HasVersionedTagTrigger = true;
                }
                else
                {
                    workflow.HasUnsupportedTagPattern = true;
                }
            }
        }
    }
}
