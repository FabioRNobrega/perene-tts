from pathlib import Path
import threading
import time
from uuid import uuid4

from fastapi.testclient import TestClient
import pytest

from app.engine import SynthesisError, SynthesisResult
from app.main import create_app
from app.studio import MAX_UPLOAD, PREVIEWS, StudioService, ffmpeg
from conftest import write_wav


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
        self.block = None
        self.fail = False

    def prepare_conditioning(self, path):
        self.prepared += 1

    def save_conditioning(self, path):
        path.write_bytes(b"fake-conditioning")

    def load_conditioning(self, path):
        assert path.read_bytes() == b"fake-conditioning"
        self.loaded += 1

    def synthesize(self, chunks, output, language, *args):
        if self.block is not None:
            assert self.block.wait(10)
        if self.fail:
            raise SynthesisError("private internal path /secret")
        self.calls.append((language, " ".join(chunk.text for chunk in chunks)))
        write_wav(output)
        args[-1](len(chunks), len(chunks))
        return SynthesisResult(16000, 3.1, len(chunks))


@pytest.fixture
def environment(tmp_path):
    engine = FakeEngine()
    studio = StudioService(tmp_path / "data", engine)
    with TestClient(create_app(studio)) as client:
        yield client, studio, engine


def recording(tmp_path, duration=3.5, sample=1000):
    wav = tmp_path / "source.wav"
    mp3 = tmp_path / "source.mp3"
    write_wav(wav, duration, sample)
    ffmpeg("-i", str(wav), str(mp3))
    return mp3.read_bytes()


def wait(client, job):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        result = client.get(f"/jobs/{job['job_id']}")
        assert result.status_code == 200
        if result.json()["status"] != "running":
            return result.json()
        time.sleep(0.01)
    pytest.fail("Job did not finish")


def create(client, content, language="en", name="Reading voice"):
    return client.post("/voices", data={"name": name, "language": language},
                       files={"audio": ("reference.mp3", content, "audio/mpeg")})


@pytest.mark.parametrize("language", ["en", "pt", "sv"])
def test_voice_preview_speech_and_restart(environment, tmp_path, language):
    client, studio, engine = environment
    content = recording(tmp_path)
    response = create(client, content, language)
    assert response.status_code == 202
    preview = wait(client, response.json())
    assert preview["status"] == "completed", preview
    assert engine.calls[-1] == (language, PREVIEWS[language])
    audio = client.get(preview["audio_url"])
    assert audio.status_code == 200 and audio.headers["content-type"] == "audio/mpeg"
    assert len(audio.content) > 100
    # Same recording/language reuses archived voice and conditioning, including original name.
    second = wait(client, create(client, content, language, "Changed name").json())
    assert second["voice_id"] == preview["voice_id"]
    assert engine.prepared == 1
    voice = client.get("/voices").json()[0]
    assert voice["name"] == "Reading voice" and voice["language_id"] == language
    spoken = client.post("/speech", json={"voice_id": voice["voice_id"], "text": "A new phrase."})
    generated = wait(client, spoken.json())
    assert generated["status"] == "completed"
    assert engine.calls[-1] == (language, "A new phrase.")
    restarted = StudioService(studio.root, FakeEngine())
    creations = client.get("/creations").json()
    assert len(creations) == 3
    assert creations[0]["job_id"] == generated["job_id"]
    assert creations[0]["kind"] == "speech" and creations[0]["text"] == "A new phrase."
    assert creations[0]["voice_name"] == "Reading voice" and creations[0]["language_id"] == language
    assert creations[-1]["kind"] == "preview" and creations[-1]["text"] == PREVIEWS[language]
    assert restarted.creations() == creations
    with TestClient(create_app(restarted)) as restarted_client:
        assert restarted_client.get("/creations").json() == creations
        assert restarted_client.get(f"/audio/{generated['job_id']}").status_code == 200
    assert restarted.voices() == studio.voices()
    assert restarted.audio_path(preview["job_id"]).read_bytes() == audio.content
    assert not list((studio.root / "uploads").iterdir())


@pytest.mark.parametrize("duration,sample", [(1, 1000), (61, 1000), (4, 0)])
def test_invalid_reference_fails_without_publishing(environment, tmp_path, duration, sample):
    client, studio, _ = environment
    job = create(client, recording(tmp_path, duration, sample)).json()
    failed = wait(client, job)
    assert failed["status"] == "failed"
    assert client.get("/voices").json() == []
    assert not list((studio.root / "uploads").iterdir())
    assert not list((studio.root / "outputs").iterdir())


def test_input_validation(environment, tmp_path):
    client, studio, _ = environment
    assert create(client, b"anything", "xx").status_code == 422
    assert create(client, b"anything", name=" ").status_code == 422
    assert create(client, b"").status_code == 422
    assert create(client, b"x" * (MAX_UPLOAD + 1)).status_code == 413
    assert client.post("/voices", data={"name": "a", "language": "en"}, files={"audio": ("x.pt", b"x")}).status_code == 422
    malformed = wait(client, create(client, b"not an MP3").json())
    assert malformed["status"] == "failed"
    for text in (" ", "x" * 5001):
        assert client.post("/speech", json={"voice_id": str(uuid4()), "text": text}).status_code == 422
    assert client.post("/speech", json={"voice_id": str(uuid4()), "text": "Hello"}).status_code == 404
    assert client.get("/jobs/not-a-uuid").status_code == 422
    assert client.get(f"/audio/{uuid4()}").status_code == 404
    assert not list((studio.root / "uploads").iterdir())


def test_busy_unready_failure_and_recovery(environment, tmp_path):
    client, studio, engine = environment
    engine.ready = False
    assert create(client, recording(tmp_path)).status_code == 503
    engine.ready = True
    engine.block = threading.Event()
    first = create(client, recording(tmp_path)).json()
    assert create(client, recording(tmp_path)).status_code == 409
    engine.block.set()
    assert wait(client, first)["status"] == "completed"
    voice = studio.voices()[0]
    engine.fail = True
    failed = wait(client, client.post("/speech", json={"voice_id": voice["voice_id"], "text": "Hello"}).json())
    assert failed["status"] == "failed" and "/secret" not in failed["error"]
    assert client.get(f"/audio/{failed['job_id']}").status_code == 404
    assert len(client.get("/creations").json()) == 1
    engine.fail = False
    recovered = wait(client, client.post("/speech", json={"voice_id": voice["voice_id"], "text": "Hello again"}).json())
    assert recovered["status"] == "completed"


def test_conditioning_corruption_regenerates_same_voice(environment, tmp_path):
    client, studio, engine = environment
    preview = wait(client, create(client, recording(tmp_path)).json())
    studio.store.conditioning_path(preview["voice_id"]).write_bytes(b"tampered")
    job = wait(client, client.post("/speech", json={"voice_id": preview["voice_id"], "text": "Hello"}).json())
    assert job["status"] == "completed" and job["voice_id"] == preview["voice_id"]
    assert engine.prepared == 2


def test_legacy_audio_and_corrupt_metadata_remain_browsable(environment):
    client, studio, _ = environment
    audio = studio.root / "outputs" / f"{uuid4()}.mp3"
    audio.write_bytes(b"legacy-mp3")
    ignored = audio.with_name("not-a-uuid.mp3")
    ignored.write_bytes(b"ignored")
    for sidecar in (None, "bad json", "null", "[]"):
        if sidecar is not None:
            audio.with_suffix(".json").write_text(sidecar)
        creations = client.get("/creations").json()
        assert len(creations) == 1
        assert creations[0]["job_id"] == audio.stem
        assert creations[0]["voice_name"] == "Earlier creation"
        assert client.get(f"/audio/{audio.stem}").content == b"legacy-mp3"


def test_english_preview_matches_requested_text():
    assert PREVIEWS["en"] == "Hello! Welcome to PereneTTS. This is a quick voice test to hear how natural and clear my voice sounds."
