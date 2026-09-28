using NuGet.Packaging;

namespace KeelMatrix.NuGetReady;

internal static class DependencyCoherence
{
    public static IReadOnlyList<Failure> Inspect(
        NuGetReadyConfig config,
        ArtifactSnapshotSet snapshots,
        IReadOnlyDictionary<string, List<string>> actualArtifacts)
    {
        var expectedVersions = config.Packages!
            .ToDictionary(package => package.Id!, package => VersionText.Normalize(package.Version!), StringComparer.OrdinalIgnoreCase);
        var failures = new List<Failure>();

        foreach (var package in config.Packages!.OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase))
        {
            var artifact = PackageArtifacts.Primary(package);
            if (artifact is null)
            {
                continue;
            }

            if (!snapshots.TryGetByArtifactName(artifact, actualArtifacts, out var path))
            {
                continue;
            }

            try
            {
                using var reader = new PackageArchiveReader(path);
                var rawFiles = ArchiveInspectionLimits.GetFiles(reader);
                ArchiveInspectionLimits.ValidateExpandedPayload(reader, rawFiles);
                foreach (var dependency in reader.NuspecReader.GetDependencyGroups()
                             .SelectMany(group => group.Packages)
                             .OrderBy(dependency => dependency.Id, StringComparer.OrdinalIgnoreCase))
                {
                    if (!expectedVersions.TryGetValue(dependency.Id, out var expectedVersion) ||
                        string.Equals(dependency.Id, package.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (dependency.VersionRange is null || !dependency.VersionRange.Satisfies(NuGet.Versioning.NuGetVersion.Parse(expectedVersion)))
                    {
                        failures.Add(new Failure(
                            "dependency-coherence",
                            $"Internal dependency '{dependency.Id}' does not allow the configured version."));
                    }
                }
            }
            catch (Exception) when (File.Exists(path))
            {
                // Archive parsing is reported by the archive contract and exit state.
            }
        }

        return failures;
    }
}
