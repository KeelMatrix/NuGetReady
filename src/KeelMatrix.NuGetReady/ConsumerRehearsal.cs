using System.Xml.Linq;
using NuGet.Packaging;

namespace KeelMatrix.NuGetReady;

internal sealed record ConsumerRehearsalOptions(
    bool IncludeLocalFeed = true,
    bool SeedPackageCache = false,
    string? PublicFeedPath = null);

internal sealed record RehearsalOutcome(RehearsalResult Result, string Diagnostic);

internal static class ConsumerRehearsal
{
    private const int DiagnosticLimit = 16 * 1024;
    private static readonly string[] PublicPackagePatterns =
    {
        "System.*",
        "Microsoft.*",
        "NuGet.*",
        "runtime.*",
        "NETStandard.Library",
        "NETCore.App.*"
    };

    public static IReadOnlyList<RehearsalOutcome> RunDetailed(
        NuGetReadyConfig config,
        string artifactsPath,
        TimeSpan timeout,
        ConsumerRehearsalOptions? options = null)
    {
        options ??= new ConsumerRehearsalOptions();
        var root = Directory.CreateTempSubdirectory("nugetready-consumer-");

        try
        {
            var feedPath = Directory.CreateDirectory(Path.Combine(root.FullName, "feed")).FullName;
            var cachePath = Directory.CreateDirectory(Path.Combine(root.FullName, "packages")).FullName;
            var cliHome = Directory.CreateDirectory(Path.Combine(root.FullName, "cli-home")).FullName;
            var localPackageIds = config.Packages!
                .Select(package => package.Id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var publicDependencyIds = ReadPublicDependencyIds(config, artifactsPath, localPackageIds);
            var packagePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<RehearsalOutcome>();

            foreach (var package in config.Packages!)
            {
                var packageArtifact = package.Artifacts!.FirstOrDefault(artifact => artifact.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase));
                if (packageArtifact is null)
                {
                    results.Add(Failure(package, "No .nupkg artifact was declared for the package.", isError: true));
                    continue;
                }

                var sourcePath = Path.Combine(artifactsPath, packageArtifact);
                if (!File.Exists(sourcePath))
                {
                    results.Add(Failure(package, "The package artifact was not found for consumer rehearsal.", isError: true));
                    continue;
                }

                packagePaths[package.Id!] = sourcePath;
                if (options.IncludeLocalFeed)
                {
                    File.Copy(sourcePath, Path.Combine(feedPath, packageArtifact), overwrite: false);
                }
            }

            if (options.SeedPackageCache)
            {
                foreach (var package in config.Packages!)
                {
                    var version = VersionText.Normalize(package.Version!);
                    var cachePackage = Directory.CreateDirectory(Path.Combine(cachePath, package.Id!, version));
                    File.WriteAllText(Path.Combine(cachePackage.FullName, ".seeded-copy"), "seeded package copy");
                }
            }

            var configPath = Path.Combine(root.FullName, "NuGet.config");
            WriteNuGetConfig(configPath, feedPath, options, localPackageIds, publicDependencyIds);
            var environment = new Dictionary<string, string?>
            {
                ["NUGET_PACKAGES"] = cachePath,
                ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(root.FullName, "http-cache"),
                ["DOTNET_CLI_HOME"] = cliHome,
                ["DOTNET_NOLOGO"] = "1",
                ["NUGET_XMLDOC_MODE"] = "skip",
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
            };

            foreach (var package in config.Packages!)
            {
                if (!packagePaths.TryGetValue(package.Id!, out var packagePath))
                {
                    continue;
                }

                var cacheIssue = FindPreexistingCache(package, cachePath);
                if (cacheIssue is not null)
                {
                    results.Add(Failure(package, cacheIssue, isError: true));
                    continue;
                }

                var packageRoot = Directory.CreateDirectory(Path.Combine(root.FullName, SanitizeDirectoryName(package.Id!))).FullName;
                var packageResult = package.Kind!.Equals("dotnetTool", StringComparison.OrdinalIgnoreCase)
                    ? RunToolAsync(package, packagePath, packageRoot, configPath, environment, timeout).GetAwaiter().GetResult()
                    : RunLibraryAsync(package, packageRoot, configPath, environment, timeout).GetAwaiter().GetResult();
                results.Add(packageResult);
            }

            return results;
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static IReadOnlyList<RehearsalResult> Run(
        NuGetReadyConfig config,
        string artifactsPath,
        TimeSpan timeout,
        ConsumerRehearsalOptions? options = null)
    {
        return RunDetailed(config, artifactsPath, timeout, options).Select(outcome => outcome.Result).ToArray();
    }

    private static async Task<RehearsalOutcome> RunLibraryAsync(
        PackageExpectation package,
        string packageRoot,
        string configPath,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout)
    {
        var projectPath = Path.Combine(packageRoot, "Consumer.csproj");
        var sourcePath = Path.Combine(packageRoot, "Program.cs");
        await File.WriteAllTextAsync(projectPath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <UseAppHost>false</UseAppHost>
                <RestoreNoCache>true</RestoreNoCache>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{EscapeXml(package.Id!)}" Version="{EscapeXml(VersionText.Normalize(package.Version!))}" />
              </ItemGroup>
            </Project>
            """).ConfigureAwait(false);
        await File.WriteAllTextAsync(sourcePath, "Console.WriteLine(\"consumer-ok\");\n").ConfigureAwait(false);

        var restore = await RunDotnetAsync(
            ["restore", projectPath, "--configfile", configPath, "--no-cache", "--force-evaluate", "--nologo"],
            packageRoot,
            environment,
            timeout).ConfigureAwait(false);
        if (!Succeeded(restore))
        {
            return Failure(package, "The isolated library consumer could not restore the exact package.", IsInfrastructure(restore), Combine(restore));
        }

        var build = await RunDotnetAsync(
            ["build", projectPath, "--no-restore", "--nologo", "--configuration", "Release", "-p:UseSharedCompilation=false"],
            packageRoot,
            environment,
            timeout).ConfigureAwait(false);
        return Succeeded(build)
            ? Success(package, "Isolated library consumer restored and built.", Combine(build))
            : Failure(package, "The isolated library consumer did not build.", IsInfrastructure(build), Combine(build));
    }

    private static async Task<RehearsalOutcome> RunToolAsync(
        PackageExpectation package,
        string packagePath,
        string packageRoot,
        string configPath,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout)
    {
        var toolPath = Directory.CreateDirectory(Path.Combine(packageRoot, "tool")).FullName;
        var install = await RunDotnetAsync(
            ["tool", "install", package.Id!, "--version", VersionText.Normalize(package.Version!), "--tool-path", toolPath, "--configfile", configPath, "--no-cache", "--verbosity", "quiet"],
            packageRoot,
            environment,
            timeout).ConfigureAwait(false);
        if (!Succeeded(install))
        {
            return Failure(package, "The isolated tool could not be installed from the controlled feed.", IsInfrastructure(install), Combine(install));
        }

        var command = package.Command ?? package.Id!;
        var executable = Path.Combine(toolPath, OperatingSystem.IsWindows() ? command + ".exe" : command);
        if (!File.Exists(executable))
        {
            executable = Path.Combine(toolPath, command);
        }

        if (!File.Exists(executable))
        {
            return Failure(package, "The installed tool command was not created.", false, string.Empty);
        }

        var smoke = package.Smoke?.ToArray() ?? Array.Empty<string>();
        var run = await BoundedProcess.RunAsync(executable, smoke, packageRoot, environment, timeout).ConfigureAwait(false);
        return Succeeded(run)
            ? Success(package, "Isolated tool installed and safe smoke command succeeded.", Combine(run))
            : Failure(package, "The installed tool safe smoke command failed.", IsInfrastructure(run), Combine(run));
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout)
    {
        return await BoundedProcess.RunAsync("dotnet", arguments, workingDirectory, environment, timeout).ConfigureAwait(false);
    }

    private static bool Succeeded(ProcessResult result)
    {
        return result.Started && !result.TimedOut && result.ExitCode == 0;
    }

    private static bool IsInfrastructure(ProcessResult result)
    {
        return !result.Started || result.TimedOut;
    }

    private static RehearsalOutcome Success(PackageExpectation package, string message, string diagnostic)
    {
        return new RehearsalOutcome(new RehearsalResult(package.Id!, package.Kind!, "pass", message), diagnostic);
    }

    private static RehearsalOutcome Failure(PackageExpectation package, string message, bool isError, string diagnostic = "")
    {
        return new RehearsalOutcome(new RehearsalResult(package.Id!, package.Kind!, isError ? "error" : "fail", message, isError), diagnostic);
    }

    private static string? FindPreexistingCache(PackageExpectation package, string cachePath)
    {
        var packageDirectory = Directory.EnumerateDirectories(cachePath)
            .FirstOrDefault(directory => string.Equals(Path.GetFileName(directory), package.Id, StringComparison.OrdinalIgnoreCase));
        if (packageDirectory is null)
        {
            return null;
        }

        var version = VersionText.Normalize(package.Version!);
        if (Directory.EnumerateDirectories(packageDirectory)
            .Any(directory => string.Equals(Path.GetFileName(directory), version, StringComparison.OrdinalIgnoreCase)))
        {
            return "The isolated package cache already contained the package under test; the rehearsal refused a cached substitution.";
        }

        return null;
    }

    private static HashSet<string> ReadPublicDependencyIds(
        NuGetReadyConfig config,
        string artifactsPath,
        HashSet<string> localPackageIds)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in config.Packages!)
        {
            var artifact = package.Artifacts!.FirstOrDefault(name => name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase));
            if (artifact is null)
            {
                continue;
            }

            var path = Path.Combine(artifactsPath, artifact);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var reader = new PackageArchiveReader(path);
                foreach (var dependency in reader.NuspecReader.GetDependencyGroups().SelectMany(group => group.Packages))
                {
                    if (!localPackageIds.Contains(dependency.Id))
                    {
                        ids.Add(dependency.Id);
                    }
                }
            }
            catch (Exception) when (File.Exists(path))
            {
                // Archive parsing is reported by the archive contract. Do not echo archive content here.
            }
        }

        return ids;
    }

    private static void WriteNuGetConfig(
        string path,
        string feedPath,
        ConsumerRehearsalOptions options,
        IReadOnlyCollection<string> localPackageIds,
        IReadOnlyCollection<string> publicDependencyIds)
    {
        XNamespace ns = "http://schemas.microsoft.com/packaging/2010/07/NuGet.xsd";
        var sources = new List<XElement>
        {
            new("clear"),
            new XElement("add", new XAttribute("key", "local"), new XAttribute("value", feedPath))
        };

        sources.Add(new XElement("add", new XAttribute("key", "public"), new XAttribute("value", options.PublicFeedPath ?? "https://api.nuget.org/v3/index.json")));
        var mappings = new List<XElement> { new("clear") };
        mappings.Add(new XElement("packageSource", new XAttribute("key", "local"), localPackageIds
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(id => id, StringComparer.Ordinal)
            .Select(id => new XElement("package", new XAttribute("pattern", id)))));

        mappings.Add(new XElement("packageSource", new XAttribute("key", "public"),
            publicDependencyIds
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(id => id, StringComparer.Ordinal)
                .Concat(PublicPackagePatterns.Where(pattern => !localPackageIds.Any(id => MatchesPattern(id, pattern))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(pattern => new XElement("package", new XAttribute("pattern", pattern)))));

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("configuration",
                new XElement("packageSources", sources),
                new XElement("packageSourceMapping", mappings)));
        document.Save(path, SaveOptions.DisableFormatting);
    }

    private static bool MatchesPattern(string packageId, string pattern)
    {
        return pattern.EndsWith('*')
            ? packageId.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : packageId.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string Combine(ProcessResult result)
    {
        var output = string.Join("\n", new[] { result.StandardOutput, result.StandardError }.Where(text => !string.IsNullOrWhiteSpace(text)));
        return output.Length <= DiagnosticLimit ? output : output[..DiagnosticLimit] + "\n[output truncated]";
    }

    private static string SanitizeDirectoryName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static string EscapeXml(string value)
    {
        return value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static void DeleteDirectory(DirectoryInfo directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (directory.Exists)
                {
                    directory.Delete(recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }
}
