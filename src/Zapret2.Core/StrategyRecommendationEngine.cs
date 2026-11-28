namespace Zapret2.Core;

public sealed record StrategyRecommendation(
    string Strategy,
    int SuccessfulTargets,
    string Confidence,
    string Reason);

/// <summary>
/// Ranks only strategies explicitly reported as working by blockcheck.
/// This is an evidence summary, not an automatic network scanner or a guarantee
/// that a strategy will work for other hosts, protocols, or networks.
/// </summary>
public static class StrategyRecommendationEngine
{
    public static IReadOnlyList<StrategyRecommendation> Recommend(
        BlockcheckReport report,
        string engine = "winws2",
        int maximumResults = 5)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (maximumResults <= 0) return [];

        var selectedEngine = string.IsNullOrWhiteSpace(engine) ? "winws2" : engine.Trim();
        return report.Findings
            .Where(item => item.Engine.Equals(selectedEngine, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Kind == BlockcheckFindingKind.WorkingStrategyFound)
            .Where(item => !string.IsNullOrWhiteSpace(item.Strategy))
            .GroupBy(item => item.Strategy!.Trim(), StringComparer.Ordinal)
            .Select(group =>
            {
                var targets = group.Select(item => (item.Domain, item.IpVersion)).Distinct().Count();
                var confidence = targets >= 8 ? "Высокая" : targets >= 3 ? "Средняя" : "Низкая";
                var reason = $"Стратегия отмечена как рабочая для {targets} уникальных целей/IP-вариантов в отчёте.";
                return new StrategyRecommendation(group.Key, targets, confidence, reason);
            })
            .OrderByDescending(item => item.SuccessfulTargets)
            .ThenBy(item => item.Strategy, StringComparer.Ordinal)
            .Take(maximumResults)
            .ToList();
    }
}
