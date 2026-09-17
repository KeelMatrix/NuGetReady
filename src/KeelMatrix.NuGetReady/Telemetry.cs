using System.Text.Json;
using System.Text.Json.Serialization;
using KeelMatrix.Telemetry;

namespace KeelMatrix.NuGetReady;

internal interface IUsageTelemetry
{
    void RecordCompletedRehearsal(TelemetryUsage usage);
}

internal interface IKeelMatrixTelemetryClient
{
    void TrackActivation();

    void TrackHeartbeat();
}

internal sealed class NuGetReadyTelemetry : IUsageTelemetry
{
    private readonly Func<IKeelMatrixTelemetryClient> clientFactory;
    private IKeelMatrixTelemetryClient? client;

    public NuGetReadyTelemetry()
        : this(() => new KeelMatrixTelemetryClient())
    {
    }

    internal NuGetReadyTelemetry(Func<IKeelMatrixTelemetryClient> clientFactory)
    {
        this.clientFactory = clientFactory;
    }

    public void RecordCompletedRehearsal(TelemetryUsage usage)
    {
        try
        {
            // KeelMatrix CI is explicitly suppressed. Local KeelMatrix development uses
            // the same established process opt-out as the shared telemetry client.
            if (usage.ExecutionClass == "ci" || TelemetryOptOut.IsProcessDisabled())
            {
                return;
            }

            client ??= clientFactory();
            client.TrackActivation();
            // KeelMatrix.Telemetry persists the ISO-week marker and suppresses repeats.
            client.TrackHeartbeat();
        }
        catch
        {
            // Telemetry is best-effort and must never affect a release rehearsal.
        }
    }

    private sealed class KeelMatrixTelemetryClient : IKeelMatrixTelemetryClient
    {
        private readonly Client client = new("nugetready", typeof(Program));

        public void TrackActivation() => client.TrackActivation();

        public void TrackHeartbeat() => client.TrackHeartbeat();
    }
}

internal static class TelemetryCoordinator
{
    internal static void RecordIfTrustworthy(
        NuGetReadyConfig config,
        ReadinessReport report,
        TimeSpan duration,
        IUsageTelemetry telemetry)
    {
        // A completed readiness result (pass/warn/fail) is trustworthy. An input or
        // infrastructure error means the release rehearsal did not complete.
        if (report.Status is not ("pass" or "warn" or "fail") || report.ExitCode is not (0 or 1))
        {
            return;
        }

        try
        {
            telemetry.RecordCompletedRehearsal(TelemetryUsage.Create(config, report, duration));
        }
        catch
        {
            // A custom/test reporter must have the same failure isolation guarantee.
        }
    }
}

internal sealed record TelemetryUsage(
    string ProductVersion,
    string PackageCountBucket,
    IReadOnlyDictionary<string, int> ArtifactKindDistribution,
    string CheckCountBucket,
    string Outcome,
    string ExecutionClass,
    string DurationBucket)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static TelemetryUsage Create(NuGetReadyConfig config, ReadinessReport report, TimeSpan duration)
    {
        var packages = config.Packages ?? [];
        var distribution = packages
            .GroupBy(package => NormalizeArtifactKind(package.Kind), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new TelemetryUsage(
            ProductVersion: typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            PackageCountBucket: Bucket(packages.Count),
            ArtifactKindDistribution: distribution,
            CheckCountBucket: Bucket(report.Checks.Count),
            Outcome: report.ExitCode == 1 ? "fail" : "pass",
            ExecutionClass: IsCi() ? "ci" : "local",
            DurationBucket: DurationBucketFor(duration));
    }

    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    private static string NormalizeArtifactKind(string? kind)
    {
        return kind switch
        {
            "library" => "library",
            "multiTargetLibrary" => "multiTargetLibrary",
            "dotnetTool" => "dotnetTool",
            _ => "other"
        };
    }

    private static string Bucket(int count)
    {
        return count switch
        {
            <= 0 => "0",
            1 => "1",
            <= 5 => "2-5",
            <= 10 => "6-10",
            _ => "11+"
        };
    }

    private static string DurationBucketFor(TimeSpan duration)
    {
        return duration.TotalSeconds switch
        {
            < 1 => "under-1s",
            < 5 => "1-5s",
            < 30 => "5-30s",
            < 120 => "30-120s",
            _ => "120s+"
        };
    }

    private static bool IsCi()
    {
        return HasValue("CI") ||
               HasValue("GITHUB_ACTIONS") ||
               HasValue("TF_BUILD") ||
               HasValue("BUILD_BUILDID") ||
               HasValue("JENKINS_URL");
    }

    private static bool HasValue(string name)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
        }
        catch
        {
            return false;
        }
    }
}

internal static class TelemetryOptOut
{
    private static readonly string[] EnvironmentKeys =
    [
        "KEELMATRIX_NO_TELEMETRY",
        "DOTNET_CLI_TELEMETRY_OPTOUT",
        "DO_NOT_TRACK"
    ];

    internal static bool IsProcessDisabled()
    {
        try
        {
            return EnvironmentKeys.Any(key => IsTruthy(Environment.GetEnvironmentVariable(key)));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null && value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "y" or "on";
    }
}
