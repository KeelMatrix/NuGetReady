using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

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
                lines.Add($"  {failure.Message}");
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
}
