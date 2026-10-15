@echo off
rem 리플레이 파일(.bnbreplay)을 이 파일 위에 끌어다 놓으면 재생
setlocal
cd /d "%~dp0"
if not defined GODOT set GODOT=Godot_v4.7.2-stable_mono_win64.exe

if "%~1"=="" goto :usage
start "" "%GODOT%" --path client -- --replay "%~f1"
exit /b 0

:usage
echo usage: watch-replay.bat ^<file.bnbreplay^>
echo        server replays: replays\
pause
exit /b 1
