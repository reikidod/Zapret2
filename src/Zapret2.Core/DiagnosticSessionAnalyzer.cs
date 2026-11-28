using System.Security.Cryptography;
using System.Text;

namespace Zapret2.Core;

public sealed record DiagnosticSessionSummary(
    string Path,
    string SessionTitle,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastEventAt,
    TimeSpan? Duration,
    int TotalLines,
    int PotentialErrorLines,
    int PotentialWarningLines,
    string Sha256,
    long SizeBytes);

public sealed record DiagnosticSessionComparison(
    DiagnosticSessionSummary First,
    DiagnosticSessionSummary Second,
    int LineDelta,
    int PotentialErrorDelta,
    int PotentialWarningDelta,
    TimeSpan? DurationDelta,
    bool ContentChanged);

/// <summary>
/// Conservative summary for application-owned session logs. This deliberately does not
/// interpret blockcheck output or infer whether a network strategy succeeded.
/// </summary>
public static class DiagnosticSessionAnalyzer
{
    public const long MaximumFileSizeBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] ErrorMarkers = ["error", "ошибка", "exception", "failed", "fatal"];
    private static readonly string[] WarningMarkers = ["warning", "предупреждение", "warn"];

    public static DiagnosticSessionSummary Analyze(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Файл журнала не найден.", fullPath);
        if (info.Length > MaximumFileSizeBytes)
            throw new InvalidDataException($"Журнал превышает допустимый размер {MaximumFileSizeBytes / 1024 / 1024} МБ.");

        var bytes = File.ReadAllBytes(fullPath);
        var text = StrictUtf8.GetString(bytes);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var totalLines = lines.Count(line => line.Length > 0);
        var errors = 0;
        var warnings = 0;
        DateTimeOffset? startedAt = null;
        DateTimeOffset? lastEventAt = null;
        var title = "Без названия";

        foreach (var line in lines)
        {
            if (line.StartsWith("Session: ", StringComparison.Ordinal))
                title = line["Session: ".Length..].Trim();

            if (line.StartsWith("Started: ", StringComparison.Ordinal) &&
                DateTimeOffset.TryParse(line["Started: ".Length..].Trim(), out var parsedStart))
                startedAt = parsedStart;

            if (TryReadEventTimestamp(line, out var eventTime))
                lastEventAt = lastEventAt is null || eventTime > lastEventAt ? eventTime : lastEventAt;

            if (ContainsAny(line, ErrorMarkers)) errors++;
            else if (ContainsAny(line, WarningMarkers)) warnings++;
        }

        TimeSpan? duration = startedAt is not null && lastEventAt is not null
            ? lastEventAt.Value - startedAt.Value
            : null;
        if (duration < TimeSpan.Zero) duration = null;

        return new DiagnosticSessionSummary(
            fullPath, title, startedAt, lastEventAt, duration, totalLines,
            errors, warnings, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength);
    }

    public static DiagnosticSessionComparison Compare(string firstPath, string secondPath)
    {
        var first = Analyze(firstPath);
        var second = Analyze(secondPath);
        return new DiagnosticSessionComparison(
            first,
            second,
            second.TotalLines - first.TotalLines,
            second.PotentialErrorLines - first.PotentialErrorLines,
            second.PotentialWarningLines - first.PotentialWarningLines,
            first.Duration is not null && second.Duration is not null
                ? second.Duration.Value - first.Duration.Value
                : null,
            !string.Equals(first.Sha256, second.Sha256, StringComparison.Ordinal));
    }

    private static bool TryReadEventTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (line.Length < 22 || line[0] != '[') return false;
        var close = line.IndexOf(']');
        return close > 1 && DateTimeOffset.TryParse(line.AsSpan(1, close - 1), out timestamp);
    }

    private static bool ContainsAny(string line, IEnumerable<string> markers) =>
        markers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
