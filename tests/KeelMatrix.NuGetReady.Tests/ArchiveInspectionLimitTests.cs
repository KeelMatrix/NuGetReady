using System.IO.Compression;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class ArchiveInspectionLimitTests
{
    [Fact]
    public void Entry_count_limit_rejects_a_controlled_archive_before_payload_reads()
    {
        using var fixture = TemporaryArchive.Create(archive =>
        {
            for (var index = 0; index <= ArchiveInspectionLimits.MaxEntryCount; index++)
            {
                using var stream = archive.CreateEntry($"payload/{index}.bin").Open();
                stream.WriteByte(1);
            }
        });

        using var reader = new NuGet.Packaging.PackageArchiveReader(fixture.Path);
        var exception = Assert.Throws<ArchiveLimitExceededException>(() => ArchiveInspectionLimits.GetFiles(reader));
        Assert.Contains("entries", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expanded_entry_limit_rejects_compressed_payload_without_reading_it_all_into_memory()
    {
        using var fixture = TemporaryArchive.Create(archive =>
        {
            var entry = archive.CreateEntry("lib/net8.0/oversized.dll", CompressionLevel.SmallestSize);
            using var stream = entry.Open();
            WriteZeros(stream, ArchiveInspectionLimits.MaxEntryExpandedBytes + 1);
        });

        using var reader = new NuGet.Packaging.PackageArchiveReader(fixture.Path);
        var files = ArchiveInspectionLimits.GetFiles(reader);
        var exception = Assert.Throws<ArchiveLimitExceededException>(() => ArchiveInspectionLimits.ValidateExpandedPayload(reader, files));
        Assert.Contains("expanded-entry", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Aggregate_expanded_limit_stops_streaming_before_the_next_entry_is_consumed()
    {
        var streams = Enumerable.Range(0, 9)
            .ToDictionary(index => $"payload/{index}.bin", _ => new CountingPatternStream(ArchiveInspectionLimits.MaxEntryExpandedBytes));

        var exception = Assert.Throws<ArchiveLimitExceededException>(() => ArchiveInspectionLimits.ValidateExpandedPayload(
            streams.Keys.ToArray(),
            path => streams[path]));

        Assert.Contains("aggregate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(streams.Values.Sum(stream => stream.BytesRead) <= ArchiveInspectionLimits.MaxAggregateExpandedBytes + (64 * 1024));
    }

    [Fact]
    public void Many_file_supported_archive_remains_within_the_entry_budget()
    {
        using var fixture = TemporaryArchive.Create(archive =>
        {
            for (var index = 0; index < 1000; index++)
            {
                using var stream = archive.CreateEntry($"payload/{index}.bin").Open();
                stream.WriteByte(1);
            }
        });

        using var reader = new NuGet.Packaging.PackageArchiveReader(fixture.Path);
        var files = ArchiveInspectionLimits.GetFiles(reader);
        ArchiveInspectionLimits.ValidateExpandedPayload(reader, files);
        Assert.Equal(1000, files.Length);
    }

    private static void WriteZeros(Stream stream, long length)
    {
        var buffer = new byte[64 * 1024];
        while (length > 0)
        {
            var count = (int)Math.Min(length, buffer.Length);
            stream.Write(buffer, 0, count);
            length -= count;
        }
    }

    private sealed class TemporaryArchive : IDisposable
    {
        private readonly DirectoryInfo root;

        private TemporaryArchive(DirectoryInfo root, string path)
        {
            this.root = root;
            Path = path;
        }

        public string Path { get; }

        public static TemporaryArchive Create(Action<ZipArchive> populate)
        {
            var root = Directory.CreateTempSubdirectory("nugetready-archive-limits-");
            var path = System.IO.Path.Combine(root.FullName, "fixture.nupkg");
            using (var file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                populate(archive);
            }

            return new TemporaryArchive(root, path);
        }

        public void Dispose() => root.Delete(recursive: true);
    }

    private sealed class CountingPatternStream : Stream
    {
        private long remaining;

        public CountingPatternStream(long length)
        {
            remaining = length;
        }

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => BytesRead + remaining;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (remaining == 0)
            {
                return 0;
            }

            var read = (int)Math.Min(remaining, count);
            remaining -= read;
            BytesRead += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
