using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Zapret2.Core;

namespace Zapret2.App;

public partial class MainWindow : Window
{
    private readonly ProfileStore _store = new();
    private readonly WinwsRunner _runner = new();
    private readonly ObservableCollection<Profile> _profiles = [];
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Components _components = new();
    private Profile? _current;
    private bool _loading;
    private bool _refreshingPath;
    private bool _searching;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _store.Load();
        foreach (var profile in _store.Config.Profiles) _profiles.Add(profile);
        ProfilesList.ItemsSource = _profiles;
        BasePath.Text = _store.Config.BaseDirectory;
        DebugCheck.IsChecked = _store.Config.DebugEnabled;

        _runner.OutputReceived += line =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
        };
        _runner.Exited += (code, requested) =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RunStatus.Text = requested ? "Остановлен пользователем" : $"Завершён (код {code?.ToString() ?? "неизвестен"})";
                StartButton.Content = "▶   Запустить";
                UpdateRuntimeState();
                AppendLog(requested ? "winws2 остановлен." : $"winws2 завершился с кодом {code?.ToString() ?? "?"}.");
            }));
        };

        UpdateAdminStatus();
        RefreshComponents();
        SelectActiveProfile();
        SetPage("overview");
        UpdateRuntimeState();
        _statusTimer.Tick += (_, _) => UpdateRuntimeState();
        _statusTimer.Start();
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _runner.Dispose();
        };
        AppendLog("Zapret2 Control Center запущен. Настройки профилей сохранены отдельно от EXE.");
    }

    private void SetPage(string page)
    {
        OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = page == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = page == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;

        PageTitle.Text = page switch
        {
            "profiles" => "Профили",
            "diagnostics" => "Диагностика",
            "settings" => "Настройки",
            _ => "Обзор"
        };
        PageSubtitle.Text = page switch
        {
            "profiles" => "Настройка, импорт и управление параметрами winws2",
            "diagnostics" => "Состояние компонентов, конфликтующие процессы и журнал",
            "settings" => "Пути, режим запуска и резервное копирование профилей",
            _ => "Состояние компонентов и управление winws2"
        };

        var active = new SolidColorBrush(Color.FromRgb(38, 58, 86));
        var inactive = Brushes.Transparent;
        var activeBorder = new SolidColorBrush(Color.FromRgb(53, 79, 114));
        foreach (var (button, key) in new[]
        {
            (NavOverview, "overview"), (NavProfiles, "profiles"),
            (NavDiagnostics, "diagnostics"), (NavSettings, "settings")
        })
        {
            var selected = key == page;
            button.Background = selected ? active : inactive;
            button.BorderBrush = selected ? activeBorder : Brushes.Transparent;
        }

        if (page == "diagnostics") RefreshDiagnostics();
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page }) SetPage(page);
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e) => SetPage("diagnostics");
    private void OpenProfiles_Click(object sender, RoutedEventArgs e) => SetPage("profiles");

    private void SelectActiveProfile()
    {
        var profile = _profiles.FirstOrDefault(p => p.Name == _store.Config.ActiveProfile) ?? _profiles.FirstOrDefault();
        if (profile is not null) ProfilesList.SelectedItem = profile;
    }

    private void UpdateAdminStatus()
    {
        var admin = WindowsIntegration.IsAdministrator();
        AdminStatus.Text = admin ? "Права администратора" : "Обычный режим";
        DiagnosticAdminText.Text = admin ? "Есть" : "Не получены";
    }

    private void RefreshComponents()
    {
        _components = ComponentDiscovery.Find(BasePath.Text.Trim());
        static string Mark(string? path, string label) => path is null ? $"○ {label}: не найден" : $"● {label}: {path}";
        var lines = new[]
        {
            Mark(_components.Winws2, "winws2.exe"),
            Mark(_components.LuaDirectory, "Каталог Lua"),
            Mark(_components.Blockcheck, "blockcheck2.cmd")
        };
        ComponentsStatus.Text = string.Join(Environment.NewLine, lines);
        WinwsStatusText.Text = _components.Winws2 is null ? "Не найден" : "Найден";
        LuaStatusText.Text = _components.LuaDirectory is null ? "Не найдены" : "Найдены";
        BlockcheckStatusText.Text = _components.Blockcheck is null ? "Не найден" : "Найден";
        WinwsStatusText.Foreground = _components.Winws2 is null ? (Brush)FindResource("WarningBrush") : (Brush)FindResource("SuccessBrush");
        LuaStatusText.Foreground = _components.LuaDirectory is null ? (Brush)FindResource("WarningBrush") : (Brush)FindResource("SuccessBrush");
        BlockcheckStatusText.Foreground = _components.Blockcheck is null ? (Brush)FindResource("WarningBrush") : (Brush)FindResource("SuccessBrush");
    }

    private void RefreshDiagnostics()
    {
        RefreshComponents();
        UpdateAdminStatus();
        var processes = WindowsIntegration.GetRunningDpiProcesses();
        if (processes.Count == 0)
        {
            ConflictStatusText.Text = "Другие DPI-процессы не обнаружены";
            ConflictDetailsText.Text = "winws / winws2 не найдены среди запущенных процессов.";
        }
        else
        {
            ConflictStatusText.Text = $"Найдено процессов: {processes.Count}";
            ConflictDetailsText.Text = string.Join(Environment.NewLine,
                processes.Select(p => $"{p.Name} · PID {p.Id}" + (string.IsNullOrWhiteSpace(p.ExecutablePath) ? "" : $" · {p.ExecutablePath}")));
        }
        DiagnosticProfileText.Text = _current?.Name ?? "Не выбран";
    }

    private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        RefreshDiagnostics();
        AppendLog("Диагностика обновлена.");
    }

    private void UpdateRuntimeState()
    {
        var running = _runner.IsRunning;
        SidebarStatusText.Text = running ? "winws2 работает" : "Не запущен";
        SidebarStatusDot.Fill = running ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("WarningBrush");
        if (running)
        {
            RunStatus.Text = "Работает";
            StartButton.Content = "■   Остановить";
        }
        else if (StartButton.Content?.ToString()?.Contains("Остановить", StringComparison.OrdinalIgnoreCase) == true)
        {
            StartButton.Content = "▶   Запустить";
        }
        ActiveProfileText.Text = _current is null ? "Профиль не выбран" : $"Профиль: {_current.Name}";
        DiagnosticProfileText.Text = _current?.Name ?? "Не выбран";
    }

    private void BasePath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshingPath) return;
        _store.Config.BaseDirectory = BasePath.Text.Trim();
        try { _store.Save(); }
        catch (Exception ex) { AppendLog("Не удалось сохранить путь: " + ex.Message); }
        RefreshComponents();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Выберите распакованную папку zapret2 bundle",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (Directory.Exists(BasePath.Text)) dialog.SelectedPath = BasePath.Text;
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) BasePath.Text = dialog.SelectedPath;
    }

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_searching) return;
        _searching = true;
        try
        {
            RunStatus.Text = "Поиск компонентов…";
            AppendLog("Начат поиск winws2.exe на доступных дисках. Поиск ограничен по времени.");
            var found = await Task.Run(() => WinwsFinder.Find(timeBudgetSeconds: 18));
            if (found.Count == 0)
            {
                AppendLog("winws2.exe не найден. Укажите папку zapret-win-bundle вручную.");
                MessageBox.Show("Не удалось найти winws2.exe. Выберите папку bundle вручную.", "Поиск компонентов", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var best = found[0];
            var baseDir = ResolveBase(best);
            _refreshingPath = true;
            BasePath.Text = baseDir;
            _refreshingPath = false;
            _store.Config.BaseDirectory = baseDir;
            _store.Save();
            RefreshComponents();
            AppendLog($"Найден winws2.exe: {best}");
            foreach (var other in found.Skip(1).Take(5)) AppendLog("Дополнительная копия: " + other);
            if (found.Count > 1) AppendLog($"Всего найдено копий: {found.Count}. Выбрана первая по приоритету.");
        }
        catch (Exception ex) { AppendLog("Ошибка поиска: " + ex.Message); }
        finally
        {
            _searching = false;
            UpdateRuntimeState();
        }
    }

    private static string ResolveBase(string exe)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(exe)!);
        if (dir.Parent?.Name.Equals("zapret-winws", StringComparison.OrdinalIgnoreCase) == true && dir.Parent.Parent is not null)
            return dir.Parent.Parent.FullName;
        return dir.FullName;
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not Profile selected || ReferenceEquals(selected, _current)) return;
        CommitEditor();
        _current = selected;
        _loading = true;
        ProfileName.Text = selected.Name;
        ArgumentsEditor.Text = selected.Args;
        ProfileNote.Text = selected.Note;
        _loading = false;
        _store.Config.ActiveProfile = selected.Name;
        try { _store.Save(); }
        catch (Exception ex) { AppendLog("Не удалось сохранить выбранный профиль: " + ex.Message); }
        UpdateRuntimeState();
    }

    private void ProfileEditor_Changed(object sender, TextChangedEventArgs e)
    {
        // Изменения сохраняются явно кнопкой, перед переключением профиля или перед запуском.
    }

    private void CommitEditor()
    {
        if (_current is null || _loading) return;
        var desired = string.IsNullOrWhiteSpace(ProfileName.Text) ? _current.Name : ProfileName.Text.Trim();
        _current.Name = _store.UniqueName(desired, _current);
        _current.Args = ArgumentsEditor.Text;
        _store.Config.ActiveProfile = _current.Name;
        _store.Config.Profiles = _profiles.ToList();
        _store.Save();
        ProfilesList.Items.Refresh();
        UpdateRuntimeState();
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitEditor();
            AppendLog($"Профиль «{_current?.Name}» сохранён.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Сохранение профиля", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitEditor();
            var profile = new Profile { Name = _store.UniqueName("Новый профиль"), Args = ProfileStore.TemplateArgs };
            _profiles.Add(profile);
            _store.Config.Profiles = _profiles.ToList();
            _store.Config.ActiveProfile = profile.Name;
            _store.Save();
            ProfilesList.SelectedItem = profile;
            AppendLog("Создан новый профиль.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Создание профиля", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        try
        {
            CommitEditor();
            var profile = new Profile
            {
                Name = _store.UniqueName(_current.Name + " — копия"),
                Args = _current.Args,
                Note = _current.Note
            };
            _profiles.Add(profile);
            _store.Config.Profiles = _profiles.ToList();
            _store.Config.ActiveProfile = profile.Name;
            _store.Save();
            ProfilesList.SelectedItem = profile;
            AppendLog($"Создана копия профиля «{_current.Name}».");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Копирование профиля", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || _profiles.Count <= 1)
        {
            MessageBox.Show("Нельзя удалить последний профиль.", "Профили", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Удалить профиль «{_current.Name}»?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var removedName = _current.Name;
        _profiles.Remove(_current);
        _current = null;
        _store.Config.Profiles = _profiles.ToList();
        _store.Config.ActiveProfile = _profiles[0].Name;
        _store.Save();
        ProfilesList.SelectedItem = _profiles[0];
        AppendLog($"Профиль «{removedName}» удалён.");
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitEditor();
            var added = _store.SeedBuiltins(force: true);
            _profiles.Clear();
            foreach (var profile in _store.Config.Profiles) _profiles.Add(profile);
            _store.Save();
            SelectActiveProfile();
            AppendLog($"Встроенных профилей восстановлено: {added}.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Восстановление профилей", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Batch-файлы (*.cmd;*.bat)|*.cmd;*.bat|Все файлы (*.*)|*.*",
            InitialDirectory = Directory.Exists(BasePath.Text) ? BasePath.Text : ""
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var imported = ArgumentParser.ImportCmd(File.ReadAllText(dialog.FileName));
            ArgumentsEditor.Text = imported.Args;
            CommitEditor();
            foreach (var warning in imported.Warnings) AppendLog("[!] " + warning);
            AppendLog("Параметры импортированы из " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ImportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Профили Zapret2 (*.json)|*.json|Все файлы (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            CommitEditor();
            var count = _store.ImportProfiles(dialog.FileName);
            _profiles.Clear();
            foreach (var profile in _store.Config.Profiles) _profiles.Add(profile);
            SelectActiveProfile();
            AppendLog($"Импортировано профилей: {count}.");
            MessageBox.Show($"Добавлено профилей: {count}.", "Импорт завершён", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Импорт профилей", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Профили Zapret2 (*.json)|*.json", FileName = "zapret2-profiles.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            CommitEditor();
            _store.ExportProfiles(dialog.FileName);
            AppendLog("Профили экспортированы: " + dialog.FileName);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Экспорт профилей", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ShowCommand_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitEditor();
            RefreshComponents();
            if (_components.Winws2 is null) throw new InvalidOperationException("Сначала укажите папку с winws2.exe.");
            var args = ArgumentParser.Render(ArgumentsEditor.Text, _components);
            var command = "\"" + _components.Winws2 + "\" " + string.Join(" ", args.Select(QuoteArg));
            Clipboard.SetText(command);
            AppendLog("Команда проверена и скопирована в буфер обмена. Она ещё не запускалась.");
            MessageBox.Show(command, "Команда winws2 скопирована", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Профиль", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string QuoteArg(string arg)
    {
        if (arg.Length > 0 && !arg.Any(char.IsWhiteSpace) && !arg.Contains('"')) return arg;
        // Windows command-line quoting: backslashes immediately before a quote are doubled.
        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_runner.IsRunning)
        {
            StartButton.IsEnabled = false;
            RunStatus.Text = "Остановка…";
            _ = StopRunnerAsync();
            return;
        }

        try
        {
            CommitEditor();
            RefreshComponents();
            if (!WindowsIntegration.IsAdministrator())
            {
                var ask = MessageBox.Show("Для WinDivert обычно требуются права администратора. Перезапустить приложение с UAC?", "Требуются права", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ask == MessageBoxResult.Yes && WindowsIntegration.RelaunchAsAdministrator()) Close();
                return;
            }
            if (_components.Winws2 is null) throw new InvalidOperationException("Не найден winws2.exe. Укажите папку zapret-win-bundle.");

            var otherInstances = WindowsIntegration.GetRunningDpiProcesses();
            if (otherInstances.Count > 0)
            {
                var details = string.Join(Environment.NewLine, otherInstances.Select(p => $"{p.Name} (PID {p.Id})"));
                if (MessageBox.Show($"Уже обнаружены DPI-процессы:\n{details}\n\nДва фильтра могут конфликтовать. Всё равно продолжить?", "Обнаружен другой процесс", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            var args = ArgumentParser.Render(_current?.Args ?? "", _components);
            if (DebugCheck.IsChecked == true && !args.Contains("--debug", StringComparer.Ordinal)) args.Insert(0, "--debug");
            _runner.Start(_components.Winws2, args);
            RunStatus.Text = "Запускается…";
            StartButton.Content = "■   Остановить";
            AppendLog("Запуск winws2: " + _components.Winws2);
            AppendLog($"Профиль: {_current?.Name ?? "не выбран"}; аргументов: {args.Count}.");
            UpdateRuntimeState();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось запустить", MessageBoxButton.OK, MessageBoxImage.Error); AppendLog("Ошибка запуска: " + ex.Message); }
    }

    private async Task StopRunnerAsync()
    {
        try { await _runner.StopAsync(); }
        catch (Exception ex) { AppendLog("Ошибка остановки: " + ex.Message); }
        finally
        {
            StartButton.IsEnabled = true;
            UpdateRuntimeState();
        }
    }

    private async void Blockcheck_Click(object sender, RoutedEventArgs e)
    {
        RefreshComponents();
        if (_components.Blockcheck is null)
        {
            MessageBox.Show("Не найден blockcheck2.cmd в выбранной папке.", "Диагностика", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_runner.IsRunning)
        {
            if (MessageBox.Show("Для корректного теста нужно остановить winws2. Остановить и продолжить?", "blockcheck2", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await _runner.StopAsync();
        }
        var others = WindowsIntegration.GetRunningDpiProcesses();
        if (others.Count > 0)
        {
            var details = string.Join(Environment.NewLine, others.Select(p => $"{p.Name} (PID {p.Id})"));
            MessageBox.Show($"Для теста остановите другие DPI-процессы:\n{details}", "blockcheck2", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            WindowsIntegration.LaunchBlockcheck(_components.Blockcheck);
            AppendLog("blockcheck2 запущен в отдельной консоли. Следуйте инструкциям в её окне.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка запуска blockcheck2", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (_runner.IsRunning) { MessageBox.Show("Сначала остановите winws2."); return; }
        if (WindowsIntegration.RelaunchAsAdministrator()) Close();
        else MessageBox.Show("Запрос UAC отменён или не выполнен.", "Права администратора", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void DebugCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _store.Config.DebugEnabled = DebugCheck.IsChecked == true;
        try { _store.Save(); }
        catch (Exception ex) { AppendLog("Не удалось сохранить настройку отладки: " + ex.Message); }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.DirectoryPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Папка настроек", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Текстовый файл (*.txt)|*.txt", FileName = $"zapret2-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, LogBox.Text, new System.Text.UTF8Encoding(false)); AppendLog("Журнал сохранён: " + dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Сохранение журнала", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_runner.IsRunning)
        {
            if (MessageBox.Show("winws2 ещё работает. Остановить его и закрыть приложение?", "Выход", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            e.Cancel = true;
            await StopRunnerAsync();
            if (_runner.IsRunning)
            {
                MessageBox.Show("Не удалось подтвердить остановку winws2. Приложение оставлено открытым, чтобы вы могли повторить попытку.",
                    "Процесс ещё работает", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        _allowClose = true;
        Close();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
            return;
        }
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        var lines = LogBox.LineCount;
        if (lines > 1500)
        {
            var cutLine = Math.Min(500, lines - 1);
            var cutIndex = LogBox.GetCharacterIndexFromLineIndex(cutLine);
            if (cutIndex > 0)
            {
                LogBox.Select(0, cutIndex);
                LogBox.SelectedText = "";
            }
        }
        LogBox.ScrollToEnd();
    }
}
