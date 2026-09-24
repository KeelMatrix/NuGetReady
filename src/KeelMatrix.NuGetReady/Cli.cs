using System.Globalization;
using System.Text.RegularExpressions;

namespace KeelMatrix.NuGetReady;

internal static partial class CliParser
{
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].Equals("--help", StringComparison.Ordinal))
        {
            throw new HelpRequestedException();
        }

        if (!args[0].Equals("check", StringComparison.Ordinal))
        {
            throw new CliInputException($"Unknown command '{Sanitize(args[0])}'.", OutputFormat.Text);
        }

        var config = Path.Combine(Directory.GetCurrentDirectory(), "nugetready.json");
        var artifacts = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "packages");
        var format = OutputFormat.Text;
        var timeout = TimeSpan.FromMinutes(5);

        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--config":
                    config = ReadValue(args, ref index, option, format);
                    break;
                case "--artifacts":
                    artifacts = ReadValue(args, ref index, option, format);
                    break;
                case "--format":
                    var formatValue = ReadValue(args, ref index, option, format);
                    format = formatValue.ToLowerInvariant() switch
                    {
                        "text" => OutputFormat.Text,
                        "json" => OutputFormat.Json,
                        _ => throw new CliInputException("Format must be text or json.", format)
                    };
                    break;
                case "--timeout":
                    timeout = ParseTimeout(ReadValue(args, ref index, option, format), format);
                    break;
                case "--help":
                    throw new HelpRequestedException();
                default:
                    throw new CliInputException($"Unknown option '{Sanitize(option)}'.", format);
            }
        }

        if (string.IsNullOrWhiteSpace(config) || string.IsNullOrWhiteSpace(artifacts))
        {
            throw new CliInputException("Config and artifact paths must not be empty.", format);
        }

        return new CliOptions(config, artifacts, format, timeout);
    }

    public static string HelpText => """
        nugetready check [options]

        Options:
          --config <path>       Version-1 repository config (default: ./nugetready.json)
          --artifacts <path>    Package archive directory (default: ./artifacts/packages)
          --format text|json    Report format (default: text)
          --timeout <duration>  Bounded operation timeout (for example 30s or 00:05:00)

        Check statuses are pass, warn, fail, error, not-run, and not-applicable.
        Each package expects exactly one primary .nupkg and at most one associated .snupkg; duplicate primary identities fail closed.
        Consumer provenance checks the library package cache or installed tool store's versioned .nupkg/.nupkg.sha512, package identity, and expanded payload, including XML documentation; tool asset roots are derived from tools/<tfm>/any and unsupported layouts are unproven.
        Workflow-policy pass certifies only the closed-world 0.1.0 release profile: one literal v*.*.* tag trigger and separate unconditional ubuntu-latest producer, fresh-runner validator, and publisher jobs. The 30-minute producer checks out the exact triggering github.sha, uses a global.json containing only SDK 8.0.425, rollForward latestPatch, and allowPrerelease false, runs the exact restore/format/Release build/Release test/pack commands, and immediately uploads the exact immutable validated-release-artifacts identity. Before checkout, the 10-minute validator creates a fixed runner-controlled /tmp/nugetready-acquisition/global.json with that same SDK resolver, sets up SDK 8.0.425, downloads the artifact identity, and installs NuGetReady 0.1.0 through an exclusive source selection while /tmp/nugetready-acquisition is the working directory. It then checks out github.sha and runs `/tmp/nugetready-tool/nugetready check --config nugetready.json --artifacts /tmp/nugetready-artifacts --format json` as its final step. Certification requires the active config to be the exact repository-root nugetready.json used by that validator. The .github/workflows directory, lower-case workflow extension, global.json, NuGet.config, nugetready.json, and referenced solution/project paths must exist with exact cross-platform casing. Normal repositories use only https://api.nuget.org/v3/index.json. NuGetReady's own exact 0.1.0 candidate uses an exact temporary config that maps only the tool ID to the immutable artifact directory and transitive dependencies to NuGet.org; any resolver, acquisition-directory, or source variation is unsupported/unproven. The 10-minute publish job depends on validation, downloads the original producer artifact, authenticates with unconditional NuGet/login@v1 Trusted Publishing, and directly pushes the exact primary .nupkg with the temporary login output. Any executable workflow with effective publication capability from OIDC, supported write permissions, or a secret-backed credential enters closed-world evaluation even when its command name is normally quiet. Arbitrary GitHub Actions, shell, composite/reusable workflow, dynamic expression, alias/merge-key, arbitrary MSBuild outside the isolated producer, wrapper, generated-script, command-resolution, additional release-job, and GitHub Release semantics are not interpreted. A deterministic violation is fail/exit 1. Every unknown or unsupported publication-relevant node is unproven and blocks as error/exit 2; it never defaults to safe or to a warning. Simplify and isolate an unconventional publication path or accept the explicit unsupported result.
        Unix child processes run in a dedicated process group with bounded descendant cleanup and verification on successful completion, timeout, and cancellation.
        """.TrimEnd();

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option, OutputFormat format)
    {
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliInputException($"Option {option} requires a value.", format);
        }

        index++;
        return args[index];
    }

    private static TimeSpan ParseTimeout(string value, OutputFormat format)
    {
        TimeSpan timeout;
        if (DurationRegex().Match(value) is { Success: true } match)
        {
            var number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            timeout = match.Groups[2].Value.ToLowerInvariant() switch
            {
                "s" => TimeSpan.FromSeconds(number),
                "m" => TimeSpan.FromMinutes(number),
                "h" => TimeSpan.FromHours(number),
                _ => TimeSpan.Zero
            };
        }
        else if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out timeout))
        {
            throw new CliInputException("Timeout must be a positive duration such as 30s or 00:05:00.", format);
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1))
        {
            throw new CliInputException("Timeout must be greater than zero and no more than one day.", format);
        }

        return timeout;
    }

    private static string Sanitize(string value)
    {
        return value.Length > 80 ? value[..80] : value;
    }

    [GeneratedRegex("^([0-9]+(?:\\.[0-9]+)?)([smh])$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();
}

internal sealed class HelpRequestedException : Exception
{
}

internal sealed class CliInputException : Exception
{
    public CliInputException(string message, OutputFormat format)
        : base(message)
    {
        Format = format;
    }

    public OutputFormat Format { get; }
}
