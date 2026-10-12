#!/usr/bin/env bash
# Geliştirme: API (5080) + Vite (5173). llama-server'ı ayrı terminalde scripts/start-llm.sh ile başlatın.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1
(cd api && dotnet run) &
API_PID=$!
trap 'kill $API_PID 2>/dev/null' EXIT
cd web && npm run dev
