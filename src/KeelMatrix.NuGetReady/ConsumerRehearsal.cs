using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using NuGet.Frameworks;
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
    private static readonly string[] PublicPackagePatterns = { "*" };

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
            var cliHome = Directory.CreateDirectory(Path.Combine(root.FullName, "cli-home")).FullName;
            var localPackageIds = config.Packages!
                .Select(package => package.Id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var packagePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<RehearsalOutcome>();

            foreach (var package in config.Packages!)
            {
                var packageArtifact = PackageArtifacts.Primary(package);
                if (packageArtifact is null)
                {
                    results.Add(Failure(package, "Exactly one primary .nupkg artifact must be declared for the package.", isError: true));
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

            var configPath = Path.Combine(root.FullName, "NuGet.config");
            WriteNuGetConfig(configPath, feedPath, options, localPackageIds);
            var baseEnvironment = new Dictionary<string, string?>
            {
                ["NUGET_FALLBACK_PACKAGES"] = null,
                ["NUGET_ADDITIONAL_SIGNING_STORE"] = null,
                ["RestoreFallbackFolders"] = string.Empty,
                ["RestoreAdditionalProjectSources"] = null,
                ["RestoreSources"] = null,
                ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(root.FullName, "http-cache"),
                ["DOTNET_CLI_HOME"] = cliHome,
                ["DOTNET_NOLOGO"] = "1",
                // Restore must extract the complete package payload, even when
                // the invoking CI process globally skips XML documentation.
                ["NUGET_XMLDOC_MODE"] = null,
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
            };

            foreach (var package in config.Packages!)
            {
                if (!packagePaths.TryGetValue(package.Id!, out var packagePath))
                {
                    continue;
                }

                var packageRoot = Directory.CreateDirectory(Path.Combine(root.FullName, SanitizeDirectoryName(package.Id!))).FullName;
                var cachePath = Directory.CreateDirectory(Path.Combine(packageRoot, "packages")).FullName;
                if (options.SeedPackageCache)
                {
                    var version = VersionText.Normalize(package.Version!);
                    var cachePackage = Directory.CreateDirectory(Path.Combine(cachePath, package.Id!, version));
                    File.WriteAllText(Path.Combine(cachePackage.FullName, ".seeded-copy"), "seeded package copy");
                }

                var environment = new Dictionary<string, string?>(baseEnvironment, StringComparer.Ordinal)
                {
                    ["NUGET_PACKAGES"] = cachePath
                };
                var cacheIssue = FindPreexistingCache(package, cachePath);
                if (cacheIssue is not null)
                {
                    results.Add(Failure(package, cacheIssue, isError: true));
                    continue;
                }

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
        catch (ArchiveLimitExceededException exception)
        {
            return Failure(package, exception.Message, true);
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
                packagePath,
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
        string packagePath,
        LibraryTarget target,
        string packageRoot,
        string configPath,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        ConsumerRehearsalOptions options)
    {
        var projectPath = Path.Combine(packageRoot, "Consumer.csproj");
        var sourcePath = Path.Combine(packageRoot, "Program.cs");
        var frameworkSupport = GetConsumerTargetFrameworkSupport(target.Framework);
        if (frameworkSupport == ConsumerTargetFrameworkSupport.Unsupported)
        {
            return new TargetRehearsalOutcome(
                false,
                true,
                $"Target framework '{target.Framework}' is outside the supported consumer rehearsal framework matrix for this host.");
        }

        var outputType = frameworkSupport == ConsumerTargetFrameworkSupport.Runnable ? "Exe" : "Library";
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

        var provenance = VerifyRestoredPackage(package, packagePath, environment);
        if (!provenance.Passed)
        {
            return provenance;
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

        if (frameworkSupport != ConsumerTargetFrameworkSupport.Runnable)
        {
            return buildOutcome with
            {
                Diagnostic = "Build-only validation completed; execution is not part of the supported contract for this target framework."
            };
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

        var provenance = VerifyInstalledToolPackage(package, packagePath, toolPath);
        if (!provenance.Passed)
        {
            return Failure(package, "The isolated tool did not restore the exact supplied artifact.", provenance.IsError, provenance.Diagnostic);
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

    internal enum ProcessPhase
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
        "no .net sdks were found",
        "a compatible installed .net sdk",
        "it was not possible to find any compatible framework version",
        "the framework 'microsoft.",
        "netsdk",
        "msb4236",
        "workload",
        "permission denied",
        "access to the path",
        "disk full",
        "not enough space"
    };

    internal static TargetRehearsalOutcome ClassifyProcessResult(ProcessResult result, ProcessPhase phase)
    {
        var diagnostic = Combine(result);
        if (result.Started && !result.CleanupConfirmed)
        {
            return new TargetRehearsalOutcome(false, true, "The child process lifecycle completed without confirmed process-tree cleanup; the rehearsal result is unproven.");
        }

        if (result.TimedOut && !result.CleanupConfirmed)
        {
            return new TargetRehearsalOutcome(false, true, "The bounded child process timed out and its process-group cleanup could not be confirmed.");
        }

        if (result.Started && !result.TimedOut && result.ExitCode == 0 && !result.CleanupConfirmed)
        {
            return new TargetRehearsalOutcome(false, true, "The bounded child process completed successfully, but its process-group cleanup could not be confirmed.");
        }

        if (Succeeded(result) && !HasUnusableAssetDiagnostic(result))
        {
            return new TargetRehearsalOutcome(true, false, string.Empty);
        }

        var isInfrastructure = IsInfrastructure(result);
        return new TargetRehearsalOutcome(
            false,
            isInfrastructure,
            isInfrastructure
                ? InfrastructureDiagnostic(phase, result, diagnostic)
                : StructuredDiagnostic(phase, result));
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

    private static string StructuredDiagnostic(ProcessPhase phase, ProcessResult result)
    {
        if (!result.Started)
        {
            return "The child process could not be started.";
        }

        var marker = UnusableAssetMarkers.FirstOrDefault(candidate =>
            string.Join("\n", result.StandardOutput, result.StandardError).Contains(candidate, StringComparison.OrdinalIgnoreCase));
        var operation = phase switch
        {
            ProcessPhase.Restore => "package restore",
            ProcessPhase.ToolInstall => "tool installation",
            ProcessPhase.Build => "consumer build",
            ProcessPhase.Run => "consumer run",
            _ => "tool smoke command"
        };
        return marker is null
            ? $"The {operation} child process failed with exit code {result.ExitCode}."
            : $"The {operation} reported diagnostic marker {marker}.";
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
        var rawFiles = ArchiveInspectionLimits.GetFiles(reader);
        ArchiveInspectionLimits.ValidateExpandedPayload(reader, rawFiles);
        var assemblyPaths = rawFiles
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
                using var rawStream = reader.GetStream(asset.Path);
                using var stream = new MemoryStream(ArchiveInspectionLimits.ReadBounded(rawStream), writable: false);
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
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
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
            .Where(item => !HasObsoleteAttribute(metadata, item.definition))
            .OrderBy(item => metadata.GetString(item.definition.Namespace), StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => FormatTypeName(metadata, item.definition, item.Name))
            .FirstOrDefault();
    }

    private static bool HasObsoleteAttribute(MetadataReader metadata, TypeDefinition definition)
    {
        foreach (var attributeHandle in definition.GetCustomAttributes())
        {
            var constructor = metadata.GetCustomAttribute(attributeHandle).Constructor;
            EntityHandle typeHandle = constructor.Kind switch
            {
                HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
                HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
                _ => default
            };

            if (typeHandle.IsNil)
            {
                continue;
            }

            var (namespaceName, typeName) = typeHandle.Kind switch
            {
                HandleKind.TypeDefinition => GetTypeName(metadata.GetTypeDefinition((TypeDefinitionHandle)typeHandle), metadata),
                HandleKind.TypeReference => GetTypeName(metadata.GetTypeReference((TypeReferenceHandle)typeHandle), metadata),
                _ => (string.Empty, string.Empty)
            };
            if (namespaceName == "System" && typeName == "ObsoleteAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static (string Namespace, string Name) GetTypeName(TypeDefinition definition, MetadataReader metadata)
    {
        return (metadata.GetString(definition.Namespace), metadata.GetString(definition.Name));
    }

    private static (string Namespace, string Name) GetTypeName(TypeReference reference, MetadataReader metadata)
    {
        return (metadata.GetString(reference.Namespace), metadata.GetString(reference.Name));
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

    internal enum ConsumerTargetFrameworkSupport
    {
        Unsupported,
        BuildOnly,
        Runnable
    }

    internal static ConsumerTargetFrameworkSupport GetConsumerTargetFrameworkSupport(string framework)
    {
        NuGetFramework parsed;
        try
        {
            parsed = NuGetFramework.Parse(framework);
        }
        catch (FormatException)
        {
            return ConsumerTargetFrameworkSupport.Unsupported;
        }

        if (parsed.IsUnsupported)
        {
            return ConsumerTargetFrameworkSupport.Unsupported;
        }

        if (parsed.HasPlatform &&
            !string.Equals(parsed.Platform, "windows", StringComparison.OrdinalIgnoreCase))
        {
            return ConsumerTargetFrameworkSupport.Unsupported;
        }

        if (parsed.HasPlatform &&
            string.Equals(parsed.Platform, "windows", StringComparison.OrdinalIgnoreCase) &&
            !OperatingSystem.IsWindows())
        {
            return ConsumerTargetFrameworkSupport.Unsupported;
        }

        if (string.Equals(parsed.Framework, FrameworkConstants.FrameworkIdentifiers.NetStandard, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parsed.Framework, FrameworkConstants.FrameworkIdentifiers.Net, StringComparison.OrdinalIgnoreCase))
        {
            return OperatingSystem.IsWindows() ||
                   string.Equals(parsed.Framework, FrameworkConstants.FrameworkIdentifiers.NetStandard, StringComparison.OrdinalIgnoreCase)
                ? ConsumerTargetFrameworkSupport.BuildOnly
                : ConsumerTargetFrameworkSupport.Unsupported;
        }

        if (string.Equals(parsed.Framework, FrameworkConstants.FrameworkIdentifiers.NetCoreApp, StringComparison.OrdinalIgnoreCase) &&
            parsed.Version.Major >= 5)
        {
            return ConsumerTargetFrameworkSupport.Runnable;
        }

        return ConsumerTargetFrameworkSupport.Unsupported;
    }

    private static string NormalizeArchivePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static void WriteNuGetConfig(
        string path,
        string feedPath,
        ConsumerRehearsalOptions options,
        IReadOnlyCollection<string> localPackageIds)
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
            PublicPackagePatterns
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(pattern => new XElement("package", new XAttribute("pattern", pattern)))));

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("configuration",
                new XElement("packageSources", sources),
                new XElement("packageSourceMapping", mappings)));
        document.Save(path, SaveOptions.DisableFormatting);
    }

    private static TargetRehearsalOutcome VerifyRestoredPackage(
        PackageExpectation package,
        string packagePath,
        IReadOnlyDictionary<string, string?> environment,
        string? installedToolPath = null)
    {
        try
        {
            using var reader = new PackageArchiveReader(packagePath);
            var expectedIdentity = reader.GetIdentity();
            var rawFiles = ArchiveInspectionLimits.GetFiles(reader);
            ArchiveInspectionLimits.ValidateExpandedPayload(reader, rawFiles);
            var expectedVersion = VersionText.Normalize(package.Version!);
            if (!expectedIdentity.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(expectedIdentity.Version.ToNormalizedString(), expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                return new TargetRehearsalOutcome(false, false, "The supplied package identity did not match the configured package expectation.");
            }

            string? packageDirectory;
            if (installedToolPath is not null)
            {
                packageDirectory = Directory.EnumerateDirectories(installedToolPath, expectedVersion, SearchOption.AllDirectories)
                    .Where(path => Directory.EnumerateFiles(path, "*.nuspec", SearchOption.TopDirectoryOnly).Any())
                    .SingleOrDefault();
            }
            else
            {
                if (!environment.TryGetValue("NUGET_PACKAGES", out var cachePath) || string.IsNullOrWhiteSpace(cachePath))
                {
                    return new TargetRehearsalOutcome(false, true, "The isolated package cache was not configured.");
                }

                packageDirectory = Directory.EnumerateDirectories(cachePath)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(path), package.Id, StringComparison.OrdinalIgnoreCase));
                packageDirectory = packageDirectory is null
                    ? null
                    : Directory.EnumerateDirectories(packageDirectory)
                        .FirstOrDefault(path => string.Equals(Path.GetFileName(path), expectedVersion, StringComparison.OrdinalIgnoreCase));
            }

            if (packageDirectory is null)
            {
                return new TargetRehearsalOutcome(false, true, installedToolPath is null
                    ? "The isolated package cache did not contain the restored package."
                    : "The installed tool store did not contain the restored package.");
            }

            if (installedToolPath is not null)
            {
                var toolAssetRoots = ReadToolAssetRoots(reader);
                if (toolAssetRoots.Length == 0)
                {
                    return new TargetRehearsalOutcome(false, false, "The installed tool uses an unsupported layout; expected at least one tools/<tfm>/any asset root, so exact-artifact verification is unproven.");
                }

                if (!toolAssetRoots.Any(root => Directory.Exists(Path.Combine(packageDirectory, root.Replace('/', Path.DirectorySeparatorChar)))))
                {
                    return new TargetRehearsalOutcome(false, false, "The installed tool uses an unsupported framework layout; no supplied tools/<tfm>/any asset root was selected, so exact-artifact verification is unproven.");
                }
            }

            string expectedHash;
            using (var expectedArchive = File.OpenRead(packagePath))
            {
                expectedHash = ArchiveInspectionLimits.ComputeSha512(expectedArchive);
            }
            var cachedArchives = Directory.EnumerateFiles(packageDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".nupkg", StringComparison.OrdinalIgnoreCase))
                .Where(path => installedToolPath is null || string.Equals(
                    Path.GetFileName(path),
                    $"{package.Id}.{expectedVersion}.nupkg",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (cachedArchives.Length != 1)
            {
                return new TargetRehearsalOutcome(false, false, "The isolated package cache did not contain exactly one canonical .nupkg for the restored package.");
            }

            string cachedHash;
            using (var cachedArchive = File.OpenRead(cachedArchives[0]))
            {
                cachedHash = ArchiveInspectionLimits.ComputeSha512(cachedArchive);
            }
            if (!string.Equals(cachedHash, expectedHash, StringComparison.Ordinal))
            {
                return new TargetRehearsalOutcome(false, false, "The restored package hash did not match the supplied artifact.");
            }

            var hashPath = cachedArchives[0] + ".sha512";
            if (!File.Exists(hashPath))
            {
                return new TargetRehearsalOutcome(false, false, "The restored package provenance sidecar was missing.");
            }

            var actualHash = File.ReadAllText(hashPath).Trim();
            var expectedToolStoreHash = installedToolPath is null ? null : ReadNuspecHash(reader);
            if (!HashSidecarMatches(actualHash, expectedHash) &&
                !HashSidecarMatches(actualHash, expectedToolStoreHash))
            {
                return new TargetRehearsalOutcome(false, false, "The restored package hash did not match the supplied artifact.");
            }

            using (var cachedReader = new PackageArchiveReader(cachedArchives[0]))
            {
                var cachedIdentity = cachedReader.GetIdentity();
                if (!cachedIdentity.Id.Equals(expectedIdentity.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(cachedIdentity.Version.ToNormalizedString(), expectedIdentity.Version.ToNormalizedString(), StringComparison.OrdinalIgnoreCase))
                {
                    return new TargetRehearsalOutcome(false, false, "The restored package identity did not match the supplied artifact.");
                }
            }

            var expectedPayload = rawFiles
                .Select(NormalizeArchivePath)
                .Where(file => !IsGeneratedPackageEntry(file))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var restoredFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = NormalizeArchivePath(Path.GetRelativePath(packageDirectory, path));
                if (!restoredFiles.TryAdd(relativePath, path))
                {
                    return new TargetRehearsalOutcome(false, false, "The restored package contained duplicate normalized payload paths.");
                }
            }

            var restoredPayload = restoredFiles.Keys
                .Where(file => !IsGeneratedToolStoreFile(file))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!expectedPayload.SetEquals(restoredPayload))
            {
                return new TargetRehearsalOutcome(false, false, "The restored package contents did not match the supplied artifact.");
            }

            foreach (var file in expectedPayload)
            {
                if (!restoredFiles.TryGetValue(file, out var restoredFile))
                {
                    return new TargetRehearsalOutcome(false, false, "The restored package contents did not match the supplied artifact.");
                }

                using var stream = reader.GetStream(file);
                using var restoredStream = File.OpenRead(restoredFile);
                if (!ArchiveInspectionLimits.ContentsEqual(stream, restoredStream))
                {
                    return new TargetRehearsalOutcome(false, false, "The restored package contents did not match the supplied artifact.");
                }
            }

            return new TargetRehearsalOutcome(true, false, string.Empty);
        }
        catch (IOException)
        {
            return new TargetRehearsalOutcome(false, true, "The restored package could not be verified in the isolated cache.");
        }
        catch (UnauthorizedAccessException)
        {
            return new TargetRehearsalOutcome(false, true, "The restored package could not be verified in the isolated cache.");
        }
    }

    private static TargetRehearsalOutcome VerifyInstalledToolPackage(PackageExpectation package, string packagePath, string toolPath)
    {
        return VerifyRestoredPackage(package, packagePath, new Dictionary<string, string?>(), toolPath);
    }

    private static string[] ReadToolAssetRoots(PackageArchiveReader reader)
    {
        return ArchiveInspectionLimits.GetFiles(reader)
            .Select(NormalizeArchivePath)
            .Select(path => (Path: path, Parts: path.Split('/')))
            .Where(item => item.Parts.Length >= 4 &&
                           item.Parts[0].Equals("tools", StringComparison.OrdinalIgnoreCase) &&
                           !string.IsNullOrWhiteSpace(item.Parts[1]) &&
                           item.Parts[2].Equals("any", StringComparison.OrdinalIgnoreCase))
            .Select(item => $"tools/{item.Parts[1]}/any")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ReadNuspecHash(PackageArchiveReader reader)
    {
        var nuspec = ArchiveInspectionLimits.GetFiles(reader)
            .Select(NormalizeArchivePath)
            .SingleOrDefault(file => file.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (nuspec is null)
        {
            return null;
        }

        using var stream = reader.GetStream(nuspec);
        return Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(ArchiveInspectionLimits.ReadBounded(stream)));
    }

    private static bool HashSidecarMatches(string actualHash, string? expectedHash)
    {
        return expectedHash is not null &&
               (string.Equals(actualHash, expectedHash, StringComparison.Ordinal) ||
                string.Equals(actualHash, $"sha512-{expectedHash}", StringComparison.Ordinal));
    }

    private static bool IsGeneratedPackageEntry(string file)
    {
        return file.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase) ||
               file.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) ||
               file.StartsWith("package/services/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedToolStoreFile(string file)
    {
        return file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) ||
               file.EndsWith(".nupkg.sha512", StringComparison.OrdinalIgnoreCase) ||
               file.Equals(".nupkg.metadata", StringComparison.OrdinalIgnoreCase);
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
