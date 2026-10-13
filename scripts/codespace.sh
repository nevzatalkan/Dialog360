#!/usr/bin/env bash
# GitHub Codespaces / bulut: yerel model yerine ücretsiz GitHub Models API'si → http://localhost:5080
# Codespaces'te GITHUB_TOKEN hazır gelir. Başka bir OpenAI uyumlu servis için Llm__* değişkenlerini önceden verin.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export Llm__BaseUrl="${Llm__BaseUrl:-https://models.github.ai/inference}"
export Llm__Model="${Llm__Model:-openai/gpt-4.1-mini}"
export Llm__ApiKey="${Llm__ApiKey:-${GH_MODELS_TOKEN:-${GITHUB_TOKEN:-}}}"
export Llm__LlamaCachePrompt=false
if [[ -z "${Llm__HealthUrl:-}" && "$Llm__BaseUrl" == https://models.github.ai/* ]]; then
  export Llm__HealthUrl=https://models.github.ai/catalog/models
fi
[ -n "$Llm__ApiKey" ] || echo "UYARI: API anahtarı yok (GITHUB_TOKEN / GH_MODELS_TOKEN / Llm__ApiKey)." >&2
echo "LLM: $Llm__Model @ $Llm__BaseUrl"
(cd web && { [ -d node_modules ] || npm ci; } && npm run build)
cd api && dotnet run -c Release -- --urls http://0.0.0.0:5080
