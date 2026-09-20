using System.Diagnostics;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class BoundedProcessTests
{
    [Fact]
    public async Task Early_parent_exit_with_inherited_pipe_is_bounded_and_cleaned_up()
    {
        var (fileName, arguments) = CreatePipeHoldingProcess();
        var stopwatch = Stopwatch.StartNew();

        var result = await BoundedProcess.RunAsync(
            fileName,
            arguments,
            Environment.CurrentDirectory,
            new Dictionary<string, string?>(),
            TimeSpan.FromMilliseconds(150));

        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Process lifecycle took {stopwatch.Elapsed}.");
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task Cancellation_terminates_the_complete_process_lifecycle()
    {
        var (fileName, arguments) = CreatePipeHoldingProcess();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedProcess.RunAsync(
            fileName,
            arguments,
            Environment.CurrentDirectory,
            new Dictionary<string, string?>(),
            TimeSpan.FromMinutes(1),
            cancellationToken: cancellation.Token));
    }

    private static (string FileName, IReadOnlyList<string> Arguments) CreatePipeHoldingProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return (
                "cmd.exe",
                ["/c", "start \"\" /b powershell.exe -NoProfile -Command Start-Sleep -Seconds 30 & exit /b 0"]);
        }

        return ("sh", ["-c", "sleep 30 & exit 0"]);
    }
}
