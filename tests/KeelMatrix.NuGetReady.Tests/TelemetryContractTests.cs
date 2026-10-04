namespace KeelMatrix.NuGetReady.Tests;

public sealed class TelemetryContractTests
{
    [Theory]
    [InlineData("pass", 0, 1)]
    [InlineData("pass", 1, 0)]
    [InlineData("pass", 2, 0)]
    [InlineData("warn", 0, 1)]
    [InlineData("warn", 1, 0)]
    [InlineData("warn", 2, 0)]
    [InlineData("fail", 0, 0)]
    [InlineData("fail", 1, 1)]
    [InlineData("fail", 2, 0)]
    [InlineData("error", 0, 0)]
    [InlineData("error", 1, 0)]
    [InlineData("error", 2, 0)]
    [InlineData("unknown", 0, 0)]
    [InlineData("unknown", 1, 0)]
    [InlineData("unknown", 2, 0)]
    public void Telemetry_is_requested_only_after_a_trustworthy_completed_rehearsal(
        string status,
        int exitCode,
        int expectedCompletedRehearsals)
    {
        var telemetry = new RecordingTelemetry();

        TelemetryCoordinator.RecordIfTrustworthy(Report(status, exitCode), telemetry);

        Assert.Equal(expectedCompletedRehearsals, telemetry.CompletedRehearsals);
    }

    [Fact]
    public void Usage_telemetry_seam_does_not_accept_rehearsal_context()
    {
        Assert.Empty(typeof(IUsageTelemetry).GetMethod(nameof(IUsageTelemetry.RecordCompletedRehearsal))!.GetParameters());
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
}
