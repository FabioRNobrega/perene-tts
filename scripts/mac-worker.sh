#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../services/ChatterboxTtsService"
if [[ "$(uname -s)" != Darwin ]]; then
    echo "This command needs native macOS. Use make up for Docker CPU mode." >&2
    exit 1
fi
case "${1:-run}" in
setup)
    command -v ffmpeg >/dev/null || { echo "Install FFmpeg first (brew install ffmpeg)." >&2; exit 1; }
    python3.11 -m venv .venv
    .venv/bin/python -m pip install --upgrade pip
    .venv/bin/python -m pip install -r requirements.lock.txt
    .venv/bin/python -m pip install --no-deps -r requirements.txt
    ;;
run)
    [[ -x .venv/bin/python ]] || { echo "Run make mac-setup first." >&2; exit 1; }
    export TTS_DEVICE="${TTS_DEVICE:-auto}"
    export TTS_DATA_DIR="${TTS_DATA_DIR:-$PWD/data}"
    export HF_HOME="${HF_HOME:-$PWD/models}"
    export TTS_BATCH_ENABLED="${TTS_BATCH_ENABLED:-true}"
    export PYTORCH_ENABLE_MPS_FALLBACK=1
    # Explicit opt-in binding for Docker Desktop to reach the native host worker.
    exec .venv/bin/python -m uvicorn app.main:app --host "${TTS_HOST:-0.0.0.0}" --port 5081 --workers 1
    ;;
*) echo "Usage: $0 setup|run" >&2; exit 1 ;;
esac
