FROM python:3.11-slim-bookworm AS base
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg libsndfile1 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY requirements-test.txt ./
RUN pip install --no-cache-dir -r requirements-test.txt

FROM base AS test
COPY app ./app
COPY tests ./tests
CMD ["python", "-m", "pytest", "-q"]

FROM base AS runtime
COPY requirements.lock.txt requirements.txt ./
# Native Linux ARM64 wheels come from PyPI; avoid amd64 emulation on Apple Silicon.
RUN if python -c "import platform; raise SystemExit(0 if platform.machine() == 'x86_64' else 1)"; then pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu torch==2.6.0 torchaudio==2.6.0; else pip install --no-cache-dir torch==2.6.0 torchaudio==2.6.0; fi
RUN pip install --no-cache-dir -r requirements.lock.txt && pip install --no-cache-dir --no-deps -r requirements.txt
COPY app ./app
ENV HF_HOME=/models TTS_DATA_DIR=/data TTS_DEVICE=cpu
EXPOSE 5081
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "5081", "--workers", "1"]
