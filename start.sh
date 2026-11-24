#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"

# 控制器现在是 Windows 专属（托盘 GUI，net10.0-windows + WinForms）。
# 其它平台构建不了，直接退化为只跑主服务，免得抛一堆看不懂的框架错误。
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    ;;
  *)
    echo "=========================================================="
    echo "  The controller (tray GUI) is Windows-only."
    echo "  Starting the API alone on http://127.0.0.1:5180 instead."
    echo "=========================================================="
    echo
    cd ApiPlayground
    exec dotnet run --urls http://127.0.0.1:5180
    ;;
esac

API_DLL="ApiPlayground/bin/Debug/net10.0/ApiPlayground.dll"
CTL_DLL="ServiceController/bin/Debug/net10.0-windows/ServiceController.dll"

echo "=========================================================="
echo "  .NET 10 API Playground - Service Controller (console mode)"
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

exec dotnet "$CTL_DLL" --console
