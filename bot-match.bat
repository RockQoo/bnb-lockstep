@echo off
rem 화면 없이 서버 + 봇 2개로 N판 돌려서 디싱크와 리플레이 검증 결과를 본다
setlocal
cd /d "%~dp0"
set GAMES=%~1
if "%GAMES%"=="" set GAMES=3

call build.bat || goto :fail

start "BnB Server" dotnet src\BnB.Server\bin\Debug\net8.0\BnB.Server.dll --players 2 --games %GAMES% --port 7799
timeout /t 1 /nobreak >nul
start "BnB Bot A" dotnet src\BnB.Bot\bin\Debug\net8.0\BnB.Bot.dll --name Alpha --games %GAMES% --port 7799
dotnet src\BnB.Bot\bin\Debug\net8.0\BnB.Bot.dll --name Beta --games %GAMES% --port 7799
pause
exit /b 0

:fail
pause
exit /b 1
