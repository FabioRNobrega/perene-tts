import io
import json
import re
import socket
import sys
import threading
import types

import pytest
from fastapi.testclient import TestClient

from app import studio
from app.desktop import READY_PREFIX, bind_loopback, desktop_main
from app.engine import BUNDLED_MODEL_ERROR, MODEL_FILES, MODEL_REPOSITORY, ChatterboxEngine, SynthesisError, verify_bundled_model
from app.main import ApiToken, create_app
from app.studio import MODEL_REVISION, StudioService
from conftest import FakeEngine


def client_for(tmp_path, monkeypatch, token=None):
    if token is None:
        monkeypatch.delenv("TTS_API_TOKEN", raising=False)
    else:
        monkeypatch.setenv("TTS_API_TOKEN", token)
    return TestClient(create_app(StudioService(tmp_path, FakeEngine())))


@pytest.mark.parametrize("headers", [{}, {"X-Perene-Token": "abd"}, {"X-Perene-Token": "abcdef"}, {"X-Perene-Token": ""}])
def test_token_rejects_missing_wrong_or_different_length_headers(tmp_path, monkeypatch, headers):
    with client_for(tmp_path, monkeypatch, "abc") as client:
        response = client.get("/health", headers=headers)
    assert response.status_code == 401
    assert response.json() == {"detail": "Unauthorized"}


def test_token_allows_the_matching_header(tmp_path, monkeypatch):
    with client_for(tmp_path, monkeypatch, "abc") as client:
        response = client.get("/health", headers={"X-Perene-Token": "abc"})
    assert response.status_code == 200
    assert response.json()["model_ready"] is True


def test_without_token_setting_the_middleware_is_absent(tmp_path, monkeypatch):
    with client_for(tmp_path, monkeypatch) as client:
        assert all(middleware.cls is not ApiToken for middleware in client.app.user_middleware)
        assert client.get("/health").status_code == 200


class FakeServer:
    def __init__(self, events, stdout):
        self.should_exit = False
        self.events = events
        self.stdout = stdout
        self.sockets = None

    def run(self, sockets=None):
        self.events.append(("run", self.stdout.getvalue()))
        self.sockets = sockets


def test_ready_line_is_written_before_the_server_runs_and_names_the_bound_port():
    events, stdout = [], io.StringIO()
    servers = []

    def factory():
        servers.append(FakeServer(events, stdout))
        return servers[0]

    assert desktop_main(io.BytesIO(), stdout, factory) == 0
    sock = servers[0].sockets[0]
    try:
        host, port = sock.getsockname()[:2]
        assert host == "127.0.0.1"
        assert re.fullmatch(rf"{READY_PREFIX} port=\d+\n", events[0][1])
        assert events[0][1] == f"{READY_PREFIX} port={port}\n"
    finally:
        sock.close()


def test_bound_socket_listens_only_on_loopback():
    sock = bind_loopback()
    try:
        assert sock.family == socket.AF_INET
        assert sock.getsockname()[0] == "127.0.0.1"
        assert sock.getsockname()[1] > 0
    finally:
        sock.close()


def test_end_of_stdin_asks_the_server_to_exit():
    exited = threading.Event()

    class WaitingServer:
        should_exit = False

        def run(self, sockets=None):
            # Uvicorn polls should_exit; this fake waits for the watcher thread to set it.
            for _ in range(500):
                if self.should_exit:
                    exited.set()
                    break
                threading.Event().wait(0.01)
            for sock in sockets:
                sock.close()

    desktop_main(io.BytesIO(b"keep-alive bytes"), io.StringIO(), WaitingServer)
    assert exited.is_set()


def test_desktop_entry_point_logs_setting_names_but_never_values(monkeypatch, caplog):
    monkeypatch.setenv("TTS_API_TOKEN", "secret-token-value")

    class NoopServer:
        should_exit = False

        def run(self, sockets=None):
            sockets[0].close()

    with caplog.at_level("INFO", logger="perene.desktop"):
        desktop_main(io.BytesIO(), io.StringIO(), NoopServer)
    assert "TTS_API_TOKEN" in caplog.text
    assert "secret-token-value" not in caplog.text


def write_model(tmp_path, revision=MODEL_REVISION, repository=MODEL_REPOSITORY, sizes=None):
    files = {}
    for index, name in enumerate(MODEL_FILES):
        data = b"x" * (index + 1)
        (tmp_path / name).write_bytes(data)
        files[name] = {"size": (sizes or {}).get(name, len(data)), "sha256": "0" * 64}
    (tmp_path / "model-manifest.json").write_text(
        json.dumps({"repository": repository, "revision": revision, "files": files}), encoding="utf-8")
    return tmp_path


def test_verify_bundled_model_accepts_matching_files(tmp_path):
    assert verify_bundled_model(write_model(tmp_path), MODEL_REVISION) == tmp_path


@pytest.mark.parametrize("damage", ["missing", "size", "revision", "repository", "malformed", "no-manifest"])
def test_verify_bundled_model_rejects_damage(tmp_path, damage):
    write_model(tmp_path,
                revision="0" * 40 if damage == "revision" else MODEL_REVISION,
                repository="someone/else" if damage == "repository" else MODEL_REPOSITORY,
                sizes={MODEL_FILES[0]: 999} if damage == "size" else None)
    if damage == "missing":
        (tmp_path / MODEL_FILES[2]).unlink()
    if damage == "malformed":
        (tmp_path / "model-manifest.json").write_text("{not json", encoding="utf-8")
    if damage == "no-manifest":
        (tmp_path / "model-manifest.json").unlink()
    with pytest.raises(SynthesisError, match="bundled voice model is missing or damaged"):
        verify_bundled_model(tmp_path, MODEL_REVISION)


def install_stub_model(monkeypatch, received):
    def refuse_download(**kwargs):
        raise AssertionError("snapshot_download must not run with a bundled model")

    class StubTTS:
        @classmethod
        def from_local(cls, checkpoint_dir, device, t3_model):
            received.append((checkpoint_dir, device, t3_model))
            return cls()

    hub = types.ModuleType("huggingface_hub")
    hub.snapshot_download = refuse_download
    package = types.ModuleType("chatterbox")
    package.__path__ = []
    mtl = types.ModuleType("chatterbox.mtl_tts")
    mtl.ChatterboxMultilingualTTS = StubTTS
    monkeypatch.setitem(sys.modules, "huggingface_hub", hub)
    monkeypatch.setitem(sys.modules, "chatterbox", package)
    monkeypatch.setitem(sys.modules, "chatterbox.mtl_tts", mtl)


def test_engine_loads_from_the_bundled_directory_without_downloading(tmp_path, monkeypatch):
    received = []
    install_stub_model(monkeypatch, received)
    engine = ChatterboxEngine(MODEL_REVISION, device="mps", model_dir=write_model(tmp_path))
    engine.load()
    assert engine.ready and engine.load_error is None
    assert received == [(str(tmp_path), "mps", "v3")]


def test_engine_reports_a_damaged_bundle_through_load_error(tmp_path, monkeypatch):
    received = []
    install_stub_model(monkeypatch, received)
    write_model(tmp_path)
    (tmp_path / MODEL_FILES[1]).write_bytes(b"truncated-but-longer")
    engine = ChatterboxEngine(MODEL_REVISION, model_dir=tmp_path)
    engine.load()
    assert not engine.ready and received == []
    assert engine.load_error == BUNDLED_MODEL_ERROR


@pytest.mark.parametrize("configured, expected", [("/opt/ff/ffmpeg", "/opt/ff/ffmpeg"), (None, "ffmpeg")])
def test_ffmpeg_uses_the_configured_executable(monkeypatch, configured, expected):
    calls = []
    if configured is None:
        monkeypatch.delenv("TTS_FFMPEG", raising=False)
    else:
        monkeypatch.setenv("TTS_FFMPEG", configured)
    monkeypatch.setattr(studio.subprocess, "run", lambda argv, **kwargs: calls.append(argv))
    studio.ffmpeg("-version")
    assert calls[0][0] == expected
    assert calls[0][-1] == "-version"


def test_ffmpeg_reports_a_missing_configured_executable(monkeypatch, tmp_path):
    monkeypatch.setenv("TTS_FFMPEG", str(tmp_path / "missing-ffmpeg"))
    with pytest.raises(ValueError, match="Audio conversion failed"):
        studio.ffmpeg("-version")
