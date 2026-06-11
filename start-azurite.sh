#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

export PATH="$PWD/.tools/node_modules/.bin:$PATH"

azurite --silent --location ./azurite --debug ./azurite/debug.log
