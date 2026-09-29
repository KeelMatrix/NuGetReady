using System.Reflection;
using System.Globalization;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
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
    internal static Action<string>? BeforeToolLaunchForTests { get; set; }
    internal static Action<string>? BeforeToolLaunchSnapshotForTests { get; set; }

    public static IReadOnlyList<RehearsalOutcome> RunDetailed(
        NuGetReadyConfig config,
        string artifactsPath,
        TimeSpan timeout,
        ConsumerRehearsalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArtifactTreeScanResult artifactScan;
        try
        {
            artifactScan = ArtifactTreeScanner.Scan(artifactsPath);
            ArtifactTreeScanner.AfterScanForTests?.Invoke(artifactsPath);
        }
        catch (ArtifactTreeLimitExceededException exception)
        {
            return config.Packages!
                .Select(package => Failure(package, exception.Message, isError: true))
                .ToArray();
        }
        catch (IOException)
        {
            return config.Packages!
                .Select(package => Failure(package, "Artifact directory could not be enumerated.", isError: true))
                .ToArray();
        }

        try
        {
            using var snapshots = ArtifactSnapshotSet.Create(artifactsPath, artifactScan);
            return RunDetailed(config, snapshots, artifactScan.Artifacts, timeout, options, cancellationToken);
        }
        catch (ArchiveLimitExceededException exception)
        {
            return config.Packages!
                .Select(package => Failure(package, exception.Message, isError: true))
                .ToArray();
        }
        catch (IOException)
        {
            return config.Packages!
                .Select(package => Failure(package, "Artifact files could not be snapshotted for immutable inspection.", isError: true))
                .ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            return config.Packages!
                .Select(package => Failure(package, "Artifact files could not be snapshotted for immutable inspection.", isError: true))
                .ToArray();
        }
        finally
        {
            artifactScan.Dispose();
        }
    }

    internal static IReadOnlyList<RehearsalOutcome> RunDetailed(
        NuGetReadyConfig config,
        ArtifactSnapshotSet snapshots,
        IReadOnlyDictionary<string, List<string>> actualArtifacts,
        TimeSpan timeout,
        ConsumerRehearsalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ConsumerRehearsalOptions();
        var root = Directory.CreateTempSubdirectory("nugetready-consumer-");

        try
        {
            var feedPath = Directory.CreateDirectory(Path.Combine(root.FullName, "feed")).FullName;
            var cliHome = Directory.CreateDirectory(Path.Combine(root.FullName, "cli-home")).FullName;
            var isolatedMsBuildUserExtensions = Directory.CreateDirectory(Path.Combine(root.FullName, "empty-msbuild-user-extensions")).FullName;
            var localPackageIds = config.Packages!
                .Select(package => package.Id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var packagePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<RehearsalOutcome>();

            foreach (var package in config.Packages!)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packageArtifact = PackageArtifacts.Primary(package);
                if (packageArtifact is null)
                {
                    results.Add(Failure(package, "Exactly one primary .nupkg artifact must be declared for the package.", isError: true));
                    continue;
                }

                if (!snapshots.TryGetByArtifactName(packageArtifact, actualArtifacts, out var sourcePath))
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
                ["DirectoryBuildPropsPath"] = null,
                ["DirectoryBuildTargetsPath"] = null,
                ["ImportDirectoryBuildProps"] = "false",
                ["ImportDirectoryBuildTargets"] = "false",
                ["ImportDirectoryTargets"] = "false",
                ["CustomBeforeMicrosoftCommonTargets"] = null,
                ["CustomAfterMicrosoftCommonTargets"] = null,
                ["MSBuildUserExtensionsPath"] = isolatedMsBuildUserExtensions,
                ["DOTNET_CLI_HOME"] = cliHome,
                ["DOTNET_NOLOGO"] = "1",
                // Restore must extract the complete package payload, even when
                // the invoking CI process globally skips XML documentation.
                ["NUGET_XMLDOC_MODE"] = null,
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
            };

            var sdkProbe = RunDotnetAsync(
                ["--version"],
                root.FullName,
                baseEnvironment,
                timeout,
                options.ProcessRunner,
                cancellationToken).GetAwaiter().GetResult();
            if (!Succeeded(sdkProbe))
            {
                return config.Packages!
                    .Select(package => Failure(
                        package,
                        "The required .NET SDK could not be verified before consumer rehearsal.",
                        isError: true,
                        diagnostic: "The consumer rehearsal infrastructure is unavailable because the SDK preflight did not complete successfully."))
                    .ToArray();
            }

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
                    ? RunToolAsync(package, packagePath, packageRoot, configPath, environment, timeout, options, cancellationToken).GetAwaiter().GetResult()
                    : RunLibraryAsync(package, packagePath, packageRoot, configPath, environment, timeout, options, cancellationToken).GetAwaiter().GetResult();
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
        ConsumerRehearsalOptions options,
        CancellationToken cancellationToken)
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
                options,
                cancellationToken).ConfigureAwait(false);
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
        ConsumerRehearsalOptions options,
        CancellationToken cancellationToken)
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

        if (!HasTargetFrameworkInfrastructure(target.Framework))
        {
            return new TargetRehearsalOutcome(
                false,
                true,
                $"Target framework '{target.Framework}' cannot be rehearsed because its required reference infrastructure is not installed on this host.");
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
                <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
                <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
                <ImportDirectoryTargets>false</ImportDirectoryTargets>
                <CustomBeforeMicrosoftCommonTargets></CustomBeforeMicrosoftCommonTargets>
                <CustomAfterMicrosoftCommonTargets></CustomAfterMicrosoftCommonTargets>
                <MSBuildUserExtensionsPath>{EscapeXml(Path.Combine(Path.GetDirectoryName(projectPath)!, "empty-msbuild-user-extensions"))}</MSBuildUserExtensionsPath>
                <RestoreNoCache>true</RestoreNoCache>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{EscapeXml(package.Id!)}" Version="{EscapeXml(VersionText.Normalize(package.Version!))}" />
              </ItemGroup>
            </Project>
            """, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(sourcePath, source, cancellationToken).ConfigureAwait(false);

        var restore = await RunDotnetAsync(
            ["restore", projectPath, "--configfile", configPath, "--no-cache", "--force-evaluate", "--nologo"],
            packageRoot,
                environment,
                timeout,
                options.ProcessRunner,
                cancellationToken).ConfigureAwait(false);
        var restoreOutcome = ClassifyProcessResult(restore, ProcessPhase.Restore);
        if (!restoreOutcome.Passed)
        {
            if (!restoreOutcome.IsError && IsConfiguredRestoreInfrastructureUnavailable(options, timeout, cancellationToken))
            {
                return restoreOutcome with
                {
                    IsError = true,
                    Diagnostic = "The consumer rehearsal infrastructure is unavailable because a configured package source could not be reached."
                };
            }

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
            options.ProcessRunner,
            cancellationToken).ConfigureAwait(false);
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
            options.ProcessRunner,
            cancellationToken).ConfigureAwait(false);
        return ClassifyProcessResult(run, ProcessPhase.Run);
    }

    private static bool HasTargetFrameworkInfrastructure(string framework)
    {
        if (framework.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return HasReferencePack("NETStandard.Library.Ref", framework["netstandard".Length..]);
        }

        if (framework.StartsWith("net4", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            var versionDigits = framework[3..];
            var version = versionDigits.Length switch
            {
                2 when versionDigits[0] == '4' => $"4.{versionDigits[1]}",
                3 when versionDigits[0] == '4' => $"4.{versionDigits[1]}.{versionDigits[2]}",
                _ => string.Empty
            };
            if (version.Length == 0)
            {
                return false;
            }

            return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(path, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", $"v{version}"))
            .Any(Directory.Exists);
        }

        if (!framework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = framework[3..];
        var separator = suffix.IndexOf('.');
        var minorText = separator >= 0 && separator + 1 < suffix.Length
            ? new string(suffix[(separator + 1)..].TakeWhile(char.IsDigit).ToArray())
            : string.Empty;
        if (separator <= 0 || minorText.Length == 0 ||
            !int.TryParse(suffix[..separator], out var major) ||
            !int.TryParse(minorText, out var minor))
        {
            return false;
        }

        var pack = suffix.Contains("-windows", StringComparison.OrdinalIgnoreCase)
            ? "Microsoft.WindowsDesktop.App.Ref"
            : "Microsoft.NETCore.App.Ref";
        return HasReferencePack(pack, $"{major}.{minor}");
    }

    private static bool HasReferencePack(string packName, string versionPrefix)
    {
        var runtimeDirectory = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtimeDirectory.Parent?.Parent?.Parent?.FullName;
        if (dotnetRoot is null)
        {
            return false;
        }

        var packRoot = Path.Combine(dotnetRoot, "packs", packName);
        try
        {
            return Directory.EnumerateDirectories(packRoot)
                .Any(path => string.Equals(Path.GetFileName(path), versionPrefix, StringComparison.OrdinalIgnoreCase) ||
                             Path.GetFileName(path).StartsWith(versionPrefix + ".", StringComparison.OrdinalIgnoreCase) ||
                             (packName.Equals("NETStandard.Library.Ref", StringComparison.Ordinal) &&
                              versionPrefix.Equals("2.0", StringComparison.Ordinal) &&
                              Path.GetFileName(path).StartsWith("2.", StringComparison.OrdinalIgnoreCase)));
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<RehearsalOutcome> RunToolAsync(
        PackageExpectation package,
        string packagePath,
        string packageRoot,
        string configPath,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        ConsumerRehearsalOptions options,
        CancellationToken cancellationToken)
    {
        var toolPath = Directory.CreateDirectory(Path.Combine(packageRoot, "tool")).FullName;
        var install = await RunDotnetAsync(
            ["tool", "install", package.Id!, "--version", VersionText.Normalize(package.Version!), "--tool-path", toolPath, "--configfile", configPath, "--no-cache", "--verbosity", "quiet"],
            packageRoot,
            environment,
            timeout,
            options.ProcessRunner,
            cancellationToken).ConfigureAwait(false);
        var installOutcome = ClassifyProcessResult(install, ProcessPhase.ToolInstall);
        if (!installOutcome.Passed)
        {
            if (!installOutcome.IsError && IsConfiguredRestoreInfrastructureUnavailable(options, timeout, cancellationToken))
            {
                installOutcome = installOutcome with
                {
                    IsError = true,
                    Diagnostic = "The consumer rehearsal infrastructure is unavailable because a configured package source could not be reached."
                };
            }

            return Failure(package, "The isolated tool could not be installed from the controlled feed.", installOutcome.IsError, installOutcome.Diagnostic);
        }

        var provenance = VerifyInstalledToolPackage(package, packagePath, toolPath);
        if (!provenance.Passed)
        {
            return Failure(package, "The isolated tool did not restore the exact supplied artifact.", provenance.IsError, provenance.Diagnostic);
        }

        var command = package.Command ?? package.Id!;
        if (!TryResolveToolExecutable(toolPath, command, out var executable, out var executableIdentity))
        {
            return Failure(package, "The configured tool command was not created as a single executable child of the isolated tool directory.", false, string.Empty);
        }

        ToolLaunchSnapshot launch;
        try
        {
            var verifiedLaunchManifest = CaptureLaunchManifest(toolPath);
            BeforeToolLaunchSnapshotForTests?.Invoke(toolPath);
            launch = ToolLaunchSnapshot.Create(
                toolPath,
                packageRoot,
                Path.GetFileName(executable),
                executableIdentity,
                verifiedLaunchManifest);
        }
        catch (IOException)
        {
            return Failure(package, "The installed tool executable could not be pinned for the safe smoke command.", true, string.Empty);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(package, "The installed tool executable could not be pinned for the safe smoke command.", true, string.Empty);
        }

        using (launch)
        {
            BeforeToolLaunchForTests?.Invoke(launch.ExecutablePath);
            if (!launch.VerifyUnchanged())
            {
                return Failure(package, "The verified tool launch image changed before process creation.", true, string.Empty);
            }

            var smoke = package.Smoke?.ToArray() ?? Array.Empty<string>();
            var run = await BoundedProcess.RunAsync(launch.ExecutablePath, smoke, packageRoot, environment, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            var runOutcome = ClassifyProcessResult(run, ProcessPhase.ToolSmoke);
            return runOutcome.Passed
                ? Success(package, "Isolated tool installed and safe smoke command succeeded.", runOutcome.Diagnostic)
                : Failure(package, "The installed tool safe smoke command failed.", runOutcome.IsError, runOutcome.Diagnostic);
        }
    }

    private static bool TryResolveToolExecutable(
        string toolPath,
        string command,
        out string executable,
        out string executableIdentity)
    {
        executable = string.Empty;
        executableIdentity = string.Empty;
        if (!ToolCommandPolicy.IsValid(command))
        {
            return false;
        }

        var isolatedRoot = Path.GetFullPath(toolPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidates = OperatingSystem.IsWindows()
            ? new[] { command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? command : command + ".exe", command }
            : new[] { command };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(Path.Combine(isolatedRoot, candidate));
            if (!string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), isolatedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileName(path), candidate, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return false;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return false;
                }

                using var tree = ArtifactTreeHandle.Open(isolatedRoot);
                using var child = tree.Root.OpenChild(candidate, exclusiveForLaunch: true);
                if (child.IsDirectory || child.IsReparsePoint || !tree.VerifyBinding())
                {
                    return false;
                }

                executableIdentity = child.Identity;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            executable = path;
            return true;
        }

        return false;
    }

    private sealed class ToolLaunchSnapshot : IDisposable
    {
        private ToolLaunchSnapshot(
            string root,
            string executablePath,
            ArtifactTreeHandle launchTree,
            IReadOnlyList<ArtifactDirectoryHandle> launchEntries,
            IReadOnlyDictionary<string, string> verifiedManifest)
        {
            Root = root;
            ExecutablePath = executablePath;
            LaunchTree = launchTree;
            LaunchEntries = launchEntries;
            VerifiedManifest = verifiedManifest;
        }

        private string Root { get; }
        private ArtifactTreeHandle LaunchTree { get; }
        private IReadOnlyList<ArtifactDirectoryHandle> LaunchEntries { get; }
        private IReadOnlyDictionary<string, string> VerifiedManifest { get; }
        public string ExecutablePath { get; }
        // Windows uses held launch handles; Unix uses a private content-bound
        // copy whose bytes are verified before process creation.
        public static bool CanLaunchSafely => true;

        public static ToolLaunchSnapshot Create(
            string toolPath,
            string packageRoot,
            string executableName,
            string expectedExecutableIdentity,
            IReadOnlyDictionary<string, string> verifiedManifest)
        {
            using var source = ArtifactTreeHandle.Open(toolPath);
            if (!source.VerifyBinding())
            {
                throw new IOException("The installed tool directory changed before it could be pinned.");
            }

            var root = Directory.CreateDirectory(Path.Combine(packageRoot, ".nugetready-tool-launch")).FullName;
            try
            {
                using var verifiedExecutable = source.Root.OpenChild(executableName, exclusiveForLaunch: true);
                if (verifiedExecutable.IsDirectory || verifiedExecutable.IsReparsePoint ||
                    !string.Equals(verifiedExecutable.Identity, expectedExecutableIdentity, StringComparison.Ordinal))
                {
                    throw new IOException("The installed tool executable changed before it could be pinned.");
                }

                var copiedFiles = new HashSet<string>(StringComparer.Ordinal);
                var aggregateBytes = 0L;
                CopyDirectory(
                    source,
                    source.Root,
                    root,
                    executableName,
                    verifiedExecutable,
                    verifiedManifest,
                    copiedFiles,
                    ref aggregateBytes,
                    relativeDirectory: string.Empty,
                    topLevel: true);
                if (!copiedFiles.SetEquals(verifiedManifest.Keys))
                {
                    throw new IOException("The installed tool changed while its verified launch image was being copied.");
                }
                if (!source.VerifyBinding())
                {
                    throw new IOException("The installed tool directory changed while it was being pinned.");
                }

                var executable = Path.Combine(root, executableName);
                if (!File.Exists(executable) ||
                    (File.GetAttributes(executable) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new IOException("The pinned tool executable was not created as a regular file.");
                }

                var launchTree = ArtifactTreeHandle.Open(root);
                var launchEntries = new List<ArtifactDirectoryHandle>();
                try
                {
                    HoldLaunchEntries(launchTree, launchTree.Root, launchEntries);
                    if (!launchTree.VerifyBinding())
                    {
                        throw new IOException("The pinned tool launch image changed before it could be launched.");
                    }

                    return new ToolLaunchSnapshot(root, executable, launchTree, launchEntries, verifiedManifest);
                }
                catch
                {
                    foreach (var entry in launchEntries)
                    {
                        entry.Dispose();
                    }

                    launchTree.Dispose();
                    throw;
                }
            }
            catch
            {
                DeleteDirectory(new DirectoryInfo(root));
                throw;
            }
        }

        private static void CopyDirectory(
            ArtifactTreeHandle tree,
            ArtifactDirectoryHandle source,
            string destination,
            string executableName,
            ArtifactDirectoryHandle pinnedExecutable,
            IReadOnlyDictionary<string, string> verifiedManifest,
            ISet<string> copiedFiles,
            ref long aggregateBytes,
            string relativeDirectory,
            bool topLevel)
        {
            Directory.CreateDirectory(destination);
            foreach (var name in source.EnumerateNames().OrderBy(name => name, StringComparer.Ordinal))
            {
                if (!tree.VerifyBinding() || !source.VerifyBinding())
                {
                    throw new IOException("The installed tool directory changed while it was being pinned.");
                }

                using var child = topLevel && string.Equals(name, executableName, StringComparison.Ordinal)
                    ? pinnedExecutable
                    : source.OpenChild(name, exclusiveForLaunch: false);
                if (child.IsReparsePoint)
                {
                    throw new IOException("The installed tool directory contains a reparse point.");
                }

                var target = Path.Combine(destination, name);
                var relativePath = string.IsNullOrEmpty(relativeDirectory) ? name : $"{relativeDirectory}/{name}";
                if (child.IsDirectory)
                {
                    CopyDirectory(
                        tree,
                        child,
                        target,
                        executableName,
                        pinnedExecutable,
                        verifiedManifest,
                        copiedFiles,
                        ref aggregateBytes,
                        relativePath,
                        topLevel: false);
                    continue;
                }

                if (!verifiedManifest.TryGetValue(relativePath, out var expectedHash))
                {
                    throw new IOException("The installed tool gained an unverified launch file.");
                }

                using var file = new ArtifactFileHandle(child);
                using var input = file.OpenRead();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var copiedHash = ArtifactSnapshotSet.CopyAndHash(input, output, ref aggregateBytes);
                if (!string.Equals(copiedHash, expectedHash, StringComparison.Ordinal))
                {
                    throw new IOException("The installed tool launch file changed after provenance verification.");
                }

                copiedFiles.Add(relativePath);

                // FileStream creates a non-executable destination on Unix. The
                // verified tool child is an apphost and must retain execute
                // permission in the private launch image.
                if (!OperatingSystem.IsWindows() &&
                    topLevel &&
                    string.Equals(name, executableName, StringComparison.Ordinal))
                {
                    File.SetUnixFileMode(
                        target,
                        File.GetUnixFileMode(target) |
                        UnixFileMode.UserExecute |
                        UnixFileMode.GroupExecute |
                        UnixFileMode.OtherExecute);
                }
            }
        }

        private static void HoldLaunchEntries(
            ArtifactTreeHandle tree,
            ArtifactDirectoryHandle directory,
            ICollection<ArtifactDirectoryHandle> heldEntries)
        {
            foreach (var name in directory.EnumerateNames().OrderBy(name => name, StringComparer.Ordinal))
            {
                if (!tree.VerifyBinding() || !directory.VerifyBinding())
                {
                    throw new IOException("The pinned tool launch image changed while launch handles were being acquired.");
                }

                var observed = directory.OpenChild(name);
                if (observed.IsReparsePoint)
                {
                    observed.Dispose();
                    throw new IOException("The pinned tool launch image contains a reparse point.");
                }

                if (!observed.IsDirectory)
                {
                    ArtifactDirectoryHandle? launchEntry = null;
                    try
                    {
                        launchEntry = directory.OpenFileForLaunch(name);
                        if (launchEntry.IsReparsePoint || launchEntry.IsDirectory ||
                            !string.Equals(launchEntry.Identity, observed.Identity, StringComparison.Ordinal))
                        {
                            launchEntry.Dispose();
                            throw new IOException("The pinned tool launch image changed while launch handles were being acquired.");
                        }

                        heldEntries.Add(launchEntry);
                        launchEntry = null;
                    }
                    finally
                    {
                        launchEntry?.Dispose();
                        observed.Dispose();
                    }

                    continue;
                }

                heldEntries.Add(observed);
                HoldLaunchEntries(tree, observed, heldEntries);
            }
        }

        public void Dispose()
        {
            foreach (var entry in LaunchEntries)
            {
                entry.Dispose();
            }

            LaunchTree.Dispose();
            DeleteDirectory(new DirectoryInfo(Root));
        }

        public bool VerifyUnchanged()
        {
            try
            {
                var current = CaptureLaunchManifest(Root);
                return current.Count == VerifiedManifest.Count &&
                       current.All(pair => VerifiedManifest.TryGetValue(pair.Key, out var expected) &&
                                           string.Equals(expected, pair.Value, StringComparison.Ordinal));
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static Dictionary<string, string> CaptureLaunchManifest(string toolPath)
    {
        using var scan = ArtifactTreeScanner.Scan(
            ArtifactTreeHandle.Open(toolPath),
            ownsHandle: true,
            hashArchives: false);
        var manifest = new Dictionary<string, string>(StringComparer.Ordinal);
        var aggregateBytes = 0L;
        foreach (var entry in scan.Entries.Where(entry => !entry.IsDirectory).OrderBy(entry => entry.RelativePath, StringComparer.Ordinal))
        {
            using var file = scan.OpenFile(entry.RelativePath);
            using var stream = file.OpenRead();
            manifest.Add(entry.RelativePath, ArtifactSnapshotSet.CopyAndHash(stream, Stream.Null, ref aggregateBytes));
        }

        return manifest;
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        ConsumerProcessRunner? processRunner,
        CancellationToken cancellationToken)
    {
        return processRunner is null
            ? await BoundedProcess.RunAsync("dotnet", arguments, workingDirectory, environment, timeout, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await processRunner("dotnet", arguments, workingDirectory, environment, timeout).ConfigureAwait(false);
    }

    private static bool IsConfiguredRestoreInfrastructureUnavailable(
        ConsumerRehearsalOptions options,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!options.IncludeLocalFeed)
        {
            return true;
        }

        var source = options.PublicFeedPath ?? "https://api.nuget.org/v3/index.json";
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return !Directory.Exists(source);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var client = new HttpClient
        {
            Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : timeout
        };
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCancellation.CancelAfter(TimeSpan.FromSeconds(Math.Min(10, Math.Max(1, timeout.TotalSeconds))));
        try
        {
            using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, linkedCancellation.Token);
            return !response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return true;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }
    }

    private static bool Succeeded(ProcessResult result)
    {
        return result.Started && !result.TimedOut && result.ExitCode == 0;
    }

    internal enum ProcessPhase
    {
        Restore,
        Build,
        Run,
        ToolInstall,
        ToolSmoke
    }

    internal static TargetRehearsalOutcome ClassifyProcessResult(ProcessResult result, ProcessPhase phase)
    {
        if (!result.Started)
        {
            return new TargetRehearsalOutcome(false, true, "The child process could not be started.");
        }

        if (!result.CleanupConfirmed)
        {
            return new TargetRehearsalOutcome(false, true, "The child process lifecycle completed without confirmed process-tree cleanup; the rehearsal result is unproven.");
        }

        if (result.TimedOut)
        {
            return new TargetRehearsalOutcome(false, true, $"The bounded {phase.ToString().ToLowerInvariant()} child process timed out.");
        }

        if (Succeeded(result))
        {
            return new TargetRehearsalOutcome(true, false, string.Empty);
        }

        return new TargetRehearsalOutcome(false, false, StructuredDiagnostic(phase, result));
    }

    private static string StructuredDiagnostic(ProcessPhase phase, ProcessResult result)
    {
        if (!result.Started)
        {
            return "The child process could not be started.";
        }

        var operation = phase switch
        {
            ProcessPhase.Restore => "package restore",
            ProcessPhase.ToolInstall => "tool installation",
            ProcessPhase.Build => "consumer build",
            ProcessPhase.Run => "consumer run",
            _ => "tool smoke command"
        };
        return $"The {operation} child process failed with exit code {result.ExitCode}.";
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
        var declared = metadata.TypeDefinitions
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
        if (declared is not null)
        {
            return declared;
        }

        return metadata.ExportedTypes
            .Select(metadata.GetExportedType)
            .Where(type => (type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public &&
                           type.IsForwarder)
            .Select(type => (Namespace: metadata.GetString(type.Namespace), Name: metadata.GetString(type.Name)))
            .Where(item => item.Name is not "<Module>" && !item.Name.Contains('<', StringComparison.Ordinal))
            .Where(item => IsSupportedTypeName(item.Name))
            .OrderBy(item => item.Namespace, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => FormatForwardedTypeName(item.Namespace, item.Name))
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

    private static string FormatForwardedTypeName(string namespaceName, string metadataName)
    {
        var tick = metadataName.IndexOf('`');
        var name = EscapeCSharpIdentifier(tick >= 0 ? metadataName[..tick] : metadataName);
        if (tick >= 0 && int.TryParse(metadataName[(tick + 1)..], out var genericCount) && genericCount > 0)
        {
            name += "<" + new string(',', genericCount - 1) + ">";
        }
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
        var runes = value.EnumerateRunes().ToArray();
        return runes.Length > 0 &&
               IsIdentifierStart(runes[0]) &&
               runes.Skip(1).All(IsIdentifierPart);
    }

    private static bool IsIdentifierStart(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return rune.Value == '_' || category is
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
    }

    private static bool IsIdentifierPart(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return IsIdentifierStart(rune) || category is
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.Format;
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
            var rawFiles = ArchiveInspectionLimits.GetFiles(reader);
            ArchiveInspectionLimits.ValidateExpandedPayload(reader, rawFiles);
            var expectedIdentity = reader.GetIdentity();
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
