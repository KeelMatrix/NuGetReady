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
    string Identity,
    string ContentHash);

internal sealed class ArtifactTreeScanResult : IDisposable
{
    private readonly bool ownsHandle;
    private bool disposed;

    internal ArtifactTreeScanResult(
        Dictionary<string, List<string>> artifacts,
        int entryCount,
        int archiveCount,
        long compressedArchiveBytes,
        IReadOnlyList<ArtifactTreeEntry> entries,
        ArtifactTreeHandle handle,
        bool ownsHandle)
    {
        Artifacts = artifacts;
        EntryCount = entryCount;
        ArchiveCount = archiveCount;
        CompressedArchiveBytes = compressedArchiveBytes;
        Entries = entries;
        Handle = handle;
        RootIdentity = handle.Root.Identity;
        this.ownsHandle = ownsHandle;
    }

    public Dictionary<string, List<string>> Artifacts { get; }
    public int EntryCount { get; }
    public int ArchiveCount { get; }
    public long CompressedArchiveBytes { get; }
    public IReadOnlyList<ArtifactTreeEntry> Entries { get; }
    public string RootIdentity { get; }
    internal ArtifactTreeHandle Handle { get; }

    public bool HasSameTree(ArtifactTreeScanResult other)
    {
        return HasSameMetadata(other) && Entries.SequenceEqual(other.Entries);
    }

    public bool HasSameMetadata(ArtifactTreeScanResult other)
    {
        return string.Equals(RootIdentity, other.RootIdentity, StringComparison.Ordinal) &&
               Entries.Select(entry => entry with { ContentHash = string.Empty }).SequenceEqual(
                   other.Entries.Select(entry => entry with { ContentHash = string.Empty }));
    }

    internal IReadOnlyDictionary<string, ArtifactTreeEntry> EntriesByPath()
    {
        return Entries.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
    }

    internal ArtifactFileHandle OpenFile(string relativePath)
    {
        return Handle.OpenFile(relativePath, EntriesByPath());
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            if (ownsHandle)
            {
                Handle.Dispose();
            }
        }
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
    internal static Action<string>? BeforeDirectoryEnumerationForTests { get; set; }
    internal static Action<string>? BeforeChildOpenForTests { get; set; }
    internal static Action<string>? BeforeChildAttributeForTests { get; set; }
    internal static Action<string>? BeforeArchiveOpenForTests { get; set; }

    public static ArtifactTreeScanResult Scan(string artifactsPath)
    {
        var root = Path.GetFullPath(artifactsPath);
        var handle = ArtifactTreeHandle.Open(root);
        try
        {
            return Scan(handle, ownsHandle: true, hashArchives: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static ArtifactTreeScanResult Scan(
        ArtifactTreeHandle handle,
        bool ownsHandle = false,
        bool hashArchives = true)
    {
        if (!handle.VerifyBinding())
        {
            throw new ArtifactTreeLimitExceededException("Artifact tree path identity changed; links and junctions are not followed.");
        }

        var artifacts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ArtifactTreeEntry>();
        var pending = new Queue<(ArtifactDirectoryHandle Directory, string RelativePath, int Depth)>();
        var openDirectories = new List<ArtifactDirectoryHandle>();
        pending.Enqueue((handle.Root, string.Empty, 0));
        openDirectories.Add(handle.Root);
        var entryCount = 0;
        var archiveCount = 0;
        var compressedBytes = 0L;

        try
        {
            while (pending.Count > 0)
            {
                var (directory, relativeDirectory, depth) = pending.Dequeue();
                if (!handle.VerifyBinding() || !directory.VerifyBinding())
                {
                    throw new ArtifactTreeLimitExceededException("Artifact tree path identity changed; links and junctions are not followed.");
                }

                IReadOnlyList<string> childNames;
                try
                {
                    BeforeDirectoryEnumerationForTests?.Invoke(relativeDirectory);
                    if (!handle.VerifyBinding() || !directory.VerifyBinding())
                    {
                        throw new ArtifactTreeLimitExceededException("Artifact tree path identity changed before directory enumeration.");
                    }

                    childNames = directory.EnumerateNames();
                }
                catch (IOException exception)
                {
                    throw new IOException("Artifact directory could not be enumerated.", exception);
                }

                foreach (var name in childNames.OrderBy(path => path, StringComparer.Ordinal))
                {
                    entryCount = checked(entryCount + 1);
                    if (entryCount > ArtifactTreeLimits.MaxEntryCount)
                    {
                        throw new ArtifactTreeLimitExceededException(
                            $"Artifact tree exceeds the {ArtifactTreeLimits.MaxEntryCount} entry limit.");
                    }

                    var relative = string.IsNullOrEmpty(relativeDirectory)
                        ? name
                        : relativeDirectory + "/" + name;
                    if (relative.Length > ArtifactTreeLimits.MaxPathCharacters)
                    {
                        throw new ArtifactTreeLimitExceededException(
                            $"Artifact tree contains a path longer than the {ArtifactTreeLimits.MaxPathCharacters}-character limit.");
                    }

                    if (!handle.VerifyBinding() || !directory.VerifyBinding())
                    {
                        throw new ArtifactTreeLimitExceededException("Artifact tree path identity changed; links and junctions are not followed.");
                    }

                    ArtifactDirectoryHandle child;
                    try
                    {
                        BeforeChildOpenForTests?.Invoke(relative);
                        if (!handle.VerifyBinding() || !directory.VerifyBinding())
                        {
                            throw new ArtifactTreeLimitExceededException("Artifact tree path identity changed before a child entry could be opened.");
                        }

                        child = directory.OpenChild(
                            name,
                            exclusiveForLaunch: false,
                            beforeAttribute: () => BeforeChildAttributeForTests?.Invoke(relative));
                    }
                    catch (ArtifactTreeLimitExceededException)
                    {
                        throw;
                    }
                    catch (IOException exception)
                    {
                        throw new IOException("Artifact directory entry could not be inspected.", exception);
                    }

                    openDirectories.Add(child);
                    try
                    {
                        var info = child.GetInfo();
                        if (info.Identity.IsReparsePoint || child.IsReparsePoint)
                        {
                            throw new ArtifactTreeLimitExceededException(
                                $"Artifact tree contains a reparse point at '{relative}'; links and junctions are not followed.");
                        }

                        if (info.IsDirectory)
                        {
                            var childDepth = depth + 1;
                            if (childDepth > ArtifactTreeLimits.MaxTraversalDepth)
                            {
                                throw new ArtifactTreeLimitExceededException(
                                    $"Artifact tree exceeds the {ArtifactTreeLimits.MaxTraversalDepth}-level traversal-depth limit.");
                            }

                            entries.Add(new ArtifactTreeEntry(
                                relative,
                                true,
                                0,
                                info.LastWriteUtcTicks,
                                info.CreationUtcTicks,
                                StableAttributes(info.Attributes),
                                info.Identity.Value,
                                string.Empty));
                            pending.Enqueue((child, relative, childDepth));
                            continue;
                        }

                        var hash = string.Empty;
                        if (IsArchive(name))
                        {
                            archiveCount = checked(archiveCount + 1);
                            if (archiveCount > ArtifactTreeLimits.MaxArchiveCount)
                            {
                                throw new ArtifactTreeLimitExceededException(
                                    $"Artifact tree exceeds the {ArtifactTreeLimits.MaxArchiveCount} archive count limit.");
                            }

                            if (hashArchives)
                            {
                                BeforeArchiveOpenForTests?.Invoke(relative);
                                if (!handle.VerifyBinding() || !child.VerifyBinding())
                                {
                                    throw new IOException("Artifact archive identity changed before it was opened.");
                                }

                                using var file = new ArtifactFileHandle(child);
                                using var stream = file.OpenRead();
                                hash = ComputeHash(stream, ref compressedBytes);
                            }
                        }

                        entries.Add(new ArtifactTreeEntry(
                            relative,
                            false,
                            info.Length,
                            info.LastWriteUtcTicks,
                            info.CreationUtcTicks,
                            StableAttributes(info.Attributes),
                            info.Identity.Value,
                            hash));
                        if (IsArchive(name))
                        {
                            if (!artifacts.TryGetValue(name, out var paths))
                            {
                                paths = new List<string>();
                                artifacts[name] = paths;
                            }

                            paths.Add(relative);
                        }

                        child.Dispose();
                    }
                    catch
                    {
                        if (!child.IsDirectory)
                        {
                            child.Dispose();
                        }

                        throw;
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

            foreach (var directory in openDirectories.AsEnumerable().Reverse().Distinct())
            {
                if (!ReferenceEquals(directory, handle.Root))
                {
                    directory.Dispose();
                }
            }

            return new ArtifactTreeScanResult(
                artifacts,
                entryCount,
                archiveCount,
                compressedBytes,
                entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray(),
                handle,
                ownsHandle);
        }
        catch
        {
            foreach (var directory in openDirectories.AsEnumerable().Reverse().Distinct())
            {
                if (!ReferenceEquals(directory, handle.Root))
                {
                    directory.Dispose();
                }
            }

            throw;
        }
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

    private static string ComputeHash(Stream stream, ref long compressedBytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
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
