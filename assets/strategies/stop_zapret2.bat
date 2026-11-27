@echo off
chcp 65001 >nul
rem Останавливает все процессы winws2.
fltmc >nul 2>&1
if errorlevel 1 echo [!] Лучше запускать от администратора.
taskkill /F /IM winws2.exe /T
echo.
echo Готово. Если сеть ведёт себя странно, выполните windivert_delete.cmd из бандла.
pause
