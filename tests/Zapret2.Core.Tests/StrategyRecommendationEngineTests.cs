using Xunit;
using Zapret2.Core;

namespace Zapret2.Core.Tests;

public sealed class StrategyRecommendationEngineTests
{
    [Fact]
    public void RanksStrategiesByDistinctConfirmedTargetsForWinws2()
    {
        const string output = """
            !!!!! curl_test_https_tls12: working strategy found for ipv4 one.example : winws2 --dpi-desync=fake !!!!!!
            !!!!! curl_test_https_tls12: working strategy found for ipv6 one.example : winws2 --dpi-desync=fake !!!!!!
            !!!!! curl_test_https_tls12: working strategy found for ipv4 two.example : winws2 --dpi-desync=fake !!!!!!
            !!!!! curl_test_https_tls12: working strategy found for ipv4 three.example : winws2 --dpi-desync=split !!!!!
            !!!!! curl_test_https_tls12: working strategy found for ipv4 four.example : winws --dpi-desync=multisplit !!!!!
            """;

        var recommendations = StrategyRecommendationEngine.Recommend(BlockcheckOutputParser.Parse(output));

        Assert.Equal(2, recommendations.Count);
        Assert.Equal("--dpi-desync=fake", recommendations[0].Strategy);
        Assert.Equal(3, recommendations[0].SuccessfulTargets);
        Assert.Equal("Средняя", recommendations[0].Confidence);
        Assert.Equal("--dpi-desync=split", recommendations[1].Strategy);
        Assert.Equal(1, recommendations[1].SuccessfulTargets);
    }

    [Fact]
    public void CanFilterToClassicEngineAndLimitResults()
    {
        const string output = """
            !!!!! curl_test_https_tls12: working strategy found for ipv4 one.example : winws2 --dpi-desync=fake !!!!!
            !!!!! curl_test_https_tls12: working strategy found for ipv4 two.example : winws --dpi-desync=split !!!!!
            !!!!! curl_test_https_tls12: working strategy found for ipv4 three.example : winws --dpi-desync=split !!!!!
            """;

        var recommendations = StrategyRecommendationEngine.Recommend(
            BlockcheckOutputParser.Parse(output), engine: "winws", maximumResults: 1);

        var recommendation = Assert.Single(recommendations);
        Assert.Equal("--dpi-desync=split", recommendation.Strategy);
        Assert.Equal(2, recommendation.SuccessfulTargets);
    }

    [Fact]
    public void DoesNotRecommendStrategiesWithoutExplicitSuccessMarkers()
    {
        var report = BlockcheckOutputParser.Parse("SUCCESS\nFAILED\n");
        Assert.Empty(StrategyRecommendationEngine.Recommend(report));
    }

    [Fact]
    public void RejectsNonPositiveResultLimitByReturningNoRecommendations()
    {
        const string output = "!!!!! curl_test_https_tls12: working strategy found for ipv4 one.example : winws2 --dpi-desync=fake !!!!!";
        var report = BlockcheckOutputParser.Parse(output);

        Assert.Empty(StrategyRecommendationEngine.Recommend(report, maximumResults: 0));
    }
}
