using System.Buffers;
using System.Security.Cryptography;
using NuGet.Packaging;

namespace KeelMatrix.NuGetReady;

internal sealed class ArchiveLimitExceededException : Exception
{
    public ArchiveLimitExceededException(string message)
        : base(message)
    {
    }
}

internal static class ArchiveInspectionLimits
{
    public const int MaxEntryCount = 4096;
    public const long MaxEntryExpandedBytes = 32 * 1024 * 1024;
    public const long MaxAggregateExpandedBytes = 256 * 1024 * 1024;

    public static string[] GetFiles(PackageArchiveReader reader)
    {
        var files = new List<string>();
        foreach (var file in reader.GetFiles())
        {
            if (files.Count == MaxEntryCount)
            {
                throw new ArchiveLimitExceededException($"Archive inspection supports at most {MaxEntryCount} entries.");
            }

            files.Add(file);
        }

        return files.ToArray();
    }

    public static void ValidateExpandedPayload(PackageArchiveReader reader, IReadOnlyList<string> files)
    {
        ValidateExpandedPayload(files, reader.GetStream);
    }

    internal static void ValidateExpandedPayload(
        IReadOnlyList<string> files,
        Func<string, Stream> openStream)
    {
        var total = 0L;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (var file in files)
            {
                using var stream = openStream(file);
                var entryBytes = 0L;
                while (true)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    entryBytes = checked(entryBytes + read);
                    total = checked(total + read);
                    if (entryBytes > MaxEntryExpandedBytes)
                    {
                        throw new ArchiveLimitExceededException(
                            $"Archive entry '{Normalize(file)}' exceeds the {MaxEntryExpandedBytes} byte expanded-entry limit.");
                    }

                    if (total > MaxAggregateExpandedBytes)
                    {
                        throw new ArchiveLimitExceededException(
                            $"Archive expanded content exceeds the {MaxAggregateExpandedBytes} byte aggregate limit.");
                    }
                }
            }
        }
        catch (OverflowException)
        {
            throw new ArchiveLimitExceededException("Archive expanded content exceeds the supported size limits.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static byte[] ReadBounded(Stream stream)
    {
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var total = 0L;
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > MaxEntryExpandedBytes)
                {
                    throw new ArchiveLimitExceededException(
                        $"Archive entry exceeds the {MaxEntryExpandedBytes} byte expanded-entry limit.");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (OverflowException)
        {
            throw new ArchiveLimitExceededException("Archive entry exceeds the supported size limits.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string ComputeSha512(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var total = 0L;
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > MaxAggregateExpandedBytes)
                {
                    throw new ArchiveLimitExceededException(
                        $"Archive content exceeds the {MaxAggregateExpandedBytes} byte aggregate limit.");
                }

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToBase64String(hash.GetHashAndReset());
        }
        catch (OverflowException)
        {
            throw new ArchiveLimitExceededException("Archive entry exceeds the supported size limits.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static bool ContentsEqual(Stream left, Stream right)
    {
        var leftBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var rightBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var leftRead = ReadChunk(left, leftBuffer);
                var rightRead = ReadChunk(right, rightBuffer);
                if (leftRead != rightRead)
                {
                    return false;
                }

                if (leftRead == 0)
                {
                    return true;
                }

                if (!CryptographicOperations.FixedTimeEquals(
                        leftBuffer.AsSpan(0, leftRead),
                        rightBuffer.AsSpan(0, rightRead)))
                {
                    return false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
            ArrayPool<byte>.Shared.Return(rightBuffer);
        }
    }

    private static int ReadChunk(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
