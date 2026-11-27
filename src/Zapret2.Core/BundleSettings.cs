namespace Zapret2.Core;

public static class BundleSettings
{
    private const string DefaultFilter = "12";
    private const string DefaultRange = "1024-65535";

    public static void Apply(string bundleRoot, Components components)
    {
        var root = Path.GetFullPath(bundleRoot);
        components.ListsDirectory = Path.Combine(root, "lists");
        components.GameFilter = DefaultFilter;
        components.GameFilterTcp = DefaultFilter;
        components.GameFilterUdp = DefaultFilter;

        var settingsPath = Path.Combine(root, "utils", "game_filter.enabled");
        if (!File.Exists(settingsPath)) return;

        var mode = "disabled";
        var tcpCandidate = "";
        var udpCandidate = "";
        foreach (var rawLine in File.ReadLines(settingsPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

            var separator = line.IndexOf('=');
            var key = separator < 0 ? line : line[..separator].Trim();
            var value = separator < 0 ? "" : line[(separator + 1)..].Trim();

            if (key.Equals("mode", StringComparison.OrdinalIgnoreCase))
                mode = value;
            else if (key.Equals("all", StringComparison.OrdinalIgnoreCase))
                mode = "all";
            else if (key.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length == 0) mode = "tcp";
                else tcpCandidate = value;
            }
            else if (key.Equals("udp", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length == 0) mode = "udp";
                else udpCandidate = value;
            }
        }

        var tcpRange = ValidateRange(tcpCandidate) ? tcpCandidate : DefaultRange;
        var udpRange = ValidateRange(udpCandidate) ? udpCandidate : DefaultRange;

        switch (mode.Trim().ToLowerInvariant())
        {
            case "all":
                components.GameFilter = tcpRange;
                components.GameFilterTcp = tcpRange;
                components.GameFilterUdp = udpRange;
                break;
            case "tcp":
                components.GameFilter = tcpRange;
                components.GameFilterTcp = tcpRange;
                components.GameFilterUdp = DefaultFilter;
                break;
            case "udp":
                components.GameFilter = udpRange;
                components.GameFilterTcp = DefaultFilter;
                components.GameFilterUdp = udpRange;
                break;
        }
    }

    public static bool ValidateRange(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split('-', 2);
            if (!int.TryParse(parts[0], out var first) || first < 1 || first > 65535)
                return false;
            if (parts.Length == 2 &&
                (!int.TryParse(parts[1], out var last) || last < first || last > 65535))
                return false;
        }

        return true;
    }
}
