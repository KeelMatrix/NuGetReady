using NuGet.Packaging;
using NuGet.Packaging.Core;
using System.Xml.Linq;

namespace KeelMatrix.NuGetReady;

internal static class ArchiveInspector
{
    public static IReadOnlyList<Failure> Inspect(string path, PackageExpectation expectation, bool symbols)
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
            if (!files.Any(file => file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(new Failure("archive-layout", "Symbol archive does not contain a portable symbol file."));
            }

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

        if (nuspec.GetLicenseMetadata() is null)
        {
            failures.Add(new Failure("archive-metadata", "Package license metadata is missing."));
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
        return lower.Contains("../", StringComparison.Ordinal) ||
               lower.StartsWith(".git/", StringComparison.Ordinal) ||
               lower.StartsWith(".github/", StringComparison.Ordinal) ||
               lower.Contains("/obj/", StringComparison.Ordinal) ||
               lower.Contains("/bin/", StringComparison.Ordinal) ||
               lower.Contains("node_modules/", StringComparison.Ordinal) ||
               lower.Equals("agents.md", StringComparison.Ordinal) ||
               lower.EndsWith("/.env", StringComparison.Ordinal) ||
               lower.Contains("/.env.", StringComparison.Ordinal) ||
               lower.EndsWith(".pfx", StringComparison.Ordinal) ||
               lower.EndsWith(".p12", StringComparison.Ordinal) ||
               lower.EndsWith(".pem", StringComparison.Ordinal) ||
               lower.EndsWith(".key", StringComparison.Ordinal) ||
               lower.Contains("secret", StringComparison.Ordinal) ||
               lower.Contains("credential", StringComparison.Ordinal) ||
               lower.Contains("password", StringComparison.Ordinal) ||
               lower.Contains("apikey", StringComparison.Ordinal) ||
               lower.Contains("api-key", StringComparison.Ordinal) ||
               lower.Contains("access-token", StringComparison.Ordinal) ||
               lower.EndsWith(".user", StringComparison.Ordinal) ||
               lower.EndsWith(".suo", StringComparison.Ordinal);
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}
