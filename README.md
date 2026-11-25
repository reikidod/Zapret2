# Zapret2 Control Center (Rust)

Перенос GUI с Python/Tkinter на Rust + egui. Целевая платформа: Windows 10/11 x64.

## Возможности текущей реализации
- Fluent-inspired тёмный интерфейс: обзор, стратегии, списки и настройки.
- Поиск `winws2.exe`, `zapret-lib.lua` и `blockcheck2.cmd` в выбранной папке.
- Восемь встроенных профилей; пользовательские профили сохраняются в `%APPDATA%\\Zapret2ControlCenter\\config.json`.
- Запуск/остановка `winws2.exe`, журнал вывода, запуск `blockcheck2.cmd` в отдельной консоли.
- Импорт аргументов из `.cmd/.bat`, редактор профилей.
- Открытие списков и обновление общих списков Flowseal без перезаписи `*-user.txt`.
- Запрос UAC при запуске стратегии.

## Сборка
GitHub Actions workflow **Build Windows x64 portable EXE** компилирует Windows x64 release и публикует артефакт `Zapret2ControlCenter.exe`.

## Важные ограничения
Это GUI-оболочка, а не полный zapret bundle. `winws2.exe`, WinDivert, Lua, `blockcheck2.cmd` и остальные файлы бандла должны быть предоставлены отдельно. `blockcheck2` запускается в отдельной консоли; интерактивные результаты автоматически не разбираются.

Сборка и runtime-проверка должны быть подтверждены Windows GitHub Actions runner. В текущей среде нет Rust/Cargo, поэтому локальная компиляция не выполнялась.