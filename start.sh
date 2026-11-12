#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"

API_DLL="ApiPlayground/bin/Debug/net10.0/ApiPlayground.dll"
CTL_DLL="ServiceController/bin/Debug/net10.0/ServiceController.dll"

echo "=========================================================="
echo "  .NET 10 API Playground - Service Controller"
echo "  Page: http://127.0.0.1:5188/"
echo "  Type \"help\" in this terminal for commands."
echo "  Keep this terminal open. Closing it stops both processes."
echo "=========================================================="
echo

need_build=0
if [ "$1" = "build" ]; then need_build=1; fi
if [ ! -f "$API_DLL" ] || [ ! -f "$CTL_DLL" ]; then need_build=1; fi

if [ "$need_build" = "1" ]; then
  echo "Building projects..."
  dotnet build ApiPlayground/ApiPlayground.csproj -v minimal
  dotnet build ServiceController/ServiceController.csproj -v minimal
  echo
else
  echo "Already built - skipping compile for a fast start."
  echo "Changed C# code? Type \"build\" in this terminal, or run: ./start.sh build"
  echo
fi

exec dotnet "$CTL_DLL"
