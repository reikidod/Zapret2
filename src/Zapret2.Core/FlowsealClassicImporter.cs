using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Zapret2.Core;

/// <summary>
/// Downloads the official Flowseal classic winws2 presets as text, extracts only
/// the winws2 argument block, and never executes the upstream BAT/CMD files.
/// </summary>
public static class FlowsealClassicImporter
{
    public const string SourceRepository = "Flowseal/zapret-discord-youtube";
    public const string SourceCommit = "ffce9179b2b1f2aeb961f4d17274ada3b6d6bea5";
    private const string RawRoot = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/" + SourceCommit + "/";
    private const int MaxScriptBytes = 128 * 1024;

    private static readonly string[] Files =
    [
        "general.bat",
        "general (ALT).bat",
        "general (ALT2).bat",
        "general (ALT3).bat",
        "general (ALT4).bat",
        "general (ALT5).bat",
        "general (ALT6).bat",
        "general (ALT7).bat",
        "general (ALT8).bat",
        "general (ALT9).bat",
        "general (ALT10).bat",
        "general (ALT11).bat",
        "general (ALT12).bat",
        "general (ALT13).bat",
        "general (EXP).bat",
        "general (FAKE TLS AUTO).bat",
        "general (FAKE TLS AUTO ALT).bat",
        "general (FAKE TLS AUTO ALT2).bat",
        "general (FAKE TLS AUTO ALT3).bat",
        "general (SIMPLE FAKE).bat",
        "general (SIMPLE FAKE ALT).bat",
        "general (SIMPLE FAKE ALT2).bat"
    ];

    public static IReadOnlyList<Profile> ImportDirectory(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Локальный каталог стратегий Flowseal не найден.");
        var profiles = new List<Profile>(Files.Length);
        foreach (var file in Files)
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path)) throw new FileNotFoundException("Встроенный набор стратегий неполон: " + file, path);
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > MaxScriptBytes) throw new InvalidDataException($"Flowseal script {file} is empty or too large.");
            var source = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF');
            profiles.Add(new Profile
            {
                Name = "Flowseal · " + Path.GetFileNameWithoutExtension(file),
                Args = ExtractArguments(source, file),
                Engine = "winws2",
                Note = $"Оригинальный профиль Flowseal: {file}. Встроенная версия источника: {SourceCommit}. BAT/CMD не запускается — извлекаются только параметры winws2."
            });
        }
        return profiles;
    }

    public static async Task<IReadOnlyList<Profile>> DownloadAsync(
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var ownsClient = httpClient is null;
        httpClient ??= new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        try
        {
            var profiles = new List<Profile>(Files.Length);
            foreach (var file in Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = RawRoot + Uri.EscapeDataString(file);
                using var response = await httpClient.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var finalHost = response.RequestMessage?.RequestUri?.Host;
                if (finalHost is not null &&
                    !string.Equals(finalHost, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Flowseal script {file} redirected to an unexpected host.");

                if (response.Content.Headers.ContentLength is long length && length > MaxScriptBytes)
                    throw new InvalidDataException($"Flowseal script {file} exceeds the allowed size.");

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length == 0 || bytes.Length > MaxScriptBytes)
                    throw new InvalidDataException($"Flowseal script {file} is empty or too large.");

                var source = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                var args = ExtractArguments(source, file);
                var displayName = "Flowseal · " + Path.GetFileNameWithoutExtension(file);

                profiles.Add(new Profile
                {
                    Name = displayName,
                    Args = args,
                    Note = $"Оригинальный профиль Flowseal: {file}. Источник: {SourceRepository}, commit {SourceCommit}. " +
                           "BAT/CMD не запускается — приложение извлекает только параметры winws2. " +
                           "Игровые фильтры используют настройки utils/game_filter.enabled из выбранного bundle; " +
                           "если файла нет, фильтры выключены (порт 12), как в Flowseal по умолчанию."
                });
            }

            return profiles;
        }
        finally
        {
            if (ownsClient) httpClient.Dispose();
        }
    }

    public static string ExtractArguments(string script, string sourceName = "Flowseal BAT")
    {
        var lines = script.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n');

        var startIndex = -1;
        var firstArgs = "";
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("start ", StringComparison.OrdinalIgnoreCase) ||
                !line.Contains("winws2.exe", StringComparison.OrdinalIgnoreCase))
                continue;

            var exeIndex = line.IndexOf("winws2.exe", StringComparison.OrdinalIgnoreCase);
            var tail = line[(exeIndex + "winws2.exe".Length)..].Trim();
            if (tail.StartsWith('"')) tail = tail[1..].TrimStart();
            firstArgs = tail;
            if (firstArgs.EndsWith('^')) firstArgs = firstArgs[..^1].TrimEnd();
            startIndex = i;
            break;
        }

        if (startIndex < 0 || !firstArgs.StartsWith("--", StringComparison.Ordinal))
            throw new InvalidDataException($"Не найдена команда запуска winws2.exe в {sourceName}.");

        var collected = new List<string> { firstArgs };
        var continuation = lines[startIndex].TrimEnd().EndsWith('^');
        for (var i = startIndex + 1; continuation && i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("::", StringComparison.Ordinal) ||
                line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase))
                continue;

            continuation = line.EndsWith('^');
            if (continuation) line = line[..^1].TrimEnd();
            if (line.Length > 0) collected.Add(line);
        }

        if (continuation)
            throw new InvalidDataException($"Незавершённая команда winws2.exe в {sourceName}.");

        var args = string.Join(Environment.NewLine, collected);
        args = args.Replace("%LUA%", "{LUA}\\", StringComparison.OrdinalIgnoreCase)
            .Replace("%LISTS%", "{LISTS}\\", StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterTCP%", "{GAMEFILTERTCP}", StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterUDP%", "{GAMEFILTERUDP}", StringComparison.OrdinalIgnoreCase);

        // Remove batch continuation remnants and reject shell control statements.
        if (args.Contains('%'))
            throw new InvalidDataException($"В {sourceName} остались неизвестные переменные cmd.");
        if (args.Contains("&", StringComparison.Ordinal) ||
            args.Contains("|", StringComparison.Ordinal) ||
            args.Contains(">", StringComparison.Ordinal) ||
            args.Contains("<", StringComparison.Ordinal))
            throw new InvalidDataException($"В {sourceName} обнаружены shell-операторы вне аргументов winws2.");

        var tokens = ArgumentParser.Tokenize(args);
        if (tokens.Count == 0 || tokens.Any(token => !token.StartsWith("--", StringComparison.Ordinal)))
            throw new InvalidDataException($"Не удалось безопасно разобрать параметры winws2 в {sourceName}.");

        return string.Join(Environment.NewLine, tokens.Select(QuoteIfNeeded));
    }

    private static string QuoteIfNeeded(string token) =>
        token.Any(char.IsWhiteSpace) ? "\"" + token.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : token;
}
