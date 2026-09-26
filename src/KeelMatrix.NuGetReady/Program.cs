namespace KeelMatrix.NuGetReady;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["--internal-unix-supervisor", var unixPayload])
        {
            return UnixProcessSupervisor.Run(unixPayload);
        }

        if (args is ["--internal-windows-supervisor", var windowsPayload])
        {
            return WindowsProcessSupervisor.Run(windowsPayload);
        }

        return NuGetReadyApplication.Run(args, new NuGetReadyTelemetry());
    }
}

internal static class NuGetReadyApplication
{
    internal static int Run(string[] args, IUsageTelemetry telemetry)
    {
        CliOptions? options = null;
        try
        {
            options = CliParser.Parse(args);
            var configPath = Path.GetFullPath(options.ConfigPath);
            var config = ConfigurationLoader.Load(configPath);
            var repositoryPath = RepositoryLocator.FindRoot(configPath);
            var report = CheckRunner.Run(
                config,
                options.ArtifactsPath,
                repositoryPath,
                options.Timeout,
                configPath: configPath);
            ReportWriter.Write(report, options.Format);
            TelemetryCoordinator.RecordIfTrustworthy(report, telemetry);
            return report.ExitCode;
        }
        catch (HelpRequestedException)
        {
            Console.WriteLine(CliParser.HelpText);
            return 0;
        }
        catch (CliInputException exception)
        {
            WriteError(exception.Message, exception.Format);
            return 2;
        }
        catch (NuGetReadyInputException exception)
        {
            WriteError(exception.Message, options?.Format ?? OutputFormat.Text);
            return 2;
        }
        catch (NuGetReadyInfrastructureException exception)
        {
            WriteError(exception.Message, options?.Format ?? OutputFormat.Text);
            return 2;
        }
        catch (Exception)
        {
            WriteError("NuGetReady could not complete because of an infrastructure error.", options?.Format ?? OutputFormat.Text);
            return 2;
        }
    }

    private static void WriteError(string message, OutputFormat format)
    {
        if (format == OutputFormat.Json)
        {
            var failure = new Failure("input", message, true);
            ReportWriter.Write(new ReadinessReport
            {
                Status = "error",
                ExitCode = 2,
                Checks = new[] { new CheckResult("input", new[] { failure }) },
                Failures = new[] { failure }
            }, format);
        }
        else
        {
            Console.Error.WriteLine($"Configuration error: {message}");
        }
    }
}

internal static class RepositoryLocator
{
    public static string FindRoot(string configPath)
    {
        var configDirectory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        for (var directory = configDirectory; directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".github")))
            {
                return directory.FullName;
            }
        }

        return configDirectory.FullName;
    }
}
