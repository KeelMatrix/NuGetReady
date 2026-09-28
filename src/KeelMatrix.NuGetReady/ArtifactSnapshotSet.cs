using System.Security.Cryptography;

namespace KeelMatrix.NuGetReady;

/// <summary>Provides one immutable-on-disk view of the artifact set for a check run.</summary>
internal sealed class ArtifactSnapshotSet : IDisposable
{
    private const long MaxCompressedArtifactBytes = 512L * 1024 * 1024;
    private readonly string sourceRoot;
    private readonly string snapshotRoot;
    private readonly Dictionary<string, string> snapshotByRelativePath;
    private readonly Dictionary<string, string> sourceHashes;
    private readonly ArtifactTreeScanResult sourceScan;

    private ArtifactSnapshotSet(
        string sourceRoot,
        string snapshotRoot,
        Dictionary<string, string> snapshotByRelativePath,
        Dictionary<string, string> sourceHashes,
        ArtifactTreeScanResult sourceScan)
    {
        this.sourceRoot = sourceRoot;
        this.snapshotRoot = snapshotRoot;
        this.snapshotByRelativePath = snapshotByRelativePath;
        this.sourceHashes = sourceHashes;
        this.sourceScan = sourceScan;
    }

    public static ArtifactSnapshotSet Create(
        string artifactsPath,
        ArtifactTreeScanResult sourceScan)
    {
        var sourceRoot = Path.GetFullPath(artifactsPath);
        var currentScan = ArtifactTreeScanner.Scan(sourceRoot);
        if (!sourceScan.HasSameTree(currentScan))
        {
            throw new IOException("The artifact tree changed after it was scanned and could not be snapshotted consistently.");
        }

        var snapshotRoot = Directory.CreateTempSubdirectory("nugetready-artifacts-").FullName;
        var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aggregateBytes = 0L;
        try
        {
            foreach (var relativePath in currentScan.Artifacts.Values.SelectMany(paths => paths)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(path => path, StringComparer.Ordinal))
            {
                var normalized = Normalize(relativePath);
                var sourcePath = Path.GetFullPath(Path.Combine(sourceRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(sourcePath))
                {
                    throw new IOException($"Artifact '{normalized}' was not found while taking the artifact snapshot.");
                }

                if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Artifact '{normalized}' is a reparse point and cannot be snapshotted.");
                }

                var snapshotPath = Path.Combine(snapshotRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                hashes[normalized] = CopyAndHashBounded(sourcePath, snapshotPath, ref aggregateBytes);
                snapshots[normalized] = snapshotPath;
            }

            return new ArtifactSnapshotSet(sourceRoot, snapshotRoot, snapshots, hashes, sourceScan);
        }
        catch
        {
            DeleteDirectory(snapshotRoot);
            throw;
        }
    }

    public bool TryGetByRelativePath(string relativePath, out string snapshotPath)
    {
        return snapshotByRelativePath.TryGetValue(Normalize(relativePath), out snapshotPath!);
    }

    public bool TryGetByArtifactName(
        string artifactName,
        IReadOnlyDictionary<string, List<string>> actualArtifacts,
        out string snapshotPath)
    {
        snapshotPath = string.Empty;
        if (!actualArtifacts.TryGetValue(artifactName, out var paths) || paths.Count != 1)
        {
            return false;
        }

        return TryGetByRelativePath(paths[0], out snapshotPath);
    }

    public bool VerifySourcesUnchanged()
    {
        try
        {
            if (!sourceScan.HasSameTree(ArtifactTreeScanner.Scan(sourceRoot)))
            {
                return false;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var pair in sourceHashes)
        {
            var sourcePath = Path.Combine(sourceRoot, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(sourcePath) ||
                    (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0 ||
                    !string.Equals(ComputeHash(sourcePath), pair.Value, StringComparison.Ordinal))
                {
                    return false;
                }
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

        return true;
    }

    public void Dispose()
    {
        DeleteDirectory(snapshotRoot);
    }

    internal static string CopyAndHash(Stream source, Stream destination, ref long aggregateBytes)
    {
        var buffer = new byte[64 * 1024];
        var total = 0L;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > MaxCompressedArtifactBytes)
            {
                throw new ArchiveLimitExceededException($"Artifact exceeds the {MaxCompressedArtifactBytes} byte compressed-artifact limit.");
            }

            aggregateBytes = checked(aggregateBytes + read);
            if (aggregateBytes > ArtifactTreeLimits.MaxTotalCompressedArchiveBytes)
            {
                throw new ArchiveLimitExceededException(
                    $"Artifact archives exceed the {ArtifactTreeLimits.MaxTotalCompressedArchiveBytes} byte aggregate compressed-size limit.");
            }

            hash.AppendData(buffer, 0, read);
            destination.Write(buffer, 0, read);
        }

        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    private static string CopyAndHashBounded(string sourcePath, string destinationPath, ref long aggregateBytes)
    {
        using var source = File.OpenRead(sourcePath);
        using var destination = File.Create(destinationPath);
        return CopyAndHash(source, destination, ref aggregateBytes);
    }

    private static string ComputeHash(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        using var stream = File.OpenRead(path);
        var buffer = new byte[64 * 1024];
        var total = 0L;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > MaxCompressedArtifactBytes)
            {
                throw new ArchiveLimitExceededException($"Artifact exceeds the {MaxCompressedArtifactBytes} byte compressed-artifact limit.");
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
