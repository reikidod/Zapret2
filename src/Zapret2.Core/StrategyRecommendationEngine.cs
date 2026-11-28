namespace Zapret2.Core;

public sealed record StrategyRecommendation(
    string Strategy,
    int SuccessfulTargets,
    int FailedTargets,
    int TotalTargets,
    double SuccessRate,
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
        var findings = report.Findings
            .Where(item => item.Engine.Equals(selectedEngine, StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.IsNullOrWhiteSpace(item.Strategy))
            .ToList();

        var groups = findings
            .GroupBy(item => item.Strategy!.Trim(), StringComparer.Ordinal)
            .Select(group =>
            {
                var successes = group
                    .Where(item => item.Kind == BlockcheckFindingKind.WorkingStrategyFound)
                    .Select(item => (item.Domain, item.IpVersion))
                    .Distinct()
                    .Count();
                var failures = group
                    .Where(item => item.Kind == BlockcheckFindingKind.StrategyNotFound)
                    .Select(item => (item.Domain, item.IpVersion))
                    .Distinct()
                    .Count();
                var total = successes + failures;
                var rate = total == 0 ? 0 : (double)successes / total;
                var confidence = total >= 8 && rate >= 0.75 ? "Высокая"
                    : total >= 3 && rate >= 0.60 ? "Средняя"
                    : "Низкая";
                var reason = $"Подтверждено: {successes}; не подтверждено: {failures}. " +
                    "Оценка относится только к целям и условиям этого blockcheck.";
                return new StrategyRecommendation(group.Key, successes, failures, total, rate, confidence, reason);
            })
            .Where(item => item.SuccessfulTargets > 0)
            .OrderByDescending(item => item.SuccessRate)
            .ThenByDescending(item => item.SuccessfulTargets)
            .ThenBy(item => item.Strategy, StringComparer.Ordinal)
            .Take(maximumResults)
            .ToList();

        return groups;
    }
}
