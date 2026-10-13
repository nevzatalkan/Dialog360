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
if [ -n "${GH_MODELS_TOKEN:-}" ]; then KEY_SRC=GH_MODELS_TOKEN; elif [ -n "${GITHUB_TOKEN:-}" ]; then KEY_SRC=GITHUB_TOKEN; else KEY_SRC=yok; fi
echo "LLM: $Llm__Model @ $Llm__BaseUrl (anahtar: $KEY_SRC)"

# Model erişimini baştan dene: hata varsa sebebi burada görünür.
CODE=$(curl -sS -m 30 -o /tmp/llm-test.json -w '%{http_code}' "${Llm__BaseUrl%/}/chat/completions" \
  -H "Authorization: Bearer $Llm__ApiKey" -H 'Content-Type: application/json' \
  -d "{\"model\":\"$Llm__Model\",\"messages\":[{\"role\":\"user\",\"content\":\"merhaba\"}],\"max_tokens\":5}" || true)
if [ "$CODE" = 200 ]; then
  echo "LLM testi: OK"
else
  echo "LLM testi BAŞARISIZ (HTTP $CODE): $(head -c 400 /tmp/llm-test.json 2>/dev/null)" >&2
fi
(cd web && { [ -d node_modules ] || npm ci; } && npm run build)
cd api && dotnet run -c Release -- --urls http://0.0.0.0:5080
