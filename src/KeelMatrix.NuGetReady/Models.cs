using NuGet.Versioning;
using System.Text.Json.Serialization;

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

internal static class PackageArtifacts
{
    public static string? Primary(PackageExpectation package)
    {
        var primary = package.Artifacts?.Where(IsPrimary).ToArray();
        return primary is { Length: 1 } ? primary[0] : null;
    }

    public static int PrimaryCount(PackageExpectation package)
    {
        return package.Artifacts?.Count(IsPrimary) ?? 0;
    }

    public static int SymbolsCount(PackageExpectation package)
    {
        return package.Artifacts?.Count(IsSymbols) ?? 0;
    }

    private static bool IsPrimary(string artifact)
    {
        return artifact.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) &&
               !artifact.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSymbols(string artifact)
    {
        return artifact.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record Failure(string CheckId, string Message, bool IsError = false, bool IsWarning = false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtifactFileName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpectationName { get; init; }
}

internal static class FailureContext
{
    public static Failure ForArchive(Failure failure, PackageExpectation expectation, string artifactFileName)
    {
        return failure with
        {
            PackageId = expectation.Id,
            PackageVersion = VersionText.Normalize(expectation.Version!),
            ArtifactFileName = Path.GetFileName(artifactFileName),
            ExpectationName = expectation.Id
        };
    }
}

internal static class CheckContract
{
    public static readonly string[] Order =
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

    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Error = "error";
    public const string NotRun = "not-run";
    public const string NotApplicable = "not-applicable";

    public static readonly IReadOnlySet<string> States = new HashSet<string>(StringComparer.Ordinal)
    {
        Pass,
        Warn,
        Fail,
        Error,
        NotRun,
        NotApplicable
    };
}

internal sealed class CheckResult
{
    public CheckResult(string id, IReadOnlyList<Failure> failures, string? state = null)
    {
        Id = id;
        Failures = failures
            .OrderBy(failure => failure.Message, StringComparer.Ordinal)
            .ThenBy(failure => failure.PackageId, StringComparer.Ordinal)
            .ThenBy(failure => failure.PackageVersion, StringComparer.Ordinal)
            .ThenBy(failure => failure.ArtifactFileName, StringComparer.Ordinal)
            .ThenBy(failure => failure.ExpectationName, StringComparer.Ordinal)
            .ToArray();
        Status = state ?? (Failures.Any(failure => failure.IsError)
            ? CheckContract.Error
            : Failures.Any(failure => !failure.IsWarning)
                ? CheckContract.Fail
                : Failures.Count == 0 ? CheckContract.Pass : CheckContract.Warn);
        if (!CheckContract.States.Contains(Status))
        {
            throw new ArgumentException($"Unsupported check state '{Status}'.", nameof(state));
        }
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
