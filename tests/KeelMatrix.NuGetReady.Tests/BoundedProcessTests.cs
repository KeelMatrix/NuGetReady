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
        var timeout = IsSlowProcessHost ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(150);

        var result = await BoundedProcess.RunAsync(
            fileName,
            arguments,
            Environment.CurrentDirectory,
            new Dictionary<string, string?>(),
            timeout);

        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Process lifecycle took {stopwatch.Elapsed}.");
        Assert.True(result.TimedOut, $"stdout={result.StandardOutput}; stderr={result.StandardError}; cleanup={result.CleanupConfirmed}");
        Assert.True(result.CleanupConfirmed, $"stdout={result.StandardOutput}; stderr={result.StandardError}");
        AssertDescendantsTerminated(pidFile, expectedPidCount: 2);
    }

    [Fact]
    public async Task Cancellation_terminates_the_complete_process_lifecycle()
    {
        var (fileName, arguments, pidFile) = CreatePipeHoldingProcess();
        var cancellationDelay = IsSlowProcessHost ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(150);
        using var cancellation = new CancellationTokenSource(cancellationDelay);

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
            AssertDescendantsTerminated(pidFile, expectedPidCount: 2);
        }
    }

    [Fact]
    public async Task Successful_parent_exit_with_redirected_descendant_is_cleaned_up()
    {
        var (fileName, arguments, pidFile) = CreateSuccessfulParentExitProcess();
        try
        {
            var result = await BoundedProcess.RunAsync(
                fileName,
                arguments,
                Environment.CurrentDirectory,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(2));

            Assert.False(result.TimedOut, $"stdout={result.StandardOutput}; stderr={result.StandardError}; cleanup={result.CleanupConfirmed}");
            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.True(result.CleanupConfirmed);

            AssertDescendantsTerminated(pidFile, expectedPidCount: 2);
        }
        finally
        {
            if (File.Exists(pidFile))
            {
                File.Delete(pidFile);
            }

            DeleteWindowsFixtureArtifacts(pidFile);
        }
    }

    private static (string FileName, IReadOnlyList<string> Arguments, string? PidFile) CreatePipeHoldingProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsPidFile = Path.Combine(Path.GetTempPath(), $"nugetready-process-pids-{Guid.NewGuid():N}.txt");
            return (
                "pwsh.exe",
                CreateWindowsProcessArguments(windowsPidFile, redirectOutput: false, keepParentAlive: true),
                windowsPidFile);
        }

        var pidFile = Path.Combine(Path.GetTempPath(), $"nugetready-process-pids-{Guid.NewGuid():N}.txt");
        return (
            "sh",
            ["-c", "sleep 30 & child=$!; printf '%s %s\\n' \"$$\" \"$child\" > \"$1\"; exit 0", "nugetready-test", pidFile],
            pidFile);
    }

    private static (string FileName, IReadOnlyList<string> Arguments, string PidFile) CreateSuccessfulParentExitProcess()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"nugetready-success-pids-{Guid.NewGuid():N}.txt");
        if (OperatingSystem.IsWindows())
        {
            return (
                "pwsh.exe",
                CreateWindowsProcessArguments(pidFile, redirectOutput: true, keepParentAlive: false),
                pidFile);
        }

        return (
            "sh",
            ["-c", "sleep 30 >/dev/null 2>&1 & child=$!; printf '%s %s\\n' \"$$\" \"$child\" > \"$1\"; exit 0", "nugetready-test", pidFile],
            pidFile);
    }

    private static IReadOnlyList<string> CreateWindowsProcessArguments(string pidFile, bool redirectOutput, bool keepParentAlive)
    {
        var grandchildScript = "Start-Sleep -Seconds 30";
        var encodedGrandchildScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(grandchildScript));
        var childScript = $"[System.IO.File]::WriteAllText('{EscapePowerShellLiteral(pidFile)}', [string]$PID + ' '); $grandchild = Start-Process -FilePath 'pwsh.exe' -ArgumentList @('-NoProfile', '-EncodedCommand', '{encodedGrandchildScript}') -PassThru; [System.IO.File]::AppendAllText('{EscapePowerShellLiteral(pidFile)}', [string]$grandchild.Id + ' '); Start-Sleep -Seconds 30";
        var encodedChildScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(childScript));
        var standardOutputFile = pidFile + ".stdout";
        var standardErrorFile = pidFile + ".stderr";
        var waitForDescendants = $"for ($i = 0; $i -lt 200 -and ((-not (Test-Path '{EscapePowerShellLiteral(pidFile)}')) -or ((Get-Content '{EscapePowerShellLiteral(pidFile)}' -Raw).Trim().Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries).Count -lt 2)); $i++) {{ Start-Sleep -Milliseconds 10 }}";
        var holdParent = keepParentAlive ? "; Start-Sleep -Seconds 30" : string.Empty;
        var processScript = redirectOutput
            ? $"Start-Process -FilePath 'pwsh.exe' -ArgumentList @('-NoProfile', '-EncodedCommand', '{encodedChildScript}') -RedirectStandardOutput '{EscapePowerShellLiteral(standardOutputFile)}' -RedirectStandardError '{EscapePowerShellLiteral(standardErrorFile)}' -PassThru | Out-Null; {waitForDescendants}{holdParent}; exit 0"
            : $"$psi = [System.Diagnostics.ProcessStartInfo]::new(); $psi.FileName = 'pwsh.exe'; $psi.UseShellExecute = $false; $psi.ArgumentList.Add('-NoProfile'); $psi.ArgumentList.Add('-EncodedCommand'); $psi.ArgumentList.Add('{encodedChildScript}'); [System.Diagnostics.Process]::Start($psi) | Out-Null; {waitForDescendants}{holdParent}; exit 0";
        var encodedProcessScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(processScript));
        return ["-NoProfile", "-EncodedCommand", encodedProcessScript];
    }

    private static void DeleteWindowsFixtureArtifacts(string? pidFile)
    {
        if (OperatingSystem.IsWindows() && pidFile is not null)
        {
            foreach (var suffix in new[] { ".stdout", ".stderr" })
            {
                var path = pidFile + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static bool IsSlowProcessHost => OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("WSL_INTEROP") is not null;

    private static void AssertDescendantsTerminated(string? pidFile, int expectedPidCount)
    {
        if (OperatingSystem.IsWindows())
        {
            AssertWindowsDescendantsTerminated(pidFile, expectedPidCount);
            return;
        }

        AssertUnixDescendantsTerminated(pidFile, expectedPidCount);
    }

    private static void AssertUnixDescendantsTerminated(string? pidFile, int expectedPidCount)
    {
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
            Assert.Equal(expectedPidCount, pids.Length);
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

            DeleteWindowsFixtureArtifacts(pidFile);
        }
    }

    private static void AssertWindowsDescendantsTerminated(string? pidFile, int expectedPidCount)
    {
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
                    if (lines.Any(line => !string.IsNullOrWhiteSpace(line)))
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
            Assert.Equal(expectedPidCount, pids.Length);
            foreach (var pid in pids)
            {
                Assert.True(WaitForExit(pid), $"Windows descendant PID {pid} survived cleanup.");
            }
        }
        finally
        {
            if (File.Exists(pidFile))
            {
                File.Delete(pidFile);
            }

            DeleteWindowsFixtureArtifacts(pidFile);
        }
    }

    private static bool WaitForExit(int pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsLiveProcess(pid))
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return !IsLiveProcess(pid);
    }

    private static bool IsLiveProcess(int pid)
    {
        return OperatingSystem.IsWindows() ? IsLiveWindowsProcess(pid) : IsLiveUnixProcess(pid);
    }

    private static bool IsLiveUnixProcess(int pid)
    {
        if (kill(pid, 0) != 0)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/ps",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-o", "state=", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            });
            if (process is null)
            {
                return true;
            }

            var state = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(1000) || process.ExitCode != 0)
            {
                return true;
            }

            return state.IndexOf('Z') < 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static bool IsLiveWindowsProcess(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, checked((uint)pid));
        if (handle == IntPtr.Zero)
        {
            return Marshal.GetLastWin32Error() != ErrorInvalidParameter;
        }

        try
        {
            if (!GetExitCodeProcess(handle, out var exitCode))
            {
                return true;
            }

            return exitCode == StillActive;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const int ErrorInvalidParameter = 87;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
