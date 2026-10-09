import hashlib
import io
import logging
from types import SimpleNamespace
from urllib.parse import quote
from uuid import uuid4
import zipfile

from fastapi.testclient import TestClient
import pytest

from app.batch_runner import BatchRunner
from app.batch_service import BatchService
from app.main import create_app
from app.studio import StudioService, encode_mp3
from conftest import FakeEngine, make_voice

SECRET_TEXT = "Confidential chapter wording"


@pytest.fixture
def env(tmp_path):
    engine = FakeEngine()
    studio = StudioService(tmp_path / "data", engine)
    batches = BatchService(studio)
    runner = BatchRunner(batches, engine, studio.gate, studio.select_voice, encode_mp3)
    with TestClient(create_app(studio, batches)) as client:
        yield SimpleNamespace(client=client, studio=studio, batches=batches, runner=runner, engine=engine,
                              voice_id=make_voice(client, tmp_path, language="pt"), root=batches.repository.root)


def post(env, files, name="Lumky", voice_id=None):
    return env.client.post("/batches", data={"name": name, "voice_id": voice_id or env.voice_id},
                           files=[("files", (filename, content, "text/plain")) for filename, content in files])


def example_files(count=3):
    return [(f"chapter-{index:03d}.txt", f"{SECRET_TEXT} {index}. Second sentence.".encode()) for index in range(1, count + 1)]


def test_create_list_and_detail_shapes(env):
    response = post(env, [("../InMiltonLumkyTerritoryIntro.txt", b"Intro.")] + example_files(2))
    assert response.status_code == 202
    summary = response.json()
    assert summary["state"] == "queued" and summary["language_id"] == "pt"
    assert summary["track_count"] == 3 and summary["chunk_count"] == 3 and summary["chunks_done"] == 0
    batch_dir = env.root / summary["batch_id"]
    assert sorted(path.name for path in batch_dir.iterdir()) == ["manifest.json", "sources"]
    assert sorted(path.name for path in (batch_dir / "sources").iterdir()) == ["001.txt", "002.txt", "003.txt"]
    assert not any("chapter" in str(path) or "Lumky" in str(path) for path in batch_dir.rglob("*"))
    listing = env.client.get("/batches")
    assert listing.status_code == 200
    assert listing.json() == [summary]
    assert "tracks" not in listing.json()[0] and SECRET_TEXT not in listing.text
    detail = env.client.get(f"/batches/{summary['batch_id']}").json()
    assert detail["batch"] == summary
    assert [track["display_name"] for track in detail["tracks"]] == \
        ["InMiltonLumkyTerritoryIntro.txt", "chapter-001.txt", "chapter-002.txt"]
    assert [track["output_name"] for track in detail["tracks"]] == ["Lumky 001.mp3", "Lumky 002.mp3", "Lumky 003.mp3"]
    for body in (listing.text, env.client.get(f"/batches/{summary['batch_id']}").text):
        assert str(env.studio.root) not in body and "/data" not in body and SECRET_TEXT not in body


def test_dotnet_style_non_ascii_file_name_is_kept_as_label(env):
    boundary = "perene-boundary"
    encoded = "=?utf-8?B?SW50cm9kdcOnw6NvLnR4dA==?="
    body = (
        f"--{boundary}\r\nContent-Disposition: form-data; name=name\r\n\r\nLivro\r\n"
        f"--{boundary}\r\nContent-Disposition: form-data; name=voice_id\r\n\r\n{env.voice_id}\r\n"
        f"--{boundary}\r\nContent-Type: text/plain\r\nContent-Disposition: form-data; name=files; "
        f"filename=\"{encoded}\"; filename*=utf-8''{quote('Introdução.txt')}\r\n\r\nOlá.\r\n"
        f"--{boundary}--\r\n").encode()
    response = env.client.post("/batches", content=body,
                               headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    assert response.status_code == 202, response.text
    detail = env.client.get(f"/batches/{response.json()['batch_id']}").json()
    assert detail["tracks"][0]["display_name"] == "Introdução.txt"


@pytest.mark.parametrize("files,name,status,message", [
    ([("big.txt", b"x" * (1024 * 1024 + 1))], "Lumky", 413, "big.txt: file exceeds 1 MiB"),
    ([("bad.txt", b"\xff\xfe\xfa")], "Lumky", 422, "bad.txt: file is not valid UTF-8"),
    ([("blank.txt", b"  \n ")], "Lumky", 422, "blank.txt: file is empty"),
    ([("cover.jpg", b"jpeg")], "Lumky", 422, "cover.jpg: only .txt"),
    ([("ok.txt", b"Fine.")], "a/b", 422, "cannot contain"),
    ([("ok.txt", b"Fine.")], " ", 422, "1–80"),
])
def test_invalid_batches_are_rejected_without_creating_anything(env, files, name, status, message):
    response = post(env, files, name)
    assert response.status_code == status
    assert message in response.json()["detail"]
    assert list(env.root.iterdir()) == []
    assert list((env.studio.root / "uploads").iterdir()) == []


def test_file_count_and_total_size_limits(env):
    response = post(env, [(f"f{index}.txt", b"x") for index in range(201)])
    assert response.status_code == 422 and "200 files" in response.json()["detail"]
    eleven = [(f"f{index}.txt", b"x" * (1024 * 1024 - 10)) for index in range(11)]
    assert post(env, eleven).status_code == 413
    assert post(env, example_files(), voice_id=str(uuid4())).status_code == 404
    assert post(env, example_files(), voice_id="not-a-uuid").status_code == 422
    assert list(env.root.iterdir()) == []


def test_transitions_and_errors(env):
    batch_id = post(env, example_files()).json()["batch_id"]
    route = f"/batches/{batch_id}"
    assert env.client.post(f"{route}/resume").status_code == 409
    assert env.client.post(f"{route}/pause").json()["state"] == "paused"
    conflict = env.client.post(f"{route}/pause")
    assert conflict.status_code == 409 and conflict.json()["detail"] == "Only queued or running batches can be paused"
    assert env.client.post(f"{route}/resume").json()["state"] == "queued"
    assert env.client.post(f"{route}/stop").json()["state"] == "stopped"
    assert env.client.post(f"{route}/stop").status_code == 409
    assert env.client.post(f"{route}/retry").json()["state"] == "queued"
    assert env.client.post(f"{route}/tracks/1/retry").status_code == 409
    assert env.client.post(f"{route}/tracks/9/retry").status_code == 404
    assert env.client.post(f"{route}/tracks/x/retry").status_code == 422
    env.batches.claim_next(True)
    assert env.client.delete(route).status_code == 409
    env.client.post(f"{route}/pause")
    env.batches.chunk_done(batch_id, 1, 0, 0.0)
    assert env.client.get(route).json()["batch"]["state"] == "paused"
    assert env.client.delete(route).status_code == 204
    assert not (env.root / batch_id).exists()
    assert env.client.get(route).status_code == 404
    assert env.client.get(f"/batches/{uuid4()}").status_code == 404
    assert env.client.get(f"/batches/{batch_id.upper()}").status_code == 422
    assert env.client.post("/batches/not-a-uuid/pause").status_code == 422


def test_track_downloads_and_archive(env, caplog):
    caplog.set_level(logging.DEBUG)
    env.engine.fail_when = lambda chunks: "wording 2." in chunks[0].text
    batch_id = post(env, example_files(), name="Lumky ção").json()["batch_id"]
    assert env.runner.run_once()
    detail = env.client.get(f"/batches/{batch_id}").json()
    assert [track["state"] for track in detail["tracks"]] == ["completed", "failed", "completed"]
    audio = env.client.get(f"/batches/{batch_id}/tracks/1/audio")
    assert audio.status_code == 200 and audio.headers["content-type"] == "audio/mpeg"
    assert quote("Lumky ção 001.mp3") in audio.headers["content-disposition"]
    assert env.client.get(f"/batches/{batch_id}/tracks/2/audio").status_code == 404
    assert env.client.get(f"/batches/{batch_id}/tracks/0/audio").status_code == 404
    assert env.client.get(f"/batches/{batch_id}/tracks/4/audio").status_code == 404
    archive = env.client.get(f"/batches/{batch_id}/archive")
    assert archive.status_code == 200 and archive.headers["content-type"] == "application/zip"
    assert quote("Lumky ção.zip") in archive.headers["content-disposition"]
    with zipfile.ZipFile(io.BytesIO(archive.content)) as content:
        assert content.namelist() == ["Lumky ção 001.mp3", "Lumky ção 003.mp3"]
        for number, name in ((1, "Lumky ção 001.mp3"), (3, "Lumky ção 003.mp3")):
            expected = env.batches.repository.load(batch_id).tracks[number - 1].output_sha256
            assert hashlib.sha256(content.read(name)).hexdigest() == expected
    logs = caplog.text
    assert SECRET_TEXT not in logs and "Second sentence" not in logs
    assert f"Batch {batch_id} track 001 chunk 1/1" in logs


def test_archive_without_completed_tracks_is_not_found(env):
    batch_id = post(env, example_files(1)).json()["batch_id"]
    assert env.client.get(f"/batches/{batch_id}/archive").status_code == 404


def test_disabled_batches_reject_creation_but_keep_downloads(env):
    batch_id = post(env, example_files(1)).json()["batch_id"]
    env.runner.run_once()
    disabled = BatchService(env.studio, enabled=False)
    with TestClient(create_app(env.studio, disabled)) as client:
        response = client.post("/batches", data={"name": "Nope", "voice_id": env.voice_id},
                               files=[("files", ("a.txt", b"Text.", "text/plain"))])
        assert response.status_code == 503
        assert client.get("/health").json()["batch_enabled"] is False
        assert client.get("/batches").json()[0]["batch_id"] == batch_id
        assert client.get(f"/batches/{batch_id}/tracks/1/audio").status_code == 200
        assert client.get(f"/batches/{batch_id}/archive").status_code == 200
    assert env.client.get("/health").json()["batch_enabled"] is True
