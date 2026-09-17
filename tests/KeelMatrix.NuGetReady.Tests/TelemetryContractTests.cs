using System.Text.Json;
using KeelMatrix.Telemetry;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class TelemetryContractTests
{
    [Fact]
    public void Telemetry_is_requested_only_after_a_trustworthy_completed_rehearsal()
    {
        var telemetry = new RecordingTelemetry();
        var config = Config("Synthetic.Package", "library");

        TelemetryCoordinator.RecordIfTrustworthy(config, Report("pass", 0), TimeSpan.FromSeconds(2), telemetry);
        TelemetryCoordinator.RecordIfTrustworthy(config, Report("fail", 1), TimeSpan.FromSeconds(3), telemetry);
        TelemetryCoordinator.RecordIfTrustworthy(config, Report("error", 2), TimeSpan.FromSeconds(4), telemetry);

        Assert.Equal(2, telemetry.Usages.Count);
        Assert.Equal("pass", telemetry.Usages[0].Outcome);
        Assert.Equal("fail", telemetry.Usages[1].Outcome);
    }

    [Fact]
    public void Telemetry_context_contains_only_bounded_approved_fields()
    {
        var config = Config("Synthetic.Package.Id", "library");
        config.Packages!.Add(new PackageExpectation
        {
            Id = "Synthetic.Dependency.Name",
            Kind = "dotnetTool",
            Version = "1.0.0",
            Artifacts = ["synthetic-tool.nupkg"]
        });
        var report = Report("fail", 1, "C:/private/repository/config.json contains workflow content");

        var usage = TelemetryUsage.Create(config, report, TimeSpan.FromSeconds(12));
        using var json = JsonDocument.Parse(usage.ToJson());
        var properties = json.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            new[]
            {
                "artifactKindDistribution",
                "checkCountBucket",
                "durationBucket",
                "executionClass",
                "outcome",
                "packageCountBucket",
                "productVersion"
            },
            properties);
        Assert.DoesNotContain("Synthetic.Package.Id", usage.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic.Dependency.Name", usage.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("C:/private/repository/config.json", usage.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("workflow content", usage.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Process_opt_out_suppresses_client_creation_and_emission()
    {
        using var environment = new EnvironmentScope(("KEELMATRIX_NO_TELEMETRY", "1"), ("DOTNET_CLI_TELEMETRY_OPTOUT", null), ("DO_NOT_TRACK", null));
        var created = 0;
        var reporter = new NuGetReadyTelemetry(() =>
        {
            created++;
            return new RecordingClient();
        });

        reporter.RecordCompletedRehearsal(Usage());

        Assert.Equal(0, created);
    }

    [Fact]
    public void KeelMatrix_ci_suppresses_client_creation_and_emission()
    {
        using var environment = new EnvironmentScope(("CI", "true"), ("KEELMATRIX_NO_TELEMETRY", null), ("DOTNET_CLI_TELEMETRY_OPTOUT", null), ("DO_NOT_TRACK", null));
        var created = 0;
        var reporter = new NuGetReadyTelemetry(() =>
        {
            created++;
            return new RecordingClient();
        });

        reporter.RecordCompletedRehearsal(Usage("ci"));

        Assert.Equal(0, created);
    }

    [Fact]
    public void Telemetry_failure_cannot_change_the_completed_result()
    {
        var report = Report("fail", 1);
        var config = Config("Synthetic.Package", "library");
        var telemetry = new ThrowingTelemetry();

        var exception = Record.Exception(() => TelemetryCoordinator.RecordIfTrustworthy(config, report, TimeSpan.FromSeconds(1), telemetry));

        Assert.Null(exception);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal("fail", report.Status);
    }

    [Fact]
    public void Established_client_receives_activation_and_heartbeat_requests()
    {
        using var environment = new EnvironmentScope(("KEELMATRIX_NO_TELEMETRY", null), ("DOTNET_CLI_TELEMETRY_OPTOUT", null), ("DO_NOT_TRACK", null), ("CI", null));
        var client = new RecordingClient();
        var reporter = new NuGetReadyTelemetry(() => client);

        reporter.RecordCompletedRehearsal(Usage());

        Assert.Equal(1, client.ActivationRequests);
        Assert.Equal(1, client.HeartbeatRequests);
    }

    [Fact]
    public void Established_client_contract_limits_heartbeats_to_one_per_iso_week()
    {
        var xmlPath = Path.ChangeExtension(typeof(Client).Assembly.Location, ".xml");
        Assert.True(File.Exists(xmlPath), $"Telemetry contract documentation was not found: {xmlPath}");

        var contract = File.ReadAllText(xmlPath);
        Assert.Contains("At most one heartbeat is emitted per project per", contract, StringComparison.Ordinal);
        Assert.Contains("ISO week", contract, StringComparison.Ordinal);
    }

    private static TelemetryUsage Usage(string executionClass = "local")
    {
        return new TelemetryUsage("0.1.0", "1", new Dictionary<string, int> { ["library"] = 1 }, "6-10", "pass", executionClass, "1-5s");
    }

    private static ReadinessReport Report(string status, int exitCode, string? message = null)
    {
        var failure = message is null ? Array.Empty<Failure>() : new[] { new Failure("consumer-rehearsal", message) };
        var check = new CheckResult("consumer-rehearsal", failure);
        return new ReadinessReport
        {
            Status = status,
            ExitCode = exitCode,
            Checks = [check],
            Failures = failure
        };
    }

    private static NuGetReadyConfig Config(string id, string kind)
    {
        return new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages = [new PackageExpectation { Id = id, Kind = kind, Version = "1.0.0", Artifacts = [$"{id}.1.0.0.nupkg"] }]
        };
    }

    private sealed class RecordingTelemetry : IUsageTelemetry
    {
        internal List<TelemetryUsage> Usages { get; } = [];

        public void RecordCompletedRehearsal(TelemetryUsage usage) => Usages.Add(usage);
    }

    private sealed class ThrowingTelemetry : IUsageTelemetry
    {
        public void RecordCompletedRehearsal(TelemetryUsage usage) => throw new InvalidOperationException("synthetic telemetry outage");
    }

    private sealed class RecordingClient : IKeelMatrixTelemetryClient
    {
        internal int ActivationRequests { get; private set; }

        internal int HeartbeatRequests { get; private set; }

        public void TrackActivation() => ActivationRequests++;

        public void TrackHeartbeat() => HeartbeatRequests++;
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new(StringComparer.Ordinal);

        internal EnvironmentScope(params (string Name, string? Value)[] values)
        {
            foreach (var (name, value) in values)
            {
                previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var pair in previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}
