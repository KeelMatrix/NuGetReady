using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace KeelMatrix.NuGetReady;

internal static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Write(ReadinessReport report, OutputFormat format)
    {
        Console.Write(format == OutputFormat.Json ? RenderJson(report) : RenderText(report));
        Console.Write('\n');
    }

    public static string RenderJson(ReadinessReport report)
    {
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static string RenderText(ReadinessReport report)
    {
        var lines = new List<string>
        {
            $"NuGetReady: {report.Status.ToUpperInvariant()}",
            string.Empty,
            $"Expected artifacts: {report.ExpectedArtifacts}",
            $"Found artifacts: {report.FoundArtifacts}",
            string.Empty
        };

        foreach (var check in report.Checks)
        {
            lines.Add($"{check.Status.ToUpperInvariant()} {DisplayName(check.Id)}");
            foreach (var failure in check.Failures)
            {
                lines.Add($"  {FormatFailure(failure)}");
            }
        }

        if (report.Status == "pass")
        {
            lines.Add(string.Empty);
            lines.Add("Release checks passed.");
        }
        else if (report.Status == "warn")
        {
            lines.Add(string.Empty);
            lines.Add("Release checks passed with warnings.");
        }
        else if (report.Status == "fail")
        {
            lines.Add(string.Empty);
            lines.Add("Release checks found blocking readiness failures.");
        }
        else
        {
            lines.Add(string.Empty);
            lines.Add("Release checks did not complete trustworthily.");
        }

        return string.Join("\n", lines);
    }

    private static string DisplayName(string checkId)
    {
        return checkId switch
        {
            "artifact-set" => "artifact set",
            "archive-metadata" => "package metadata",
            "archive-layout" => "package layout",
            "dependency-groups" => "dependency groups",
            "dependency-coherence" => "dependency coherence",
            "archive-security" => "archive security",
            "archive-parse" => "archive parsing",
            "workflow-policy" => "workflow policy",
            "consumer-rehearsal" => "consumer rehearsal",
            _ => checkId
        };
    }

    private static string FormatFailure(Failure failure)
    {
        if (failure.PackageId is null || failure.PackageVersion is null ||
            failure.ArtifactFileName is null || failure.ExpectationName is null)
        {
            return EscapeText(failure.Message);
        }

        return $"Package '{EscapeText(failure.PackageId)}' version '{EscapeText(failure.PackageVersion)}' " +
               $"(artifact '{EscapeText(failure.ArtifactFileName)}', expectation '{EscapeText(failure.ExpectationName)}'): {EscapeText(failure.Message)}";
    }

    private static string EscapeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u").Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
