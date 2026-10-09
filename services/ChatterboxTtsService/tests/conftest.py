from pathlib import Path
import struct
import time
import wave

import pytest

from app.engine import SynthesisError, SynthesisResult


def write_wav(path: Path, duration_seconds: float = 3.1, sample: int = 1000):
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(16000)
        frames = int(16000 * duration_seconds)
        audio.writeframes(struct.pack('<h', sample) * frames)


class FakeEngine:
    ready = True
    load_error = None
    device = "cpu"
    model_name = "multilingual-v3"
    model_revision = "5bb1f6ee58e50c3b8d408bc82a6d3740c2db6e18"

    def __init__(self):
        self.prepared = 0
        self.loaded = 0
        self.calls = []
        self.offsets = []
        self.block = None
        self.fail = False
        self.fail_when = None
        self.before = None

    def prepare_conditioning(self, path):
        self.prepared += 1

    def save_conditioning(self, path):
        path.write_bytes(b"fake-conditioning")

    def load_conditioning(self, path):
        assert path.read_bytes() == b"fake-conditioning"
        self.loaded += 1

    def synthesize(self, chunks, output, language, sentence_ms, paragraph_ms, seed, progress=None, *, seed_offset=0):
        if self.before is not None:
            self.before(chunks, seed_offset)
        if self.block is not None:
            assert self.block.wait(10)
        if self.fail or (self.fail_when is not None and self.fail_when(chunks)):
            raise SynthesisError("private internal path /secret")
        self.calls.append((language, " ".join(chunk.text for chunk in chunks)))
        self.offsets.append(seed_offset)
        write_wav(output)
        if progress is not None:
            progress(len(chunks), len(chunks))
        return SynthesisResult(16000, 3.1, len(chunks))


def wait(client, job):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        result = client.get(f"/jobs/{job['job_id']}")
        assert result.status_code == 200
        if result.json()["status"] != "running":
            return result.json()
        time.sleep(0.01)
    pytest.fail("Job did not finish")


def make_voice(client, tmp_path, language="en", name="Reading voice"):
    source = tmp_path / f"voice-{language}.wav"
    write_wav(source, 3.5, 1000)
    response = client.post("/voices", data={"name": name, "language": language},
                           files={"audio": ("reference.wav", source.read_bytes(), "application/octet-stream")})
    job = wait(client, response.json())
    assert job["status"] == "completed", job
    return job["voice_id"]
