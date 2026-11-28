<div align="center">

# Zapret2 Control Center

**Единый центр управления Zapret и Zapret2 для Windows 10/11**

Управляйте профилями, сетевыми списками, движками и диагностикой из одного portable-приложения.

[Скачать portable EXE](https://github.com/reikidod/Zapret2/releases/download/continuous/Zapret2ControlCenter.exe) · [Исходный код](https://github.com/reikidod/Zapret2) · [План развития](ROADMAP.md)

![Build](https://github.com/reikidod/Zapret2/actions/workflows/build-windows.yml/badge.svg?branch=main)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-1687C9)
![Architecture](https://img.shields.io/badge/architecture-x64-64748B)

</div>

---

## О проекте

Zapret2 Control Center — WPF-приложение с комплектом классического Zapret и Zapret2. В portable EXE включены winws.exe, winws2.exe, оба blockcheck, WinDivert, Cygwin, Lua, профили и списки Flowseal. Отдельно скачивать runtime не требуется.

**Стек:** C# · WPF · .NET 10 · Rust native library (C ABI) · Windows x64.

**Архитектура:** C# / WPF отвечает за интерфейс, профили, настройки, процессы и диагностику; Rust подключается как компактная нативная библиотека через версионированный C ABI. Python не используется. PowerShell применяется только в CI и подготовке встроенного runtime; BAT/CMD — штатные стратегии и диагностические скрипты Zapret.

> При первом запуске встроенный комплект распаковывается в %LOCALAPPDATA%\\Zapret2GUI\\components. Настройки пользователя хранятся отдельно от EXE.

## Возможности

| Раздел | Что можно делать |
|---|---|
| **Обзор** | Проверять состояние движков и библиотек; запускать профиль и открывать журнал |
| **Профили** | Создавать, копировать, удалять и восстанавливать профили; выбирать движок; импортировать 22 профиля Flowseal офлайн |
| **Редактор** | Импортировать аргументы из CMD/BAT, разрешать переменные путей, проверять и копировать итоговую команду |
| **Импорт и экспорт** | Переносить пользовательские профили через JSON |
| **Процессы** | Запускать и останавливать выбранный движок, читать stdout/stderr и предотвращать повторный запуск во время очистки |
| **Проверка запуска** | Проверять EXE, Lua и list-зависимости; видеть TCP/UDP-порты, hostlist, ipset и предупреждения о глобальных фильтрах |
| **Списки** | Обновлять общие файлы Flowseal с проверкой данных, резервированием и откатом; хранить собственные пользовательские списки |
| **Диагностика** | Находить компоненты и DPI-процессы, запускать нужный blockcheck, просматривать журналы и историю сессий |
| **Сохранность** | Автоматически создавать резервную копию конфигурации и восстанавливать её при повреждении |

## Загрузка

<div align="center">

### [Скачать Zapret2ControlCenter.exe](https://github.com/reikidod/Zapret2/releases/download/continuous/Zapret2ControlCenter.exe)

Один self-contained EXE · Windows x64 · .NET Runtime не требуется

</div>

Постоянная ссылка ведёт на автоматически обновляемую сборку continuous. После успешного CI на main файл заменяется новой версией. Каждая сборка также сохраняется в GitHub Actions как артефакт Zapret2ControlCenter-Windows-x64.

## Классические стратегии Flowseal

На странице **«Профили»** нажмите **«Импортировать 22 стратегии Flowseal (офлайн)»**. Используются встроенные исходные general.bat, general (ALT)–ALT13, EXP, FAKE TLS AUTO и SIMPLE FAKE из [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube), закреплённые на commit ffce9179b2b1f2aeb961f4d17274ada3b6d6bea5.

Текст читается из локальных файлов; извлекаются только параметры winws2.exe, BAT/CMD не исполняются. Пути к lists и игровым фильтрам разрешаются из выбранного bundle. Эти профили — отправные точки, а не гарантия совместимости с конкретной сетью или приложением.

## Установка и требования

- Windows 10/11 x64.
- Запустите EXE; комплект движков и библиотек распакуется при первом старте.
- Для WinDivert обычно требуются права администратора.
- Blockcheck требует сетевого доступа и запускается отдельно для соответствующего движка.

Профили и настройки хранятся в %APPDATA%\\Zapret2GUI и сохраняются при замене EXE.

## Сборка из исходников

Откройте Zapret2.sln в Visual Studio с компонентом разработки WPF или выполните:

```powershell
dotnet restore .\\Zapret2.sln
dotnet test .\\Zapret2.sln --configuration Release
.\\build-portable.ps1
```

Результат: publish\\win-x64\\Zapret2ControlCenter.exe — self-contained single-file приложение для Windows x64.

## Ограничения и безопасность

- Результаты blockcheck.cmd и blockcheck2.cmd пока не разбираются автоматически: скрипт открывается в отдельной консоли.
- Журналы находятся в %APPDATA%\\Zapret2GUI\\logs. Они могут содержать пути, домены и IP-адреса из вывода движка; приложение не отправляет их автоматически.
- Обновляются только общие ipset-all.txt, ipset-exclude.txt, list-general.txt, list-google.txt и list-exclude.txt. Пользовательские списки не заменяются.
- Обновление не сливает автоматически локальные правки в общих файлах. Собственные записи храните в отдельных пользовательских списках.
- Фильтры по портам не изолируют приложения по имени процесса и могут затрагивать другие программы.
- Steam, Telegram Proxy, Valorant и другие приложения требуют проверки на реальной системе. Выбор профиля не гарантирует совместимость.
- Не запускайте неизвестные BAT/CMD-файлы и сетевые стратегии без проверки содержимого. Перед тестированием убедитесь, что знаете, как остановить выбранный движок.

## Документация проекта

- [ROADMAP — план развития](ROADMAP.md)
- [CONTRIBUTING — правила разработки](CONTRIBUTING.md)
- [CHANGELOG — история изменений](CHANGELOG.md)
- [SECURITY — сообщение об уязвимостях](SECURITY.md)