using System.Text.Json;

namespace KeelMatrix.NuGetReady.Tests;

public sealed class SensitivePathPolicyTests
{
    [Fact]
    public async Task Tool_and_power_shell_policy_accept_manifest_generated_legitimate_embedded_names()
    {
        var manifest = PackageSensitiveFilePolicy.ReadManifestForTests();
        var corpus = SensitivePathCorpus.GenerateAcceptCorpus(manifest).ToArray();

        Assert.NotEmpty(corpus);
        Assert.All(corpus, path => Assert.False(
            PackageSensitiveFilePolicy.IsSensitive(path),
            $"Tool rejected legitimate embedded name: {path}"));

        var root = FindRepositoryRoot();
        var policyScript = Path.Combine(root.FullName, "scripts", "package-sensitive-path-policy.ps1");
        var pathsJson = JsonSerializer.Serialize(corpus);
        var probe = Path.Combine(Path.GetTempPath(), $"nugetready-sensitive-accept-parity-{Guid.NewGuid():N}.ps1");
        var probeScript = ". '" + policyScript.Replace("'", "''") + "'\n" +
            "$paths = ConvertFrom-Json @'\n" + pathsJson + "\n'@\n" +
            "$falsePositives = @($paths | Where-Object { Test-SensitivePackagePath ([string]$_) })\n" +
            "if ($falsePositives.Count -gt 0) { throw \"Script rejected legitimate embedded names: $($falsePositives -join ', ')\" }\n";
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
    public async Task Tool_pack_guard_and_archive_inspection_share_manifest_family_semantics()
    {
        var manifest = PackageSensitiveFilePolicy.ReadManifestForTests();
        var corpus = SensitivePathCorpus.GenerateRejectCorpus(manifest)
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
            "KeelMatrix.NuGetReady.0.1.0.snupkg",
            "_rels/.rels",
            "KeelMatrix.NuGetReady.nuspec",
            "tools/net8.0/any/DotnetToolSettings.xml",
            "tools/net8.0/any/KeelMatrix.NuGetReady.dll",
            "tools/net8.0/any/KeelMatrix.NuGetReady.deps.json",
            "tools/net8.0/any/KeelMatrix.NuGetReady.runtimeconfig.json",
            "tools/net8.0/any/KeelMatrix.NuGetReady.pdb",
            "tools/net8.0/any/KeelMatrix.Telemetry.dll",
            "tools/net8.0/any/Newtonsoft.Json.dll",
            "tools/net8.0/any/NuGet.Common.dll",
            "tools/net8.0/any/NuGet.Configuration.dll",
            "tools/net8.0/any/NuGet.Frameworks.dll",
            "tools/net8.0/any/NuGet.Packaging.dll",
            "tools/net8.0/any/NuGet.Versioning.dll",
            "tools/net8.0/any/YamlDotNet.dll",
            "tools/net8.0/any/System.Security.Cryptography.Pkcs.dll",
            "tools/net8.0/any/System.Security.Cryptography.ProtectedData.dll",
            "tools/net8.0/any/runtimes/win/lib/net8.0/System.Security.Cryptography.Pkcs.dll",
            "package/services/metadata/core-properties/488dca78b17847359446f37e5ec6caab.psmdcp",
            "tools/net8.0/any/KeelMatrix.NuGetReady.pdb",
            "[Content_Types].xml",
            "package/services/metadata/core-properties/a93271adeac642279987e486df9ee25a.psmdcp"
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
    public void Tool_policy_rejects_full_generated_exact_name_transformation_space()
    {
        var manifest = PackageSensitiveFilePolicy.ReadManifestForTests();
        Assert.Contains("local-telemetry.json", manifest.FileNameFragments, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(manifest.FamilyRules, rule =>
            rule.Source.Equals("fileNameFragments", StringComparison.OrdinalIgnoreCase) &&
            rule.Match.Equals("contains", StringComparison.OrdinalIgnoreCase) &&
            rule.Scope.Equals("pathSegments", StringComparison.OrdinalIgnoreCase));

        var variants = SensitivePathCorpus.GenerateRejectCorpus(manifest);
        var accepted = variants.Where(path => !PackageSensitiveFilePolicy.IsSensitive(path)).ToArray();

        Assert.Empty(accepted);
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
