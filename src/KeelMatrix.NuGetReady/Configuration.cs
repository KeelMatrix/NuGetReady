using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KeelMatrix.NuGetReady;

internal static partial class ConfigurationLoader
{
    private const int MaxConfigBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "library",
        "multiTargetLibrary",
        "dotnetTool"
    };

    public static NuGetReadyConfig Load(string path)
    {
        string json;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new NuGetReadyInputException("Configuration file was not found.");
            }

            if (file.Length > MaxConfigBytes)
            {
                throw new NuGetReadyInputException("Configuration file is too large.");
            }

            json = File.ReadAllText(path);
        }
        catch (NuGetReadyInputException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new NuGetReadyInfrastructureException("Configuration file could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new NuGetReadyInfrastructureException("Configuration file could not be read.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 32,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            InspectObject(document.RootElement, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            var config = JsonSerializer.Deserialize<NuGetReadyConfig>(json, JsonOptions);
            if (config is null)
            {
                throw new NuGetReadyInputException("Configuration must be a JSON object.");
            }

            Validate(config);
            return config;
        }
        catch (NuGetReadyInputException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new NuGetReadyInputException("Configuration is not valid JSON.");
        }
    }

    private static void InspectObject(JsonElement element, HashSet<string> propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            propertyNames.Clear();
            foreach (var property in element.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    throw new NuGetReadyInputException("Configuration contains a duplicate property.");
                }

                if (SensitivePropertyRegex().IsMatch(property.Name))
                {
                    throw new NuGetReadyInputException("Configuration must not contain publishing credentials.");
                }

                InspectObject(property.Value, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                InspectObject(item, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    private static void Validate(NuGetReadyConfig config)
    {
        if (config.SchemaVersion != 1)
        {
            throw new NuGetReadyInputException("Configuration schema version must be 1.");
        }

        if (config.Packages is not { Count: > 0 and <= 100 })
        {
            throw new NuGetReadyInputException("Configuration must declare between 1 and 100 packages.");
        }

        var artifactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in config.Packages)
        {
            if (string.IsNullOrWhiteSpace(package.Id) || package.Id.Length > 200 || package.Id.Any(char.IsControl))
            {
                throw new NuGetReadyInputException("Every package must have a valid ID.");
            }

            if (string.IsNullOrWhiteSpace(package.Kind) || !AllowedKinds.Contains(package.Kind))
            {
                throw new NuGetReadyInputException("Package kind must be library, multiTargetLibrary, or dotnetTool.");
            }

            if (string.IsNullOrWhiteSpace(package.Version))
            {
                throw new NuGetReadyInputException("Every package must declare a version.");
            }

            try
            {
                _ = VersionText.Normalize(package.Version);
            }
            catch (ArgumentException)
            {
                throw new NuGetReadyInputException("Every package must declare a valid NuGet version.");
            }

            if (package.Artifacts is not { Count: > 0 and <= 100 })
            {
                throw new NuGetReadyInputException("Every package must declare one or more exact artifacts.");
            }

            foreach (var artifact in package.Artifacts)
            {
                if (string.IsNullOrWhiteSpace(artifact) || artifact.Length > 240 ||
                    artifact.Contains('/') || artifact.Contains('\\') ||
                    (!artifact.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) &&
                     !artifact.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new NuGetReadyInputException("Artifacts must be exact .nupkg or .snupkg filenames.");
                }

                if (!artifactNames.Add(artifact))
                {
                    throw new NuGetReadyInputException("An artifact may be declared only once.");
                }
            }

            if (package.Smoke is not null && (package.Smoke.Count > 20 || package.Smoke.Any(argument => argument is null || argument.Length > 200)))
            {
                throw new NuGetReadyInputException("Tool smoke arguments are too large.");
            }

            if (package.Command is not null && (package.Command.Length is 0 or > 100 || package.Command.Any(char.IsWhiteSpace)))
            {
                throw new NuGetReadyInputException("A tool command must be a single non-empty name.");
            }

            if (package.Command is not null && !package.Kind.Equals("dotnetTool", StringComparison.OrdinalIgnoreCase))
            {
                throw new NuGetReadyInputException("Only dotnetTool packages may declare a tool command.");
            }
        }
    }

    [GeneratedRegex("(?i)(password|secret|token|api[._-]?key|credential|authorization|access[._-]?token|nuget[._-]?api)")]
    private static partial Regex SensitivePropertyRegex();
}
