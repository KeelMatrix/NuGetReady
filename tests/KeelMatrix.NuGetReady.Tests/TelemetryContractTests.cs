using KeelMatrix.Telemetry;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class TelemetryContractTests
{
    [Fact]
    public void Telemetry_is_requested_only_after_a_trustworthy_completed_rehearsal()
    {
        var telemetry = new RecordingTelemetry();

        TelemetryCoordinator.RecordIfTrustworthy(Report("pass", 0), telemetry);
        TelemetryCoordinator.RecordIfTrustworthy(Report("fail", 1), telemetry);
        TelemetryCoordinator.RecordIfTrustworthy(Report("error", 2), telemetry);

        Assert.Equal(2, telemetry.CompletedRehearsals);
    }

    [Fact]
    public void NuGetReady_does_not_pass_product_context_to_the_shared_client()
    {
        Assert.Empty(typeof(IUsageTelemetry).GetMethod(nameof(IUsageTelemetry.RecordCompletedRehearsal))!.GetParameters());
        Assert.All(typeof(IKeelMatrixTelemetryClient).GetMethods(), method => Assert.Empty(method.GetParameters()));
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

        reporter.RecordCompletedRehearsal();

        Assert.Equal(0, created);
    }

    [Fact]
    public void KeelMatrix_ci_uses_the_repository_opt_out_to_suppress_emission()
    {
        using var environment = new EnvironmentScope(("CI", "true"), ("KEELMATRIX_NO_TELEMETRY", "1"), ("DOTNET_CLI_TELEMETRY_OPTOUT", null), ("DO_NOT_TRACK", null));
        var created = 0;
        var reporter = new NuGetReadyTelemetry(() =>
        {
            created++;
            return new RecordingClient();
        });

        reporter.RecordCompletedRehearsal();

        Assert.Equal(0, created);
    }

    [Fact]
    public void Telemetry_failure_cannot_change_the_completed_result()
    {
        var report = Report("fail", 1);
        var telemetry = new ThrowingTelemetry();

        var exception = Record.Exception(() => TelemetryCoordinator.RecordIfTrustworthy(report, telemetry));

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

        reporter.RecordCompletedRehearsal();

        Assert.Equal(1, client.ActivationRequests);
        Assert.Equal(1, client.HeartbeatRequests);
    }

    [Fact]
    public void Shared_client_contract_exposes_only_parameterless_tracking_requests()
    {
        var methods = typeof(Client).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        Assert.Contains(methods, method => method.Name == nameof(Client.TrackActivation) && method.GetParameters().Length == 0);
        Assert.Contains(methods, method => method.Name == nameof(Client.TrackHeartbeat) && method.GetParameters().Length == 0);
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

    private sealed class RecordingTelemetry : IUsageTelemetry
    {
        internal int CompletedRehearsals { get; private set; }

        public void RecordCompletedRehearsal() => CompletedRehearsals++;
    }

    private sealed class ThrowingTelemetry : IUsageTelemetry
    {
        public void RecordCompletedRehearsal() => throw new InvalidOperationException("synthetic telemetry outage");
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
