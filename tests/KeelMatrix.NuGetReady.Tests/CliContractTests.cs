namespace KeelMatrix.NuGetReady.Tests;

public sealed class CliContractTests
{
    [Fact]
    public void Parser_accepts_the_frozen_check_options()
    {
        var options = CliParser.Parse(new[] { "check", "--config", "repo.json", "--artifacts", "packages", "--format", "json", "--timeout", "30s" });

        Assert.Equal("repo.json", options.ConfigPath);
        Assert.Equal("packages", options.ArtifactsPath);
        Assert.Equal(OutputFormat.Json, options.Format);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Fact]
    public void Parser_rejects_unknown_options()
    {
        var exception = Assert.Throws<CliInputException>(() => CliParser.Parse(new[] { "check", "--unknown" }));

        Assert.Contains("Unknown option", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_is_available_without_a_configuration_file()
    {
        var exception = Assert.Throws<HelpRequestedException>(() => CliParser.Parse(new[] { "--help" }));

        Assert.Contains("nugetready check", CliParser.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_documents_supported_hosts_and_runtime_requirements()
    {
        Assert.Contains("Windows, Linux, and macOS", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("targets net8.0 and requires the .NET 8 runtime", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("non-runnable library target frameworks are build-only", CliParser.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_documents_the_conservative_workflow_soundness_boundary()
    {
        Assert.Contains("closed-world 0.1.0 release profile", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("exact tag v<configured version>", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("configured artifact filenames", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("packed nuspec ID/version", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("validated-release-artifacts", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("NuGet/login@v1", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("/tmp/nugetready-tool", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("/tmp/nugetready-tool/nugetready check", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("exclusive source selection", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("fresh-runner validator", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("fixed runner-controlled", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("exact cross-platform casing", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("exact repository-root nugetready.json", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("case-sensitive runtime identity", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("credential-name normalization does not prove a process binding", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("preserve command position, quoting, literal argument bytes, empty arguments, and operators", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("malformed YAML, duplicate keys, invalid roots, or multiple documents are error/exit 2", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("not-applicable requires successfully inspected non-applicability", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("capability-and-reachability boundary", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("dependency and artifact-producer job", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("Omitted effective permissions", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("NUGET_API_KEY, API_KEY, ACCESS_TOKEN, AUTHORIZATION, PASSWORD, SECRET, and CREDENTIAL", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("every non-alphanumeric character is removed", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("compared case-insensitively by exact equality", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("vars.*, inputs.*, and env.*", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("not a complete static member name is unresolvable", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("Static tag filters alone do not create publication reachability", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("bounded expression-evaluated grammar follows GitHub Actions Context availability", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("workflow_call input defaults and output values", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("job name, concurrency, container, continue-on-error, defaults.run", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("outputs, runs-on, secrets, services, strategy, timeout-minutes", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("including local composite-action steps", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("Every scalar leaf in those named mappings and objects is traversed", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("regardless of the referenced context or member name", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("Non-evaluated literals such as workflow name, trigger filters, workflow-level defaults, and unrelated static configuration", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("do not enter publication policy merely because they contain expression-like text", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("before each active step is evaluated", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("literal paths or prose containing the word secrets do not imply the context", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("without a recognized credential binding stays outside", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("error/exit 2", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("warnings never create release confidence", CliParser.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_json_option_value_preserves_the_requested_json_error_contract()
    {
        var exception = Assert.Throws<CliInputException>(() => CliParser.Parse(new[] { "check", "--format", "json", "--config" }));

        Assert.Equal(OutputFormat.Json, exception.Format);
    }

    [Theory]
    [InlineData("automation.yml")]
    [InlineData("broken-release.yml")]
    public void Parsed_workflow_input_errors_are_public_blocking_results(string workflowFileName)
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("Example.Core.1.2.3.nupkg", "Example.Core", "1.2.3");
        using var repository = WorkflowRepository.Create(workflowFileName, "on: [push\nsecret: SUPER_SECRET_SENTINEL");
        var config = new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages =
            [
                new PackageExpectation
                {
                    Id = "Example.Core",
                    Kind = "library",
                    Version = "1.2.3",
                    Artifacts = ["Example.Core.1.2.3.nupkg"]
                }
            ]
        };
        var configPath = Path.Combine(repository.Root.FullName, "nugetready.json");
        File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(config));

        var report = CheckRunner.Run(
            config,
            fixture.ArtifactsPath,
            repository.Root.FullName,
            TimeSpan.FromSeconds(1),
            configPath: configPath);
        var text = ReportWriter.RenderText(report);
        var json = ReportWriter.RenderJson(report);

        Assert.Equal("error", report.Status);
        Assert.Equal(2, report.ExitCode);
        Assert.Equal("error", report.Checks.Single(check => check.Id == "workflow-policy").Status);
        Assert.Contains($".github/workflows/{workflowFileName}", text, StringComparison.Ordinal);
        Assert.Contains($".github/workflows/{workflowFileName}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPER_SECRET_SENTINEL", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPER_SECRET_SENTINEL", json, StringComparison.Ordinal);
    }
}
