@echo off
chcp 65001 >nul
title zapret2: 04_badack
rem ============================================================
rem Стратегия: 04 Битый ACK + ts_up
rem Фулинг через искажённый ACK (-66000) с tcp_ts_up и автоматическим TTL. Экспериментальная.
rem Файл сгенерирован из zapret2_gui/strategies.py (python -m zapret2_gui.batch)
rem ============================================================
call "%~dp0_common.cmd" "%~f0"
if errorlevel 1 exit /b
cd /d "%BIN%"
echo Запуск winws2 из: %BIN%
echo Остановить: Ctrl+C или закрыть окно.
echo.
"%BIN%winws2.exe" %DBG% ^
--wf-tcp-out=80,443 ^
--wf-udp-out=443 ^
--lua-init=@"%BIN%lua\zapret-lib.lua" --lua-init=@"%BIN%lua\zapret-antidpi.lua" ^
--lua-init="fake_default_tls = tls_mod(fake_default_tls,'rnd,rndsni')" ^
--filter-tcp=80 --filter-l7=http ^
--out-range=-d10 ^
--payload=http_req ^
--lua-desync=fake:blob=fake_default_http:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5 ^
--lua-desync=fakedsplit:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5 ^
--new ^
--filter-tcp=443 --filter-l7=tls ^
--out-range=-d10 ^
--payload=tls_client_hello ^
--lua-desync=fake:blob=fake_default_tls:tcp_ack=-66000:tcp_ts_up:tls_mod=rnd:repeats=6 ^
--lua-desync=multidisorder:pos=7,sld+1:tcp_ack=-66000:tcp_ts_up:ip_autottl=-1,3-20:ip6_autottl=-1,3-20 ^
--new ^
--filter-udp=443 --filter-l7=quic ^
--payload=quic_initial ^
--lua-desync=fake:blob=fake_default_quic:repeats=11
echo.
echo winws2 завершился (код %errorlevel%).
pause
