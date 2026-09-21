using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class BoundedProcessTests
{
    [Fact]
    public async Task Early_parent_exit_with_inherited_pipe_is_bounded_and_cleaned_up()
    {
        var (fileName, arguments, pidFile) = CreatePipeHoldingProcess();
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
        Assert.True(result.CleanupConfirmed);
        AssertUnixDescendantsTerminated(pidFile);
    }

    [Fact]
    public async Task Cancellation_terminates_the_complete_process_lifecycle()
    {
        var (fileName, arguments, pidFile) = CreatePipeHoldingProcess();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedProcess.RunAsync(
                fileName,
                arguments,
                Environment.CurrentDirectory,
                new Dictionary<string, string?>(),
                TimeSpan.FromMinutes(1),
                cancellationToken: cancellation.Token));
        }
        finally
        {
            AssertUnixDescendantsTerminated(pidFile);
        }
    }

    private static (string FileName, IReadOnlyList<string> Arguments, string? PidFile) CreatePipeHoldingProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return (
                "cmd.exe",
                ["/c", "start \"\" /b powershell.exe -NoProfile -Command Start-Sleep -Seconds 30 & exit /b 0"],
                null);
        }

        var pidFile = Path.Combine(Path.GetTempPath(), $"nugetready-process-pids-{Guid.NewGuid():N}.txt");
        return (
            "sh",
            ["-c", "sleep 30 & child=$!; printf '%s %s\\n' \"$$\" \"$child\" > \"$1\"; exit 0", "nugetready-test", pidFile],
            pidFile);
    }

    private static void AssertUnixDescendantsTerminated(string? pidFile)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.NotNull(pidFile);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            string[]? lines = null;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(pidFile))
                {
                    lines = File.ReadAllLines(pidFile);
                    if (lines.Length > 0)
                    {
                        break;
                    }
                }

                Thread.Sleep(25);
            }

            Assert.NotNull(lines);
            var pids = lines!
                .SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            Assert.Equal(2, pids.Length);
            foreach (var pid in pids)
            {
                Assert.True(WaitForExit(pid), $"Unix descendant PID {pid} survived cleanup.");
            }
        }
        finally
        {
            if (File.Exists(pidFile))
            {
                File.Delete(pidFile);
            }
        }
    }

    private static bool WaitForExit(int pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            if (kill(pid, 0) != 0)
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return kill(pid, 0) != 0;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
