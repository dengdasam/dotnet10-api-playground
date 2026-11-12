@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo .NET 10 API Playground
echo Open http://127.0.0.1:5188 in your browser.
echo Press Ctrl+C to stop.
echo.
dotnet run --urls http://127.0.0.1:5188
