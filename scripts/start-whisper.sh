#!/usr/bin/env bash
# Yerel ses tanıma (whisper.cpp) → http://127.0.0.1:18090   (sesle soru için; dil ve alan terimleri istekte gönderilir)
# Model: https://huggingface.co/ggerganov/whisper.cpp → ggml-large-v3-turbo-q5_0.bin (~574 MB). Yol WHISPER_MODEL ile değiştirilebilir.
set -euo pipefail
MODEL="${WHISPER_MODEL:-$HOME/models/ggml-large-v3-turbo-q5_0.bin}"
PORT="${WHISPER_PORT:-18090}"
if curl -sf "http://127.0.0.1:$PORT/" >/dev/null 2>&1; then
  echo "whisper-server zaten 127.0.0.1:$PORT üzerinde çalışıyor; yeniden başlatmaya gerek yok."
  exit 0
fi
if [ ! -f "$MODEL" ]; then
  echo "Whisper modeli bulunamadı: $MODEL" >&2
  echo "İndirmek için: curl -L -o ~/models/ggml-large-v3-turbo-q5_0.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin" >&2
  exit 1
fi
exec whisper-server -m "$MODEL" --host 127.0.0.1 --port "$PORT"
