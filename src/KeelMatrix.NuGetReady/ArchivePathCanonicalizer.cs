using System.Text;

namespace KeelMatrix.NuGetReady;

internal sealed class CanonicalArchiveFiles
{
    private readonly IReadOnlyDictionary<string, string> rawByCanonical;

    public CanonicalArchiveFiles(IReadOnlyDictionary<string, string> rawByCanonical)
    {
        this.rawByCanonical = rawByCanonical;
        Paths = rawByCanonical.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> Paths { get; }

    public IReadOnlyList<string> RawPaths => rawByCanonical.Values
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

    public string RawPath(string canonicalPath)
    {
        return rawByCanonical[canonicalPath];
    }
}

internal static class ArchivePathCanonicalizer
{
    public static bool TryCreate(
        IEnumerable<string> rawPaths,
        out CanonicalArchiveFiles? files,
        out string error)
    {
        var rawByCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in rawPaths)
        {
            if (!TryCanonicalize(rawPath, out var canonicalPath, out error))
            {
                files = null;
                return false;
            }

            if (!rawByCanonical.TryAdd(canonicalPath, rawPath))
            {
                files = null;
                error = "Archive contains duplicate or colliding entry paths.";
                return false;
            }
        }

        files = new CanonicalArchiveFiles(rawByCanonical);
        error = string.Empty;
        return true;
    }

    public static bool TryCanonicalize(string rawPath, out string canonicalPath, out string error)
    {
        canonicalPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Contains('\0'))
        {
            error = "Archive contains an empty or invalid entry path.";
            return false;
        }

        if (rawPath.Contains('\\'))
        {
            error = "Archive contains a non-canonical backslash entry path.";
            return false;
        }

        if (rawPath.StartsWith('/') || rawPath.StartsWith('\\'))
        {
            error = "Archive contains a rooted entry path.";
            return false;
        }

        var segments = rawPath.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':')))
        {
            error = "Archive contains an uncontained entry path.";
            return false;
        }

        try
        {
            canonicalPath = rawPath.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            error = "Archive contains an invalid Unicode entry path.";
            return false;
        }

        return true;
    }
}
