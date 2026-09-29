using System.Text;

namespace KeelMatrix.NuGetReady;

internal enum RepositoryPathStatus
{
    Exact,
    Missing,
    InexactCasing,
    WrongKind,
    Unavailable,
    Invalid,
    Unsafe
}

internal sealed class RepositoryInspectionContext : IDisposable
{
    private readonly ArtifactTreeHandle tree;
    private readonly string rootIdentity;
    private readonly Dictionary<string, string> expectedIdentities = new(StringComparer.Ordinal);
    private bool disposed;

    private RepositoryInspectionContext(string rootPath, ArtifactTreeHandle tree)
    {
        RootPath = rootPath;
        this.tree = tree;
        rootIdentity = tree.Root.Identity;
        try
        {
            SnapshotDirectory(tree.Root, string.Empty);
        }
        catch
        {
            tree.Dispose();
            throw;
        }
    }

    public string RootPath { get; }

    public static RepositoryInspectionContext Open(string repositoryPath)
    {
        var rootPath = Path.GetFullPath(repositoryPath);
        return new RepositoryInspectionContext(rootPath, ArtifactTreeHandle.Open(rootPath));
    }

    public RepositoryPathStatus GetExact(
        string relativePath,
        bool expectDirectory,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (!IsValidRelativePath(relativePath))
        {
            return RepositoryPathStatus.Invalid;
        }

        if (!IsRootPathBound())
        {
            return RepositoryPathStatus.Unsafe;
        }

        var opened = new List<ArtifactDirectoryHandle>();
        try
        {
            var current = tree.Root;
            var segments = relativePath.Split('/');
            for (var index = 0; index < segments.Length; index++)
            {
                if (!current.VerifyBinding())
                {
                    return RepositoryPathStatus.Unsafe;
                }

                var segment = segments[index];
                string[] matches;
                try
                {
                    matches = current.EnumerateNames()
                        .Where(name => string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }
                catch (IOException)
                {
                    return RepositoryPathStatus.Unavailable;
                }

                if (matches.Length == 0)
                {
                    var currentRelativePath = string.Join('/', segments.Take(index + 1));
                    return expectedIdentities.ContainsKey(currentRelativePath)
                        ? RepositoryPathStatus.Unsafe
                        : RepositoryPathStatus.Missing;
                }

                if (matches.Length != 1 || !string.Equals(matches[0], segment, StringComparison.Ordinal))
                {
                    return RepositoryPathStatus.InexactCasing;
                }

                ArtifactDirectoryHandle child;
                try
                {
                    child = current.OpenChild(matches[0]);
                }
                catch (ArtifactTreeLimitExceededException)
                {
                    return RepositoryPathStatus.Unsafe;
                }
                catch (IOException)
                {
                    return RepositoryPathStatus.Unavailable;
                }
                catch (UnauthorizedAccessException)
                {
                    return RepositoryPathStatus.Unavailable;
                }

                opened.Add(child);
                var childRelativePath = string.Join('/', segments.Take(index + 1));
                if (!IsExpected(childRelativePath, child))
                {
                    return RepositoryPathStatus.Unsafe;
                }

                if (index < segments.Length - 1 && !child.IsDirectory)
                {
                    return RepositoryPathStatus.WrongKind;
                }

                current = child;
            }

            var leaf = opened[^1];
            if (leaf.IsReparsePoint)
            {
                return RepositoryPathStatus.Unsafe;
            }

            if (expectDirectory != leaf.IsDirectory)
            {
                return RepositoryPathStatus.WrongKind;
            }

            if (!IsRootPathBound())
            {
                return RepositoryPathStatus.Unsafe;
            }

            resolvedPath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return RepositoryPathStatus.Exact;
        }
        finally
        {
            for (var index = opened.Count - 1; index >= 0; index--)
            {
                opened[index].Dispose();
            }
        }
    }

    public RepositoryPathStatus EnumerateFiles(
        string relativeDirectory,
        out IReadOnlyList<string> relativeFiles)
    {
        relativeFiles = Array.Empty<string>();
        var status = GetExact(relativeDirectory, expectDirectory: true, out _);
        if (status != RepositoryPathStatus.Exact)
        {
            return status;
        }

        if (!TryOpen(relativeDirectory, expectDirectory: true, out var directory, out var opened))
        {
            return RepositoryPathStatus.Unsafe;
        }

        var files = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            status = EnumerateDirectory(directory, relativeDirectory, files, visited);
            if (status == RepositoryPathStatus.Exact && expectedIdentities.Keys.Any(expectedPath =>
                    expectedPath.StartsWith(relativeDirectory + "/", StringComparison.Ordinal) &&
                    !visited.Contains(expectedPath)))
            {
                status = RepositoryPathStatus.Unsafe;
            }

            if (status == RepositoryPathStatus.Exact)
            {
                relativeFiles = files;
            }

            return status;
        }
        finally
        {
            for (var index = opened.Count - 1; index >= 0; index--)
            {
                opened[index].Dispose();
            }
        }
    }

    public bool TryReadBytes(string path, int maxBytes, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var relativePath = ToRelativePath(path);
        if (relativePath is null || !IsRootPathBound() || !TryOpen(relativePath, expectDirectory: false, out var file, out var opened))
        {
            return false;
        }

        try
        {
            if (!IsBound() || file.IsReparsePoint || file.IsDirectory)
            {
                return false;
            }

            var length = file.GetInfo().Length;
            if (length < 0 || length > maxBytes || length > int.MaxValue)
            {
                return false;
            }

            bytes = new byte[(int)length];
            using var stream = new FileStream(
                ArtifactNative.Duplicate(file.Handle),
                FileAccess.Read,
                64 * 1024,
                isAsync: false);
            stream.ReadExactly(bytes);
            if (!IsRootPathBound())
            {
                bytes = Array.Empty<byte>();
                return false;
            }

            return true;
        }
        catch (IOException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
        finally
        {
            for (var index = opened.Count - 1; index >= 0; index--)
            {
                opened[index].Dispose();
            }
        }
    }

    public bool TryReadText(
        string path,
        int maxBytes,
        ref long bytesRead,
        long maxTotalBytes,
        out string content)
    {
        content = string.Empty;
        if (!TryReadBytes(path, maxBytes, out var bytes) || bytesRead + bytes.LongLength > maxTotalBytes)
        {
            return false;
        }

        bytesRead += bytes.LongLength;
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        content = reader.ReadToEnd();
        return true;
    }

    public bool TryReadText(string path, int maxBytes, out string content)
    {
        var bytesRead = 0L;
        return TryReadText(path, maxBytes, ref bytesRead, long.MaxValue, out content);
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            tree.Dispose();
        }
    }

    private RepositoryPathStatus EnumerateDirectory(
        ArtifactDirectoryHandle directory,
        string relativeDirectory,
        ICollection<string> files,
        ISet<string> visited)
    {
        if (!IsBound() || !directory.VerifyBinding())
        {
            return RepositoryPathStatus.Unsafe;
        }

        IReadOnlyList<string> names;
        try
        {
            names = directory.EnumerateNames();
        }
        catch (IOException)
        {
            return RepositoryPathStatus.Unavailable;
        }

        foreach (var name in names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.Ordinal))
        {
            if (!IsBound() || !directory.VerifyBinding())
            {
                return RepositoryPathStatus.Unsafe;
            }

            ArtifactDirectoryHandle child;
            try
            {
                child = directory.OpenChild(name);
            }
            catch (ArtifactTreeLimitExceededException)
            {
                return RepositoryPathStatus.Unsafe;
            }
            catch (IOException)
            {
                return RepositoryPathStatus.Unavailable;
            }
            catch (UnauthorizedAccessException)
            {
                return RepositoryPathStatus.Unavailable;
            }

            using (child)
            {
                if (child.IsReparsePoint)
                {
                    return RepositoryPathStatus.Unsafe;
                }

                var relativePath = $"{relativeDirectory}/{name}";
                if (!IsExpected(relativePath, child))
                {
                    return RepositoryPathStatus.Unsafe;
                }

                visited.Add(relativePath);

                if (child.IsDirectory)
                {
                    var status = EnumerateDirectory(child, relativePath, files, visited);
                    if (status != RepositoryPathStatus.Exact)
                    {
                        return status;
                    }
                }
                else
                {
                    files.Add(relativePath);
                }
            }
        }

        return IsBound() ? RepositoryPathStatus.Exact : RepositoryPathStatus.Unsafe;
    }

    private bool TryOpen(
        string relativePath,
        bool expectDirectory,
        out ArtifactDirectoryHandle node,
        out List<ArtifactDirectoryHandle> opened)
    {
        node = null!;
        opened = new List<ArtifactDirectoryHandle>();
        if (!IsValidRelativePath(relativePath) || !IsBound())
        {
            return false;
        }

        var current = tree.Root;
        foreach (var segment in relativePath.Split('/'))
        {
            if (!current.VerifyBinding())
            {
                DisposeAll(opened);
                return false;
            }

            ArtifactDirectoryHandle child;
            try
            {
                child = current.OpenChild(segment);
            }
            catch
            {
                DisposeAll(opened);
                return false;
            }

            opened.Add(child);
            var childRelativePath = string.Join('/', relativePath.Split('/').Take(opened.Count));
            if (!IsExpected(childRelativePath, child))
            {
                DisposeAll(opened);
                opened.Clear();
                return false;
            }
            current = child;
        }

        node = current;
        if (!node.IsReparsePoint && node.IsDirectory == expectDirectory && IsBound())
        {
            return true;
        }

        DisposeAll(opened);
        opened.Clear();
        return false;
    }

    private bool IsBound()
    {
        return !disposed && tree.VerifyBinding();
    }

    private void SnapshotDirectory(ArtifactDirectoryHandle directory, string relativeDirectory)
    {
        foreach (var name in directory.EnumerateNames().OrderBy(name => name, StringComparer.Ordinal))
        {
            using var child = directory.OpenChild(name);
            if (child.IsReparsePoint)
            {
                throw new ArtifactTreeLimitExceededException("Repository policy tree contains a reparse point; links and junctions are not followed.");
            }

            var relativePath = string.IsNullOrEmpty(relativeDirectory)
                ? name
                : $"{relativeDirectory}/{name}";
            expectedIdentities[relativePath] = child.Identity;
            if (child.IsDirectory && ShouldSnapshotChildren(relativePath))
            {
                SnapshotDirectory(child, relativePath);
            }
        }
    }

    private bool IsExpected(string relativePath, ArtifactDirectoryHandle node)
    {
        return expectedIdentities.TryGetValue(relativePath, out var expectedIdentity) &&
               string.Equals(expectedIdentity, node.Identity, StringComparison.Ordinal);
    }

    private static bool ShouldSnapshotChildren(string relativePath)
    {
        var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        return name is not ".git" and not ".vs" and not "artifacts" and not "bin" and not "obj" and
               not "packages" and not "TestResults" and not ".nuget";
    }

    private bool IsRootPathBound()
    {
        if (!IsBound())
        {
            return false;
        }

        try
        {
            using var current = ArtifactTreeHandle.Open(RootPath);
            return string.Equals(current.Root.Identity, rootIdentity, StringComparison.Ordinal) && current.VerifyBinding();
        }
        catch (ArtifactTreeLimitExceededException)
        {
            return false;
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

    private string? ToRelativePath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(fullPath, RootPath, StringComparison.OrdinalIgnoreCase) &&
                !fullPath.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !fullPath.StartsWith(RootPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var relative = Path.GetRelativePath(RootPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            {
                relative = relative.Replace(Path.AltDirectorySeparatorChar, '/');
            }

            return IsValidRelativePath(relative) ? relative : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsValidRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
        {
            return false;
        }

        var segments = relativePath.Split('/');
        return segments.Length > 0 && segments.All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static void DisposeAll(IEnumerable<ArtifactDirectoryHandle> handles)
    {
        foreach (var handle in handles.Reverse())
        {
            handle.Dispose();
        }
    }
}
