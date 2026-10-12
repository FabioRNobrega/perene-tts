"""Offline end-to-end check of the packaged worker (make desktop-smoke). Standard library only; it runs with the
app's own bundled Python, so nothing needs to be installed on the Mac.

Launches the worker from the built app the way the desktop host does, with outbound network denied except loopback
(sandbox-exec), waits for the model, creates a voice from a generated 5-second tone, synthesizes a short text, and
checks the MP3. Environment: SMOKE_DEVICE (default auto), SMOKE_MODEL_DIR (override, e.g. to test a broken model),
SMOKE_NO_SANDBOX=1 (run with Wi-Fi off instead).
"""
from __future__ import annotations

import io
import json
import math
import os
from pathlib import Path
import secrets
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid
import wave

HERE = Path(__file__).resolve().parent


def fail(message: str) -> None:
    print(f"SMOKE FAILED: {message}", file=sys.stderr)
    raise SystemExit(1)


def tone_wav(seconds: float = 5.0, rate: int = 24000) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(rate)
        samples = (int(8000 * math.sin(2 * math.pi * (180 + 40 * math.sin(i / rate * 3)) * i / rate))
                   for i in range(int(seconds * rate)))
        audio.writeframes(b"".join(struct.pack("<h", sample) for sample in samples))
    return buffer.getvalue()


class Worker:
    def __init__(self, app: Path, data: Path):
        contents = app / "Contents"
        self.token = secrets.token_urlsafe(32)
        cache = data / "cache"
        env = {**os.environ,
               "TTS_API_TOKEN": self.token, "TTS_DATA_DIR": str(data / "studio"),
               "TTS_MODEL_DIR": os.getenv("SMOKE_MODEL_DIR", str(contents / "Resources" / "model")),
               "TTS_FFMPEG": str(contents / "MacOS" / "ffmpeg"), "TTS_DEVICE": os.getenv("SMOKE_DEVICE", "auto"),
               "TTS_BATCH_ENABLED": "true", "PYTORCH_ENABLE_MPS_FALLBACK": "1", "HF_HUB_OFFLINE": "1",
               "TRANSFORMERS_OFFLINE": "1", "HF_HOME": str(cache / "huggingface"), "NUMBA_CACHE_DIR": str(cache / "numba")}
        # Same launch as the desktop host (WorkerProcess.Arguments): isolated bundled Python, bytecode in the cache.
        command = [str(contents / "Resources" / "python" / "bin" / "python3.11"), "-I", "-X",
                   f"pycache_prefix={cache / 'pycache'}", "-m", "app.desktop"]
        if os.getenv("SMOKE_NO_SANDBOX") != "1":
            command = ["/usr/bin/sandbox-exec", "-f", str(HERE / "smoke.sb"), *command]
        # Outside the temporary data folder, so it is still there when a failure message points at it.
        self.log = Path(tempfile.gettempdir()) / "perene-smoke-worker.log"
        self.process = subprocess.Popen(command, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=open(self.log, "wb"), cwd=data)
        line = self._first_line(timeout=120)
        if not line.startswith("PERENE_WORKER_READY port="):
            fail(f"no ready line (got {line!r}); see {self.log}")
        self.base = f"http://127.0.0.1:{int(line.split('=', 1)[1])}"

    def _first_line(self, timeout: float) -> str:
        result: list[str] = []
        reader = threading.Thread(target=lambda: result.append(self.process.stdout.readline().decode().strip()), daemon=True)
        reader.start()
        reader.join(timeout)
        return result[0] if result else ""

    def request(self, method: str, path: str, body: bytes | None = None, content_type: str | None = None):
        request = urllib.request.Request(self.base + path, data=body, method=method, headers={"X-Perene-Token": self.token})
        if content_type:
            request.add_header("Content-Type", content_type)
        with urllib.request.urlopen(request, timeout=60) as response:
            data = response.read()
            return json.loads(data) if response.headers.get_content_type() == "application/json" else data

    def wait_job(self, job_id: str, limit: float) -> dict:
        deadline = time.monotonic() + limit
        while time.monotonic() < deadline:
            job = self.request("GET", f"/jobs/{job_id}")
            if job["status"] != "running":
                if job["status"] != "completed":
                    fail(f"job {job_id} {job['status']}: {job.get('error')}")
                return job
            time.sleep(1)
        fail(f"job {job_id} did not finish in {limit:.0f} s")

    def stop(self) -> None:
        if self.process.poll() is None:
            self.process.stdin.close()
            try:
                self.process.wait(10)
            except subprocess.TimeoutExpired:
                self.process.kill()


def multipart(fields: dict[str, str], file_field: str, file_name: str, payload: bytes) -> tuple[bytes, str]:
    boundary = uuid.uuid4().hex
    parts = [f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode()
             for name, value in fields.items()]
    parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{file_field}"; filename="{file_name}"\r\n'
                 f"Content-Type: audio/wav\r\n\r\n".encode() + payload + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    return b"".join(parts), f"multipart/form-data; boundary={boundary}"


def main() -> None:
    app = Path(sys.argv[1] if len(sys.argv) > 1 else "dist/Perene TTS.app").resolve()
    if not (app / "Contents" / "Resources" / "python" / "bin" / "python3.11").is_file():
        fail(f"{app} is not a built Perene TTS app; run make desktop-mac first")
    with tempfile.TemporaryDirectory(prefix="perene-smoke-") as temporary:
        started = time.monotonic()
        worker = Worker(app, Path(temporary))
        try:
            unauthorized = urllib.request.Request(worker.base + "/health")
            try:
                urllib.request.urlopen(unauthorized, timeout=10)
                fail("worker answered without the token")
            except urllib.error.HTTPError as error:
                if error.code != 401:
                    fail(f"expected 401 without the token, got {error.code}")
            while True:
                health = worker.request("GET", "/health")
                if health.get("load_error"):
                    fail(f"model failed to load: {health['load_error']}")
                if health.get("model_ready"):
                    break
                if time.monotonic() - started > 900:
                    fail("model was not ready within 15 minutes")
                time.sleep(2)
            ready = time.monotonic() - started
            body, content_type = multipart({"name": "Smoke voice", "language": "en"}, "audio", "reference.wav", tone_wav())
            began = time.monotonic()
            voice = worker.wait_job(worker.request("POST", "/voices", body, content_type)["job_id"], 900)
            voice_seconds = time.monotonic() - began
            began = time.monotonic()
            speech = worker.request("POST", "/speech", json.dumps(
                {"voice_id": voice["voice_id"], "text": "Perene TTS works offline on this Mac."}).encode(), "application/json")
            job = worker.wait_job(speech["job_id"], 900)
            speech_seconds = time.monotonic() - began
            mp3 = worker.request("GET", f"/audio/{job['job_id']}")
            if len(mp3) < 1000 or not (mp3[:3] == b"ID3" or mp3[0] == 0xFF):
                fail(f"speech MP3 looks wrong ({len(mp3)} bytes)")
            print(f"device:            {health.get('device')}")
            print(f"model ready after: {ready:.1f} s")
            print(f"voice job:         {voice_seconds:.1f} s")
            print(f"speech job:        {speech_seconds:.1f} s")
            print(f"speech MP3:        {len(mp3):,} bytes")
            print("SMOKE PASSED")
        except urllib.error.URLError as error:
            fail(f"worker request failed: {error}; see {worker.log}")
        finally:
            worker.stop()


if __name__ == "__main__":
    main()
