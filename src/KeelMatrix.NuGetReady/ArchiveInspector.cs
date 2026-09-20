using NuGet.Packaging;
using NuGet.Packaging.Core;
using System.Reflection.Metadata;
using System.Xml.Linq;

namespace KeelMatrix.NuGetReady;

internal static class ArchiveInspector
{
    public static IReadOnlyList<Failure> Inspect(
        string path,
        PackageExpectation expectation,
        bool symbols,
        string? mainPackagePath = null)
    {
        using var reader = new PackageArchiveReader(path);
        var nuspec = reader.NuspecReader;
        var identity = nuspec.GetIdentity();
        var files = reader.GetFiles()
            .Select(Normalize)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
        var failures = new List<Failure>();

        CheckIdentity(identity, expectation, failures);

        if (symbols)
        {
            CheckSymbols(reader, files, mainPackagePath, failures);

            CheckUnexpectedFiles(files, failures);
            return failures;
        }

        if (string.IsNullOrWhiteSpace(nuspec.GetAuthors()))
        {
            failures.Add(new Failure("archive-metadata", "Package authors are missing."));
        }

        if (string.IsNullOrWhiteSpace(nuspec.GetDescription()))
        {
            failures.Add(new Failure("archive-metadata", "Package description is missing."));
        }

        if (string.IsNullOrWhiteSpace(nuspec.GetTags()))
        {
            failures.Add(new Failure("archive-metadata", "Package tags are missing."));
        }

        var license = nuspec.GetLicenseMetadata();
        if (license is null)
        {
            failures.Add(new Failure("archive-metadata", "Package license metadata is missing."));
        }
        else if (string.Equals(license.Type.ToString(), "file", StringComparison.OrdinalIgnoreCase) &&
                 (string.IsNullOrWhiteSpace(license.License) || !ContainsFile(files, license.License)))
        {
            failures.Add(new Failure("archive-metadata", "Package license metadata does not resolve to an archive file."));
        }

        var readme = nuspec.GetReadme();
        if (string.IsNullOrWhiteSpace(readme) || !ContainsFile(files, readme))
        {
            failures.Add(new Failure("archive-metadata", "Package README metadata does not resolve to an archive file."));
        }

        var icon = nuspec.GetIcon();
        if (string.IsNullOrWhiteSpace(icon) || !ContainsFile(files, icon))
        {
            failures.Add(new Failure("archive-metadata", "Package icon metadata does not resolve to an archive file."));
        }

        var repository = nuspec.GetRepositoryMetadata();
        if (repository is null || string.IsNullOrWhiteSpace(repository.Url))
        {
            failures.Add(new Failure("archive-metadata", "Package repository metadata is missing."));
        }

        CheckDependencyGroups(nuspec, files, failures);
        CheckLayout(reader, expectation, files, failures);
        CheckUnexpectedFiles(files, failures);
        return failures;
    }

    private static void CheckIdentity(PackageIdentity identity, PackageExpectation expectation, List<Failure> failures)
    {
        if (!string.Equals(identity.Id, expectation.Id, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(new Failure("archive-metadata", "Package identity does not match the configured package ID."));
        }

        var expectedVersion = VersionText.Normalize(expectation.Version!);
        if (!string.Equals(identity.Version.ToNormalizedString(), expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(new Failure("archive-metadata", "Package version does not match the configured version."));
        }
    }

    private static void CheckDependencyGroups(NuspecReader nuspec, IReadOnlyList<string> files, List<Failure> failures)
    {
        var groups = nuspec.GetDependencyGroups().ToArray();
        var assetFrameworks = files
            .Select(GetAssetFramework)
            .Where(framework => framework is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(framework => framework, StringComparer.Ordinal)
            .ToArray();

        foreach (var group in groups)
        {
            if (group.TargetFramework is null || group.TargetFramework.IsUnsupported)
            {
                failures.Add(new Failure("dependency-groups", "A dependency group has an unsupported target framework."));
                continue;
            }

            foreach (var dependency in group.Packages)
            {
                if (string.IsNullOrWhiteSpace(dependency.Id) || dependency.VersionRange is null)
                {
                    failures.Add(new Failure("dependency-groups", "A dependency group contains an incomplete dependency declaration."));
                }
            }
        }

        if (groups.Length > 0 && assetFrameworks.Length > 0)
        {
            var groupFrameworks = groups
                .Where(group => group.TargetFramework is not null && !group.TargetFramework.IsUnsupported)
                .Select(group => group.TargetFramework!.GetShortFolderName())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var framework in assetFrameworks.Where(framework => !groupFrameworks.Contains(framework)))
            {
                failures.Add(new Failure("dependency-groups", $"Package assets for target framework '{framework}' have no matching dependency group."));
            }
        }
    }

    private static void CheckLayout(PackageArchiveReader reader, PackageExpectation expectation, IReadOnlyList<string> files, List<Failure> failures)
    {
        if (expectation.Kind!.Equals("dotnetTool", StringComparison.OrdinalIgnoreCase))
        {
            if (!files.Any(file =>
                    file.Split('/') is ["tools", _, "any", ..] layout &&
                    layout[^1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(new Failure("archive-layout", "Tool package does not contain a tools/<tfm>/any assembly."));
            }

            CheckToolCommand(reader, files, expectation, failures);

            return;
        }

        if (!files.Any(file => (file.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) || file.StartsWith("ref/", StringComparison.OrdinalIgnoreCase)) &&
                               file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add(new Failure("archive-layout", "Library package does not contain a lib or ref assembly."));
        }
    }

    private static void CheckUnexpectedFiles(IReadOnlyList<string> files, List<Failure> failures)
    {
        foreach (var file in files.Where(IsUnexpectedFile).OrderBy(file => file, StringComparer.Ordinal))
        {
            failures.Add(new Failure("archive-security", "Archive contains an unexpected sensitive or internal file."));
        }
    }

    private static void CheckSymbols(
        PackageArchiveReader symbolReader,
        IReadOnlyList<string> symbolFiles,
        string? mainPackagePath,
        List<Failure> failures)
    {
        var pdbFiles = symbolFiles
            .Where(file => file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
        if (pdbFiles.Length == 0)
        {
            failures.Add(new Failure("archive-layout", "Symbol archive does not contain a portable symbol file."));
            return;
        }

        PackageArchiveReader? mainReader = null;
        try
        {
            if (mainPackagePath is not null)
            {
                mainReader = new PackageArchiveReader(mainPackagePath);
            }

            var mainFiles = mainReader?.GetFiles()
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var pdbFile in pdbFiles)
            {
                try
                {
                    using var source = symbolReader.GetStream(pdbFile);
                    using var stream = new MemoryStream();
                    source.CopyTo(stream);
                    stream.Position = 0;
                    using var provider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbStream(stream);
                    var metadata = provider.GetMetadataReader();
                    if (metadata.DebugMetadataHeader is null)
                    {
                        failures.Add(new Failure("archive-layout", "Symbol archive contains a PDB without portable debug metadata."));
                    }
                    else
                    {
                        ValidateSourceLink(metadata, failures);
                    }

                    if (mainFiles is not null)
                    {
                        var expectedAssembly = pdbFile[..^4] + ".dll";
                        if (!mainFiles.Contains(expectedAssembly))
                        {
                            failures.Add(new Failure("archive-layout", "Symbol archive contains a PDB without a matching package assembly."));
                        }
                    }
                }
                catch (BadImageFormatException)
                {
                    failures.Add(new Failure("archive-layout", "Symbol archive contains a malformed portable PDB."));
                }
                catch (InvalidDataException)
                {
                    failures.Add(new Failure("archive-layout", "Symbol archive contains a malformed portable PDB."));
                }
                catch (Exception)
                {
                    failures.Add(new Failure("archive-layout", "Symbol archive contains a malformed portable PDB."));
                }
            }
        }
        finally
        {
            mainReader?.Dispose();
        }
    }

    private static void ValidateSourceLink(MetadataReader metadata, List<Failure> failures)
    {
        var sourceLinkGuid = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");
        foreach (var handle in metadata.CustomDebugInformation)
        {
            var information = metadata.GetCustomDebugInformation(handle);
            if (metadata.GetGuid(information.Kind) != sourceLinkGuid)
            {
                continue;
            }

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(metadata.GetBlobBytes(information.Value));
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("documents", out var documents) ||
                    documents.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    failures.Add(new Failure("archive-layout", "Portable PDB SourceLink metadata is invalid."));
                }
            }
            catch (System.Text.Json.JsonException)
            {
                failures.Add(new Failure("archive-layout", "Portable PDB SourceLink metadata is invalid."));
            }
        }
    }

    private static void CheckToolCommand(PackageArchiveReader reader, IReadOnlyList<string> files, PackageExpectation expectation, List<Failure> failures)
    {
        var settingsPath = files.FirstOrDefault(file => file.EndsWith("/DotnetToolSettings.xml", StringComparison.OrdinalIgnoreCase));
        if (settingsPath is null)
        {
            failures.Add(new Failure("archive-layout", "Tool package does not contain DotnetToolSettings.xml."));
            return;
        }

        using var stream = reader.GetStream(settingsPath);
        var document = XDocument.Load(stream, LoadOptions.None);
        var commands = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("Command", StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (commands.Length == 0)
        {
            failures.Add(new Failure("archive-layout", "Tool settings do not declare a command."));
        }
        else if (expectation.Command is not null && !commands.Contains(expectation.Command, StringComparer.Ordinal))
        {
            failures.Add(new Failure("archive-layout", "Tool settings do not declare the configured command."));
        }
    }

    private static string? GetAssetFramework(string file)
    {
        var segments = file.Split('/');
        return segments.Length >= 3 &&
               (segments[0].Equals("lib", StringComparison.OrdinalIgnoreCase) || segments[0].Equals("ref", StringComparison.OrdinalIgnoreCase))
            ? segments[1]
            : null;
    }

    private static bool ContainsFile(IReadOnlyList<string> files, string file)
    {
        var expected = Normalize(file);
        return files.Any(candidate => string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnexpectedFile(string file)
    {
        var lower = file.ToLowerInvariant();
        var name = lower[(lower.LastIndexOf('/') + 1)..];
        return lower.Contains("../", StringComparison.Ordinal) ||
               lower.StartsWith(".git/", StringComparison.Ordinal) ||
               lower.StartsWith(".github/", StringComparison.Ordinal) ||
               lower.Contains("/obj/", StringComparison.Ordinal) ||
               lower.Contains("/bin/", StringComparison.Ordinal) ||
               lower.Contains("node_modules/", StringComparison.Ordinal) ||
               lower.EndsWith("/agents.md", StringComparison.Ordinal) ||
               lower.Equals("agents.md", StringComparison.Ordinal) ||
               lower.Equals(".env", StringComparison.Ordinal) ||
               lower.StartsWith(".env.", StringComparison.Ordinal) ||
               lower.Contains("/.env.", StringComparison.Ordinal) ||
               lower.EndsWith("/.env", StringComparison.Ordinal) ||
               lower.EndsWith("/keelmatrix.telemetry.json", StringComparison.Ordinal) ||
               lower.Equals("keelmatrix.telemetry.json", StringComparison.Ordinal) ||
               lower.EndsWith(".pfx", StringComparison.Ordinal) ||
               lower.EndsWith(".p12", StringComparison.Ordinal) ||
               lower.EndsWith(".pem", StringComparison.Ordinal) ||
               lower.EndsWith(".key", StringComparison.Ordinal) ||
               name is "apikey" or "api-key" or "access-token" or "credentials.json" or "password" or "secret" ||
               lower.EndsWith(".user", StringComparison.Ordinal) ||
               lower.EndsWith(".suo", StringComparison.Ordinal);
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}
