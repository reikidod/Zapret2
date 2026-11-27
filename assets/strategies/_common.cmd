@echo off
chcp 65001 >nul
setlocal EnableExtensions
rem Вызывается из стратегий: call "%~dp0_common.cmd" "%~f0"
rem Результат: переменная BIN (папка winws2.exe со слэшем в конце) и DBG.
set "CALLER=%~1"
set "STATE_DIR=%APPDATA%\Zapret2GUI"

fltmc >nul 2>&1
if errorlevel 1 goto :need_admin

set "BIN="
rem 1) переменная окружения ZAPRET2_DIR
if defined ZAPRET2_DIR call :try "%ZAPRET2_DIR%"
rem 2) путь, который запомнили GUI или прошлый запуск
if not defined BIN if exist "%STATE_DIR%\winws2_dir.txt" call :try_saved
rem 3) рядом с этим файлом и на уровень-два выше
if not defined BIN call :try "%~dp0"
if not defined BIN call :try "%~dp0.."
if not defined BIN call :try "%~dp0..\.."
rem 4) в PATH
if not defined BIN for /f "delims=" %%I in ('where winws2.exe 2^>nul') do call :try "%%~dpI"
rem 5) папки zapret* в типичных местах
if not defined BIN call :scan_roots
rem 6) глубокий поиск в Загрузках, на Рабочем столе и в Документах
if not defined BIN call :deep_search

if not defined BIN goto :not_found

if not exist "%STATE_DIR%" mkdir "%STATE_DIR%" >nul 2>&1
>"%STATE_DIR%\winws2_dir.txt" echo(%BIN%

set "DBG="
if "%ZAPRET_DEBUG%"=="1" set "DBG=--debug"
endlocal & set "BIN=%BIN%" & set "DBG=%DBG%" & exit /b 0

:try
if defined BIN exit /b 0
set "P=%~1"
if "%P%"=="" exit /b 0
for %%I in ("%P%") do set "P=%%~fI"
if "%P:~-1%"=="\" set "P=%P:~0,-1%"
if exist "%P%\winws2.exe" set "BIN=%P%\"& exit /b 0
if exist "%P%\zapret-winws\winws2.exe" set "BIN=%P%\zapret-winws\"& exit /b 0
exit /b 0

:try_saved
set "SAVED="
set /p SAVED=<"%STATE_DIR%\winws2_dir.txt"
if defined SAVED call :try "%SAVED%"
exit /b 0

:scan_roots
for %%R in ("%USERPROFILE%\Downloads" "%USERPROFILE%\Desktop" "%USERPROFILE%\Documents" "%USERPROFILE%" "%SystemDrive%" "%ProgramFiles%" "%LOCALAPPDATA%") do call :scan_one "%%~R"
exit /b 0

:scan_one
if defined BIN exit /b 0
for /d %%D in ("%~1\zapret*") do call :scan_dir "%%~fD"
exit /b 0

:scan_dir
if defined BIN exit /b 0
call :try "%~1"
for /d %%E in ("%~1\*") do call :try "%%~fE"
exit /b 0

:deep_search
echo Ищу winws2.exe (может занять до минуты)...
for %%R in ("%USERPROFILE%\Downloads" "%USERPROFILE%\Desktop" "%USERPROFILE%\Documents") do call :deep_one "%%~R"
exit /b 0

:deep_one
if defined BIN exit /b 0
for /f "delims=" %%F in ('dir /s /b "%~1\winws2.exe" 2^>nul') do call :try "%%~dpF"
exit /b 0

:need_admin
echo [!] Нужны права администратора. Запрашиваю повышение (UAC)...
set "CALLER_PS=%CALLER:'=''%"
powershell -NoProfile -Command "Start-Process -FilePath '%CALLER_PS%' -Verb RunAs"
endlocal & exit /b 1

:not_found
echo [X] winws2.exe не найден.
echo     Положите этот .bat рядом с winws2.exe (папка zapret-winws из zapret-win-bundle),
echo     либо задайте переменную окружения ZAPRET2_DIR с путём к этой папке.
pause
endlocal & exit /b 2
