#!/usr/bin/env bash
# Demo: önyüzü derleyip API'nin içinden servis eder → http://localhost:5080
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1
(cd web && npm run build)
cd api && dotnet run -c Release
