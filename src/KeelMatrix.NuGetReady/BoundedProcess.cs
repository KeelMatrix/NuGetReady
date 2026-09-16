using System.ComponentModel;
using System.Diagnostics;
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

        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        return new ProcessResult(
            true,
            timedOut ? -1 : process.ExitCode,
            timedOut,
            standardOutput.Result,
            standardError.Result);
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
}
