using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Zapret2.Core;

public sealed record ManagedListDefinition(string FileName, string RemotePath, bool IsIpSet);

public sealed record UpdatedListInfo(string FileName, int EntryCount, long SizeBytes, string Sha256);

public sealed record ListUpdateResult(string BackupDirectory, IReadOnlyList<UpdatedListInfo> UpdatedFiles);

public static class ListCatalog
{
    public const string UpstreamRepository = "Flowseal/zapret-discord-youtube";
    private const string RawRoot = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/";

    public static IReadOnlyList<ManagedListDefinition> Managed { get; } =
    [
        new("ipset-all.txt", ".service/ipset-service.txt", true),
        new("ipset-exclude.txt", "lists/ipset-exclude.txt", true),
        new("list-general.txt", "lists/list-general.txt", false),
        new("list-google.txt", "lists/list-google.txt", false),
        new("list-exclude.txt", "lists/list-exclude.txt", false)
    ];

    public static IReadOnlyDictionary<string, string> UserFiles { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["list-general-user.txt"] = "# Пользовательские домены для обработки\n# По одному домену на строку\n",
            ["list-exclude-user.txt"] = "# Домены, которые нужно исключить из обработки\n# По одному домену на строку\n",
            ["ipset-exclude-user.txt"] = "# IP-адреса и подсети, которые нужно исключить из обработки\n# По одному адресу или CIDR на строку\n"
        };

    public static string GetRawUrl(ManagedListDefinition definition) =>
        RawRoot + definition.RemotePath;
}

public sealed class ListUpdateService : IDisposable
{
    private const int MaxListBytes = 2 * 1024 * 1024;
    private const int MaxUserListBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public ListUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        _ownsClient = httpClient is null;
    }

    public static string GetListsDirectory(string bundleRoot)
    {
        if (string.IsNullOrWhiteSpace(bundleRoot))
            throw new ArgumentException("Не задан путь к папке zapret2 bundle.", nameof(bundleRoot));
        return Path.Combine(Path.GetFullPath(bundleRoot), "lists");
    }

    public static IReadOnlyList<string> EnsureUserLists(string bundleRoot)
    {
        var directory = GetListsDirectory(bundleRoot);
        Directory.CreateDirectory(directory);
        var created = new List<string>();
        foreach (var (fileName, template) in ListCatalog.UserFiles)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path)) continue;
            WriteAtomically(path, new UTF8Encoding(false).GetBytes(template));
            created.Add(fileName);
        }
        return created;
    }

    public static string ReadUserList(string bundleRoot, string fileName)
    {
        if (!ListCatalog.UserFiles.ContainsKey(fileName))
            throw new ArgumentException("Разрешено открывать только пользовательские файлы списков.", nameof(fileName));

        var path = Path.Combine(GetListsDirectory(bundleRoot), fileName);
        if (!File.Exists(path))
            return ListCatalog.UserFiles[fileName];

        var info = new FileInfo(path);
        if (info.Length > MaxUserListBytes)
            throw new InvalidDataException($"Файл {fileName} превышает лимит 256 КБ.");

        return File.ReadAllText(path, Encoding.UTF8);
    }

    public static void SaveUserList(string bundleRoot, string fileName, string content)
    {
        if (!ListCatalog.UserFiles.ContainsKey(fileName))
            throw new ArgumentException("Разрешено изменять только пользовательские файлы списков.", nameof(fileName));
        if (content.IndexOf('\0') >= 0)
            throw new InvalidDataException("Файл содержит нулевой байт.");
        var bytes = new UTF8Encoding(false, true).GetBytes(content);
        if (bytes.Length > MaxUserListBytes)
            throw new InvalidDataException("Размер пользовательского списка не должен превышать 256 КБ.");

        var directory = GetListsDirectory(bundleRoot);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        if (File.Exists(path))
        {
            var backupDirectory = Path.Combine(directory, ".backups", "user-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupDirectory);
            File.Copy(path, Path.Combine(backupDirectory, fileName), overwrite: true);
        }
        WriteAtomically(path, bytes);
    }

    public async Task<ListUpdateResult> UpdateAsync(string bundleRoot, CancellationToken cancellationToken = default)
    {
        var listsDirectory = GetListsDirectory(bundleRoot);
        if (!Directory.Exists(listsDirectory))
            throw new DirectoryNotFoundException("В выбранном bundle не найдена папка lists. Проверьте, что выбран корень zapret2 bundle.");

        // Download and validate every managed list before changing any local file.
        var staged = new List<StagedList>();
        foreach (var definition in ListCatalog.Managed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = ListCatalog.GetRawUrl(definition);
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var finalHost = response.RequestMessage?.RequestUri?.Host;
            if (finalHost is not null &&
                !string.Equals(finalHost, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Источник {definition.FileName} перенаправил запрос на неожиданный хост.");

            if (response.Content.Headers.ContentLength is long announced && announced > MaxListBytes)
                throw new InvalidDataException($"Источник {definition.FileName} превысил лимит 2 МБ.");

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxListBytes)
                throw new InvalidDataException($"Получен пустой или слишком большой файл {definition.FileName}.");

            string text;
            try { text = StrictUtf8.GetString(bytes).TrimStart('\uFEFF'); }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException($"Файл {definition.FileName} не является корректным UTF-8.", ex);
            }

            var trimmed = text.TrimStart();
            if (trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Источник {definition.FileName} вернул HTML вместо списка.");

            text = NormalizeText(text);
            var entryCount = ValidateContent(definition, text);
            var normalizedBytes = new UTF8Encoding(false).GetBytes(text);
            staged.Add(new StagedList(
                definition,
                Path.Combine(listsDirectory, definition.FileName),
                normalizedBytes,
                entryCount,
                Convert.ToHexString(SHA256.HashData(normalizedBytes)).ToLowerInvariant()));
        }

        var backupDirectory = Path.Combine(
            listsDirectory, ".backups", "update-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backupDirectory);

        var hadOriginal = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in staged)
        {
            hadOriginal[item.Path] = File.Exists(item.Path);
            if (hadOriginal[item.Path])
                File.Copy(item.Path, Path.Combine(backupDirectory, item.Definition.FileName), overwrite: true);
        }

        var replaced = new List<StagedList>();
        try
        {
            foreach (var item in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteAtomically(item.Path, item.Bytes);
                replaced.Add(item);
            }
        }
        catch (Exception updateError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var item in replaced.AsEnumerable().Reverse())
            {
                try
                {
                    if (hadOriginal[item.Path])
                        File.Copy(Path.Combine(backupDirectory, item.Definition.FileName), item.Path, overwrite: true);
                    else if (File.Exists(item.Path))
                        File.Delete(item.Path);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (rollbackErrors.Count > 0)
                throw new AggregateException("Обновление списков прервано; при откате возникли дополнительные ошибки.", new[] { updateError }.Concat(rollbackErrors));
            throw new IOException("Обновление списков отменено. Предыдущие файлы восстановлены.", updateError);
        }

        return new ListUpdateResult(
            backupDirectory,
            staged.Select(item => new UpdatedListInfo(
                item.Definition.FileName, item.EntryCount, item.Bytes.Length, item.Sha256)).ToList());
    }

    private static int ValidateContent(ManagedListDefinition definition, string text)
    {
        if (text.IndexOf('\0') >= 0)
            throw new InvalidDataException($"Файл {definition.FileName} содержит нулевой байт.");

        var entries = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 &&
                           !line.StartsWith('#') &&
                           !line.StartsWith(';') &&
                           !line.StartsWith("//", StringComparison.Ordinal))
            .ToArray();

        if (entries.Length == 0)
            throw new InvalidDataException($"Файл {definition.FileName} не содержит ни одной записи.");

        if (definition.IsIpSet)
        {
            foreach (var entry in entries)
            {
                var parts = entry.Split('/', 2);
                if (!IPAddress.TryParse(parts[0], out var address))
                    throw new InvalidDataException($"Некорректная IP-запись в {definition.FileName}: {entry}");

                if (parts.Length == 2)
                {
                    var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                    if (!int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > maxPrefix)
                        throw new InvalidDataException($"Некорректная маска подсети в {definition.FileName}: {entry}");
                }
            }
        }

        return entries.Length;
    }

    private static string NormalizeText(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');
        return normalized + "\n";
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }

    private sealed record StagedList(
        ManagedListDefinition Definition,
        string Path,
        byte[] Bytes,
        int EntryCount,
        string Sha256);
}
