using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private readonly ListUpdateService _listUpdater = new();
    private readonly DiagnosticLogStore _logStore = new();
    private readonly ObservableCollection<Profile> _profiles = [];
    private readonly ObservableCollection<DiagnosticLogInfo> _sessions = [];
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Components _components = new();
    private Profile? _current;
    private bool _loading;
    private bool _refreshingPath;
    private bool _searching;
    private bool _allowClose;
    private string? _embeddedBundleRoot;
    private string? _bundleInstallError;

    public MainWindow()
    {
        InitializeComponent();
        try { _logStore.StartSession("Запуск приложения"); } catch { }
        try { _embeddedBundleRoot = EmbeddedRuntimeBundle.Install(); }
        catch (Exception ex) { _bundleInstallError = ex.Message; }
        _store.Load();
        foreach (var profile in _store.Config.Profiles) _profiles.Add(profile);
        ProfilesList.ItemsSource = _profiles;
        SessionList.ItemsSource = _sessions;
        var configuredBase = _store.Config.BaseDirectory;
        var existingComponents = ComponentDiscovery.Find(configuredBase);
        var hasCompleteExternalBundle = existingComponents.Winws is not null &&
            existingComponents.Winws2 is not null && existingComponents.Blockcheck is not null &&
            existingComponents.Blockcheck2 is not null && existingComponents.LuaDirectory is not null;
        var isPreviouslyEmbeddedBundle = false;
        if (!string.IsNullOrWhiteSpace(configuredBase))
        {
            try
            {
                var managedRoot = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Zapret2GUI", "components")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                isPreviouslyEmbeddedBundle = Path.GetFullPath(configuredBase)
                    .StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }
        if (_embeddedBundleRoot is not null && (isPreviouslyEmbeddedBundle || !hasCompleteExternalBundle))
            configuredBase = _embeddedBundleRoot;
        else if (string.IsNullOrWhiteSpace(configuredBase) || !Directory.Exists(configuredBase))
            configuredBase = _embeddedBundleRoot ?? configuredBase;
        BasePath.Text = configuredBase;
        if (!string.Equals(_store.Config.BaseDirectory, configuredBase, StringComparison.OrdinalIgnoreCase))
        {
            _store.Config.BaseDirectory = configuredBase;
            try { _store.Save(); } catch (Exception ex) { _bundleInstallError ??= ex.Message; }
        }
        DebugCheck.IsChecked = _store.Config.DebugEnabled;
        UserListPicker.ItemsSource = ListCatalog.UserFiles.Keys.ToList();
        UserListPicker.SelectedIndex = 0;
        RefreshListStatus();

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
                AppendLog(requested ? "DPI-движок остановлен." : $"DPI-движок завершился с кодом {code?.ToString() ?? "?"}.");
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
            _listUpdater.Dispose();
        };
        AppendLog("Zapret2 Control Center запущен. Профили и настройки хранятся отдельно от EXE.");
        if (_embeddedBundleRoot is not null) AppendLog("Встроенный комплект winws/winws2 и blockcheck установлен: " + _embeddedBundleRoot);
        else if (_bundleInstallError is not null) AppendLog("Не удалось подготовить встроенный комплект: " + _bundleInstallError);
    }

    private void SetPage(string page)
    {
        OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = page == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        ListsPage.Visibility = page == "lists" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = page == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;

        PageTitle.Text = page switch
        {
            "profiles" => "Профили",
            "lists" => "Списки",
            "diagnostics" => "Диагностика",
            "settings" => "Настройки",
            _ => "Обзор"
        };
        PageSubtitle.Text = page switch
        {
            "profiles" => "Настройка, импорт и управление параметрами winws2",
            "lists" => "Обновление общих списков и сохранение собственных правил",
            "diagnostics" => "Состояние компонентов, конфликтующие процессы и журнал",
            "settings" => "Пути, режим запуска и резервное копирование профилей",
            _ => "Состояние компонентов и управление winws2"
        };

        var active = new SolidColorBrush(Color.FromRgb(38, 58, 86));
        var inactive = Brushes.Transparent;
        var activeBorder = new SolidColorBrush(Color.FromRgb(53, 79, 114));
        foreach (var (button, key) in new[]
        {
            (NavOverview, "overview"), (NavProfiles, "profiles"), (NavLists, "lists"),
            (NavDiagnostics, "diagnostics"), (NavSettings, "settings")
        })
        {
            var selected = key == page;
            button.Background = selected ? active : inactive;
            button.BorderBrush = selected ? activeBorder : Brushes.Transparent;
        }

        if (page == "diagnostics") { RefreshDiagnostics(); RefreshSessions(); }
        if (page == "lists") RefreshListStatus();
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
        AdminStatusDot.Fill = admin ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("WarningBrush");
        AdminStatus.Foreground = admin ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("MutedBrush");
        DiagnosticAdminText.Text = admin ? "Есть" : "Не получены";
        DiagnosticAdminText.Foreground = admin ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("WarningBrush");
    }

    private void RefreshComponents()
    {
        _components = ComponentDiscovery.Find(BasePath.Text.Trim());
        static string Mark(string? path, string label) => path is null ? $"○ {label}: не найден" : $"● {label}: {path}";
        var listStatus = !string.IsNullOrWhiteSpace(_components.ListsDirectory) && Directory.Exists(_components.ListsDirectory)
            ? $"● Папка lists: {_components.ListsDirectory}"
            : $"○ Папка lists: не найдена ({_components.ListsDirectory ?? "путь неизвестен"})";
        var gameFilters = GameFilterSettings.Read(_components.BinaryDirectory);
        static string FilterValue(string value) => value == "12" ? "12 (отключено)" : value;
        var lines = new[]
        {
            Mark(_components.Winws, "winws.exe"),
            Mark(_components.Winws2, "winws2.exe"),
            Mark(_components.LuaDirectory, "Каталог Lua"),
            Mark(_components.Blockcheck, "blockcheck.cmd"),
            Mark(_components.Blockcheck2, "blockcheck2.cmd"),
            listStatus,
            $"Игровые фильтры: TCP {FilterValue(gameFilters.Tcp)} · UDP {FilterValue(gameFilters.Udp)}"
        };
        ComponentsStatus.Text = string.Join(Environment.NewLine, lines);
        WinwsStatusText.Text = _components.Winws is not null && _components.Winws2 is not null ? "Оба движка найдены" :
            _components.Winws2 is not null ? "Найден только winws2" : _components.Winws is not null ? "Найден только winws" : "Не найдены";
        LuaStatusText.Text = _components.LuaDirectory is null ? "Не найдены" : "Найдены";
        BlockcheckStatusText.Text = _components.Blockcheck is not null && _components.Blockcheck2 is not null ? "Оба теста найдены" :
            _components.Blockcheck2 is not null ? "Найден только blockcheck2" : _components.Blockcheck is not null ? "Найден только blockcheck" : "Не найдены";
        WinwsStatusText.Foreground = _components.Winws is not null && _components.Winws2 is not null ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("WarningBrush");
        LuaStatusText.Foreground = _components.LuaDirectory is null ? (Brush)FindResource("WarningBrush") : (Brush)FindResource("SuccessBrush");
        BlockcheckStatusText.Foreground = _components.Blockcheck is not null && _components.Blockcheck2 is not null ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("WarningBrush");
        UpdateEngineButtons();
    }

    private string SelectedEngine =>
        (EnginePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() is { Length: > 0 } engine
            ? engine : _current?.Engine ?? "winws2";

    private void UpdateEngineButtons()
    {
        if (BlockcheckButton is not null)
            BlockcheckButton.Content = SelectedEngine == "winws" ? "Проверка: blockcheck" : "Проверка: blockcheck2";
    }

    private void EnginePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EnginePicker is null) return;
        CommitEditor();
        UpdateEngineButtons();
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
        SidebarStatusText.Text = running ? "DPI-движок работает" : "Не запущен";
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
        RefreshListStatus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите распакованную папку zapret2 bundle",
            Multiselect = false
        };
        if (Directory.Exists(BasePath.Text)) dialog.InitialDirectory = BasePath.Text;
        if (dialog.ShowDialog(this) == true) BasePath.Text = dialog.FolderName;
    }

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_searching) return;
        _searching = true;
        try
        {
            RunStatus.Text = "Поиск компонентов…";
            AppendLog("Начат поиск winws.exe и winws2.exe на доступных дисках. Поиск ограничен по времени.");
            var found = await Task.Run(() => WinwsFinder.Find(timeBudgetSeconds: 18));
            if (found.Count == 0)
            {
                AppendLog("winws.exe / winws2.exe не найдены. Укажите папку комплекта вручную.");
                MessageBox.Show("Не удалось найти winws.exe или winws2.exe. Выберите папку bundle вручную.", "Поиск компонентов", MessageBoxButton.OK, MessageBoxImage.Information);
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
            AppendLog($"Найден движок: {best}");
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
        if (dir.Name.Equals("zapret-winws", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null)
            return dir.Parent.FullName;
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
        EnginePicker.SelectedIndex = selected.Engine.Equals("winws", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        _loading = false;
        UpdateEngineButtons();
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
        _current.Engine = SelectedEngine;
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
                Note = _current.Note,
                Engine = _current.Engine
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

    private void ImportFlowseal_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
            "Будут импортированы 22 BAT-файла Flowseal из встроенного комплекта. Приложение извлечёт только аргументы winws2 и не будет запускать BAT/CMD. Интернет не требуется. Продолжить?",
            "Импорт классических профилей Flowseal", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;

        RefreshComponents();
        CommitEditor();
        ImportFlowsealButton.IsEnabled = false;
        ImportFlowsealButton.Content = "Импорт стратегий Flowseal…";
        try
        {
            AppendLog($"Импорт 22 встроенных стратегий Flowseal (commit {FlowsealClassicImporter.SourceCommit})…");
            var strategyDirectory = Path.Combine(GetResolvedBundleRoot(), "flowseal-strategies");
            var downloaded = FlowsealClassicImporter.ImportDirectory(strategyDirectory);

            if (!string.IsNullOrWhiteSpace(_components.ListsDirectory) && Directory.Exists(_components.ListsDirectory))
            {
                try
                {
                    var bundleRoot = Directory.GetParent(_components.ListsDirectory)?.FullName;
                    if (!string.IsNullOrWhiteSpace(bundleRoot))
                    {
                        var created = ListUpdateService.EnsureUserLists(bundleRoot);
                        foreach (var name in created) AppendLog("Создана зависимость профилей Flowseal: " + name);
                    }
                }
                catch (Exception listError)
                {
                    AppendLog("Не удалось создать пользовательские списки автоматически: " + listError.Message);
                }
            }

            var added = 0;
            foreach (var profile in downloaded)
            {
                if (_profiles.Any(existing => existing.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                profile.Name = _store.UniqueName(profile.Name);
                _profiles.Add(profile);
                _store.Config.Profiles.Add(profile);
                added++;
            }

            _store.Save();
            if (added > 0)
            {
                var first = downloaded.FirstOrDefault(profile => _profiles.Contains(profile));
                if (first is not null) ProfilesList.SelectedItem = first;
            }

            AppendLog($"Flowseal classic: получено {downloaded.Count}, добавлено новых профилей {added}. BAT/CMD не запускались.");
            MessageBox.Show(
                $"Получено стратегий: {downloaded.Count}.{Environment.NewLine}Добавлено новых профилей: {added}.{Environment.NewLine}{Environment.NewLine}" +
                "Оригинальные BAT/CMD не запускались. Игровые фильтры читаются из utils/game_filter.enabled; без него отключены.",
                "Стратегии Flowseal добавлены", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка импорта Flowseal: " + ex.Message);
            MessageBox.Show(ex.Message, "Не удалось импортировать стратегии Flowseal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ImportFlowsealButton.IsEnabled = true;
            ImportFlowsealButton.Content = "Импортировать 22 стратегии Flowseal (офлайн)";
        }
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
            var impact = ProfileImpactAnalyzer.Analyze(ArgumentsEditor.Text).ToDisplayText();
            MessageBox.Show(command + Environment.NewLine + Environment.NewLine +
                "Область действия:" + Environment.NewLine + impact,
                "Команда winws2 скопирована", MessageBoxButton.OK, MessageBoxImage.Information);
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
            var engine = SelectedEngine;
            var executable = engine == "winws" ? _components.Winws : _components.Winws2;
            if (executable is null) throw new InvalidOperationException(engine == "winws"
                ? "Не найден winws.exe в выбранном комплекте." : "Не найден winws2.exe в выбранном комплекте.");

            var otherInstances = WindowsIntegration.GetRunningDpiProcesses();
            if (otherInstances.Count > 0)
            {
                var details = string.Join(Environment.NewLine, otherInstances.Select(p => $"{p.Name} (PID {p.Id})"));
                if (MessageBox.Show($"Уже обнаружены DPI-процессы:\n{details}\n\nДва фильтра могут конфликтовать. Всё равно продолжить?", "Обнаружен другой процесс", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            var profileTemplate = _current?.Args ?? "";
            if (_components.ListsDirectory is { } listsDirectory && Directory.Exists(listsDirectory))
            {
                var createdLists = ListUpdateService.EnsureUserLists(GetResolvedBundleRoot());
                foreach (var name in createdLists) AppendLog("Создан пользовательский список: " + name);
            }
            var preflightErrors = LaunchPreflight.Validate(profileTemplate, _components, engine);
            if (preflightErrors.Count > 0)
                throw new InvalidOperationException("Проверка перед запуском не пройдена:" +
                    Environment.NewLine + "• " + string.Join(Environment.NewLine + "• ", preflightErrors));

            var scopeWarnings = TrafficScopeAnalyzer.GetWarnings(profileTemplate);
            if (scopeWarnings.Count > 0)
            {
                var impact = ProfileImpactAnalyzer.Analyze(profileTemplate).ToDisplayText();
                var prompt = "Что будет затронуто:" + Environment.NewLine + impact +
                    Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, scopeWarnings) +
                    Environment.NewLine + Environment.NewLine + "Запустить этот профиль?";
                if (MessageBox.Show(prompt, "Проверьте область действия профиля",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            var args = ArgumentParser.Render(profileTemplate, _components);
            if (DebugCheck.IsChecked == true && !args.Contains("--debug", StringComparer.Ordinal)) args.Insert(0, "--debug");
            try { _logStore.StartSession($"{engine} — {_current?.Name ?? "без профиля"}"); } catch { }
            _runner.Start(executable, args);
            RunStatus.Text = "Запускается…";
            StartButton.Content = "■   Остановить";
            AppendLog($"Запуск {Path.GetFileName(executable)}: {executable}");
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
        var engine = SelectedEngine;
        var script = engine == "winws" ? _components.Blockcheck : _components.Blockcheck2;
        var scriptName = engine == "winws" ? "blockcheck.cmd" : "blockcheck2.cmd";
        if (script is null)
        {
            MessageBox.Show($"Не найден {scriptName} во встроенном комплекте.", "Диагностика", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_runner.IsRunning)
        {
            if (MessageBox.Show($"Для корректного теста нужно остановить {engine}. Остановить и продолжить?", scriptName, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await _runner.StopAsync();
            if (_runner.IsRunning)
            {
                MessageBox.Show($"Не удалось подтвердить остановку {engine}. {scriptName} не будет запущен.",
                    "Остановка не подтверждена", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        var others = WindowsIntegration.GetRunningDpiProcesses();
        if (others.Count > 0)
        {
            var details = string.Join(Environment.NewLine, others.Select(p => $"{p.Name} (PID {p.Id})"));
            MessageBox.Show($"Для теста остановите другие DPI-процессы:\n{details}", scriptName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            WindowsIntegration.LaunchBlockcheck(script);
            AppendLog($"{scriptName} запущен в отдельной консоли. Следуйте инструкциям в её окне.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка запуска " + scriptName, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (_runner.IsRunning) { MessageBox.Show("Сначала остановите DPI-движок."); return; }
        if (WindowsIntegration.RelaunchAsAdministrator()) Close();
        else MessageBox.Show("Запрос UAC отменён или не выполнен.", "Права администратора", MessageBoxButton.OK, MessageBoxImage.Warning);
    }


    private string GetResolvedBundleRoot()
    {
        if (!string.IsNullOrWhiteSpace(_components.ListsDirectory))
            return Directory.GetParent(_components.ListsDirectory)?.FullName ?? BasePath.Text.Trim();
        return BasePath.Text.Trim();
    }

    private void RefreshListStatus()
    {
        if (string.IsNullOrWhiteSpace(BasePath.Text))
        {
            ListsDirectoryText.Text = "Сначала выберите папку zapret2 bundle в настройках.";
            ListsStatusText.Text = "Путь к спискам неизвестен.";
            return;
        }

        try
        {
            var directory = _components.ListsDirectory ?? ListUpdateService.GetListsDirectory(GetResolvedBundleRoot());
            ListsDirectoryText.Text = directory;
            if (!Directory.Exists(directory))
            {
                ListsStatusText.Text = "Папка lists не найдена. Выберите корень bundle, а не папку с winws2.exe.";
                return;
            }

            var lines = new List<string>();
            foreach (var item in ListCatalog.Managed)
            {
                var path = Path.Combine(directory, item.FileName);
                if (!File.Exists(path))
                {
                    lines.Add($"○ {item.FileName} — отсутствует");
                    continue;
                }

                var info = new FileInfo(path);
                var count = File.ReadLines(path).Count(line =>
                    !string.IsNullOrWhiteSpace(line) &&
                    !line.TrimStart().StartsWith('#') &&
                    !line.TrimStart().StartsWith(';') &&
                    !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
                lines.Add($"● {item.FileName} — {count:N0} записей · {info.Length / 1024.0:N1} КБ");
            }

            var userCount = ListCatalog.UserFiles.Keys.Count(name => File.Exists(Path.Combine(directory, name)));
            lines.Add($"Пользовательские файлы: {userCount}/{ListCatalog.UserFiles.Count}");
            ListsStatusText.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            ListsStatusText.Text = "Не удалось прочитать состояние списков: " + ex.Message;
        }
    }

    private async void UpdateLists_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BasePath.Text))
        {
            MessageBox.Show("Сначала выберите папку zapret2 bundle.", "Списки", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
            "Общие списки будут заменены только после загрузки и проверки всех файлов. Перед заменой создаётся резервная копия в lists/.backups. Пользовательские *-user.txt не изменяются. Продолжить?",
            "Обновление общих списков", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        UpdateListsButton.IsEnabled = false;
        try
        {
            var created = ListUpdateService.EnsureUserLists(GetResolvedBundleRoot());
            foreach (var name in created) AppendLog("Создан пользовательский список: " + name);

            AppendLog("Загрузка и проверка общих списков Flowseal…");
            var result = await _listUpdater.UpdateAsync(GetResolvedBundleRoot());
            foreach (var item in result.UpdatedFiles)
                AppendLog($"Обновлён {item.FileName}: {item.EntryCount:N0} записей, SHA-256 {item.Sha256}");

            AppendLog("Резервные копии общих списков: " + result.BackupDirectory);
            RefreshListStatus();
            MessageBox.Show(
                $"Обновлено файлов: {result.UpdatedFiles.Count}.{Environment.NewLine}" +
                $"Резервные копии сохранены в:{Environment.NewLine}{result.BackupDirectory}{Environment.NewLine}{Environment.NewLine}" +
                "Пользовательские файлы *-user.txt не изменялись.",
                "Списки обновлены", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка обновления списков: " + ex.Message);
            MessageBox.Show(ex.Message, "Не удалось обновить списки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateListsButton.IsEnabled = true;
            RefreshListStatus();
        }
    }

    private void RefreshLists_Click(object sender, RoutedEventArgs e)
    {
        RefreshListStatus();
        AppendLog("Состояние файлов списков обновлено.");
    }

    private void EnsureUserLists_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BasePath.Text))
        {
            MessageBox.Show("Сначала выберите папку zapret2 bundle.", "Списки", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var created = ListUpdateService.EnsureUserLists(BasePath.Text.Trim());
            foreach (var name in created) AppendLog("Создан пользовательский список: " + name);
            RefreshListStatus();
            MessageBox.Show(created.Count == 0
                ? "Все пользовательские списки уже существуют. Их содержимое не менялось."
                : "Созданы файлы:" + Environment.NewLine + string.Join(Environment.NewLine, created),
                "Пользовательские списки", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось создать списки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenListsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BasePath.Text)) return;
        try
        {
            var directory = ListUpdateService.GetListsDirectory(BasePath.Text.Trim());
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("Папка lists не найдена. Проверьте выбранный корень bundle.");
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(directory);
            Process.Start(startInfo);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Папка списков", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenUserList_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BasePath.Text) || UserListPicker.SelectedItem is not string fileName) return;
        try
        {
            ListUpdateService.EnsureUserLists(BasePath.Text.Trim());
            var path = Path.Combine(_components.ListsDirectory ?? ListUpdateService.GetListsDirectory(GetResolvedBundleRoot()), fileName);
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
            AppendLog("Открыт пользовательский список: " + fileName);
            RefreshListStatus();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Пользовательский список", MessageBoxButton.OK, MessageBoxImage.Error); }
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

    private void RefreshSessions_Click(object sender, RoutedEventArgs e) => RefreshSessions();

    private void RefreshSessions()
    {
        try
        {
            _sessions.Clear();
            foreach (var session in _logStore.GetSessions())
                _sessions.Add(session);
            SessionSummaryText.Text = $"Найдено журналов: {_sessions.Count}. Выберите одну сессию для сводки или две для сравнения.";
        }
        catch (Exception ex)
        {
            SessionSummaryText.Text = "Не удалось прочитать историю сессий: " + ex.Message;
        }
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SessionList.SelectedItems.Cast<DiagnosticLogInfo>().ToList();
        if (selected.Count == 0)
        {
            SessionSummaryText.Text = "Выберите одну сессию для просмотра сводки или две для сравнения.";
            return;
        }
        if (selected.Count > 1)
        {
            SessionSummaryText.Text = selected.Count == 2
                ? "Выбраны две сессии. Нажмите «Сравнить две»."
                : "Для сравнения выберите ровно две сессии.";
            return;
        }

        try
        {
            var summary = DiagnosticSessionAnalyzer.Analyze(selected[0].Path);
            SessionSummaryText.Text =
                $"{summary.SessionTitle}\n" +
                $"Изменён: {selected[0].LastWriteTimeUtc.ToLocalTime():g}\n" +
                $"Строк: {summary.TotalLines} · Возможные ошибки: {summary.PotentialErrorLines} · Предупреждения: {summary.PotentialWarningLines}\n" +
                $"Длительность по временным меткам: {summary.Duration?.ToString(@"hh\\:mm\\:ss") ?? "н/д"}\n" +
                $"SHA-256: {summary.Sha256[..16]}…";
        }
        catch (Exception ex)
        {
            SessionSummaryText.Text = "Не удалось разобрать журнал: " + ex.Message;
        }
    }

    private void CompareSessions_Click(object sender, RoutedEventArgs e)
    {
        var selected = SessionList.SelectedItems.Cast<DiagnosticLogInfo>().ToList();
        if (selected.Count != 2)
        {
            MessageBox.Show("Для сравнения выберите ровно две сессии (Ctrl/Shift + щелчок).",
                "Сравнение журналов", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var comparison = DiagnosticSessionAnalyzer.Compare(selected[0].Path, selected[1].Path);
            SessionSummaryText.Text =
                $"Сравнение: {comparison.First.SessionTitle} → {comparison.Second.SessionTitle}\n" +
                $"Строки: {comparison.LineDelta:+#;-#;0} · Возможные ошибки: {comparison.PotentialErrorDelta:+#;-#;0} · Предупреждения: {comparison.PotentialWarningDelta:+#;-#;0}\n" +
                $"Изменение длительности: {comparison.DurationDelta?.ToString(@"hh\\:mm\\:ss") ?? "н/д"}\n" +
                $"Содержимое {(comparison.ContentChanged ? "изменилось" : "не изменилось")} (SHA-256).\n" +
                "Счётчики ошибок/предупреждений — поиск слов-маркеров, не диагноз сетевой совместимости.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Сравнение журналов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSelectedSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not DiagnosticLogInfo session) return;
        try
        {
            var startInfo = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            startInfo.ArgumentList.Add(session.Path);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Открытие журнала", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_logStore.DirectoryPath);
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(_logStore.DirectoryPath);
            Process.Start(startInfo);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Папка журналов", MessageBoxButton.OK, MessageBoxImage.Error); }
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
        try { _logStore.Append(line); } catch { /* UI logging must continue if disk logging is unavailable. */ }
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
