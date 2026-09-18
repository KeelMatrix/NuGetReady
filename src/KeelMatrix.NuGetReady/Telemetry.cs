using KeelMatrix.Telemetry;

namespace KeelMatrix.NuGetReady;

internal interface IUsageTelemetry
{
    void RecordCompletedRehearsal();
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

    public void RecordCompletedRehearsal()
    {
        try
        {
            // KeelMatrix development and CI set the shared process opt-out. Consumer CI
            // remains a valid usage class and is represented by the shared client.
            if (TelemetryOptOut.IsProcessDisabled())
            {
                return;
            }

            client ??= clientFactory();
            client.TrackActivation();
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
        ReadinessReport report,
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
            telemetry.RecordCompletedRehearsal();
        }
        catch
        {
            // A custom/test reporter must have the same failure isolation guarantee.
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
