namespace KeelMatrix.NuGetReady.Tests;

internal static class SensitivePathCorpus
{
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
