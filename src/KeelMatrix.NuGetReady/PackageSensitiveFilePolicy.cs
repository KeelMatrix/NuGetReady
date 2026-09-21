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

        var name = lower[(lower.LastIndexOf('/') + 1)..];
        return Manifest.ExactFileNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
               Manifest.FileNamePrefixes.Any(name.StartsWith) ||
               Manifest.FileNameSuffixes.Any(name.EndsWith) ||
               Manifest.FileExtensions.Any(name.EndsWith);
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
