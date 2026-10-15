@echo off
rem 서버 + 봇 + Godot 클라 하나를 띄워서 봇과 1:1
setlocal
cd /d "%~dp0"
if not defined GODOT set GODOT=Godot_v4.7.2-stable_mono_win64.exe

call build.bat || goto :fail

start "BnB Server" dotnet src\BnB.Server\bin\Debug\net8.0\BnB.Server.dll --players 2
timeout /t 1 /nobreak >nul
start "BnB Bot" dotnet src\BnB.Bot\bin\Debug\net8.0\BnB.Bot.dll --name Bot --games 0
start "" "%GODOT%" --path client -- --name Me
exit /b 0

:fail
pause
exit /b 1
