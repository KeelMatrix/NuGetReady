using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace KeelMatrix.NuGetReady;

internal sealed record ProcessResult(
    bool Started,
    int ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError);

internal static class BoundedProcess
{
    private const int DefaultOutputLimit = 16 * 1024;

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        int outputLimit = DefaultOutputLimit,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in environment)
        {
            process.StartInfo.Environment[pair.Key] = pair.Value;
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
        try
        {
            var standardOutput = CaptureAsync(process.StandardOutput, outputLimit, cancellationToken);
            var standardError = CaptureAsync(process.StandardError, outputLimit, cancellationToken);
            var waitForExit = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(waitForExit, timeoutTask).ConfigureAwait(false);
            var timedOut = completed != waitForExit;

            if (timedOut)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            processJob?.Dispose();
            processJob = null;
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            return new ProcessResult(
                true,
                timedOut ? -1 : process.ExitCode,
                timedOut,
                standardOutput.Result,
                standardError.Result);
        }
        finally
        {
            processJob?.Dispose();
        }
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

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
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
