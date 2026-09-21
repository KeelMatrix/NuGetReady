using System.Text.Json;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class SensitivePathPolicyTests
{
    [Fact]
    public async Task Tool_pack_guard_and_archive_inspection_share_manifest_family_semantics()
    {
        var manifest = PackageSensitiveFilePolicy.ReadManifestForTests();
        var corpus = manifest.ExactFileNames
            .SelectMany(name => new[]
            {
                name,
                $"{name}.bak",
                $"deep/{name}/child.bin",
                $"deep\\{name.ToUpperInvariant()}\\child.bin"
            })
            .Concat(manifest.FileNamePrefixes.Select(prefix => $"{prefix}.local"))
            .Concat(manifest.FileNameSuffixes.Select(suffix => $"local{suffix}.bak"))
            .Concat(manifest.FileExtensions.Select(extension => $"release{extension}.bak"))
            .Concat(manifest.PathSegments.Select(segment => $"{segment}-old/file.bin"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var controls = new[]
        {
            "Microsoft.Extensions.Configuration.UserSecrets.dll",
            "KeelMatrix.Telemetry.dll",
            "NuGet.Configuration.dll",
            "TelemetryMention.dll",
            "README.md",
            "LICENSE",
            "icon.png",
            "KeelMatrix.NuGetReady.0.1.0.nupkg",
            "KeelMatrix.NuGetReady.0.1.0.snupkg"
        };

        Assert.All(corpus, path => Assert.True(PackageSensitiveFilePolicy.IsSensitive(path), $"Tool accepted generated protected path: {path}"));
        Assert.All(controls, path => Assert.False(PackageSensitiveFilePolicy.IsSensitive(path), $"Tool rejected negative control: {path}"));

        var root = FindRepositoryRoot();
        var scripts = new[] { "inspect-package.ps1", "reject-sensitive-pack-inputs.ps1" }
            .Select(name => File.ReadAllText(Path.Combine(root.FullName, "scripts", name)))
            .ToArray();
        Assert.All(scripts, script => Assert.Contains("package-sensitive-path-policy.ps1", script, StringComparison.Ordinal));

        var policyScript = Path.Combine(root.FullName, "scripts", "package-sensitive-path-policy.ps1");
        var pathsJson = JsonSerializer.Serialize(corpus);
        var controlsJson = JsonSerializer.Serialize(controls);
        var probe = Path.Combine(Path.GetTempPath(), $"nugetready-sensitive-parity-{Guid.NewGuid():N}.ps1");
        var probeScript = ". '" + policyScript.Replace("'", "''") + "'\n" +
            "$paths = ConvertFrom-Json @'\n" + pathsJson + "\n'@\n" +
            "$failed = @($paths | Where-Object { -not (Test-SensitivePackagePath ([string]$_)) })\n" +
            "if ($failed.Count -gt 0) { throw \"Script accepted generated protected paths: $($failed -join ', ')\" }\n" +
            "$controls = ConvertFrom-Json @'\n" + controlsJson + "\n'@\n" +
            "$falsePositives = @($controls | Where-Object { Test-SensitivePackagePath ([string]$_) })\n" +
            "if ($falsePositives.Count -gt 0) { throw \"Script rejected negative controls: $($falsePositives -join ', ')\" }\n";
        File.WriteAllText(probe, probeScript);
        try
        {
            var result = await BoundedProcess.RunAsync(
                "pwsh",
                ["-NoProfile", "-NonInteractive", "-File", probe],
                root.FullName,
                new Dictionary<string, string?> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
                TimeSpan.FromSeconds(30));

            Assert.True(result.Started, result.StandardError);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
    }

    [Fact]
    public void Tool_policy_rejects_extended_family_for_every_manifest_exact_name()
    {
        var manifest = PackageSensitiveFilePolicy.ReadManifestForTests();
        var variants = manifest.ExactFileNames.SelectMany(name => new[]
        {
            $"{name}.bak",
            $"{name}.old",
            $"{name}.tmp",
            $"{name}x",
            $"{name}/child.bin",
            $"deep/nested/{name}/child.bin",
            $"{name.ToUpperInvariant()}.bak",
            $"deep\\nested\\{name.ToUpperInvariant()}\\child.bin"
        });

        foreach (var variant in variants)
        {
            Assert.True(PackageSensitiveFilePolicy.IsSensitive(variant), $"Extended protected path was accepted: {variant}");
        }
    }

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

        var root = FindRepositoryRoot();
        var policyScript = File.ReadAllText(Path.Combine(root!.FullName, "scripts", "package-sensitive-path-policy.ps1"));
        Assert.Contains("package-sensitive-paths.json", policyScript, StringComparison.Ordinal);
        Assert.Contains("package-sensitive-path-policy.ps1", File.ReadAllText(Path.Combine(root.FullName, "scripts", "inspect-package.ps1")), StringComparison.Ordinal);
        Assert.Contains("reject-sensitive-pack-inputs.ps1", File.ReadAllText(Path.Combine(root.FullName, "src", "KeelMatrix.NuGetReady", "KeelMatrix.NuGetReady.csproj")), StringComparison.Ordinal);
        Assert.Contains("package-sensitive-path-policy.ps1", File.ReadAllText(Path.Combine(root.FullName, "scripts", "reject-sensitive-pack-inputs.ps1")), StringComparison.Ordinal);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KeelMatrix.NuGetReady.sln")))
        {
            root = root.Parent;
        }

        return root ?? throw new Xunit.Sdk.XunitException("NuGetReady repository root could not be located.");
    }
}
