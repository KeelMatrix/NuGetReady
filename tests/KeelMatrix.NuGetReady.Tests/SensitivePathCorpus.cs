namespace KeelMatrix.NuGetReady.Tests;

internal static class SensitivePathCorpus
{
    public static IReadOnlyCollection<string> GenerateAcceptCorpus(PackageSensitivePathManifest manifest)
    {
        var protectedNames = manifest.ExactFileNames
            .Concat(manifest.FileNameFragments)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var corpus = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in protectedNames)
        {
            foreach (var variant in new[]
            {
                $"A{name}B.dll",
                $"{name}s.dll",
                $"{name}arian.dll",
                $"Foo.{name}.dll",
                $"{name}Configuration.dll"
            })
            {
                corpus.Add(variant);
                corpus.Add($"nested/{variant}");
                corpus.Add($"deep/nested/{variant}");
            }
        }

        foreach (var reviewerFalsePositive in new[]
        {
            "Foo.NuGet.Configuration.dll",
            "NuGet.Configuration.Extensions.dll",
            "MySecretStore.dll",
            "Secretarial.dll",
            "Configuration.UserSecrets.dll",
            "TelemetryConfiguration.dll",
            "MyTelemetryMention.dll"
        })
        {
            corpus.Add(reviewerFalsePositive);
            corpus.Add($"nested/{reviewerFalsePositive}");
            corpus.Add($"deep/nested/{reviewerFalsePositive}");
        }

        return corpus;
    }

    public static IReadOnlyCollection<string> GenerateRejectCorpus(PackageSensitivePathManifest manifest)
    {
        var corpus = GenerateExactNameCorpus(manifest)
            .Concat(new[]
            {
                "payload/.local-telemetry.json.bak",
                "payload/xlocal-telemetry.json.bak",
                "nested/deep/.local-telemetry.json.bak",
                "nested/deep/xlocal-telemetry.json.bak",
                "payload/.AGENTS.md",
                "payload/xAGENTS.md.bak",
                "payload/.CHANGELOG.md.old",
                "payload/xNuGet.config.bak",
                "payload/.global.jsonx",
                "payload/.credentials.json.bak",
                "payload/xsecret.old",
                "payload/xapikey",
                "local-telemetry.json",
                "nested/local-telemetry.json",
                "NESTED\\LOCAL-TELEMETRY.JSON",
                "deep/nested/local-telemetry.json",
                "deep\\nested\\LOCAL-TELEMETRY.JSON",
                "zero-byte/local-telemetry.json",
                "local-telemetry.json.bak",
                "local-telemetry.jsonx",
                "local-telemetry.json/child.bin"
            });

        return corpus.ToHashSet(StringComparer.Ordinal);
    }

    public static HashSet<string> GenerateExactNameCorpus(PackageSensitivePathManifest manifest)
    {
        var corpus = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in manifest.ExactFileNames)
        {
            var variants = new[]
            {
                name,
                $"{name}.bak",
                $"{name}.old",
                $"{name}.tmp",
                $"{name}x",
                $"{name}.ab",
                $".{name}",
                $"x{name}",
                $"x{name}.bak",
                $".{name}x",
                $"{name}.",
                $"{name} ",
                name.ToUpperInvariant(),
                ToggleCase(name)
            };

            foreach (var variant in variants)
            {
                corpus.Add(variant);
                corpus.Add($"nested/{variant}");
                corpus.Add($"nested\\{variant}");
            }

            foreach (var directoryName in new[] { name, name.ToUpperInvariant(), ToggleCase(name) })
            {
                corpus.Add($"{directoryName}/child.bin");
                corpus.Add($"deep/nested/{directoryName}/child.bin");
                corpus.Add($"deep\\nested\\{directoryName}\\child.bin");
            }
        }

        return corpus;
    }

    private static string ToggleCase(string value)
    {
        return string.Concat(value.Select((character, index) =>
            index % 2 == 0 ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character)));
    }
}
