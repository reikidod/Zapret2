using Xunit;
using Zapret2.Core;

namespace Zapret2.Core.Tests;

public sealed class BlockcheckOutputParserTests
{
    [Fact]
    public void ParsesPinnedZapret2WorkingAndNotFoundMarkers()
    {
        const string output = """
            !!!!! curl_test_https_tls12: working strategy found for ipv4 example.com : winws2 --dpi-desync=fake --dpi-desync-ttl=5 !!!!!
            curl_test_http: winws2 strategy for ipv6 example.org not found
            * SUMMARY
            curl_test_https_tls12 ipv4 example.com : winws2 --dpi-desync=fake
            * COMMON
            curl_test_https_tls12 ipv4 : winws2 --dpi-desync=fake
            * COVERAGE
            3/5 : curl_test_https_tls12 ipv4 : winws2 --dpi-desync=fake
            Current scan level was "force"
            It was designed not as a DPI bypass test tool.
            """;

        var report = BlockcheckOutputParser.Parse(output);

        Assert.Equal(2, report.Findings.Count);
        Assert.Equal(BlockcheckFindingKind.WorkingStrategyFound, report.Findings[0].Kind);
        Assert.Equal("example.com", report.Findings[0].Domain);
        Assert.Equal("winws2", report.Findings[0].Engine);
        Assert.Equal("--dpi-desync=fake --dpi-desync-ttl=5", report.Findings[0].Strategy);
        Assert.Equal(BlockcheckFindingKind.StrategyNotFound, report.Findings[1].Kind);
        Assert.Equal(6, report.Findings[1].IpVersion);
        Assert.Single(report.CommonStrategies);
        Assert.Single(report.Coverage);
        Assert.Equal(3, report.Coverage[0].SuccessfulTargets);
        Assert.Equal(5, report.Coverage[0].TotalTargets);
        Assert.Equal("force", report.ScanLevel);
        Assert.True(report.HasSummary);
        Assert.True(report.HasCompletionFooter);
    }

    [Fact]
    public void ParsesClassicWinwsAndDiagnosticWarnings()
    {
        const string output = """
            !!!!! curl_test_https_tls13: working strategy found for ipv6 example.net : winws --dpi-desync=split !!!!!
            * checking DNS
            POSSIBLE DNS HIJACK DETECTED. ZAPRET WILL NOT HELP YOU IN CASE DNS IS SPOOFED !!!
            - IP block tests (requires manual interpretation)
            """;

        var report = BlockcheckOutputParser.Parse(output);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("winws", finding.Engine);
        Assert.Equal(6, finding.IpVersion);
        Assert.True(report.HasDnsHijackWarning);
        Assert.True(report.HasManualIpBlockSection);
    }

    [Fact]
    public void DoesNotTreatGenericSuccessOrFailedTextAsConfirmedStrategyResults()
    {
        const string output = """
            SUCCESS
            FAILED
            curl returned HTTP 200
            working strategy found maybe for ipv4 example.com
            """;

        var report = BlockcheckOutputParser.Parse(output);

        Assert.Empty(report.Findings);
        Assert.Empty(report.CommonStrategies);
        Assert.Empty(report.Coverage);
        Assert.False(report.HasSummary);
        Assert.False(report.HasCompletionFooter);
    }

    [Fact]
    public void EmptyOutputReturnsAnEmptyReport()
    {
        var report = BlockcheckOutputParser.Parse("  \r\n");

        Assert.Empty(report.Findings);
        Assert.Empty(report.Coverage);
        Assert.Null(report.ScanLevel);
        Assert.False(report.HasSummary);
    }
}
