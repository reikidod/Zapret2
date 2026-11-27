using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Zapret2.Core;

public sealed class Profile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("args")]
    public string Args { get; set; } = "";
    [JsonPropertyName("note")]
    public string Note { get; set; } = "";
}

public sealed class Components
{
    public string? Winws2 { get; set; }
    public string? LuaDirectory { get; set; }
    public string? Blockcheck { get; set; }
    public string? BinaryDirectory => Winws2 is null ? null : Path.GetDirectoryName(Winws2);
}

public sealed class AppConfig
{
    [JsonPropertyName("base_dir")]
    public string BaseDirectory { get; set; } = "";
    [JsonPropertyName("active")]
    public string ActiveProfile { get; set; } = "";
    [JsonPropertyName("seeded")]
    public List<string> Seeded { get; set; } = [];
    [JsonPropertyName("debug")]
    public bool DebugEnabled { get; set; }
    [JsonPropertyName("profiles")]
    public List<Profile> Profiles { get; set; } = [];
}

public sealed class ProfileStore
{
    public const string TemplateArgs = """
# Добавьте параметры из blockcheck2 или импортируйте готовый .cmd.
# Переменные: {BIN} — папка winws2.exe, {LUA} — папка zapret-lib.lua.
--wf-tcp-out=80,443
--lua-init=@"{LUA}\zapret-lib.lua"
--lua-init=@"{LUA}\zapret-antidpi.lua"
""";
    public string DirectoryPath { get; }
    public string ConfigPath => Path.Combine(DirectoryPath, "config.json");
    public AppConfig Config { get; private set; } = new();

    public ProfileStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zapret2GUI");
        Directory.CreateDirectory(DirectoryPath);
    }

    public void Load()
    {
        AppConfig? loaded = null;
        var primaryInvalid = false;

        if (File.Exists(ConfigPath))
        {
            try { loaded = ReadConfig(ConfigPath); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                primaryInvalid = true;
            }
        }

        if (loaded is null && File.Exists(ConfigPath + ".bak"))
        {
            try
            {
                loaded = ReadConfig(ConfigPath + ".bak");
                if (File.Exists(ConfigPath))
                    File.Copy(ConfigPath, Path.ChangeExtension(ConfigPath, ".broken"), true);
                File.Copy(ConfigPath + ".bak", ConfigPath, true);
                primaryInvalid = false;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        if (loaded is null && primaryInvalid && File.Exists(ConfigPath))
        {
            try { File.Copy(ConfigPath, Path.ChangeExtension(ConfigPath, ".broken"), true); } catch (IOException) { }
        }

        Config = loaded ?? new AppConfig();
        Config.Seeded ??= [];
        Config.Profiles ??= [];
        Config.Profiles = Config.Profiles
            .Where(p => p is not null)
            .Select(p => new Profile
            {
                Name = string.IsNullOrWhiteSpace(p.Name) ? "Импортированный профиль" : p.Name.Trim(),
                Args = p.Args ?? "",
                Note = p.Note ?? ""
            })
            .ToList();

        // Keep the first duplicate name and rename subsequent entries without losing their arguments.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Config.Profiles)
        {
            var baseName = profile.Name;
            for (var suffix = 2; !names.Add(profile.Name); suffix++)
                profile.Name = $"{baseName} ({suffix})";
        }

        SeedBuiltins();
        if (Config.Profiles.Count == 0)
            Config.Profiles.Add(new Profile { Name = "Шаблон", Args = TemplateArgs });
        if (!Config.Profiles.Any(p => p.Name == Config.ActiveProfile))
            Config.ActiveProfile = Config.Profiles[0].Name;

        try { Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the loaded data in memory; the UI reports the persistence error on the next save.
        }
    }

    private static AppConfig ReadConfig(string path) =>
        JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path, Encoding.UTF8),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfig();

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = ConfigPath + ".tmp";
        var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        if (File.Exists(ConfigPath))
            File.Copy(ConfigPath, ConfigPath + ".bak", true);
        File.Move(temp, ConfigPath, true);
    }

    public int ImportProfiles(string path)
    {
        var imported = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(path, Encoding.UTF8),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (imported is null || imported.Count == 0)
            throw new InvalidDataException("Файл не содержит профилей.");
        if (imported.Count > 500)
            throw new InvalidDataException("В одном файле допускается не более 500 профилей.");

        var valid = imported.Where(item => item is not null).Select(item => new Profile
        {
            Name = string.IsNullOrWhiteSpace(item.Name) ? "Импортированный профиль" : item.Name.Trim(),
            Args = item.Args ?? "",
            Note = item.Note ?? ""
        }).ToList();
        var oversized = valid.FirstOrDefault(item => item.Args.Length > 256_000);
        if (oversized is not null)
            throw new InvalidDataException($"Профиль «{oversized.Name}» слишком большой.");
        if (valid.Count == 0)
            throw new InvalidDataException("В файле нет корректных профилей.");

        foreach (var item in valid)
        {
            item.Name = UniqueName(item.Name);
            Config.Profiles.Add(item);
        }
        Save();
        return valid.Count;
    }

    public void ExportProfiles(string path)
    {
        var json = JsonSerializer.Serialize(Config.Profiles, new JsonSerializerOptions { WriteIndented = true });
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    public int SeedBuiltins(bool force = false)
    {
        var added = 0;
        foreach (var strategy in StrategyCatalog.All)
        {
            if (Config.Profiles.Any(p => p.Name.Equals(strategy.Name, StringComparison.OrdinalIgnoreCase)) ||
                (!force && Config.Seeded.Contains(strategy.Name))) continue;
            Config.Profiles.Add(new Profile { Name = strategy.Name, Args = strategy.Args, Note = strategy.Note });
            if (!Config.Seeded.Contains(strategy.Name)) Config.Seeded.Add(strategy.Name);
            added++;
        }
        return added;
    }

    public string UniqueName(string candidate, Profile? ignore = null)
    {
        candidate = string.IsNullOrWhiteSpace(candidate) ? "Новый профиль" : candidate.Trim();
        var used = Config.Profiles.Where(p => !ReferenceEquals(p, ignore))
            .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(candidate)) return candidate;
        for (var i = 2; ; i++)
            if (!used.Contains($"{candidate} ({i})")) return $"{candidate} ({i})";
    }
}

public static class ComponentDiscovery
{
    public static Components Find(string? baseDirectory, int maxDepth = 6)
    {
        var result = new Components();
        if (string.IsNullOrWhiteSpace(baseDirectory) || !System.IO.Directory.Exists(baseDirectory)) return result;
        var root = new DirectoryInfo(baseDirectory);
        var stack = new Stack<(DirectoryInfo Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            FileInfo[] files;
            try { files = dir.GetFiles(); } catch { continue; }
            foreach (var file in files)
            {
                if (result.Winws2 is null && file.Name.Equals("winws2.exe", StringComparison.OrdinalIgnoreCase)) result.Winws2 = file.FullName;
                if (result.LuaDirectory is null && file.Name.Equals("zapret-lib.lua", StringComparison.OrdinalIgnoreCase)) result.LuaDirectory = dir.FullName;
                if (result.Blockcheck is null && file.Name.Equals("blockcheck2.cmd", StringComparison.OrdinalIgnoreCase)) result.Blockcheck = file.FullName;
            }
            if (result.Winws2 is not null && result.LuaDirectory is not null && result.Blockcheck is not null) break;
            if (depth >= maxDepth) continue;
            DirectoryInfo[] children;
            try { children = dir.GetDirectories(); } catch { continue; }
            foreach (var child in children)
            {
                try
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                if (!child.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
                    !child.Name.Equals("cygwin", StringComparison.OrdinalIgnoreCase))
                    stack.Push((child, depth + 1));
            }
        }
        return result;
    }
}

public static class WinwsFinder
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    { "Windows", "$Recycle.Bin", "System Volume Information", "node_modules", ".git", "cygwin", "AppData", "WinSxS", "ProgramData", "Recovery", "$Windows.~BT" };

    public static IReadOnlyList<string> Find(int timeBudgetSeconds = 12, bool deep = true, IEnumerable<string>? searchRoots = null)
    {
        if (timeBudgetSeconds <= 0) return [];
        var roots = searchRoots?.ToList() ?? BuildDefaultRoots();
        roots = roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var deadline = DateTime.UtcNow.AddSeconds(timeBudgetSeconds);
        var found = new List<string>();
        foreach (var root in roots)
        {
            Scan(root, deep ? 7 : 3, deadline, found);
            if (DateTime.UtcNow > deadline) break;
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(Score)
            .ThenByDescending(p => File.GetLastWriteTimeUtc(p))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> BuildDefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? AppContext.BaseDirectory,
            Path.Combine(home, "Downloads"),
            Path.Combine(home, "Desktop"),
            Path.Combine(home, "Documents"),
            Path.Combine(home, "OneDrive", "Downloads"),
            Path.Combine(home, "OneDrive", "Desktop"),
            Path.Combine(home, "OneDrive", "Documents"),
            home
        };
        var configured = Environment.GetEnvironmentVariable("ZAPRET2_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) roots.Insert(0, configured);
        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value)) roots.Add(value);
        }
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (File.Exists(Path.Combine(entry, "winws2.exe"))) roots.Insert(0, entry);
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            roots.Add(drive.RootDirectory.FullName);
        return roots.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    }

    private static int Score(string exe)
    {
        var folder = new DirectoryInfo(Path.GetDirectoryName(exe)!);
        DirectoryInfo? bundleRoot = null;
        DirectoryInfo? ancestor = folder;
        for (var i = 0; i <= 3 && ancestor is not null; i++, ancestor = ancestor.Parent)
        {
            if (!File.Exists(Path.Combine(ancestor.FullName, "blockcheck", "blockcheck2.cmd"))) continue;
            bundleRoot = ancestor;
            break;
        }
        var components = ComponentDiscovery.Find(bundleRoot?.FullName ?? folder.FullName);
        return (components.LuaDirectory is not null ? 2 : 0) + (bundleRoot is not null && components.Blockcheck is not null ? 1 : 0);
    }

    private static void Scan(string root, int maxDepth, DateTime deadline, List<string> found)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && DateTime.UtcNow < deadline)
        {
            var (path, depth) = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(path); } catch { continue; }
            foreach (var file in files)
                if (Path.GetFileName(file).Equals("winws2.exe", StringComparison.OrdinalIgnoreCase)) found.Add(file);
            if (depth >= maxDepth) continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(path); } catch { continue; }
            foreach (var dir in dirs)
            {
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                if (!Skip.Contains(Path.GetFileName(dir))) stack.Push((dir, depth + 1));
            }
        }
    }
}

public static class TrafficScopeAnalyzer
{
    private static readonly Regex PortFilter =
        new(@"--(?:wf-(?:tcp|udp)-out|filter-(?:tcp|udp))\s*(?:=|\s)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<string> GetWarnings(string profileTemplate)
    {
        if (!PortFilter.IsMatch(profileTemplate)) return [];
        return
        [
            "Профиль использует фильтры по сетевым протоколам/портам, а не по имени приложения. Он может затрагивать трафик других программ, если тот попадает под эти правила.",
            "Совместимость Steam, Telegram Proxy, Valorant и других приложений не гарантируется. При проблемах остановите профиль и проверьте его правила."
        ];
    }
}

public static class LaunchPreflight
{
    private static readonly Regex LuaReference =
        new(@"\{LUA\}[\\/]+([^\s""]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<string> Validate(string profileTemplate, Components components)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(components.Winws2))
            errors.Add("Не найден winws2.exe.");
        else if (!File.Exists(components.Winws2))
            errors.Add("Файл winws2.exe больше не существует по сохранённому пути.");

        var referencesLua = profileTemplate.Contains("{LUA}", StringComparison.OrdinalIgnoreCase);
        if (referencesLua)
        {
            if (string.IsNullOrWhiteSpace(components.LuaDirectory) || !Directory.Exists(components.LuaDirectory))
            {
                errors.Add("Профиль использует {LUA}, но каталог Lua не найден. Повторно выберите папку zapret2 bundle.");
            }
            else
            {
                foreach (Match match in LuaReference.Matches(profileTemplate))
                {
                    var relativeName = match.Groups[1].Value.TrimEnd('\\', '/');
                    if (relativeName.Length == 0) continue;
                    var path = Path.Combine(components.LuaDirectory, relativeName);
                    if (!File.Exists(path))
                        errors.Add($"Не найден Lua-файл: {relativeName}");
                }
            }
        }

        return errors;
    }
}

public static class ArgumentParser
{
    private static readonly Regex Variable = new(@"\{(BIN|LUA)\}([\\/]?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Leftover = new(@"\{[A-Za-z_]+\}", RegexOptions.Compiled);

    public static List<string> Tokenize(string text, bool keepQuotes = false)
    {
        var filtered = string.Join(" ", text.Replace("\r\n", "\n").Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#')));
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var hasToken = false;
        foreach (var ch in filtered)
        {
            if (ch == '"')
            {
                inQuote = !inQuote;
                hasToken = true;
                if (keepQuotes) current.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (hasToken) { tokens.Add(current.ToString()); current.Clear(); hasToken = false; }
            }
            else { current.Append(ch); hasToken = true; }
        }
        if (inQuote) throw new FormatException("В аргументах незакрытая кавычка (\").");
        if (hasToken) tokens.Add(current.ToString());
        return tokens;
    }

    public static List<string> Render(string template, Components components)
    {
        var tokens = Tokenize(template);
        var result = new List<string>();
        foreach (var token in tokens)
        {
            var replaced = Variable.Replace(token, match =>
            {
                var name = match.Groups[1].Value.ToUpperInvariant();
                var path = name == "BIN" ? components.BinaryDirectory : components.LuaDirectory;
                if (path is null)
                    throw new InvalidOperationException($"Используется {{{name}}}, но компонент не найден в выбранной папке.");
                return path.TrimEnd('\\', '/') + (match.Groups[2].Value.Length > 0 ? "\\" : "");
            });
            var leftover = Leftover.Match(replaced);
            if (leftover.Success) throw new FormatException($"Неизвестная переменная {leftover.Value} (доступны {{BIN}} и {{LUA}}).");
            result.Add(replaced);
        }
        return result;
    }

    public static (string Args, List<string> Warnings) ImportCmd(string text)
    {
        text = Regex.Replace(text, @"\^[ \t]*\r?\n", " ");
        string? best = null;
        var bestCount = 0;
        foreach (var line in text.Split('\n'))
        {
            var stripped = line.Trim();
            if (stripped.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) ||
                stripped.StartsWith("@rem ", StringComparison.OrdinalIgnoreCase) ||
                stripped.StartsWith("::", StringComparison.Ordinal) ||
                !stripped.Contains("--", StringComparison.Ordinal)) continue;
            var count = Regex.Matches(stripped, @"(?:^|\s)--[a-z]", RegexOptions.IgnoreCase).Count;
            if (count > bestCount) { best = stripped; bestCount = count; }
        }
        if (best is null) throw new FormatException("В файле не найдена строка с параметрами вида --option.");
        var args = best[best.IndexOf("--", StringComparison.Ordinal)..];
        args = Regex.Replace(args, @"\^([&|<>^])", "$1");
        args = args.Replace("%~dp0", "{BIN}\\", StringComparison.OrdinalIgnoreCase);
        args = Regex.Replace(args, @"%(BIN|LUA)%(?![\\/])", m => "{" + m.Groups[1].Value.ToUpperInvariant() + "}\\", RegexOptions.IgnoreCase);
        args = Regex.Replace(args, @"%(BIN|LUA)%", m => "{" + m.Groups[1].Value.ToUpperInvariant() + "}", RegexOptions.IgnoreCase);
        var warnings = Regex.Matches(args, @"%[^%\s]+%").Cast<Match>().Select(m => m.Value).Distinct().ToList();
        if (warnings.Count > 0) warnings.Insert(0, "Остались переменные cmd — замените их вручную на пути или {BIN}/{LUA}.");
        var lines = new List<string>();
        foreach (var token in Tokenize(args, true))
        {
            if (token.StartsWith("--", StringComparison.Ordinal) || lines.Count == 0) lines.Add(token);
            else lines[^1] += " " + token;
        }
        return (string.Join(Environment.NewLine, lines) + Environment.NewLine, warnings);
    }
}

public sealed class WinwsRunner : IDisposable
{
    private Process? _process;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private volatile bool _stopRequested;
    private readonly object _sync = new();

    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch { return false; }
        }
    }

    public event Action<string>? OutputReceived;
    public event Action<int?, bool>? Exited;

    public void Start(string exe, IReadOnlyList<string> args)
    {
        if (!File.Exists(exe)) throw new FileNotFoundException("Исполняемый файл winws2.exe не найден.", exe);

        lock (_sync)
        {
            // Do not tear down a previous reader task: it may still be draining the
            // process pipes and delivering the final diagnostics to the UI.
            if (_process is not null)
            {
                bool previousExited;
                try { previousExited = _process.HasExited; }
                catch (InvalidOperationException) { previousExited = true; }

                if (!previousExited)
                    throw new InvalidOperationException("winws2 уже запущен.");

                if (_pumpTask is { IsCompleted: false })
                    throw new InvalidOperationException("Предыдущий процесс завершился, но журнал ещё обрабатывается. Повторите запуск через несколько секунд.");

                _pumpCts?.Dispose();
                _process.Dispose();
                _pumpCts = null;
                _process = null;
                _pumpTask = null;
            }

            var start = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);

            _stopRequested = false;
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Windows не смог запустить winws2.exe.");
            }
            catch
            {
                process.Dispose();
                throw;
            }

            _process = process;
            _pumpCts = new CancellationTokenSource();
            _pumpTask = PumpAsync(process, _pumpCts.Token);
        }
    }

    private async Task PumpAsync(Process process, CancellationToken token)
    {
        int? exitCode = null;
        try
        {
            var stdout = ReadLines(process.StandardOutput, token);
            var stderr = ReadLines(process.StandardError, token);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OutputReceived?.Invoke("[runner] " + ex.Message);
            try { if (process.HasExited) exitCode = process.ExitCode; } catch { }
        }
        finally
        {
            var hasExited = false;
            try { hasExited = process.HasExited; } catch { }
            if (hasExited)
            {
                try { exitCode ??= process.ExitCode; } catch { }
                Exited?.Invoke(exitCode, _stopRequested);
            }
            else if (token.IsCancellationRequested)
            {
                OutputReceived?.Invoke("[runner] Чтение вывода остановлено до подтверждённого завершения процесса.");
            }
        }
    }

    private async Task ReadLines(StreamReader reader, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) break;
            OutputReceived?.Invoke(line);
        }
    }

    public async Task StopAsync()
    {
        var process = _process;
        if (process is null) return;
        try
        {
            // A process can exit on its own between the UI status poll and this call.
            // Still wait for the output pump so the final stdout/stderr lines are not lost.
            if (!process.HasExited)
            {
                _stopRequested = true;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            }

            if (_pumpTask is not null)
                await _pumpTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception ex)
        {
            OutputReceived?.Invoke("[runner] Не удалось остановить процесс: " + ex.Message);
        }
        catch (TimeoutException)
        {
            _pumpCts?.Cancel();
            OutputReceived?.Invoke("[runner] Истекло время ожидания завершения процесса.");
        }
    }

    public void Dispose()
    {
        // Last-resort cleanup: closing the GUI must not leave an unmanaged winws2
        // process behind if a caller forgot to invoke StopAsync first.
        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    _stopRequested = true;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.NotSupportedException)
            {
                OutputReceived?.Invoke("[runner] Не удалось завершить процесс при закрытии: " + ex.Message);
            }
        }

        _pumpCts?.Cancel();
        try { _pumpTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _process?.Dispose();
        _pumpCts?.Dispose();
        _process = null;
        _pumpCts = null;
        _pumpTask = null;
    }
}

public sealed record RunningProcessInfo(string Name, int Id, string? ExecutablePath);

public static class WindowsIntegration
{
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static bool RelaunchAsAdministrator(string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments ?? ""
            });
            return true;
        }
        catch { return false; }
    }

    public static void LaunchBlockcheck(string script)
    {
        if (!File.Exists(script)) throw new FileNotFoundException("blockcheck2.cmd не найден.", script);
        var quoted = "\"\"" + script + "\"\"";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /s /k {quoted}")
        {
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = true
        });
    }

    public static IReadOnlyList<RunningProcessInfo> GetRunningDpiProcesses()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "winws", "winws2" };
        var found = new List<RunningProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!names.Contains(process.ProcessName)) continue;
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                found.Add(new RunningProcessInfo(process.ProcessName, process.Id, path));
            }
            catch { }
            finally { process.Dispose(); }
        }
        return found.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToList();
    }

    public static IReadOnlyList<string> RunningDpiProcesses() =>
        GetRunningDpiProcesses().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed record Strategy(string Key, string Name, string Note, string Args);

public static class StrategyCatalog
{
    private const string Head = """
--wf-tcp-out=80,443
--wf-udp-out=443
--lua-init=@"{LUA}\zapret-lib.lua" --lua-init=@"{LUA}\zapret-antidpi.lua"
--lua-init="fake_default_tls = tls_mod(fake_default_tls,'rnd,rndsni')"
""";
    private const string Http = """
--filter-tcp=80 --filter-l7=http
--out-range=-d10
--payload=http_req
--lua-desync=fake:blob=fake_default_http:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5
--lua-desync=fakedsplit:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5
--new
""";
    private const string Quic = """
--filter-udp=443 --filter-l7=quic
--payload=quic_initial
--lua-desync=fake:blob=fake_default_quic:repeats=11
""";
    private static string Tls(params string[] desync) =>
        "--filter-tcp=443 --filter-l7=tls\n--out-range=-d10\n--payload=tls_client_hello\n" +
        string.Join("\n", desync.Select(d => "--lua-desync=" + d)) + "\n--new\n";
    private static string Build(string tls) => Head + "\n" + Http + "\n" + tls + "\n" + Quic;

    public static readonly IReadOnlyList<Strategy> All =
    [
        new("01_official_example", "01 Официальный пример", "Копия логики preset2_example.cmd: fake (md5, seq-10000) + multidisorder. Начните с неё.",
            Build(Tls("fake:blob=fake_default_tls:tcp_md5:tcp_seq=-10000:repeats=6", "multidisorder:pos=midsld"))),
        new("02_youtube_style", "02 Стиль YouTube", "fake с SNI www.google.com (11 повторов) + multidisorder. При проблемах с YouTube.",
            Build(Tls("fake:blob=fake_default_tls:tcp_md5:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com", "multidisorder:pos=1,midsld"))),
        new("03_datanoack", "03 Фейк без ACK", "Фулинг через сброс ACK вместо md5. Если md5 не работает у провайдера.",
            Build(Tls("fake:blob=fake_default_tls:tcp_flags_unset=ack:tls_mod=rnd,rndsni,dupsid,padencap:repeats=6", "multidisorder:pos=midsld"))),
        new("04_badack", "04 Битый ACK + ts_up", "Искажённый ACK (-66000), tcp_ts_up и auto TTL. Экспериментальная.",
            Build(Tls("fake:blob=fake_default_tls:tcp_ack=-66000:tcp_ts_up:tls_mod=rnd:repeats=6", "multidisorder:pos=7,sld+1:tcp_ack=-66000:tcp_ts_up:ip_autottl=-1,3-20:ip6_autottl=-1,3-20"))),
        new("05_multisplit_seqovl", "05 Multisplit seqovl", "Без фейков: разрез ClientHello с перекрытием seqovl.",
            Build(Tls("multisplit:pos=1:seqovl=5:seqovl_pattern=0x1603030000"))),
        new("06_syndata_tls", "06 Syndata + multisplit", "Данные в SYN + multisplit. Экспериментальная.",
            Build("--filter-tcp=443\n--out-range=-d10\n" +
                "--lua-desync=syndata:blob=fake_default_tls:tls_mod=rnd,rndsni\n" +
                "--lua-desync=multisplit:pos=1,midsld\n--new")),
        new("07_ts_fooling", "07 Фулинг по timestamp", "Требует включённых TCP timestamps через enable_timestamps.cmd из бандла.",
            Build(Tls("fake:blob=fake_default_tls:tcp_ts=-1000:tls_mod=rnd,sni=www.google.com:repeats=6", "multidisorder:pos=midsld"))),
        new("08_disorder_only", "08 Только disorder", "Лёгкий вариант без fake: multidisorder по середине SLD.",
            Build(Tls("multidisorder:pos=midsld")))
    ];
}
