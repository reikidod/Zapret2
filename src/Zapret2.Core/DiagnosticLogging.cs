using System.Text;

namespace Zapret2.Core;

public sealed record DiagnosticLogInfo(string Path, string FileName, DateTime LastWriteTimeUtc, long SizeBytes);

public sealed class DiagnosticLogStore
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object _sync = new();
    private readonly int _maxSessions;
    private string? _currentPath;

    public string DirectoryPath { get; }
    public string? CurrentPath
    {
        get { lock (_sync) return _currentPath; }
    }

    public DiagnosticLogStore(string? directory = null, int maxSessions = 30)
    {
        if (maxSessions < 1) throw new ArgumentOutOfRangeException(nameof(maxSessions));
        _maxSessions = maxSessions;
        DirectoryPath = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zapret2GUI", "logs");
    }

    public string StartSession(string title)
    {
        lock (_sync)
            return StartSessionUnsafe(title);
    }

    private string StartSessionUnsafe(string title)
    {
        Directory.CreateDirectory(DirectoryPath);
        var fileName = $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.log";
        _currentPath = Path.Combine(DirectoryPath, fileName);
        var safeTitle = (title ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        var header =
            "Zapret2 Control Center diagnostic session" + Environment.NewLine +
            "Started: " + DateTimeOffset.Now.ToString("O") + Environment.NewLine +
            "Session: " + safeTitle + Environment.NewLine +
            Environment.NewLine;
        File.WriteAllText(_currentPath, header, Utf8);
        PruneUnsafe();
        return _currentPath;
    }

    public void Append(string line)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_currentPath) || !File.Exists(_currentPath))
                StartSessionUnsafe("application");

            File.AppendAllText(
                _currentPath!,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {line}{Environment.NewLine}",
                Utf8);
        }
    }

    public IReadOnlyList<DiagnosticLogInfo> GetSessions()
    {
        lock (_sync)
        {
            if (!Directory.Exists(DirectoryPath)) return [];
            return Directory.GetFiles(DirectoryPath, "session-*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ThenByDescending(info => info.Name, StringComparer.OrdinalIgnoreCase)
                .Select(info => new DiagnosticLogInfo(
                    info.FullName, info.Name, info.LastWriteTimeUtc, info.Length))
                .ToList();
        }
    }

    public void Prune()
    {
        lock (_sync) PruneUnsafe();
    }

    private void PruneUnsafe()
    {
        if (!Directory.Exists(DirectoryPath)) return;
        var files = Directory.GetFiles(DirectoryPath, "session-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var path in files.Skip(_maxSessions))
        {
            if (string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
