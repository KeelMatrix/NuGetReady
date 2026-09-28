namespace KeelMatrix.NuGetReady.Tests;

public sealed class ArtifactContractTests
{
    [Fact]
    public void Missing_expected_artifact_returns_one()
    {
        using var fixture = PackageFixture.Create();
        var report = CheckRunner.Run(Config("Example.Core", "Example.Core.1.2.3.nupkg"), fixture.ArtifactsPath);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.Message.Contains("was not found", StringComparison.Ordinal));
    }

    [Fact]
    public void Unexpected_public_artifact_returns_one()
    {
        using var fixture = PackageFixture.Create();
        fixture.AddPackage("Example.Core.1.2.3.nupkg", "Example.Core", "1.2.3");
        File.WriteAllBytes(Path.Combine(fixture.ArtifactsPath, "Other.Core.1.2.3.nupkg"), new byte[] { 1 });

        var report = CheckRunner.Run(Config("Example.Core", "Example.Core.1.2.3.nupkg"), fixture.ArtifactsPath);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.Message.Contains("Unintended artifact", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_artifact_names_are_ambiguous()
    {
        using var fixture = PackageFixture.Create();
        var first = Directory.CreateDirectory(Path.Combine(fixture.ArtifactsPath, "first"));
        var second = Directory.CreateDirectory(Path.Combine(fixture.ArtifactsPath, "second"));
        File.WriteAllBytes(Path.Combine(first.FullName, "Example.Core.1.2.3.nupkg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(second.FullName, "Example.Core.1.2.3.nupkg"), new byte[] { 2 });

        var report = CheckRunner.Run(Config("Example.Core", "Example.Core.1.2.3.nupkg"), fixture.ArtifactsPath);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.Message.Contains("ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_artifact_directory_is_not_a_pass()
    {
        using var fixture = PackageFixture.Create();
        var report = CheckRunner.Run(Config("Example.Core", "Example.Core.1.2.3.nupkg"), Path.Combine(fixture.Root.FullName, "missing"));

        Assert.Equal(2, report.ExitCode);
        Assert.Equal("error", report.Status);
    }

    [Fact]
    public void Empty_artifact_directory_marks_every_downstream_check_not_run()
    {
        using var fixture = PackageFixture.Create();
        var report = CheckRunner.Run(
            new NuGetReadyConfig
            {
                SchemaVersion = 1,
                Packages = new List<PackageExpectation>
                {
                    new()
                    {
                        Id = "Example.Core",
                        Kind = "library",
                        Version = "1.2.3",
                        Artifacts = new List<string> { "Example.Core.1.2.3.nupkg" }
                    }
                }
            },
            fixture.ArtifactsPath);

        Assert.Equal(1, report.ExitCode);
        Assert.Equal("fail", report.Status);
        Assert.Equal("fail", report.Checks.Single(check => check.Id == "artifact-set").Status);
        Assert.All(report.Checks.Where(check => check.Id != "artifact-set"), check => Assert.Equal("not-run", check.Status));
        Assert.DoesNotContain(report.Checks, check => check.Status == "pass");
    }

    [Fact]
    public void Artifact_tree_archive_count_is_bounded_before_snapshotting()
    {
        using var fixture = PackageFixture.Create();
        for (var index = 0; index < 257; index++)
        {
            File.WriteAllBytes(Path.Combine(fixture.ArtifactsPath, $"unexpected-{index}.nupkg"), [1]);
        }

        var report = CheckRunner.Run(Config("Example.Core", "Example.Core.1.2.3.nupkg"), fixture.ArtifactsPath);

        Assert.Equal(2, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.Message.Contains("archive count", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Artifact_tree_traversal_depth_is_bounded_before_snapshotting()
    {
        using var fixture = PackageFixture.Create();
        var current = fixture.ArtifactsPath;
        for (var index = 0; index < 40; index++)
        {
            current = Directory.CreateDirectory(Path.Combine(current, $"level-{index}")).FullName;
        }

        File.WriteAllBytes(Path.Combine(current, "Example.Core.1.2.3.nupkg"), [1]);

        var report = CheckRunner.Run(Config("Example.Core", "Example.Core.1.2.3.nupkg"), fixture.ArtifactsPath);

        Assert.Equal(2, report.ExitCode);
        Assert.Contains(report.Failures, failure => failure.Message.Contains("depth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Artifact_tree_filesystem_entry_count_is_bounded()
    {
        using var fixture = PackageFixture.Create();
        for (var index = 0; index < ArtifactTreeLimits.MaxEntryCount + 1; index++)
        {
            File.WriteAllBytes(Path.Combine(fixture.ArtifactsPath, $"payload-{index}.bin"), [1]);
        }

        var exception = Assert.Throws<ArtifactTreeLimitExceededException>(() => ArtifactTreeScanner.Scan(fixture.ArtifactsPath));

        Assert.Contains("entry", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Artifact_tree_rejects_reparse_points_without_following_them()
    {
        using var fixture = PackageFixture.Create();
        var linkPath = Path.Combine(fixture.ArtifactsPath, "linked.nupkg");
        try
        {
            File.CreateSymbolicLink(linkPath, Path.Combine(fixture.Root.FullName, "outside.nupkg"));
        }
        catch (Exception linkException) when (linkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var limitException = Assert.Throws<ArtifactTreeLimitExceededException>(() => ArtifactTreeScanner.Scan(fixture.ArtifactsPath));

        Assert.Contains("reparse", limitException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Artifact_tree_aggregate_compressed_size_is_bounded_before_snapshotting()
    {
        var current = ArtifactTreeLimits.MaxTotalCompressedArchiveBytes - 1;

        var exception = Assert.Throws<ArtifactTreeLimitExceededException>(
            () => ArtifactTreeScanner.AddCompressedBytes(current, 2));

        Assert.Contains("aggregate compressed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static NuGetReadyConfig Config(string id, string artifact)
    {
        return new NuGetReadyConfig
        {
            SchemaVersion = 1,
            Packages = new List<PackageExpectation>
            {
                new() { Id = id, Kind = "library", Version = "1.2.3", Artifacts = new List<string> { artifact } }
            }
        };
    }
}
