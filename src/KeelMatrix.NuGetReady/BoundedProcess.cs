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
            var standardOutput = CaptureAsync(process.StandardOutput, outputLimit, lifecycleCancellation.Token, readiness);
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
        TaskCompletionSource<bool>? readiness = null)
    {
        var builder = new StringBuilder(Math.Min(limit, 4096));
        var buffer = new char[4096];
        var pending = string.Empty;
        var truncated = false;

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
                var markerIndex = content.IndexOf(UnixProcessSupervisor.ReadinessSignal, StringComparison.Ordinal);
                if (markerIndex >= 0)
                {
                    AppendOutput(builder, content.AsSpan(0, markerIndex), limit, ref truncated);
                    readiness?.TrySetResult(true);
                    content = content[(markerIndex + UnixProcessSupervisor.ReadinessSignal.Length)..];
                    pending = string.Empty;
                }

                if (readiness is not null && !readiness.Task.IsCompleted)
                {
                    var keep = Math.Min(content.Length, UnixProcessSupervisor.ReadinessSignal.Length - 1);
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
                var groupKill = kill(-process.Id, SigKill);
                if (groupKill == 0)
                {
                    return WaitForUnixProcessGroupExit(process.Id) && WaitForDirectExit(process);
                }

                var groupError = Marshal.GetLastWin32Error();
                var directExited = WaitForDirectExit(process);
                if (!directExited)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    directExited = WaitForDirectExit(process);
                }

                // ESRCH alone does not establish that descendants were cleaned up. A naturally
                // completed process may use it only after readiness and direct-exit proof.
                return groupError == NoSuchProcessError && naturalCompletion && processGroupReady && directExited;
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

    private static bool WaitForDirectExit(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            return process.WaitForExit((int)TerminationGracePeriod.TotalMilliseconds) && process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
    internal const string ReadinessSignal = "\u001eNU_GETREADY_SUPERVISOR_READY\u001e";

    internal sealed record SupervisorRequest(string FileName, string[] Arguments);

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

            SignalReady();
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

    private static void SignalReady()
    {
        var signal = Encoding.UTF8.GetBytes(ReadinessSignal);
        _ = write(1, signal, (nuint)signal.Length);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fileDescriptor, byte[] buffer, nuint count);

    [DllImport("libc")]
    private static extern void _exit(int status);
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
