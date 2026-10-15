@echo off
setlocal
cd /d "%~dp0"

echo [1/3] Sim / Net / Server / Bot
dotnet build BnB.slnx -nologo -v q || goto :fail

echo [2/3] Godot client
dotnet build client\BnB.Client.sln -nologo -v q || goto :fail

echo [3/3] Tests
dotnet test tests\BnB.Sim.Tests -nologo -v q || goto :fail

echo.
echo Build OK
exit /b 0

:fail
echo.
echo Build FAILED
exit /b 1
