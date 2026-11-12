@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ==========================================================
echo   Rebuilding the single-file launcher (Start-ApiPlayground.exe)
echo ==========================================================
echo.

dotnet publish ServiceController\ServiceController.csproj -c Release -r win-x64 ^
  --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o dist
if errorlevel 1 goto :fail

copy /y dist\ServiceController.exe Start-ApiPlayground.exe >nul
if errorlevel 1 goto :fail

echo.
echo Done!  Double-click Start-ApiPlayground.exe to launch.
goto :eof

:fail
echo.
echo Publish failed. Check the output above.
pause
