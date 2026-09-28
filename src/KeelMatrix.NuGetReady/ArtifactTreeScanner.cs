using System.Security.Cryptography;

namespace KeelMatrix.NuGetReady;

internal sealed class ArtifactTreeLimitExceededException : IOException
{
    public ArtifactTreeLimitExceededException(string message)
        : base(message)
    {
    }
}

internal sealed record ArtifactTreeEntry(
    string RelativePath,
    bool IsDirectory,
    long Length,
    long LastWriteUtcTicks,
    long CreationUtcTicks,
    FileAttributes Attributes,
    string ContentHash);

internal sealed record ArtifactTreeScanResult(
    Dictionary<string, List<string>> Artifacts,
    int EntryCount,
    int ArchiveCount,
    long CompressedArchiveBytes,
    IReadOnlyList<ArtifactTreeEntry> Entries)
{
    public bool HasSameTree(ArtifactTreeScanResult other)
    {
        return Entries.SequenceEqual(other.Entries);
    }
}

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
    internal static Action<string>? AfterScanForTests { get; set; }

    public static ArtifactTreeScanResult Scan(string artifactsPath)
    {
        var root = Path.GetFullPath(artifactsPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Artifact directory was not found.");
        }

        EnsureSafeEntry(root, root, isDirectory: true);
        if (IsReparsePoint(root))
        {
            throw new ArtifactTreeLimitExceededException("Artifact tree root is a reparse point; links and junctions are not followed.");
        }

        var artifacts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ArtifactTreeEntry>();
        var pending = new Queue<(string Path, string RelativePath, int Depth)>();
        pending.Enqueue((root, string.Empty, 0));
        var entryCount = 0;
        var archiveCount = 0;
        var compressedBytes = 0L;

        while (pending.Count > 0)
        {
            var (directory, relativeDirectory, depth) = pending.Dequeue();
            if (!EnsureNoReparseChain(root, relativeDirectory))
            {
                throw new ArtifactTreeLimitExceededException("Artifact tree contains a reparse point; links and junctions are not followed.");
            }

            IEnumerable<string> childEntries;
            try
            {
                childEntries = Directory.EnumerateFileSystemEntries(
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

            foreach (var entry in childEntries.OrderBy(path => path, StringComparer.Ordinal))
            {
                entryCount = checked(entryCount + 1);
                if (entryCount > ArtifactTreeLimits.MaxEntryCount)
                {
                    throw new ArtifactTreeLimitExceededException(
                        $"Artifact tree exceeds the {ArtifactTreeLimits.MaxEntryCount} entry limit.");
                }

                var name = Path.GetFileName(entry);
                var relative = string.IsNullOrEmpty(relativeDirectory)
                    ? name
                    : relativeDirectory + "/" + name;
                EnsureSafeEntry(root, entry, isDirectory: false);
                if (!EnsureNoReparseChain(root, relative))
                {
                    throw new ArtifactTreeLimitExceededException(
                        $"Artifact tree contains a reparse point at '{relative}'; links and junctions are not followed.");
                }

                FileAttributes attributes;
                try
                {
                    attributes = StableAttributes(File.GetAttributes(entry));
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
                        $"Artifact tree contains a reparse point at '{relative}'; links and junctions are not followed.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var childDepth = depth + 1;
                    if (childDepth > ArtifactTreeLimits.MaxTraversalDepth)
                    {
                        throw new ArtifactTreeLimitExceededException(
                            $"Artifact tree exceeds the {ArtifactTreeLimits.MaxTraversalDepth}-level traversal-depth limit.");
                    }

                    var info = new DirectoryInfo(entry);
                    entries.Add(new ArtifactTreeEntry(relative, true, 0, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, attributes, string.Empty));
                    pending.Enqueue((entry, relative, childDepth));
                    continue;
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

                var hash = string.Empty;
                if (IsArchive(entry))
                {
                    archiveCount = checked(archiveCount + 1);
                    if (archiveCount > ArtifactTreeLimits.MaxArchiveCount)
                    {
                        throw new ArtifactTreeLimitExceededException(
                            $"Artifact tree exceeds the {ArtifactTreeLimits.MaxArchiveCount} archive count limit.");
                    }

                    hash = ComputeHash(entry, ref compressedBytes);
                }

                var fileInfo = new FileInfo(entry);
                entries.Add(new ArtifactTreeEntry(relative, false, length, fileInfo.LastWriteTimeUtc.Ticks, fileInfo.CreationTimeUtc.Ticks, attributes, hash));
                if (IsArchive(entry))
                {
                    if (!artifacts.TryGetValue(name, out var paths))
                    {
                        paths = new List<string>();
                        artifacts[name] = paths;
                    }

                    paths.Add(relative);
                }
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

        return new ArtifactTreeScanResult(
            artifacts,
            entryCount,
            archiveCount,
            compressedBytes,
            entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray());
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

    private static string ComputeHash(string path, ref long compressedBytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        using var stream = File.OpenRead(path);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            compressedBytes = AddCompressedBytes(compressedBytes, read);
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToBase64String(hash.GetHashAndReset());
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

    private static bool EnsureNoReparseChain(string root, string relativePath)
    {
        if (IsReparsePoint(root))
        {
            return false;
        }

        var current = root;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static FileAttributes StableAttributes(FileAttributes attributes)
    {
        return attributes & (FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.ReparsePoint);
    }

    private static bool IsArchive(string path)
    {
        return path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase);
    }
}
