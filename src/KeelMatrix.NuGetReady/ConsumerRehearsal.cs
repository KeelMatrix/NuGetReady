using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using NuGet.Packaging;

namespace KeelMatrix.NuGetReady;

internal sealed record ConsumerRehearsalOptions(
    bool IncludeLocalFeed = true,
    bool SeedPackageCache = false,
    string? PublicFeedPath = null,
    ConsumerProcessRunner? ProcessRunner = null);

internal delegate Task<ProcessResult> ConsumerProcessRunner(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    IReadOnlyDictionary<string, string?> environment,
    TimeSpan timeout);

internal sealed record RehearsalOutcome(RehearsalResult Result, string Diagnostic);

internal sealed record LibraryTarget(string Framework, IReadOnlyList<string> ApiTypes);

internal sealed record TargetRehearsalOutcome(bool Passed, bool IsError, string Diagnostic);

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
                    ? RunToolAsync(package, packagePath, packageRoot, configPath, environment, timeout, options).GetAwaiter().GetResult()
                    : RunLibraryAsync(package, packagePath, packageRoot, configPath, environment, timeout, options).GetAwaiter().GetResult();
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
        string packagePath,
        string packageRoot,
        string configPath,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        ConsumerRehearsalOptions options)
    {
        IReadOnlyList<LibraryTarget> targets;
        try
        {
            targets = ReadLibraryTargets(packagePath);
        }
        catch (BadImageFormatException)
        {
            return Failure(package, "The package contains an invalid library assembly.", false);
        }
        catch (InvalidDataException)
        {
            return Failure(package, "The package does not contain a usable public library contract.", false);
        }
        catch (IOException)
        {
            return Failure(package, "The package library assemblies could not be inspected.", true);
        }

        var diagnostics = new List<string>();
        foreach (var target in targets)
        {
            var targetRoot = Directory.CreateDirectory(Path.Combine(packageRoot, SanitizeDirectoryName(target.Framework))).FullName;
            var outcome = await RunLibraryTargetAsync(
                package,
                target,
                targetRoot,
                configPath,
                environment,
                timeout,
                options).ConfigureAwait(false);
            if (!outcome.Passed)
            {
                var message = $"The isolated library consumer did not complete for target framework '{target.Framework}'.";
                return Failure(package, message, outcome.IsError, outcome.Diagnostic);
            }

            if (!string.IsNullOrWhiteSpace(outcome.Diagnostic))
            {
                diagnostics.Add($"[{target.Framework}]\n{outcome.Diagnostic}");
            }
        }

        return Success(
            package,
            $"Isolated library consumer restored and built against the public API for {string.Join(", ", targets.Select(target => target.Framework))}.",
            string.Join("\n", diagnostics));
    }

    private static async Task<TargetRehearsalOutcome> RunLibraryTargetAsync(
        PackageExpectation package,
        LibraryTarget target,
        string packageRoot,
        string configPath,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        ConsumerRehearsalOptions options)
    {
        var projectPath = Path.Combine(packageRoot, "Consumer.csproj");
        var sourcePath = Path.Combine(packageRoot, "Program.cs");
        var outputType = IsRunnableConsumerTargetFramework(target.Framework) ? "Exe" : "Library";
        var apiTypes = string.Join(", ", target.ApiTypes.Select(type => $"typeof({type})"));
        var source = $$"""
            using System;

            public static class ConsumerProbe
            {
                public static Type[] ShippedApiTypes { get; } = new[] { {{apiTypes}} };
            }
            """;
        if (outputType.Equals("Exe", StringComparison.Ordinal))
        {
            source += """

            internal static class Program
            {
                public static void Main() => Console.WriteLine($"consumer-api:{ConsumerProbe.ShippedApiTypes.Length}");
            }
            """;
        }

        await File.WriteAllTextAsync(projectPath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>{outputType}</OutputType>
                <TargetFramework>{EscapeXml(target.Framework)}</TargetFramework>
                <ImplicitUsings>disable</ImplicitUsings>
                <UseAppHost>false</UseAppHost>
                <RestoreNoCache>true</RestoreNoCache>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{EscapeXml(package.Id!)}" Version="{EscapeXml(VersionText.Normalize(package.Version!))}" />
              </ItemGroup>
            </Project>
            """).ConfigureAwait(false);
        await File.WriteAllTextAsync(sourcePath, source).ConfigureAwait(false);

        var restore = await RunDotnetAsync(
            ["restore", projectPath, "--configfile", configPath, "--no-cache", "--force-evaluate", "--nologo"],
            packageRoot,
            environment,
            timeout,
            options.ProcessRunner).ConfigureAwait(false);
        var restoreOutcome = ClassifyProcessResult(restore, ProcessPhase.Restore);
        if (!restoreOutcome.Passed)
        {
            return restoreOutcome;
        }

        var build = await RunDotnetAsync(
            ["build", projectPath, "--no-restore", "--nologo", "--configuration", "Release", "-p:UseSharedCompilation=false"],
            packageRoot,
            environment,
            timeout,
            options.ProcessRunner).ConfigureAwait(false);
        var buildOutcome = ClassifyProcessResult(build, ProcessPhase.Build);
        if (!buildOutcome.Passed)
        {
            return buildOutcome;
        }

        if (!IsRunnableConsumerTargetFramework(target.Framework))
        {
            return buildOutcome;
        }

        var outputAssembly = Path.Combine(packageRoot, "bin", "Release", target.Framework, "Consumer.dll");
        if (!File.Exists(outputAssembly))
        {
            return new TargetRehearsalOutcome(false, false, "The built consumer assembly was not found.");
        }

        var run = await RunDotnetAsync(
            [outputAssembly],
            packageRoot,
            environment,
            timeout,
            options.ProcessRunner).ConfigureAwait(false);
        return ClassifyProcessResult(run, ProcessPhase.Run);
    }

    private static async Task<RehearsalOutcome> RunToolAsync(
        PackageExpectation package,
        string packagePath,
        string packageRoot,
        string configPath,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        ConsumerRehearsalOptions options)
    {
        var toolPath = Directory.CreateDirectory(Path.Combine(packageRoot, "tool")).FullName;
        var install = await RunDotnetAsync(
            ["tool", "install", package.Id!, "--version", VersionText.Normalize(package.Version!), "--tool-path", toolPath, "--configfile", configPath, "--no-cache", "--verbosity", "quiet"],
            packageRoot,
            environment,
            timeout,
            options.ProcessRunner).ConfigureAwait(false);
        var installOutcome = ClassifyProcessResult(install, ProcessPhase.ToolInstall);
        if (!installOutcome.Passed)
        {
            return Failure(package, "The isolated tool could not be installed from the controlled feed.", installOutcome.IsError, installOutcome.Diagnostic);
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
        var runOutcome = ClassifyProcessResult(run, ProcessPhase.ToolSmoke);
        return runOutcome.Passed
            ? Success(package, "Isolated tool installed and safe smoke command succeeded.", runOutcome.Diagnostic)
            : Failure(package, "The installed tool safe smoke command failed.", runOutcome.IsError, runOutcome.Diagnostic);
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        ConsumerProcessRunner? processRunner)
    {
        return processRunner is null
            ? await BoundedProcess.RunAsync("dotnet", arguments, workingDirectory, environment, timeout).ConfigureAwait(false)
            : await processRunner("dotnet", arguments, workingDirectory, environment, timeout).ConfigureAwait(false);
    }

    private static bool Succeeded(ProcessResult result)
    {
        return result.Started && !result.TimedOut && result.ExitCode == 0;
    }

    private static bool HasUnusableAssetDiagnostic(ProcessResult result)
    {
        var output = string.Join("\n", result.StandardOutput, result.StandardError);
        return UnusableAssetMarkers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] UnusableAssetMarkers =
    {
        "MSB3246",
        "bad image",
        "bad-image",
        "image is too small",
        "could not load file or assembly",
        "is not a valid win32 application",
        "metadata is invalid"
    };

    private enum ProcessPhase
    {
        Restore,
        Build,
        Run,
        ToolInstall,
        ToolSmoke
    }

    private static readonly string[] InfrastructureMarkers =
    {
        "NU1100",
        "NU1101",
        "NU1301",
        "NU1302",
        "NU1303",
        "NU1900",
        "unable to load the service index",
        "no packages exist with this id",
        "failed to download",
        "connection refused",
        "connection reset",
        "could not resolve host",
        "the remote name could not be resolved",
        "network is unreachable",
        "permission denied",
        "access to the path",
        "disk full",
        "not enough space"
    };

    private static TargetRehearsalOutcome ClassifyProcessResult(ProcessResult result, ProcessPhase phase)
    {
        var diagnostic = Combine(result);
        if (Succeeded(result) && !HasUnusableAssetDiagnostic(result))
        {
            return new TargetRehearsalOutcome(true, false, SanitizeDiagnostic(diagnostic));
        }

        var isInfrastructure = IsInfrastructure(result);
        return new TargetRehearsalOutcome(
            false,
            isInfrastructure,
            isInfrastructure
                ? InfrastructureDiagnostic(phase, result, diagnostic)
                : SanitizeDiagnostic(diagnostic));
    }

    private static bool IsInfrastructure(ProcessResult result)
    {
        if (!result.Started || result.TimedOut)
        {
            return true;
        }

        var output = string.Join("\n", result.StandardOutput, result.StandardError);
        return InfrastructureMarkers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string InfrastructureDiagnostic(ProcessPhase phase, ProcessResult result, string diagnostic)
    {
        var marker = InfrastructureMarkers.FirstOrDefault(candidate => diagnostic.Contains(candidate, StringComparison.OrdinalIgnoreCase));
        var operation = phase switch
        {
            ProcessPhase.Restore => "package restore",
            ProcessPhase.ToolInstall => "tool installation",
            ProcessPhase.Build => "the consumer build",
            ProcessPhase.Run => "the consumer run",
            _ => "the tool smoke command"
        };
        var detail = !result.Started
            ? "the required child process could not be started"
            : result.TimedOut
                ? "the bounded child process timed out"
                : marker is null ? "the child process returned an infrastructure diagnostic" : $"diagnostic marker {marker}";
        return $"The isolated {operation} could not be trusted because {detail}. Verify the package sources, dependency availability, and local tooling, then rerun NuGetReady.";
    }

    private static string SanitizeDiagnostic(string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return string.Empty;
        }

        var sanitized = diagnostic.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"(?m)(?<![\w:/])(?:[A-Za-z]:[\\/]|/|\\\\)[^\r\n]*",
            "[path]");
        return sanitized.Length <= DiagnosticLimit ? sanitized : sanitized[..DiagnosticLimit] + "\n[output truncated]";
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

    private static LibraryTarget[] ReadLibraryTargets(string packagePath)
    {
        using var reader = new PackageArchiveReader(packagePath);
        var assemblyPaths = reader.GetFiles()
            .Select(NormalizeArchivePath)
            .Select(path => (Path: path, Parts: path.Split('/')))
            .Where(item => item.Parts.Length == 3 &&
                           (item.Parts[0].Equals("lib", StringComparison.OrdinalIgnoreCase) ||
                            item.Parts[0].Equals("ref", StringComparison.OrdinalIgnoreCase)) &&
                           item.Parts[2].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Parts[1], StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Parts[1], StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();

        if (assemblyPaths.Length == 0)
        {
            throw new InvalidDataException("No library assemblies were found.");
        }

        var targets = new List<LibraryTarget>();
        foreach (var targetGroup in assemblyPaths.GroupBy(item => item.Parts[1], StringComparer.OrdinalIgnoreCase))
        {
            var referenceAssets = targetGroup
                .Where(item => item.Parts[0].Equals("ref", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var publicTypes = new List<string>();
            foreach (var asset in targetGroup)
            {
                using var stream = reader.GetStream(asset.Path);
                var publicType = ReadPublicType(stream);
                if (publicType is not null && (referenceAssets.Length == 0 || asset.Parts[0].Equals("ref", StringComparison.OrdinalIgnoreCase)))
                {
                    publicTypes.Add(publicType);
                }
            }

            if (publicTypes.Count == 0)
            {
                throw new InvalidDataException($"No public type was found for target framework '{targetGroup.Key}'.");
            }

            targets.Add(new LibraryTarget(
                targetGroup.Key,
                publicTypes.Distinct(StringComparer.Ordinal).OrderBy(type => type, StringComparer.Ordinal).ToArray()));
        }

        return targets
            .OrderBy(target => target.Framework, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.Framework, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ReadPublicType(Stream stream)
    {
        using var image = new MemoryStream();
        stream.CopyTo(image);
        image.Position = 0;
        using var peReader = new PEReader(image);
        if (!peReader.HasMetadata)
        {
            throw new BadImageFormatException("The library asset does not contain managed assembly metadata.");
        }

        var metadata = peReader.GetMetadataReader();
        return metadata.TypeDefinitions
            .Select(metadata.GetTypeDefinition)
            .Where(definition => (definition.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public)
            .Select(definition => (definition, Name: metadata.GetString(definition.Name)))
            .Where(item => item.Name is not "<Module>" && !item.Name.Contains('<', StringComparison.Ordinal))
            .Where(item => IsSupportedTypeName(item.Name))
            .OrderBy(item => metadata.GetString(item.definition.Namespace), StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => FormatTypeName(metadata, item.definition, item.Name))
            .FirstOrDefault();
    }

    private static string FormatTypeName(MetadataReader metadata, TypeDefinition definition, string metadataName)
    {
        var tick = metadataName.IndexOf('`');
        var name = EscapeCSharpIdentifier(tick >= 0 ? metadataName[..tick] : metadataName);
        var genericCount = definition.GetGenericParameters().Count;
        if (genericCount > 0)
        {
            name += $"<{new string(',', genericCount - 1)}>";
        }

        var namespaceName = metadata.GetString(definition.Namespace);
        var qualifiedNamespace = string.IsNullOrWhiteSpace(namespaceName)
            ? string.Empty
            : string.Join(".", namespaceName.Split('.').Select(EscapeCSharpIdentifier));
        return $"global::{(qualifiedNamespace.Length == 0 ? string.Empty : qualifiedNamespace + ".")}{name}";
    }

    private static bool IsSupportedTypeName(string metadataName)
    {
        var tick = metadataName.IndexOf('`');
        var name = tick >= 0 ? metadataName[..tick] : metadataName;
        return IsCSharpIdentifier(name);
    }

    private static bool IsCSharpIdentifier(string value)
    {
        return value.Length > 0 &&
               (char.IsLetter(value[0]) || value[0] == '_') &&
               value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static string EscapeCSharpIdentifier(string value)
    {
        return $"@{value}";
    }

    private static bool IsRunnableConsumerTargetFramework(string framework)
    {
        return framework.StartsWith("net", StringComparison.OrdinalIgnoreCase) &&
               !framework.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) &&
               framework.Length > 3 && char.IsDigit(framework[3]) && int.TryParse(new string(framework.Skip(3).TakeWhile(char.IsDigit).ToArray()), out var major) &&
               major >= 5;
    }

    private static string NormalizeArchivePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
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
