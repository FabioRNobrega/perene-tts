from pathlib import Path
import threading
import time
from uuid import uuid4

from fastapi.testclient import TestClient
import pytest

from app.batch_runner import BatchRunner
from app.batch_service import BatchService
from app.main import BATCH_CREATION_FIELDS, create_app
from app.studio import MAX_UPLOAD, PREVIEWS, WAITING_FOR_BATCH, StudioService, encode_mp3, ffmpeg
from conftest import FakeEngine, make_voice, wait, write_wav


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


def create(client, content, language="en", name="Reading voice", filename="reference.mp3"):
    return client.post("/voices", data={"name": name, "language": language},
                       files={"audio": (filename, content, "application/octet-stream")})


def test_wav_reference_creates_voice(environment, tmp_path):
    client, studio, engine = environment
    wav = tmp_path / "source.wav"
    write_wav(wav, 3.5, 1000)
    preview = wait(client, create(client, wav.read_bytes(), filename="Reference.WAV").json())
    assert preview["status"] == "completed", preview
    assert engine.calls[-1] == ("en", PREVIEWS["en"])
    assert client.get(preview["audio_url"]).headers["content-type"] == "audio/mpeg"
    assert client.get("/voices").json()[0]["voice_id"] == preview["voice_id"]
    assert not list((studio.root / "uploads").iterdir())


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
    assert all(entry[key] is None for entry in creations for key in BATCH_CREATION_FIELDS)
    assert [{key: value for key, value in entry.items() if key not in BATCH_CREATION_FIELDS}
            for entry in creations] == restarted.creations()
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
    assert create(client, b"anything", filename="reference.ogg").status_code == 422
    for filename in ("reference.mp3", "reference.wav"):
        malformed = wait(client, create(client, b"not audio", filename=filename).json())
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


def batch_environment(tmp_path):
    engine = FakeEngine()
    studio = StudioService(tmp_path / "data", engine)
    batches = BatchService(studio)
    runner = BatchRunner(batches, engine, studio.gate, studio.select_voice, encode_mp3)
    return engine, studio, batches, runner


def post_batch(client, voice_id, texts, name="Lumky"):
    files = [("files", (f"chapter-{index:03d}.txt", text.encode(), "text/plain"))
             for index, text in enumerate(texts, start=1)]
    return client.post("/batches", data={"name": name, "voice_id": voice_id}, files=files)


def test_interactive_jobs_run_with_priority_during_a_batch(tmp_path):
    engine, studio, batches, runner = batch_environment(tmp_path)
    with TestClient(create_app(studio, batches)) as client:
        voice_id = make_voice(client, tmp_path)
        loads_before = engine.loaded
        batch = post_batch(client, voice_id, ["First chunk. " * 30 + "\n\n" + "Second paragraph."]).json()
        in_chunk, release = threading.Event(), threading.Event()
        jobs = {}

        def first_chunk_holds_model(chunks, offset):
            if offset == 0 and not in_chunk.is_set():
                in_chunk.set()
                assert release.wait(10)

        engine.before = first_chunk_holds_model
        worker = threading.Thread(target=runner.run_once)
        worker.start()
        assert in_chunk.wait(10)
        engine.before = None
        response = client.post("/voices", data={"name": "Second", "language": "sv"},
                               files={"audio": ("reference.mp3", recording(tmp_path), "application/octet-stream")})
        assert response.status_code == 202
        jobs["voice"] = response.json()
        deadline = time.monotonic() + 5
        while client.get(f"/jobs/{jobs['voice']['job_id']}").json()["message"] != WAITING_FOR_BATCH:
            assert time.monotonic() < deadline
            time.sleep(0.01)
        # Only a second interactive operation is busy; the batch never causes 409.
        assert client.post("/speech", json={"voice_id": voice_id, "text": "Hello"}).status_code == 409
        release.set()
        assert wait(client, jobs["voice"])["status"] == "completed"
        worker.join(15)
        assert not worker.is_alive()
        detail = client.get(f"/batches/{batch['batch_id']}").json()
        assert detail["batch"]["state"] == "completed"
        # The preview took the model between chunks, so the batch reloaded its own voice afterwards.
        assert engine.loaded >= loads_before + 2
        assert studio.gate.conditioning_owner == voice_id


def test_creations_include_completed_batch_tracks_without_text(tmp_path):
    engine, studio, batches, runner = batch_environment(tmp_path)
    with TestClient(create_app(studio, batches)) as client:
        voice_id = make_voice(client, tmp_path)
        engine.fail_when = lambda chunks: "Broken" in chunks[0].text
        batch = post_batch(client, voice_id, ["One. Chapter text.", "Broken chapter.", "Three."]).json()
        assert runner.run_once()
        creations = client.get("/creations").json()
        tracks = [entry for entry in creations if entry["kind"] == "batch"]
        assert sorted(entry["track_number"] for entry in tracks) == [1, 3]
        assert {entry["job_id"] for entry in tracks} == {f"{batch['batch_id']}-001", f"{batch['batch_id']}-003"}
        for entry in tracks:
            assert entry["text"] == "" and entry["batch_id"] == batch["batch_id"]
            assert entry["batch_name"] == "Lumky" and entry["track_count"] == 3
            assert entry["source_name"] == f"chapter-{entry['track_number']:03d}.txt"
            assert entry["voice_name"] == "Reading voice" and entry["language_id"] == "en"
        preview = next(entry for entry in creations if entry["kind"] == "preview")
        assert preview["batch_id"] is None and preview["track_number"] is None
        assert [entry["created_at"] for entry in creations] == sorted(
            (entry["created_at"] for entry in creations), reverse=True)
        assert "Chapter text" not in client.get("/creations").text
        client.delete(f"/batches/{batch['batch_id']}")
        assert all(entry["kind"] != "batch" for entry in client.get("/creations").json())
