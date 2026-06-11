#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT_DIR"

export PATH="$ROOT_DIR/.tools/node_modules/azure-functions-core-tools/bin:$ROOT_DIR/.tools/node_modules/.bin:$ROOT_DIR/.dotnet:$PATH"
export DOTNET_CLI_HOME="$ROOT_DIR/.dotnet-home"
export NUGET_PACKAGES="$ROOT_DIR/.nuget/packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if lsof -nP -iTCP:7071 -sTCP:LISTEN >/dev/null 2>&1; then
  echo "Stopping existing Azure Functions process on port 7071..."
  lsof -tiTCP:7071 -sTCP:LISTEN | xargs kill || true

  for _ in 1 2 3 4 5; do
    if ! lsof -nP -iTCP:7071 -sTCP:LISTEN >/dev/null 2>&1; then
      break
    fi
    sleep 1
  done
fi

echo "Cleaning up any leftover Azure Functions host processes..."
pkill -f "$ROOT_DIR/.tools/node_modules/azure-functions-core-tools/bin/func" >/dev/null 2>&1 || true
pkill -f "$ROOT_DIR/bin/Debug/net10.0/AutoTweetRss" >/dev/null 2>&1 || true
sleep 2

if lsof -nP -iTCP:7071 -sTCP:LISTEN >/dev/null 2>&1; then
  echo "Port 7071 is still busy; force-stopping the old process..."
  lsof -tiTCP:7071 -sTCP:LISTEN | xargs kill -9 || true
  sleep 1
fi

if lsof -nP -iTCP:7071 -sTCP:LISTEN >/dev/null 2>&1; then
  echo "Port 7071 is still unavailable. Please close the terminal running func start and retry."
  lsof -nP -iTCP:7071 -sTCP:LISTEN || true
  exit 1
fi

if ! lsof -nP -iTCP:10000 -sTCP:LISTEN >/dev/null 2>&1; then
  echo "Starting Azurite local storage on port 10000..."
  mkdir -p "$ROOT_DIR/azurite"
  nohup azurite --silent --location "$ROOT_DIR/azurite" --debug "$ROOT_DIR/azurite/debug.log" > "$ROOT_DIR/azurite/stdout.log" 2>&1 &
  sleep 3
fi

if ! lsof -nP -iTCP:10000 -sTCP:LISTEN >/dev/null 2>&1; then
  echo "Azurite did not start. Check azurite/stdout.log and azurite/debug.log."
  exit 1
fi

echo "Building project..."
"$ROOT_DIR/.dotnet/dotnet" build AutoTweetRss.csproj -p:NuGetAudit=false

echo "Starting Azure Functions on http://127.0.0.1:7071 ..."
exec "$ROOT_DIR/start-functions.sh"
