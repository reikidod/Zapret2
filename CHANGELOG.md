# Changelog

## Unreleased

### Changed
- Chose C# / WPF / .NET 10 as the single application stack.
- Moved the GUI into a conventional Visual Studio solution (`src`, `tests`, `assets`).
- Preserved profile data compatibility with the previous Python GUI configuration.
- Preserved built-in strategies and batch helper scripts.
- Added self-contained single-file Windows x64 publishing and release attachment workflow.

### Added
- Core unit tests for argument parsing, path substitution, `.cmd` import and profile persistence.
- Windows CI for restore, tests, publish and portable artifact validation.


## Unreleased — C# Control Center

- Переработан интерфейс: навигация по разделам «Обзор», «Профили», «Диагностика» и «Настройки».
- Добавлены импорт и экспорт пользовательских профилей в JSON.
- Добавлены резервная копия конфигурации и восстановление из `config.json.bak` при повреждении основного файла.
- Диагностика показывает обнаруженные `winws` / `winws2`, PID и доступные пути процессов.
- Улучшен поиск bundle: `ZAPRET2_DIR`, PATH, стандартные папки, OneDrive и диски; приоритет отдаётся полному комплекту.
- Добавлены дополнительные тесты импорта, восстановления профилей, поиска компонентов и стратегий.
- Улучшены сообщения о запуске/остановке, экспорт журнала и проверка компонентов.
- Portable EXE по-прежнему не включает сторонние бинарные файлы zapret2 или WinDivert.
