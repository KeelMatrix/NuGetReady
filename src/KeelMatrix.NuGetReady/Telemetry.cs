using KeelMatrix.Telemetry;

namespace KeelMatrix.NuGetReady;

internal interface IUsageTelemetry
{
    void RecordCompletedRehearsal();
}

internal sealed class NuGetReadyTelemetry : IUsageTelemetry
{
    public void RecordCompletedRehearsal()
    {
        var client = new Client("nugetready", typeof(Program));
        client.TrackActivation();
        client.TrackHeartbeat();
    }
}

internal static class TelemetryCoordinator
{
    internal static void RecordIfTrustworthy(
        ReadinessReport report,
        IUsageTelemetry telemetry)
    {
        // Only the public terminal result/exit-code pairs prove a trustworthy
        // completed rehearsal. Input and infrastructure errors are not activations.
        if ((report.Status, report.ExitCode) is not (("pass", 0) or ("warn", 0) or ("fail", 1)))
        {
            return;
        }

        telemetry.RecordCompletedRehearsal();
    }
}
