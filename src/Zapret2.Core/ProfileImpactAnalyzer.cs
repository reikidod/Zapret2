namespace Zapret2.Core;

public sealed record ProfileImpactSummary(
    IReadOnlyList<string> TcpPortFilters,
    IReadOnlyList<string> UdpPortFilters,
    IReadOnlyList<string> IncludedHostlists,
    IReadOnlyList<string> IncludedDomains,
    IReadOnlyList<string> ExcludedHostlists,
    IReadOnlyList<string> IncludedIpSets,
    IReadOnlyList<string> ExcludedIpSets)
{
    public string ToDisplayText()
    {
        var lines = new List<string>();
        Add("TCP-порты", TcpPortFilters);
        Add("UDP-порты", UdpPortFilters);
        Add("Доменные списки обработки", IncludedHostlists);
        Add("Домены обработки", IncludedDomains);
        Add("Доменные исключения", ExcludedHostlists);
        Add("IP-наборы обработки", IncludedIpSets);
        Add("IP-исключения", ExcludedIpSets);
        return lines.Count == 0
            ? "В профиле не найдены явные фильтры портов, доменов или IP-наборов."
            : string.Join(Environment.NewLine, lines);

        void Add(string label, IReadOnlyList<string> values)
        {
            if (values.Count > 0)
                lines.Add(label + ": " + string.Join(", ", values));
        }
    }
}

public static class ProfileImpactAnalyzer
{
    public static ProfileImpactSummary Analyze(string profileTemplate)
    {
        var tokens = ArgumentParser.Tokenize(profileTemplate);
        return new ProfileImpactSummary(
            Values(tokens, "--wf-tcp-out="),
            Values(tokens, "--wf-udp-out="),
            FileValues(tokens, "--hostlist="),
            Values(tokens, "--hostlist-domains="),
            FileValues(tokens, "--hostlist-exclude="),
            FileValues(tokens, "--ipset="),
            FileValues(tokens, "--ipset-exclude="));
    }

    private static IReadOnlyList<string> Values(IEnumerable<string> tokens, string prefix) =>
        tokens.Where(token => token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(token => token[prefix.Length..])
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> FileValues(IEnumerable<string> tokens, string prefix) =>
        Values(tokens, prefix).Select(value =>
        {
            var normalized = value.Replace('\\', '/');
            var name = normalized.Split('/').LastOrDefault() ?? normalized;
            return name.Trim('"', '\'');
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
