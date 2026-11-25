# Zapret2 Control Center (Rust)

Исходный проект Rust + egui находится в `zapret2-rust-source.zip` (Cargo.toml, src/main.rs, .gitignore и workflow snapshot). Рабочий GitHub Actions workflow расположен в `.github/workflows/build-windows.yml`.

## Build / portable EXE
GitHub Actions компилирует Windows x64 release и публикует артефакт `Zapret2ControlCenter.exe`. Откройте вкладку Actions → **Build Windows x64 portable EXE** → последний успешный запуск → Artifacts.

## Возможности исходной версии
- Fluent-inspired тёмный интерфейс: обзор, стратегии, списки и настройки.
- Поиск `winws2.exe`, `zapret-lib.lua` и `blockcheck2.cmd` в выбранной папке.
- Восемь встроенных профилей; пользовательские профили сохраняются в `%APPDATA%\\Zapret2ControlCenter\\config.json`.
- Запуск/остановка `winws2.exe`, журнал вывода, запуск `blockcheck2.cmd` в отдельной консоли.
- Импорт аргументов из `.cmd/.bat`, редактор профилей.
- Открытие списков и обновление общих списков Flowseal без перезаписи `*-user.txt`.
- Запрос UAC при запуске стратегии.

## Ограничения
Это GUI-оболочка, а не полный zapret bundle. `winws2.exe`, WinDivert, Lua, `blockcheck2.cmd` и остальные файлы бандла предоставляются отдельно. `blockcheck2` запускается в отдельной консоли; интерактивные результаты автоматически не разбираются.

EXE не считается проверенным, пока Windows Actions не завершит сборку. Runtime-проверка требует Windows x64, прав администратора, WinDivert и сетевого теста.