#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"
echo ".NET 10 API Playground"
echo "Open http://127.0.0.1:5188 in your browser. Press Ctrl+C to stop."
exec dotnet run --urls http://127.0.0.1:5188
