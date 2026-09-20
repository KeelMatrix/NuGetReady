using NuGet.Versioning;

namespace KeelMatrix.NuGetReady;

internal enum OutputFormat
{
    Text,
    Json
}

internal sealed record CliOptions(
    string ConfigPath,
    string ArtifactsPath,
    OutputFormat Format,
    TimeSpan Timeout);

internal sealed class NuGetReadyConfig
{
    public int SchemaVersion { get; set; }

    public List<PackageExpectation>? Packages { get; set; }
}

internal sealed class PackageExpectation
{
    public string? Id { get; set; }

    public string? Kind { get; set; }

    public string? Version { get; set; }

    public List<string>? Artifacts { get; set; }

    public List<string>? Smoke { get; set; }

    public string? Command { get; set; }
}

internal sealed record Failure(string CheckId, string Message, bool IsError = false, bool IsWarning = false);

internal sealed class CheckResult
{
    public CheckResult(string id, IReadOnlyList<Failure> failures, string? state = null)
    {
        Id = id;
        Failures = failures.OrderBy(failure => failure.Message, StringComparer.Ordinal).ToArray();
        Status = state ?? (Failures.Any(failure => failure.IsError)
            ? "error"
            : Failures.Any(failure => !failure.IsWarning)
                ? "fail"
                : Failures.Count == 0 ? "pass" : "warn");
    }

    public string Id { get; }

    public string Status { get; }

    public IReadOnlyList<Failure> Failures { get; }
}

internal sealed record RehearsalResult(
    string PackageId,
    string Kind,
    string Status,
    string Message,
    bool IsError = false);

internal sealed class ReadinessReport
{
    public int SchemaVersion { get; init; } = 1;

    public string Status { get; init; } = "error";

    public int ExitCode { get; init; } = 2;

    public int ExpectedArtifacts { get; init; }

    public int FoundArtifacts { get; init; }

    public IReadOnlyList<CheckResult> Checks { get; init; } = Array.Empty<CheckResult>();

    public IReadOnlyList<Failure> Failures { get; init; } = Array.Empty<Failure>();

    public IReadOnlyList<RehearsalResult> Rehearsals { get; init; } = Array.Empty<RehearsalResult>();
}

internal sealed class NuGetReadyInputException : Exception
{
    public NuGetReadyInputException(string message)
        : base(message)
    {
    }
}

internal sealed class NuGetReadyInfrastructureException : Exception
{
    public NuGetReadyInfrastructureException(string message)
        : base(message)
    {
    }
}

internal static class VersionText
{
    public static string Normalize(string version)
    {
        return NuGetVersion.Parse(version).ToNormalizedString();
    }
}
