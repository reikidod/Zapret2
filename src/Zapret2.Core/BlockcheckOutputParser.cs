using System.Text.RegularExpressions;

namespace Zapret2.Core;

public enum BlockcheckFindingKind
{
    WorkingStrategyFound,
    StrategyNotFound
}

public sealed record BlockcheckFinding(
    string TestName,
    int IpVersion,
    string Domain,
    string Engine,
    BlockcheckFindingKind Kind,
    string? Strategy,
    string SourceLine);

public sealed record BlockcheckCoverage(int SuccessfulTargets, int TotalTargets, string Strategy);

public sealed record BlockcheckReport(
    IReadOnlyList<BlockcheckFinding> Findings,
    IReadOnlyList<string> CommonStrategies,
    IReadOnlyList<BlockcheckCoverage> Coverage,
    string? ScanLevel,
    bool HasSummary,
    bool HasCompletionFooter,
    bool HasDnsHijackWarning,
    bool HasManualIpBlockSection);

/// <summary>
/// Parses only stable, explicit result markers emitted by the pinned upstream blockcheck scripts.
/// Unrecognized output is intentionally ignored rather than interpreted as a successful test.
/// </summary>
public static class BlockcheckOutputParser
{
    private static readonly Regex Found = new(
        @"^!!!!!\s*(?<test>[^:]+): working strategy found for ipv(?<ip>[46]) (?<domain>.+?) : (?<engine>winws2|winws)(?: (?<strategy>.*?))? !!!!!$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NotFound = new(
        @"^(?<test>[^:]+): (?<engine>winws2|winws) strategy for ipv(?<ip>[46]) (?<domain>.+?) not found$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Coverage = new(
        @"^(?<count>\d+)/(?<total>\d+)\s*:\s*(?<strategy>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ScanLevel = new(
        "^Current scan level was \"(?<level>[a-zA-Z0-9_-]+)\"$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static BlockcheckReport Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new([], [], [], null, false, false, false, false);

        var findings = new List<BlockcheckFinding>();
        var common = new List<string>();
        var coverage = new List<BlockcheckCoverage>();
        var section = "";
        string? scanLevel = null;
        var hasSummary = false;
        var hasFooter = false;
        var hasDnsHijackWarning = false;
        var hasManualIpBlockSection = false;

        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.Equals("* SUMMARY", StringComparison.OrdinalIgnoreCase))
            {
                hasSummary = true;
                section = "summary";
                continue;
            }
            if (line.Equals("* COMMON", StringComparison.OrdinalIgnoreCase))
            {
                section = "common";
                continue;
            }
            if (line.Equals("* COVERAGE", StringComparison.OrdinalIgnoreCase))
            {
                section = "coverage";
                continue;
            }

            hasDnsHijackWarning |= line.Contains("POSSIBLE DNS HIJACK DETECTED", StringComparison.OrdinalIgnoreCase);
            hasManualIpBlockSection |= line.Equals("- IP block tests (requires manual interpretation)", StringComparison.OrdinalIgnoreCase);
            hasFooter |= line.Equals("It was designed not as a DPI bypass test tool.", StringComparison.OrdinalIgnoreCase);

            var levelMatch = ScanLevel.Match(line);
            if (levelMatch.Success)
                scanLevel = levelMatch.Groups["level"].Value.ToLowerInvariant();

            var found = Found.Match(line);
            if (found.Success)
            {
                findings.Add(new BlockcheckFinding(
                    found.Groups["test"].Value.Trim(),
                    int.Parse(found.Groups["ip"].Value),
                    found.Groups["domain"].Value.Trim(),
                    found.Groups["engine"].Value.ToLowerInvariant(),
                    BlockcheckFindingKind.WorkingStrategyFound,
                    found.Groups["strategy"].Success ? found.Groups["strategy"].Value.Trim() : "",
                    raw));
                continue;
            }

            var notFound = NotFound.Match(line);
            if (notFound.Success)
            {
                findings.Add(new BlockcheckFinding(
                    notFound.Groups["test"].Value.Trim(),
                    int.Parse(notFound.Groups["ip"].Value),
                    notFound.Groups["domain"].Value.Trim(),
                    notFound.Groups["engine"].Value.ToLowerInvariant(),
                    BlockcheckFindingKind.StrategyNotFound,
                    null,
                    raw));
                continue;
            }

            if (section == "common")
                common.Add(line);
            else if (section == "coverage")
            {
                var match = Coverage.Match(line);
                if (match.Success &&
                    int.TryParse(match.Groups["count"].Value, out var count) &&
                    int.TryParse(match.Groups["total"].Value, out var total) &&
                    total > 0 && count <= total)
                    coverage.Add(new BlockcheckCoverage(count, total, match.Groups["strategy"].Value.Trim()));
            }
        }

        return new(
            findings,
            common.Distinct(StringComparer.Ordinal).ToList(),
            coverage,
            scanLevel,
            hasSummary,
            hasFooter,
            hasDnsHijackWarning,
            hasManualIpBlockSection);
    }
}
