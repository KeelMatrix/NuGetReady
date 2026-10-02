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
    internal const string CleanupSignal = "\u001eNU_GETREADY_SUPERVISOR_CLEANUP_CONFIRMED\u001e";
    private const int DefaultOutputLimit = 16 * 1024;
    private static readonly TimeSpan TerminationGracePeriod = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StartupReadinessTimeout = TimeSpan.FromSeconds(5);
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
        var useWindowsSupervisor = OperatingSystem.IsWindows();
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
        else if (useWindowsSupervisor)
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            startInfo.ArgumentList.Add("--internal-windows-supervisor");
            startInfo.ArgumentList.Add(UnixProcessSupervisor.Encode(fileName, arguments));
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        if (!useUnixProcessGroup && !useWindowsSupervisor)
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
            var readiness = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var standardOutput = CaptureAsync(process.StandardOutput, outputLimit, lifecycleCancellation.Token, readiness, cleanupSignal);
            var standardError = CaptureAsync(process.StandardError, outputLimit, lifecycleCancellation.Token);
            var waitForExit = process.WaitForExitAsync(CancellationToken.None);
            var completeLifecycle = Task.WhenAll(waitForExit, standardOutput, standardError);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var readinessTask = await Task.WhenAny(
                readiness.Task,
                waitForExit,
                cancellationTask,
                Task.Delay(StartupReadinessTimeout, CancellationToken.None)).ConfigureAwait(false);
            if (readinessTask == cancellationTask)
            {
                _ = Terminate(process, processJob, useUnixProcessGroup, useWindowsSupervisor, processGroupReady: false, naturalCompletion: false);
                await DrainAfterTerminationAsync(completeLifecycle, lifecycleCancellation).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var readinessConfirmed = readinessTask == readiness.Task && readiness.Task.Result;
            var startupTimedOut = !readinessConfirmed && !waitForExit.IsCompleted;
            var timeoutTask = Task.Delay(timeout, CancellationToken.None);
            var completed = startupTimedOut
                ? timeoutTask
                : OperatingSystem.IsWindows()
                    ? await Task.WhenAny(completeLifecycle, waitForExit, timeoutTask, cancellationTask).ConfigureAwait(false)
                    : await Task.WhenAny(completeLifecycle, timeoutTask, cancellationTask).ConfigureAwait(false);

            if (completed == cancellationTask)
            {
                cleanupConfirmed = Terminate(process, processJob, useUnixProcessGroup, useWindowsSupervisor, readinessConfirmed, naturalCompletion: false);
                await DrainAfterTerminationAsync(completeLifecycle, lifecycleCancellation).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var timedOut = startupTimedOut || completed == timeoutTask;
            if (timedOut)
            {
                cleanupConfirmed = Terminate(process, processJob, useUnixProcessGroup, useWindowsSupervisor, readinessConfirmed, naturalCompletion: false);
                await DrainAfterTerminationAsync(completeLifecycle, lifecycleCancellation).ConfigureAwait(false);
            }

            if (!timedOut && (completed == completeLifecycle || (OperatingSystem.IsWindows() && completed == waitForExit)))
            {
                cleanupConfirmed = Terminate(process, processJob, useUnixProcessGroup, useWindowsSupervisor, readinessConfirmed, naturalCompletion: true);
                if (completed == waitForExit)
                {
                    await DrainAfterTerminationAsync(completeLifecycle, lifecycleCancellation).ConfigureAwait(false);
                }
            }

            if (completeLifecycle.IsCompletedSuccessfully)
            {
                processJob?.Dispose();
                processJob = null;
            }

            return new ProcessResult(
                true,
                timedOut || !waitForExit.IsCompletedSuccessfully ? -1 : process.ExitCode,
                timedOut,
                GetCompletedOutput(standardOutput),
                GetCompletedOutput(standardError),
                cleanupSignal.Task.IsCompletedSuccessfully && cleanupSignal.Task.Result);
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
            var cancellationDrain = Task.Delay(TerminationGracePeriod);
            if (await Task.WhenAny(completeLifecycle, cancellationDrain).ConfigureAwait(false) != completeLifecycle)
            {
                _ = completeLifecycle.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private static string GetCompletedOutput(Task<string> output)
    {
        return output.Status == TaskStatus.RanToCompletion ? output.Result : string.Empty;
    }

    private static async Task<string> CaptureAsync(
        StreamReader reader,
        int limit,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool>? readiness = null,
        TaskCompletionSource<bool>? cleanup = null)
    {
        var builder = new StringBuilder(Math.Min(limit, 4096));
        var buffer = new char[4096];
        var pending = string.Empty;
        var truncated = false;
        var signalTailLength = Math.Max(
            readiness is null ? 0 : UnixProcessSupervisor.ReadinessSignal.Length - 1,
            cleanup is null ? 0 : CleanupSignal.Length - 1);

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var content = pending + new string(buffer, 0, read);
                pending = string.Empty;
                while (true)
                {
                    var readinessIndex = readiness is null
                        ? -1
                        : content.IndexOf(UnixProcessSupervisor.ReadinessSignal, StringComparison.Ordinal);
                    var cleanupIndex = cleanup is null
                        ? -1
                        : content.IndexOf(CleanupSignal, StringComparison.Ordinal);
                    if (readinessIndex < 0 && cleanupIndex < 0)
                    {
                        break;
                    }

                    if (readinessIndex >= 0 && (cleanupIndex < 0 || readinessIndex < cleanupIndex))
                    {
                        AppendOutput(builder, content.AsSpan(0, readinessIndex), limit, ref truncated);
                        readiness!.TrySetResult(true);
                        content = content[(readinessIndex + UnixProcessSupervisor.ReadinessSignal.Length)..];
                    }
                    else
                    {
                        AppendOutput(builder, content.AsSpan(0, cleanupIndex), limit, ref truncated);
                        cleanup!.TrySetResult(true);
                        content = content[(cleanupIndex + CleanupSignal.Length)..];
                    }
                }

                if (signalTailLength > 0)
                {
                    var keep = Math.Min(content.Length, signalTailLength);
                    if (content.Length > keep)
                    {
                        AppendOutput(builder, content.AsSpan(0, content.Length - keep), limit, ref truncated);
                        pending = content[^keep..];
                    }
                    else
                    {
                        pending = content;
                    }
                }
                else
                {
                    AppendOutput(builder, content.AsSpan(), limit, ref truncated);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (pending.Length > 0)
            {
                AppendOutput(builder, pending.AsSpan(), limit, ref truncated);
            }

            readiness?.TrySetResult(false);
            cleanup?.TrySetResult(false);
        }

        if (truncated)
        {
            builder.Append("\n[output truncated]");
        }

        return builder.ToString();
    }

    private static void AppendOutput(StringBuilder builder, ReadOnlySpan<char> content, int limit, ref bool truncated)
    {
        var remaining = limit - builder.Length;
        if (remaining > 0)
        {
            builder.Append(content[..Math.Min(content.Length, remaining)]);
        }

        if (content.Length > remaining)
        {
            truncated = true;
        }
    }

    private static bool Terminate(
        Process process,
        WindowsProcessJob? processJob,
        bool unixProcessGroup,
        bool windowsSupervisor,
        bool processGroupReady,
        bool naturalCompletion)
    {
        processJob?.Dispose();
        try
        {
            if (unixProcessGroup)
            {
                if (!process.HasExited)
                {
                    _ = UnixProcessSupervisor.RequestTermination(process.Id);
                }

                return WaitForDirectExit(process);
            }

            if (windowsSupervisor)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                return processGroupReady && WaitForDirectExit(process);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return process.WaitForExit((int)TerminationGracePeriod.TotalMilliseconds);
        }
        catch (InvalidOperationException)
        {
            return !windowsSupervisor && process.HasExited || windowsSupervisor && processGroupReady && process.HasExited;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool WaitForDirectExit(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            var waitMilliseconds = !OperatingSystem.IsWindows()
                ? (int)TimeSpan.FromSeconds(5).TotalMilliseconds
                : (int)TerminationGracePeriod.TotalMilliseconds;
            return process.WaitForExit(waitMilliseconds) && process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

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
    internal const string ReadinessSignal = "\u001eNU_GETREADY_SUPERVISOR_READY\u001e";

    internal sealed record SupervisorRequest(string FileName, string[] Arguments);

    private static int terminationRequested;

    public static bool RequestTermination(int processId)
    {
        return kill(processId, SigTerm) == 0 || Marshal.GetLastWin32Error() == NoSuchProcessError;
    }

    public static string Encode(string fileName, IReadOnlyList<string> arguments)
    {
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new SupervisorRequest(fileName, arguments.ToArray())));
    }

    public static int Run(string payload)
    {
        if (OperatingSystem.IsWindows())
        {
            return 125;
        }

        SupervisorRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<SupervisorRequest>(Convert.FromBase64String(payload));
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
        var environmentPointers = new List<IntPtr>();
        var environmentVector = IntPtr.Zero;
        var spawnAttributes = IntPtr.Zero;
        var spawnAttributesInitialized = false;
        MacProcessTreeTracker? macProcessTree = null;
        string? macMarkerPath = null;
        var macMarkerFd = -1;
        using var terminationSignal = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;
                Volatile.Write(ref terminationRequested, 1);
            });
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

            foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
            {
                environmentPointers.Add(Marshal.StringToCoTaskMemUTF8($"{pair.Key}={pair.Value}"));
            }

            environmentVector = Marshal.AllocHGlobal((environmentPointers.Count + 1) * IntPtr.Size);
            for (var index = 0; index < environmentPointers.Count; index++)
            {
                Marshal.WriteIntPtr(environmentVector, index * IntPtr.Size, environmentPointers[index]);
            }

            Marshal.WriteIntPtr(environmentVector, environmentPointers.Count * IntPtr.Size, IntPtr.Zero);

            if (setsid() < 0)
            {
                return 125;
            }

            // On Linux, keep a subreaper parent alive for the complete child lifetime.
            // This makes reparented descendants observable instead of treating the
            // original group disappearing as proof that the whole process tree is gone.
            if (OperatingSystem.IsLinux() && prctl(PrSetChildSubreaper, 1, 0, 0, 0) != 0)
            {
                return 125;
            }

            // macOS has no Linux-style subreaper. A non-CLOEXEC vnode inherited at
            // fork gives cleanup an ancestry-independent identity after reparenting.
            if (OperatingSystem.IsMacOS() && !TryCreateMacProcessMarker(out macMarkerPath, out macMarkerFd))
            {
                return 125;
            }

            spawnAttributes = Marshal.AllocHGlobal(PosixSpawnAttributeStorageSize);
            if (posix_spawnattr_init(spawnAttributes) != 0)
            {
                return 125;
            }

            spawnAttributesInitialized = true;
            var spawnFlags = (short)(PosixSpawnSetProcessGroup |
                (OperatingSystem.IsMacOS() ? PosixSpawnStartSuspended : 0));
            if (posix_spawnattr_setflags(spawnAttributes, spawnFlags) != 0 ||
                posix_spawnattr_setpgroup(spawnAttributes, 0) != 0)
            {
                return 125;
            }

            var spawnResult = posix_spawnp(
                out var child,
                filePointer,
                IntPtr.Zero,
                spawnAttributes,
                argumentVector,
                environmentVector);
            if (spawnResult != 0 || child <= 0)
            {
                return 125;
            }

            if (OperatingSystem.IsMacOS())
            {
                // Keep the root suspended until its kernel fork watch and inherited
                // marker are armed; a detached child cannot outrun containment setup.
                macProcessTree = MacProcessTreeTracker.TryCreate(child, macMarkerFd);
                if (macProcessTree is null || kill(child, SigCont) != 0)
                {
                    _ = kill(-child, SigKill);
                    return 125;
                }
            }

            SignalReady();
            var status = 0;
            if (WaitForChild(child, out status, macProcessTree) < 0)
            {
                return 125;
            }

            // The target has its own process group. Kill and verify that group before
            // claiming cleanup; on Linux also require the subreaper to have no adopted
            // child remaining, which keeps detached/reparented descendants unproven.
            var descendantsClean = KillAndVerifyProcessGroup(child, macProcessTree);
            if (!descendantsClean)
            {
                return 125;
            }

            SignalCleanupConfirmed();
            return DecodeExitStatus(status);
        }
        finally
        {
            if (spawnAttributesInitialized)
            {
                _ = posix_spawnattr_destroy(spawnAttributes);
            }

            if (spawnAttributes != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(spawnAttributes);
            }

            if (argumentVector != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(argumentVector);
            }

            if (environmentVector != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentVector);
            }

            foreach (var pointer in argumentPointers)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }

            foreach (var pointer in environmentPointers)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }

            macProcessTree?.Dispose();
            if (macMarkerFd >= 0)
            {
                _ = close(macMarkerFd);
            }

            if (macMarkerPath is not null)
            {
                UnlinkMacPath(macMarkerPath);
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    private const short PosixSpawnSetProcessGroup = 0x2;
    private const short PosixSpawnStartSuspended = 0x80;
    private const int PosixSpawnAttributeStorageSize = 512;

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_init(IntPtr attributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_destroy(IntPtr attributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_setpgroup(IntPtr attributes, int processGroupId);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnp(
        out int processId,
        IntPtr file,
        IntPtr fileActions,
        IntPtr attributes,
        IntPtr argumentVector,
        IntPtr environment);

    private static void SignalReady()
    {
        var signal = Encoding.UTF8.GetBytes(ReadinessSignal);
        _ = write(1, signal, (nuint)signal.Length);
    }

    private static void SignalCleanupConfirmed()
    {
        var signal = Encoding.UTF8.GetBytes(BoundedProcess.CleanupSignal);
        _ = write(1, signal, (nuint)signal.Length);
    }

    private static int WaitForChild(
        int child,
        out int status,
        MacProcessTreeTracker? macProcessTree)
    {
        status = 0;
        while (true)
        {
            if (macProcessTree is not null && !macProcessTree.Pump())
            {
                return -1;
            }

            if (Volatile.Read(ref terminationRequested) != 0)
            {
                _ = kill(-child, SigKill);
                macProcessTree?.KillTrackedDescendants();
            }

            var result = waitpid(child, out status, WaitNoHang);
            if (result != 0)
            {
                return result;
            }

            Thread.Sleep(10);
        }
    }

    private static bool ReapDescendants()
    {
        if (OperatingSystem.IsLinux())
        {
            return KillAndReapLinuxDescendants();
        }

        var deadline = DateTime.UtcNow + (OperatingSystem.IsMacOS()
            ? TimeSpan.FromSeconds(5)
            : TimeSpan.FromMilliseconds(250));
        while (DateTime.UtcNow < deadline)
        {
            var child = waitpid(-1, out _, WaitNoHang);
            if (child < 0)
            {
                return Marshal.GetLastWin32Error() == NoChildrenError;
            }

            if (child == 0)
            {
                Thread.Sleep(10);
                continue;
            }
        }

        return waitpid(-1, out _, WaitNoHang) < 0 && Marshal.GetLastWin32Error() == NoChildrenError;
    }

    private static bool KillAndReapLinuxDescendants()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
        var childrenPath = $"/proc/{Environment.ProcessId}/task/{Environment.ProcessId}/children";
        while (DateTime.UtcNow < deadline)
        {
            var adopted = ReadLinuxAdoptedChildren(childrenPath);
            foreach (var child in adopted)
            {
                _ = kill(child, SigKill);
            }

            var reapedAny = false;
            while (waitpid(-1, out _, WaitNoHang) > 0)
            {
                reapedAny = true;
            }

            if (ReadLinuxAdoptedChildren(childrenPath).Length == 0)
            {
                var waitResult = waitpid(-1, out _, WaitNoHang);
                return waitResult < 0 && Marshal.GetLastWin32Error() == NoChildrenError;
            }

            if (!reapedAny && adopted.Length == 0)
            {
                Thread.Sleep(10);
            }
        }

        return ReadLinuxAdoptedChildren(childrenPath).Length == 0 &&
               waitpid(-1, out _, WaitNoHang) < 0 &&
               Marshal.GetLastWin32Error() == NoChildrenError;
    }

    private static int[] ReadLinuxAdoptedChildren(string childrenPath)
    {
        try
        {
            return File.ReadAllText(childrenPath)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out var pid) ? pid : 0)
                .Where(pid => pid > 0)
                .ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<int>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<int>();
        }
    }

    private static bool KillAndVerifyProcessGroup(
        int processGroupId,
        MacProcessTreeTracker? macProcessTree = null)
    {
        _ = kill(-processGroupId, SigKill);
        macProcessTree?.DrainAndDiscover(forceDiscovery: true);
        macProcessTree?.KillTrackedDescendants();

        var deadline = DateTime.UtcNow + (OperatingSystem.IsMacOS()
            ? TimeSpan.FromSeconds(5)
            : TimeSpan.FromMilliseconds(250));
        while (DateTime.UtcNow < deadline)
        {
            if (macProcessTree is not null && !macProcessTree.DrainAndDiscover())
            {
                return false;
            }

            macProcessTree?.KillTrackedDescendants();

            if (OperatingSystem.IsLinux())
            {
                while (waitpid(-1, out _, WaitNoHang) > 0)
                {
                }
            }

            if (kill(-processGroupId, 0) != 0)
            {
                var groupError = Marshal.GetLastWin32Error();
                if (OperatingSystem.IsMacOS() &&
                    MacProcessGroupHasNoLiveMembers(processGroupId) &&
                    macProcessTree is not null &&
                    macProcessTree.HasNoLiveDescendants())
                {
                    return true;
                }

                return groupError == NoSuchProcessError &&
                    (!OperatingSystem.IsLinux() || ReapDescendants());
            }

            if (OperatingSystem.IsMacOS() &&
                MacProcessGroupHasNoLiveMembers(processGroupId) &&
                macProcessTree is not null &&
                macProcessTree.HasNoLiveDescendants())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        if (OperatingSystem.IsMacOS() &&
            MacProcessGroupHasNoLiveMembers(processGroupId) &&
            macProcessTree is not null &&
            macProcessTree.HasNoLiveDescendants())
        {
            return true;
        }

        return kill(-processGroupId, 0) != 0 &&
            Marshal.GetLastWin32Error() == NoSuchProcessError &&
            (!OperatingSystem.IsLinux() || ReapDescendants());
    }

    private static bool MacProcessGroupHasNoLiveMembers(int processGroupId)
    {
        if (kill(-processGroupId, 0) != 0)
        {
            return Marshal.GetLastWin32Error() == NoSuchProcessError;
        }

        var members = ReadMacProcessGroupMembers(processGroupId);
        return members is not null && members.All(processId => !MacProcessIsLive(processId));
    }

    private sealed class MacProcessTreeTracker : IDisposable
    {
        private readonly int queue;
        private readonly int rootProcessId;
        private readonly int supervisorProcessId;
        private readonly MacProcessMarker marker;
        private readonly Dictionary<int, ulong> tracked = new();
        private bool disposed;
        private bool healthy = true;

        private MacProcessTreeTracker(
            int queue,
            int rootProcessId,
            ulong rootUniqueId,
            int supervisorProcessId,
            MacProcessMarker marker)
        {
            this.queue = queue;
            this.rootProcessId = rootProcessId;
            this.supervisorProcessId = supervisorProcessId;
            this.marker = marker;
            tracked[rootProcessId] = rootUniqueId;
        }

        public static MacProcessTreeTracker? TryCreate(int rootProcessId, int markerFd)
        {
            var queue = kqueue();
            if (queue < 0 || !TryGetMacProcessIdentity(rootProcessId, out var rootIdentity))
            {
                if (queue >= 0)
                {
                    _ = close(queue);
                }

                return null;
            }

            var marker = TryGetMacProcessMarker(Environment.ProcessId, markerFd, out var discoveredMarker)
                ? discoveredMarker
                : new MacProcessMarker(markerFd, 0, 0);

            var tracker = new MacProcessTreeTracker(
                queue,
                rootProcessId,
                rootIdentity.UniqueId,
                Environment.ProcessId,
                marker);
            if (!tracker.Register(rootProcessId))
            {
                tracker.Dispose();
                return null;
            }

            return tracker;
        }

        public bool Pump()
        {
            if (!ReadEvents(wait: true, out var forkObserved))
            {
                return false;
            }

            return !forkObserved || DiscoverDescendants();
        }

        public bool DrainAndDiscover(bool forceDiscovery = false)
        {
            if (!ReadEvents(wait: false, out var forkObserved))
            {
                return false;
            }

            return forkObserved || forceDiscovery ? DiscoverDescendants() : true;
        }

        public void KillTrackedDescendants()
        {
            foreach (var pair in tracked)
            {
                if (pair.Key == rootProcessId ||
                    !TryGetMacProcessIdentity(pair.Key, out var identity) ||
                    identity.UniqueId != pair.Value)
                {
                    continue;
                }

                _ = kill(pair.Key, SigKill);
            }
        }

        public bool HasNoLiveDescendants()
        {
            if (!DiscoverDescendants())
            {
                return false;
            }

            foreach (var pair in tracked)
            {
                if (pair.Key == rootProcessId)
                {
                    continue;
                }

                if (!TryGetMacProcessIdentity(pair.Key, out var identity))
                {
                    if (MacProcessIsLive(pair.Key))
                    {
                        return false;
                    }

                    continue;
                }

                if (identity.UniqueId != pair.Value)
                {
                    continue;
                }

                if (MacProcessIsLive(pair.Key))
                {
                    return false;
                }
            }

            return true;
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                _ = close(queue);
            }
        }

        private bool DiscoverDescendants()
        {
            var snapshot = ReadMacProcessIdentities();
            if (snapshot is null)
            {
                return false;
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var process in snapshot)
                {
                    if (tracked.ContainsKey(process.ProcessId) ||
                        !tracked.ContainsValue(process.ParentUniqueId) ||
                        !MacProcessIsLive(process.ProcessId))
                    {
                        continue;
                    }

                    if (!Track(process.ProcessId, process.UniqueId))
                    {
                        return false;
                    }

                    changed = true;
                }
            }

            foreach (var process in snapshot)
            {
                if (process.ProcessId == rootProcessId ||
                    process.ProcessId == supervisorProcessId ||
                    tracked.ContainsKey(process.ProcessId) ||
                    !MacProcessHasMarker(process.ProcessId, marker))
                {
                    continue;
                }

                if (!Track(process.ProcessId, process.UniqueId))
                {
                    return false;
                }
            }

            return true;
        }

        private bool Track(int processId, ulong uniqueId)
        {
            if (tracked.ContainsKey(processId) || processId == supervisorProcessId)
            {
                return true;
            }

            if (!Register(processId))
            {
                return false;
            }

            tracked[processId] = uniqueId;
            return true;
        }

        private bool Register(int processId)
        {
            var change = new MacKevent
            {
                Ident = (nuint)processId,
                Filter = EvfiltProc,
                Flags = EvAdd | EvEnable | EvClear,
                Fflags = NoteFork | NoteExit
            };
            var result = kevent(queue, ref change, 1, IntPtr.Zero, 0, IntPtr.Zero);
            if (result == 0)
            {
                return true;
            }

            return Marshal.GetLastWin32Error() == NoSuchProcessError && !MacProcessIsLive(processId);
        }

        private bool ReadEvents(bool wait, out bool forkObserved)
        {
            forkObserved = false;
            var timeout = new MacTimespec
            {
                Seconds = 0,
                Nanoseconds = wait ? 100_000_000 : 0
            };
            var events = new MacKevent[64];
            var result = kevent(queue, IntPtr.Zero, 0, events, events.Length, ref timeout);
            if (result < 0)
            {
                return Marshal.GetLastWin32Error() == InterruptedSystemCallError;
            }

            for (var index = 0; index < result; index++)
            {
                if ((events[index].Flags & EvError) != 0)
                {
                    healthy = false;
                    return false;
                }

                if ((events[index].Fflags & NoteFork) != 0)
                {
                    forkObserved = true;
                    var childProcessId = (int)(events[index].Fflags & NoteProcessIdMask);
                    if (childProcessId > 0)
                    {
                        if (TryGetMacProcessIdentity(childProcessId, out var childIdentity))
                        {
                            if (!Track(childProcessId, childIdentity.UniqueId))
                            {
                                return false;
                            }
                        }
                        else if (MacProcessIsLive(childProcessId))
                        {
                            return false;
                        }
                    }
                }
            }

            return healthy;
        }
    }

    private static bool MacProcessIsLive(int processId)
    {
        if (TryGetMacProcessStatus(processId, out var status))
        {
            return status != MacZombieStatus;
        }

        var result = kill(processId, 0);
        return result == 0 || Marshal.GetLastWin32Error() != NoSuchProcessError;
    }

    private static bool TryCreateMacProcessMarker(out string? path, out int fileDescriptor)
    {
        path = null;
        fileDescriptor = -1;
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var candidate = Path.Combine(
                    Path.GetTempPath(),
                    $"nugetready-process-marker-{Guid.NewGuid():N}");
                var candidatePointer = Marshal.StringToCoTaskMemUTF8(candidate);
                var descriptor = open(candidatePointer, OpenCreate | OpenExclusive | OpenReadWrite, 0x180);
                Marshal.FreeCoTaskMem(candidatePointer);
                if (descriptor < 0)
                {
                    continue;
                }

                if (fcntl(descriptor, FSetFileDescriptorFlags, 0) != 0)
                {
                    _ = close(descriptor);
                    UnlinkMacPath(candidate);
                    continue;
                }

                path = candidate;
                fileDescriptor = descriptor;
                return true;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static void UnlinkMacPath(string path)
    {
        var pathPointer = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            _ = unlink(pathPointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private static bool TryGetMacProcessIdentity(int processId, out MacProcessIdentity identity)
    {
        identity = default;
        var buffer = Marshal.AllocHGlobal(MacProcessIdentitySize);
        try
        {
            if (proc_pidinfo(processId, ProcPidUniqIdentifierInfo, 0, buffer, MacProcessIdentitySize) < MacProcessIdentitySize)
            {
                return false;
            }

            identity = new MacProcessIdentity(
                processId,
                unchecked((ulong)Marshal.ReadInt64(buffer, 16)),
                unchecked((ulong)Marshal.ReadInt64(buffer, 24)));
            return identity.UniqueId != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetMacProcessStatus(int processId, out int status)
    {
        status = 0;
        var buffer = Marshal.AllocHGlobal(MacBsdInfoSize);
        try
        {
            if (proc_pidinfo(processId, ProcPidBsdInfo, 0, buffer, MacBsdInfoSize) < MacBsdInfoSize)
            {
                return false;
            }

            status = Marshal.ReadInt32(buffer, 4);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetMacProcessMarker(
        int processId,
        int fileDescriptor,
        out MacProcessMarker marker)
    {
        marker = default;
        var buffer = Marshal.AllocHGlobal(MacVnodeFdInfoSize);
        try
        {
            var result = proc_pidfdinfo(
                    processId,
                    fileDescriptor,
                    ProcPidFdVnodePathInfo,
                    buffer,
                    MacVnodeFdInfoSize);
            if (result < MacVnodeStatOffset + sizeof(ulong))
            {
                return false;
            }

            marker = new MacProcessMarker(
                fileDescriptor,
                unchecked((uint)Marshal.ReadInt32(buffer, MacVnodeStatOffset)),
                unchecked((ulong)Marshal.ReadInt64(buffer, MacVnodeStatOffset + 8)));
            return marker.Inode != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool MacProcessHasMarker(int processId, MacProcessMarker marker)
    {
        var size = proc_pidinfo(processId, ProcPidListFds, 0, IntPtr.Zero, 0);
        if (size <= 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = proc_pidinfo(processId, ProcPidListFds, 0, buffer, size);
            if (result <= 0)
            {
                return false;
            }

            for (var offset = 0; offset + MacProcFdInfoSize <= result; offset += MacProcFdInfoSize)
            {
                var fileDescriptor = Marshal.ReadInt32(buffer, offset);
                var fileType = unchecked((uint)Marshal.ReadInt32(buffer, offset + sizeof(int)));
                if (fileType == MacVnodeFileType && fileDescriptor == marker.FileDescriptor)
                {
                    return true;
                }

                if (fileType == MacVnodeFileType &&
                    marker.Inode != 0 &&
                    TryGetMacProcessMarker(processId, fileDescriptor, out var candidate) &&
                    candidate.Device == marker.Device &&
                    candidate.Inode == marker.Inode)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static MacProcessIdentity[]? ReadMacProcessIdentities()
    {
        var processIds = ReadMacProcessIds();
        if (processIds is null)
        {
            return null;
        }

        var identities = new List<MacProcessIdentity>(processIds.Length);
        foreach (var processId in processIds)
        {
            if (processId > 0 && TryGetMacProcessIdentity(processId, out var identity))
            {
                identities.Add(identity);
            }
        }

        return identities.ToArray();
    }

    private static int[]? ReadMacProcessIds()
    {
        var capacity = 1024;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(checked(capacity * sizeof(int)));
            try
            {
                var count = proc_listallpids(buffer, checked(capacity * sizeof(int)));
                if (count < 0)
                {
                    return null;
                }

                if (count < capacity)
                {
                    var processIds = new int[count];
                    for (var index = 0; index < count; index++)
                    {
                        processIds[index] = Marshal.ReadInt32(buffer, index * sizeof(int));
                    }

                    return processIds;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            capacity = checked(capacity * 2);
        }

        return null;
    }

    private static int[]? ReadMacProcessGroupMembers(int processGroupId)
    {
        var capacity = 64;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(checked(capacity * sizeof(int)));
            try
            {
                var count = proc_listpgrppids(processGroupId, buffer, checked(capacity * sizeof(int)));
                if (count < 0)
                {
                    return null;
                }

                if (count < capacity)
                {
                    var processIds = new int[count];
                    for (var index = 0; index < count; index++)
                    {
                        processIds[index] = Marshal.ReadInt32(buffer, index * sizeof(int));
                    }

                    return processIds;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            capacity = checked(capacity * 2);
        }

        return null;
    }

    private readonly record struct MacProcessIdentity(int ProcessId, ulong UniqueId, ulong ParentUniqueId);
    private readonly record struct MacProcessMarker(int FileDescriptor, uint Device, ulong Inode);

    [StructLayout(LayoutKind.Sequential)]
    private struct MacKevent
    {
        public nuint Ident;
        public short Filter;
        public ushort Flags;
        public uint Fflags;
        public nint Data;
        public nint Udata;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kqueue();

    [DllImport("libc", SetLastError = true)]
    private static extern int kevent(
        int queue,
        ref MacKevent change,
        int changeCount,
        IntPtr events,
        int eventCount,
        IntPtr timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int kevent(
        int queue,
        IntPtr changes,
        int changeCount,
        [Out] MacKevent[] events,
        int eventCount,
        ref MacTimespec timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fileDescriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int open(IntPtr path, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlink(IntPtr path);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fileDescriptor, int command, int argument);

    [DllImport("libproc.dylib", SetLastError = true)]
    private static extern int proc_listallpids(IntPtr buffer, int bufferSize);

    [DllImport("libproc.dylib", SetLastError = true)]
    private static extern int proc_listpgrppids(int processGroupId, IntPtr buffer, int bufferSize);

    [DllImport("libproc.dylib", SetLastError = true)]
    private static extern int proc_pidinfo(int processId, int flavor, ulong argument, IntPtr buffer, int bufferSize);

    [DllImport("libproc.dylib", SetLastError = true)]
    private static extern int proc_pidfdinfo(
        int processId,
        int fileDescriptor,
        int flavor,
        IntPtr buffer,
        int bufferSize);

    private static int DecodeExitStatus(int status)
    {
        return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
    }

    private const int PrSetChildSubreaper = 36;
    private const int SigKill = 9;
    private const int SigTerm = 15;
    private const int SigCont = 18;
    private const int WaitNoHang = 1;
    private const int NoChildrenError = 10;
    private const int NoSuchProcessError = 3;
    private const int InterruptedSystemCallError = 4;
    private const int MacZombieStatus = 5;
    private const int EvfiltProc = -5;
    private const ushort EvAdd = 0x0001;
    private const ushort EvEnable = 0x0004;
    private const ushort EvClear = 0x0020;
    private const ushort EvError = 0x4000;
    private const uint NoteExit = 0x80000000;
    private const uint NoteFork = 0x40000000;
    private const int ProcPidBsdInfo = 3;
    private const int ProcPidUniqIdentifierInfo = 17;
    private const int MacBsdInfoSize = 136;
    private const int MacProcessIdentitySize = 72;
    private const int OpenReadWrite = 0x2;
    private const int OpenCreate = 0x200;
    private const int OpenExclusive = 0x800;
    private const int FSetFileDescriptorFlags = 2;
    private const int ProcPidListFds = 1;
    private const int ProcPidFdVnodePathInfo = 2;
    private const int MacProcFdInfoSize = 8;
    private const int MacVnodeFileType = 1;
    private const int MacVnodeFdInfoSize = 1200;
    private const int MacVnodeStatOffset = 24;
    private const uint NoteProcessIdMask = 0x000fffff;

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, int arg2, int arg3, int arg4, int arg5);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int processId, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fileDescriptor, byte[] buffer, nuint count);

}

internal static class WindowsProcessSupervisor
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint Infinite = 0xFFFFFFFF;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    public static int Run(string payload)
    {
        UnixProcessSupervisor.SupervisorRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<UnixProcessSupervisor.SupervisorRequest>(Convert.FromBase64String(payload));
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

        using var job = CreateKillOnCloseJob();
        if (job is null)
        {
            return 125;
        }

        var commandLine = new StringBuilder(string.Join(
            " ",
            new[] { Quote(request.FileName) }.Concat(request.Arguments.Select(Quote))));
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = StartfUseStdHandles,
            StandardOutput = GetStdHandle(StdOutputHandle),
            StandardError = GetStdHandle(StdErrorHandle),
            StandardInput = GetStdHandle(-10)
        };

        if (!CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: true,
                CreateSuspended | CreateNoWindow,
                IntPtr.Zero,
                Environment.CurrentDirectory,
                ref startup,
                out var processInfo))
        {
            return 127;
        }

        using var processHandle = new SafeProcessHandle(processInfo.ProcessHandle);
        using var threadHandle = new SafeThreadHandle(processInfo.ThreadHandle);
        if (!AssignProcessToJobObject(job, processHandle.Handle) || ResumeThread(threadHandle.Handle) == Infinite)
        {
            _ = TerminateProcess(processHandle.Handle, 1);
            return 125;
        }

        WriteReadinessSignal();
        _ = WaitForSingleObject(processHandle.Handle, Infinite);
        var exitCode = GetExitCode(processHandle.Handle);
        if (!TerminateJobObject(job, 1))
        {
            return 125;
        }

        WriteCleanupSignal();
        return exitCode;
    }

    private static SafeJobHandle? CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job is null || job.IsInvalid)
        {
            job?.Dispose();
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
            job.Dispose();
            return null;
        }

        return job;
    }

    private static void WriteReadinessSignal()
    {
        using var output = new FileStream(
            new SafeFileHandle(GetStdHandle(StdOutputHandle), ownsHandle: false),
            FileAccess.Write);
        var bytes = Encoding.UTF8.GetBytes(UnixProcessSupervisor.ReadinessSignal);
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }

    private static void WriteCleanupSignal()
    {
        using var output = new FileStream(
            new SafeFileHandle(GetStdHandle(StdOutputHandle), ownsHandle: false),
            FileAccess.Write);
        var bytes = Encoding.UTF8.GetBytes(BoundedProcess.CleanupSignal);
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }

    private static int GetExitCode(IntPtr process)
    {
        return GetExitCodeProcess(process, out var exitCode) ? unchecked((int)exitCode) : 125;
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(character);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

#pragma warning disable CA1838
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);
#pragma warning restore CA1838

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        uint informationClass,
        ref JobObjectExtendedLimitInformation limits,
        uint limitsLength);


    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved3;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

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

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeProcessHandle(IntPtr handle)
            : base(ownsHandle: true) => SetHandle(handle);

        public IntPtr Handle => DangerousGetHandle();

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeThreadHandle(IntPtr handle)
            : base(ownsHandle: true) => SetHandle(handle);

        public IntPtr Handle => DangerousGetHandle();

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
