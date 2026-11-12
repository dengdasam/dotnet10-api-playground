@echo off
chcp 65001 >nul
cd /d "%~dp0"

set "API_DLL=ApiPlayground\bin\Debug\net10.0\ApiPlayground.dll"
set "CTL_DLL=ServiceController\bin\Debug\net10.0\ServiceController.dll"

echo ==========================================================
echo   .NET 10 API Playground - Service Controller
echo   Page: http://127.0.0.1:5188/
echo   Type "help" in this window for commands.
echo   Keep this window open. Closing it stops both processes.
echo ==========================================================
echo.

if /i "%~1"=="build" goto :build
if not exist "%API_DLL%" goto :build
if not exist "%CTL_DLL%" goto :build

echo Already built - skipping compile for a fast start.
echo Changed C# code? Type "build" in this window, or run: start.cmd build
echo.
goto :run

:build
echo Building projects...
dotnet build ApiPlayground\ApiPlayground.csproj -v minimal || goto :fail
dotnet build ServiceController\ServiceController.csproj -v minimal || goto :fail
echo.

:run
dotnet "%CTL_DLL%"
goto :eof

:fail
echo.
echo Build failed. Please check the output above.
pause
