namespace KeelMatrix.NuGetReady;

internal sealed class ArtifactTreeLimitExceededException : IOException
{
    public ArtifactTreeLimitExceededException(string message)
        : base(message)
    {
    }
}

internal sealed record ArtifactTreeScanResult(
    Dictionary<string, List<string>> Artifacts,
    int EntryCount,
    int ArchiveCount,
    long CompressedArchiveBytes);

internal static class ArtifactTreeLimits
{
    public const int MaxEntryCount = 4096;
    public const int MaxTraversalDepth = 32;
    public const int MaxPathCharacters = 4096;
    public const int MaxArchiveCount = 256;
    public const long MaxTotalCompressedArchiveBytes = 1024L * 1024 * 1024;
}

internal static class ArtifactTreeScanner
{
    public static ArtifactTreeScanResult Scan(string artifactsPath)
    {
        var root = Path.GetFullPath(artifactsPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Artifact directory was not found.");
        }

        EnsureSafeEntry(root, root, isDirectory: true);

        var artifacts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var entryCount = 0;
        var archiveCount = 0;
        var compressedBytes = 0L;

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(
                    directory,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        ReturnSpecialDirectories = false,
                        AttributesToSkip = 0
                    });
            }
            catch (IOException exception)
            {
                throw new IOException("Artifact directory could not be enumerated.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new IOException("Artifact directory could not be enumerated.", exception);
            }

            foreach (var entry in entries)
            {
                entryCount = checked(entryCount + 1);
                if (entryCount > ArtifactTreeLimits.MaxEntryCount)
                {
                    throw new ArtifactTreeLimitExceededException(
                        $"Artifact tree exceeds the {ArtifactTreeLimits.MaxEntryCount} entry limit.");
                }

                EnsureSafeEntry(root, entry, isDirectory: false);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (IOException exception)
                {
                    throw new IOException("Artifact directory could not be inspected.", exception);
                }
                catch (UnauthorizedAccessException exception)
                {
                    throw new IOException("Artifact directory could not be inspected.", exception);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArtifactTreeLimitExceededException(
                        $"Artifact tree contains a reparse point at '{Normalize(Path.GetRelativePath(root, entry))}'; links and junctions are not followed.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var childDepth = depth + 1;
                    if (childDepth > ArtifactTreeLimits.MaxTraversalDepth)
                    {
                        throw new ArtifactTreeLimitExceededException(
                            $"Artifact tree exceeds the {ArtifactTreeLimits.MaxTraversalDepth}-level traversal-depth limit.");
                    }

                    pending.Enqueue((entry, childDepth));
                    continue;
                }

                if (!IsArchive(entry))
                {
                    continue;
                }

                archiveCount = checked(archiveCount + 1);
                if (archiveCount > ArtifactTreeLimits.MaxArchiveCount)
                {
                    throw new ArtifactTreeLimitExceededException(
                        $"Artifact tree exceeds the {ArtifactTreeLimits.MaxArchiveCount} archive count limit.");
                }

                long length;
                try
                {
                    length = new FileInfo(entry).Length;
                }
                catch (IOException exception)
                {
                    throw new IOException("Artifact archive could not be inspected.", exception);
                }

                compressedBytes = AddCompressedBytes(compressedBytes, length);

                var relative = Normalize(Path.GetRelativePath(root, entry));
                var name = Path.GetFileName(relative);
                if (!artifacts.TryGetValue(name, out var paths))
                {
                    paths = new List<string>();
                    artifacts[name] = paths;
                }

                paths.Add(relative);
            }
        }

        foreach (var paths in artifacts.Values)
        {
            paths.Sort(static (left, right) =>
            {
                var comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
                return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
            });
        }

        return new ArtifactTreeScanResult(artifacts, entryCount, archiveCount, compressedBytes);
    }

    internal static long AddCompressedBytes(long current, long next)
    {
        if (next < 0 || current > ArtifactTreeLimits.MaxTotalCompressedArchiveBytes - next)
        {
            throw new ArtifactTreeLimitExceededException(
                $"Artifact archives exceed the {ArtifactTreeLimits.MaxTotalCompressedArchiveBytes} byte aggregate compressed-size limit.");
        }

        return current + next;
    }

    private static void EnsureSafeEntry(string root, string path, bool isDirectory)
    {
        if (path.Length > ArtifactTreeLimits.MaxPathCharacters)
        {
            throw new ArtifactTreeLimitExceededException(
                $"Artifact tree contains a path longer than the {ArtifactTreeLimits.MaxPathCharacters}-character limit.");
        }

        var relative = Path.GetRelativePath(root, path);
        if (isDirectory && relative == ".")
        {
            return;
        }

        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArtifactTreeLimitExceededException("Artifact tree entry escaped the artifact directory.");
        }
    }

    private static bool IsArchive(string path)
    {
        return path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
