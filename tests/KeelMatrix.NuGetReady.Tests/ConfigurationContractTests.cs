namespace KeelMatrix.NuGetReady.Tests;

public sealed class ConfigurationContractTests
{
    [Fact]
    public void Schema_version_must_be_one()
    {
        using var fixture = PackageFixture.Create();
        var path = WriteConfig(fixture, "{\"schemaVersion\":2,\"packages\":[]}");

        var exception = Assert.Throws<NuGetReadyInputException>(() => ConfigurationLoader.Load(path));

        Assert.Contains("schema version must be 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Publishing_credentials_are_rejected_from_any_object()
    {
        using var fixture = PackageFixture.Create();
        var path = WriteConfig(fixture, "{\"schemaVersion\":1,\"packages\":[],\"publishToken\":\"redacted\"}");

        var exception = Assert.Throws<NuGetReadyInputException>(() => ConfigurationLoader.Load(path));

        Assert.Contains("publishing credentials", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Artifact_names_must_be_exact_package_archive_names()
    {
        using var fixture = PackageFixture.Create();
        var path = WriteConfig(fixture, """
            {
              "schemaVersion": 1,
              "packages": [{
                "id": "Example.Core",
                "kind": "library",
                "version": "1.2.3",
                "artifacts": ["packages/*.nupkg"]
              }]
            }
            """);

        var exception = Assert.Throws<NuGetReadyInputException>(() => ConfigurationLoader.Load(path));

        Assert.Contains("exact .nupkg or .snupkg filenames", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_artifact_declarations_are_invalid_input()
    {
        using var fixture = PackageFixture.Create();
        var path = WriteConfig(fixture, """
            {
              "schemaVersion": 1,
              "packages": [
                { "id": "One", "kind": "library", "version": "1.0.0", "artifacts": ["same.nupkg"] },
                { "id": "Two", "kind": "library", "version": "1.0.0", "artifacts": ["SAME.nupkg"] }
              ]
            }
            """);

        Assert.Throws<NuGetReadyInputException>(() => ConfigurationLoader.Load(path));
    }

    [Fact]
    public void Unsupported_package_kind_is_invalid_input()
    {
        using var fixture = PackageFixture.Create();
        var path = WriteConfig(fixture, """
            {
              "schemaVersion": 1,
              "packages": [{ "id": "Example", "kind": "analyzer", "version": "1.0.0", "artifacts": ["example.nupkg"] }]
            }
            """);

        var exception = Assert.Throws<NuGetReadyInputException>(() => ConfigurationLoader.Load(path));

        Assert.Contains("Package kind", exception.Message, StringComparison.Ordinal);
    }

    private static string WriteConfig(PackageFixture fixture, string json)
    {
        var path = Path.Combine(fixture.Root.FullName, "nugetready.json");
        File.WriteAllText(path, json);
        return path;
    }
}
