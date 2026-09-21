using System.Reflection;
using System.Text.Json;

namespace KeelMatrix.NuGetReady;

internal sealed class PackageSensitivePathManifest
{
    public string[] ExactFileNames { get; set; } = Array.Empty<string>();

    public string[] FileNamePrefixes { get; set; } = Array.Empty<string>();

    public string[] FileNameSuffixes { get; set; } = Array.Empty<string>();

    public string[] FileExtensions { get; set; } = Array.Empty<string>();

    public string[] PathSegments { get; set; } = Array.Empty<string>();

    public string[] PathFragments { get; set; } = Array.Empty<string>();

    public string[] FamilyExceptions { get; set; } = Array.Empty<string>();

    public PackageSensitiveFamilyRule[] FamilyRules { get; set; } = Array.Empty<PackageSensitiveFamilyRule>();
}

internal sealed class PackageSensitiveFamilyRule
{
    public string Source { get; set; } = string.Empty;

    public string Match { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;
}

internal static class PackageSensitiveFilePolicy
{
    private const string ResourceName = "KeelMatrix.NuGetReady.package-sensitive-paths.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly PackageSensitivePathManifest Manifest = LoadManifest();

    public static bool IsSensitive(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var lower = normalized.ToLowerInvariant();
        if (Manifest.PathFragments.Any(fragment => lower.Contains(fragment, StringComparison.Ordinal)))
        {
            return true;
        }

        var segments = lower.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Manifest.PathSegments.Any(segment => segments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (Manifest.FamilyRules.Any(rule => !rule.Scope.Equals("pathSegments", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The package-sensitive path manifest contains an unsupported family rule scope.");
        }

        foreach (var rule in Manifest.FamilyRules)
        {
            var values = GetFamilyValues(rule.Source);
            foreach (var segment in segments)
            {
                if (Manifest.FamilyExceptions.Contains(segment, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (rule.Match.Equals("prefix", StringComparison.OrdinalIgnoreCase) &&
                    values.Any(value => segment.StartsWith(value, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                if (rule.Match.Equals("extension", StringComparison.OrdinalIgnoreCase) &&
                    values.Any(value => ContainsExtensionFamily(segment, value)))
                {
                    return true;
                }
            }
        }

        var name = lower[(lower.LastIndexOf('/') + 1)..];
        return Manifest.ExactFileNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
               Manifest.FileNamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
               Manifest.FileNameSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ||
               Manifest.FileExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] GetFamilyValues(string source)
    {
        return source switch
        {
            "exactFileNames" => Manifest.ExactFileNames,
            "fileNamePrefixes" => Manifest.FileNamePrefixes,
            "fileNameSuffixes" => Manifest.FileNameSuffixes,
            "fileExtensions" => Manifest.FileExtensions,
            "pathSegments" => Manifest.PathSegments,
            _ => throw new InvalidOperationException($"The package-sensitive path manifest contains an unsupported family rule source: {source}.")
        };
    }

    private static bool ContainsExtensionFamily(string segment, string value)
    {
        var start = segment.IndexOf(value, StringComparison.OrdinalIgnoreCase);
        while (start >= 0)
        {
            var end = start + value.Length;
            if (end == segment.Length || segment[end] == '.')
            {
                return true;
            }

            start = segment.IndexOf(value, end, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static PackageSensitivePathManifest ReadManifestForTests() => Manifest;

    private static PackageSensitivePathManifest LoadManifest()
    {
        using var stream = typeof(PackageSensitiveFilePolicy).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The package-sensitive path policy is not embedded.");
        return JsonSerializer.Deserialize<PackageSensitivePathManifest>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The package-sensitive path policy is empty.");
    }
}
