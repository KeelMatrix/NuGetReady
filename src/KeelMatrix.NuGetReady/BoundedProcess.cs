using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;

namespace KeelMatrix.NuGetReady;

internal sealed record ProcessResult(
    bool Started,
    int ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError,
    bool CleanupConfirmed = false);

internal static class BoundedProcess
{
    private const int DefaultOutputLimit = 16 * 1024;
    private static readonly TimeSpan TerminationGracePeriod = TimeSpan.FromMilliseconds(500);
    private static readonly string[] RestoreEnvironmentOverrides =
    {
        "NUGET_FALLBACK_PACKAGES",
        "RestoreFallbackFolders",
        "RestoreSources",
        "RestoreAdditionalProjectSources"
    };

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        int outputLimit = DefaultOutputLimit,
        CancellationToken cancellationToken = default)
    {
        var useUnixProcessGroup = !OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (useUnixProcessGroup)
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            startInfo.ArgumentList.Add("--internal-unix-supervisor");
            startInfo.ArgumentList.Add(UnixProcessSupervisor.Encode(fileName, arguments));
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        if (!useUnixProcessGroup)
        {
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
        }

        foreach (var pair in environment)
        {
            process.StartInfo.Environment[pair.Key] = pair.Value;
        }

        foreach (var key in RestoreEnvironmentOverrides)
        {
            if (!environment.ContainsKey(key))
            {
                process.StartInfo.Environment[key] = null;
            }
        }

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(false, -1, false, string.Empty, "The process could not be started.");
            }

        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            return new ProcessResult(false, -1, false, string.Empty, "The required process was not found.");
        }

        WindowsProcessJob? processJob = WindowsProcessJob.TryAttach(process);
        using var lifecycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var cleanupConfirmed = false;
            var standardOutput = CaptureAsync(process.StandardOutput, outputLimit, lifecycleCancellation.Token);
            var standardError = CaptureAsync(process.StandardError, outputLimit, lifecycleCancellation.Token);
            var waitForExit = process.WaitForExitAsync(CancellationToken.None);
            var completeLifecycle = Task.WhenAll(waitForExit, standardOutput, standardError);
            var timeoutTask = Task.Delay(timeout, CancellationToken.None);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completed = await Task.WhenAny(completeLifecycle, timeoutTask, cancellationTask).ConfigureAwait(false);

            if (completed == cancellationTask)
            {
                cleanupConfirmed = Terminate(process, processJob, useUnixProcessGroup);
                await DrainAfterTerminationAsync(completeLifecycle, lifecycleCancellation).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var timedOut = completed == timeoutTask;
            if (timedOut)
            {
                cleanupConfirmed = Terminate(process, processJob, useUnixProcessGroup);
                await DrainAfterTerminationAsync(completeLifecycle, lifecycleCancellation).ConfigureAwait(false);
            }

            if (!timedOut && completed == completeLifecycle)
            {
                cleanupConfirmed = Terminate(process, processJob, useUnixProcessGroup);
            }

            if (completeLifecycle.IsCompletedSuccessfully)
            {
                processJob?.Dispose();
                processJob = null;
            }

            return new ProcessResult(
                true,
                timedOut ? -1 : process.ExitCode,
                timedOut,
                GetCompletedOutput(standardOutput),
                GetCompletedOutput(standardError),
                cleanupConfirmed);
        }
        finally
        {
            lifecycleCancellation.Cancel();
            processJob?.Dispose();
        }
    }

    private static async Task DrainAfterTerminationAsync(
        Task completeLifecycle,
        CancellationTokenSource lifecycleCancellation)
    {
        var drainTimeout = Task.Delay(TerminationGracePeriod);
        var completed = await Task.WhenAny(completeLifecycle, drainTimeout).ConfigureAwait(false);
        if (completed != completeLifecycle)
        {
            lifecycleCancellation.Cancel();
            try
            {
                await completeLifecycle.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static string GetCompletedOutput(Task<string> output)
    {
        return output.Status == TaskStatus.RanToCompletion ? output.Result : string.Empty;
    }

    private static async Task<string> CaptureAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(limit, 4096));
        var buffer = new char[4096];
        var truncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = limit - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }

            if (read > remaining)
            {
                truncated = true;
            }
        }

        if (truncated)
        {
            builder.Append("\n[output truncated]");
        }

        return builder.ToString();
    }

    private static bool Terminate(Process process, WindowsProcessJob? processJob, bool unixProcessGroup)
    {
        processJob?.Dispose();
        try
        {
            if (unixProcessGroup)
            {
                if (kill(-process.Id, SigKill) != 0)
                {
                    return Marshal.GetLastWin32Error() == NoSuchProcessError;
                }

                return WaitForUnixProcessGroupExit(process.Id);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return process.WaitForExit((int)TerminationGracePeriod.TotalMilliseconds);
        }
        catch (InvalidOperationException)
        {
            return process.HasExited;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private const int SigKill = 9;
    private const int NoSuchProcessError = 3;

    private static bool WaitForUnixProcessGroupExit(int processGroupId)
    {
        var deadline = DateTime.UtcNow + TerminationGracePeriod;
        while (DateTime.UtcNow < deadline)
        {
            if (kill(-processGroupId, 0) != 0)
            {
                return true;
            }

            _ = kill(-processGroupId, SigKill);
            Thread.Sleep(10);
        }

        return kill(-processGroupId, 0) != 0;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);

    private sealed class WindowsProcessJob : IDisposable
    {
        private SafeJobHandle? handle;

        private WindowsProcessJob(SafeJobHandle handle)
        {
            this.handle = handle;
        }

        public static WindowsProcessJob? TryAttach(Process process)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            SafeJobHandle? job = null;
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job is null || job.IsInvalid)
                {
                    return null;
                }

                var limits = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose
                    }
                };

                if (!SetInformationJobObject(
                        job,
                        JobObjectExtendedLimitInformationClass,
                        ref limits,
                        checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())))
                {
                    return null;
                }

                if (!AssignProcessToJobObject(job, process.Handle))
                {
                    return null;
                }

                var attachedJob = new WindowsProcessJob(job);
                job = null;
                return attachedJob;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (Win32Exception)
            {
                return null;
            }
            finally
            {
                job?.Dispose();
            }
        }

        public void Dispose()
        {
            handle?.Dispose();
            handle = null;
        }

        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeJobHandle job,
            int informationClass,
            ref JobObjectExtendedLimitInformation limits,
            uint limitsLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeJobHandle()
                : base(ownsHandle: true)
            {
            }

            protected override bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }
}

internal static class UnixProcessSupervisor
{
    private sealed record Request(string FileName, string[] Arguments);

    public static string Encode(string fileName, IReadOnlyList<string> arguments)
    {
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Request(fileName, arguments.ToArray())));
    }

    public static int Run(string payload)
    {
        if (OperatingSystem.IsWindows())
        {
            return 125;
        }

        Request? request;
        try
        {
            request = JsonSerializer.Deserialize<Request>(Convert.FromBase64String(payload));
        }
        catch (FormatException)
        {
            return 125;
        }
        catch (JsonException)
        {
            return 125;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.FileName))
        {
            return 125;
        }

        var filePointer = IntPtr.Zero;
        var argumentPointers = new IntPtr[request.Arguments.Length + 2];
        var argumentVector = IntPtr.Zero;
        try
        {
            filePointer = Marshal.StringToCoTaskMemUTF8(request.FileName);
            argumentPointers[0] = filePointer;
            for (var index = 0; index < request.Arguments.Length; index++)
            {
                argumentPointers[index + 1] = Marshal.StringToCoTaskMemUTF8(request.Arguments[index]);
            }

            argumentVector = Marshal.AllocHGlobal(argumentPointers.Length * IntPtr.Size);
            for (var index = 0; index < argumentPointers.Length; index++)
            {
                Marshal.WriteIntPtr(argumentVector, index * IntPtr.Size, argumentPointers[index]);
            }

            if (setsid() < 0)
            {
                return 125;
            }

            _ = execvp(filePointer, argumentVector);
            _exit(127);
            return 127;
        }
        finally
        {
            if (argumentVector != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(argumentVector);
            }

            foreach (var pointer in argumentPointers)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int execvp(IntPtr file, IntPtr argumentVector);

    [DllImport("libc")]
    private static extern void _exit(int status);
}
