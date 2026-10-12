#!/usr/bin/env bash
# Yerel Qwen modelini OpenAI uyumlu uçla (http://127.0.0.1:18080/v1) başlatır.
# Model yolu LLM_MODEL ile değiştirilebilir. 24 GB M4'te Qwen3-30B-A3B IQ4_XS tam Metal offload ile sığar.
set -euo pipefail
MODEL="${LLM_MODEL:-$HOME/appgen-v3/LLM/models/Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf}"
PORT="${LLM_PORT:-18080}"
if curl -sf "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
  echo "llama-server zaten 127.0.0.1:$PORT üzerinde çalışıyor; yeniden başlatmaya gerek yok."
  exit 0
fi
exec llama-server \
  --model "$MODEL" \
  --ctx-size 8192 --parallel 1 --n-gpu-layers 99 \
  --flash-attn on --cache-type-k q8_0 --cache-type-v q8_0 \
  --jinja --host 127.0.0.1 --port "$PORT"
