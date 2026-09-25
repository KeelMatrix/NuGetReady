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
        Supported hosts are Windows, Linux, and macOS. The installed tool targets net8.0 and requires the .NET 8 runtime. Library packages under rehearsal may target other frameworks: every declared library target framework is built, runnable frameworks are also executed, and non-runnable library target frameworks are build-only.
        Each package expects exactly one primary .nupkg and at most one associated .snupkg; duplicate primary identities fail closed.
        Consumer provenance checks the library package cache or installed tool store's versioned .nupkg/.nupkg.sha512, package identity, and expanded payload, including XML documentation; tool asset roots are derived from tools/<tfm>/any and unsupported layouts are unproven.
        Workflow-policy pass applies only to the closed-world 0.1.0 release profile with separate unconditional ubuntu-latest producer, fresh-runner validator, and publisher jobs.
        Release identity is mechanical: the exact tag v<configured version>, configured artifact filenames, packed nuspec ID/version, immutable validated-release-artifacts identity, and exact primary .nupkg pushed after NuGet/login@v1 must agree.
        The producer checks out github.sha, uses the fixed SDK and exact restore/format/Release build/Release test/pack commands, and uploads the configured package set. The validator acquires NuGetReady through an exclusive source selection under the fixed runner-controlled resolver before checkout, then runs `/tmp/nugetready-tool/nugetready check --config nugetready.json --artifacts /tmp/nugetready-artifacts --format json` as its final step. The active config must be the exact repository-root nugetready.json and release-control paths require exact cross-platform casing. Ubuntu environment names retain case-sensitive runtime identity: required bindings are exactly KEELMATRIX_NO_TELEMETRY, DOTNET_CLI_TELEMETRY_OPTOUT, NUGET_PACKAGES, and publish-step NUGET_API_KEY where applicable; credential-name normalization does not prove a process binding.
        Exact producer and validator command templates preserve command position, quoting, literal argument bytes, empty arguments, and operators. Quoted command heads, path whitespace, grouping, punctuation, escaping, and other unmodeled PowerShell forms are rejected rather than normalized. Workflow YAML is parsed before relevance filtering; malformed YAML, duplicate keys, invalid roots, or multiple documents are error/exit 2 with a safe relative workflow location, while not-applicable requires successfully inspected non-applicability.
        Publication policy covers jobs with direct publication operations, OIDC, repository write, unknown permission, or recognized credential capability, plus every dependency and artifact-producer job that can influence them. Static tag filters alone do not create publication reachability; a reachable publishing workflow still requires the exact configured-version tag. Credential capability includes root secrets context references inside GitHub expressions, reusable-workflow secrets bindings, complete recognized vars.*, inputs.*, and env.* members, and recognized workflow/job/step env or action/reusable-workflow with keys. For the bounded names NUGET_API_KEY, API_KEY, ACCESS_TOKEN, AUTHORIZATION, PASSWORD, SECRET, and CREDENTIAL, every non-alphanumeric character is removed and the remaining token is compared case-insensitively by exact equality; prefixes and suffixes remain outside. A vars, inputs, or env member selector that is not a complete static member name is unresolvable and enters the capability boundary. The bounded expression-evaluated grammar follows GitHub Actions Context availability: workflow run-name, concurrency, and env values; workflow_call input defaults and output values; job name, concurrency, container, continue-on-error, defaults.run, env, environment, if, outputs, runs-on, secrets, services, strategy, timeout-minutes, and reusable-workflow with values; and step name, run, if, shell, working-directory, timeout-minutes, continue-on-error, env, and action with values, including local composite-action steps. Every scalar leaf in those named mappings and objects is traversed. Incomplete framing in those fields is unsupported/unproven and blocks as error/exit 2 regardless of the referenced context or member name. Non-evaluated literals such as workflow name, trigger filters, workflow-level defaults, and unrelated static configuration do not enter publication policy merely because they contain expression-like text. Ordinary step-based jobs apply workflow, job, and step environment overrides before each active step is evaluated, and literal paths or prose containing the word secrets do not imply the context. Omitted effective permissions remain unsupported because external defaults are unknown; explicit empty and read-only maps remain distinct. Unknown actions, scripts, expressions, or YAML inside this capability-and-reachability boundary are unproven and block as error/exit 2. The same unknown action in unrelated read-only CI without a recognized credential binding stays outside release policy. Deterministic modeled violations fail with exit 1, and warnings never create release confidence.
        The complete supported profile is documented in the repository README. Simplify and isolate an unconventional publication path or accept the explicit unsupported result.
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
