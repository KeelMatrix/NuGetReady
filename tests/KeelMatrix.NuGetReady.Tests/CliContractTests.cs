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
    public void Help_documents_the_conservative_workflow_soundness_boundary()
    {
        Assert.Contains("closed-world 0.1.0 release profile", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("validated-release-artifacts", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("NuGet/login@v1", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("/tmp/nugetready-tool", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("/tmp/nugetready-tool/nugetready check", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("exclusive source selection", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("maps only the tool ID", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("fresh-runner validator", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("unknown or unsupported publication-relevant node", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("error/exit 2", CliParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("never defaults to safe", CliParser.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_json_option_value_preserves_the_requested_json_error_contract()
    {
        var exception = Assert.Throws<CliInputException>(() => CliParser.Parse(new[] { "check", "--format", "json", "--config" }));

        Assert.Equal(OutputFormat.Json, exception.Format);
    }
}
