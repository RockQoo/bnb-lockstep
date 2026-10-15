@echo off
rem 서버 + Godot 클라 두 개. 같은 PC에서 창을 번갈아 클릭해서 조작한다.
setlocal
cd /d "%~dp0"
if not defined GODOT set GODOT=Godot_v4.7.2-stable_mono_win64.exe

call build.bat || goto :fail

start "BnB Server" dotnet src\BnB.Server\bin\Debug\net8.0\BnB.Server.dll --players 2
timeout /t 1 /nobreak >nul
start "" "%GODOT%" --path client -- --name Player1
timeout /t 1 /nobreak >nul
start "" "%GODOT%" --path client -- --name Player2
exit /b 0

:fail
pause
exit /b 1
