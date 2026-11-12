#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"

echo "=========================================================="
echo "  Rebuilding the single-file launcher (Start-ApiPlayground.exe)"
echo "=========================================================="
echo

dotnet publish ServiceController/ServiceController.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o dist

cp -f dist/ServiceController.exe Start-ApiPlayground.exe

echo
echo "Done! Double-click Start-ApiPlayground.exe to launch."
