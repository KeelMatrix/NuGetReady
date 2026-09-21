namespace KeelMatrix.NuGetReady.Tests;

public sealed class SensitivePathPolicyTests
{
    [Fact]
    public void Tool_policy_rejects_every_canonical_pack_guard_shape_at_root_and_nested_depth()
    {
        var manifest = PackageSensitiveFilePolicy.ReadManifestForTests();
        var names = manifest.ExactFileNames
            .Concat(manifest.FileNamePrefixes.Select(prefix => prefix + ".local"))
            .Concat(manifest.FileNameSuffixes.Select(suffix => "local" + suffix))
            .Concat(manifest.FileExtensions.Select(extension => "local" + extension))
            .ToArray();

        foreach (var name in names)
        {
            Assert.True(PackageSensitiveFilePolicy.IsSensitive(name), $"Root path was accepted: {name}");
            Assert.True(PackageSensitiveFilePolicy.IsSensitive($"nested/{name}"), $"Nested path was accepted: nested/{name}");
        }

        foreach (var segment in manifest.PathSegments)
        {
            Assert.True(PackageSensitiveFilePolicy.IsSensitive($"{segment}/file.bin"), $"Root segment was accepted: {segment}");
            Assert.True(PackageSensitiveFilePolicy.IsSensitive($"nested/{segment}/file.bin"), $"Nested segment was accepted: nested/{segment}");
        }

        foreach (var fragment in manifest.PathFragments)
        {
            Assert.True(PackageSensitiveFilePolicy.IsSensitive(fragment + "file.bin"), $"Root fragment was accepted: {fragment}");
            Assert.True(PackageSensitiveFilePolicy.IsSensitive("nested/" + fragment + "file.bin"), $"Nested fragment was accepted: nested/{fragment}");
        }

        Assert.False(PackageSensitiveFilePolicy.IsSensitive("tools/net8.0/any/Microsoft.Extensions.Configuration.UserSecrets.dll"));

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KeelMatrix.NuGetReady.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        Assert.Contains("package-sensitive-paths.json", File.ReadAllText(Path.Combine(root!.FullName, "scripts", "inspect-package.ps1")), StringComparison.Ordinal);
        Assert.Contains("reject-sensitive-pack-inputs.ps1", File.ReadAllText(Path.Combine(root.FullName, "src", "KeelMatrix.NuGetReady", "KeelMatrix.NuGetReady.csproj")), StringComparison.Ordinal);
        Assert.Contains("package-sensitive-paths.json", File.ReadAllText(Path.Combine(root.FullName, "scripts", "reject-sensitive-pack-inputs.ps1")), StringComparison.Ordinal);
    }
}
