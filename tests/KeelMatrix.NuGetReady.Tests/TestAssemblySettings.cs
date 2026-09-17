using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestTelemetrySuppression
{
    [ModuleInitializer]
    internal static void DisableProductionTelemetry()
    {
        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
    }
}
