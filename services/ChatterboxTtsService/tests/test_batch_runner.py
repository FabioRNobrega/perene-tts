from dataclasses import replace
import json
from types import SimpleNamespace

from fastapi.testclient import TestClient
import pytest

from app.batch_runner import GENERIC_FAILURE, BatchRunner
from app.batch_service import BatchService, InvalidTransition
from app.main import create_app
from app.studio import StudioService, encode_mp3
from app.voice_store import sha256_file
from conftest import FakeEngine, make_voice


def chapter(label, chunks=3):
    # Each sentence is ~240 characters, so every sentence becomes its own 280-character chunk.
    return " ".join(f"{label} sentence {index} " + "word " * 45 + "end." for index in range(chunks))


@pytest.fixture
def env(tmp_path):
    engine = FakeEngine()
    studio = StudioService(tmp_path / "data", engine)
    batches = BatchService(studio)
    runner = BatchRunner(batches, engine, studio.gate, studio.select_voice, encode_mp3)
    with TestClient(create_app(studio, batches)) as client:
        voice_id = make_voice(client, tmp_path)
        engine.calls.clear()
        engine.offsets.clear()
        yield SimpleNamespace(engine=engine, studio=studio, batches=batches, runner=runner, voice_id=voice_id,
                              tmp=tmp_path, repository=batches.repository)


def create(env, texts, name="Lumky"):
    uploads = []
    for index, text in enumerate(texts, start=1):
        path = env.tmp / f"upload-{name}-{index}.txt"
        path.write_text(text, encoding="utf-8")
        uploads.append((f"chapter-{index:03d}.txt", path))
    return env.batches.create(name, env.voice_id, uploads)["batch_id"]


def manifest(env, batch_id):
    return env.repository.load(batch_id)


def track_texts(env, label):
    return [text for _, text in env.engine.calls if text.startswith(label)]


def test_happy_path_publishes_one_verified_mp3_per_file(env):
    batch_id = create(env, [chapter("ONE"), chapter("TWO", 2), chapter("THREE", 1)])
    assert env.runner.run_once()
    result = manifest(env, batch_id)
    assert result.state == "completed"
    assert env.engine.offsets == [0, 1, 2, 0, 1, 0]
    for track in result.tracks:
        path = env.repository.track_path(batch_id, track.number)
        assert track.state == "completed" and track.chunks_done == track.chunk_count
        assert sha256_file(path) == track.output_sha256 and track.duration_seconds > 0
        assert not env.repository.work_dir(batch_id, track.number).exists()
    assert sorted(path.name for path in env.repository.batch_dir(batch_id).iterdir()) == \
        ["manifest.json", "sources", "tracks", "work"]
    assert result.chunks_done_total == 6 and result.chunk_seconds_total >= 0
    assert not env.runner.run_once()


def test_batches_run_one_after_another_in_creation_order(env):
    first = create(env, [chapter("FIRST", 1)], "First")
    second = create(env, [chapter("SECOND", 1)], "Second")
    assert env.runner.run_once()
    assert manifest(env, first).state == "completed" and manifest(env, second).state == "queued"
    assert env.runner.run_once()
    assert [text.split()[0] for _, text in env.engine.calls] == ["FIRST", "SECOND"]


def test_pause_at_chunk_boundary_then_resume_without_duplicates(env):
    batch_id = create(env, [chapter("ONE"), chapter("TWO")])

    def pause_during_second_chunk(chunks, offset):
        if offset == 1 and not env.engine.offsets[3:]:
            env.batches.pause(batch_id)
            assert manifest(env, batch_id).state == "pausing"

    env.engine.before = pause_during_second_chunk
    assert env.runner.run_once()
    env.engine.before = None
    paused = manifest(env, batch_id)
    assert paused.state == "paused"
    assert paused.tracks[0].state == "pending" and paused.tracks[0].chunks_done == 2
    assert env.batches.summary(paused)["current_track_number"] == 1
    assert env.engine.offsets == [0, 1]
    assert not env.runner.run_once()
    assert env.batches.resume(batch_id)["state"] == "queued"
    assert env.runner.run_once()
    assert env.engine.offsets == [0, 1, 2, 0, 1, 2]
    assert len(track_texts(env, "ONE")) == 3
    assert manifest(env, batch_id).state == "completed"


def test_stop_discards_partial_track_and_keeps_completed_tracks(env):
    batch_id = create(env, [chapter("ONE", 1), chapter("TWO"), chapter("THREE", 1)])

    def stop_in_track_two(chunks, offset):
        if chunks[0].text.startswith("TWO") and offset == 1:
            env.batches.stop(batch_id)

    env.engine.before = stop_in_track_two
    assert env.runner.run_once()
    stopped = manifest(env, batch_id)
    assert stopped.state == "stopped"
    assert stopped.tracks[0].state == "completed"
    assert stopped.tracks[1].state == "pending" and stopped.tracks[1].chunks_done == 0
    assert env.repository.track_path(batch_id, 1).is_file()
    assert not env.repository.track_path(batch_id, 2).exists()
    assert not env.repository.work_dir(batch_id, 2).exists()
    with pytest.raises(InvalidTransition):
        env.batches.stop(batch_id)


def test_failed_track_continues_then_retry_regenerates_only_unverified_tracks(env):
    batch_id = create(env, [chapter("ONE", 1), chapter("TWO", 1), chapter("THREE", 1)])
    env.engine.fail_when = lambda chunks: chunks[0].text.startswith("TWO")
    assert env.runner.run_once()
    failed = manifest(env, batch_id)
    assert failed.state == "failed" and "1 track failed" in failed.message
    assert [track.state for track in failed.tracks] == ["completed", "failed", "completed"]
    assert failed.tracks[1].error == GENERIC_FAILURE and "/secret" not in failed.tracks[1].error
    env.engine.fail_when = None
    env.engine.calls.clear()
    assert env.batches.retry(batch_id)["state"] == "queued"
    assert env.runner.run_once()
    assert [text.split()[0] for _, text in env.engine.calls] == ["TWO"]
    assert manifest(env, batch_id).state == "completed"
    with pytest.raises(InvalidTransition):
        env.batches.retry(batch_id)


def test_tampered_completed_track_is_regenerated_on_retry(env):
    batch_id = create(env, [chapter("ONE", 1), chapter("TWO", 1)])
    env.engine.fail_when = lambda chunks: chunks[0].text.startswith("TWO")
    env.runner.run_once()
    env.engine.fail_when = None
    env.repository.track_path(batch_id, 1).write_bytes(b"tampered")
    env.engine.calls.clear()
    env.batches.retry(batch_id)
    env.runner.run_once()
    assert [text.split()[0] for _, text in env.engine.calls] == ["ONE", "TWO"]
    result = manifest(env, batch_id)
    assert sha256_file(env.repository.track_path(batch_id, 1)) == result.tracks[0].output_sha256


def test_single_track_retry_queues_only_that_track(env):
    batch_id = create(env, [chapter("ONE", 1), chapter("TWO", 1), chapter("THREE", 1)])
    env.engine.fail_when = lambda chunks: chunks[0].text.startswith("TWO")
    env.runner.run_once()
    env.engine.fail_when = None
    with pytest.raises(InvalidTransition):
        env.batches.retry_track(batch_id, 1)
    with pytest.raises(KeyError):
        env.batches.retry_track(batch_id, 4)
    assert env.batches.retry_track(batch_id, 2)["state"] == "queued"
    assert [track.state for track in manifest(env, batch_id).tracks] == ["completed", "pending", "completed"]
    env.engine.calls.clear()
    env.runner.run_once()
    assert [text.split()[0] for _, text in env.engine.calls] == ["TWO"]
    assert manifest(env, batch_id).state == "completed"


def test_source_changed_on_disk_fails_its_track(env):
    batch_id = create(env, [chapter("ONE", 1), chapter("TWO", 1)])
    env.repository.source_path(batch_id, 2).write_text("Edited after upload.", encoding="utf-8")
    env.runner.run_once()
    result = manifest(env, batch_id)
    assert result.tracks[0].state == "completed"
    assert result.tracks[1].state == "failed" and "Source text changed" in result.tracks[1].error
    assert result.state == "failed"


def interrupt_after_two_chunks(env, batch_id):
    def pause(chunks, offset):
        if offset == 1 and len(env.engine.offsets) == 1:
            env.batches.pause(batch_id)

    env.engine.before = pause
    env.runner.run_once()
    env.engine.before = None
    env.engine.offsets.clear()


def test_restart_recovery_resumes_from_checkpoints(env):
    running = create(env, [chapter("ONE"), chapter("TWO", 1)], "Running")
    interrupt_after_two_chunks(env, running)
    # Simulate a crash mid-chunk: the manifest still says running.
    crashed = manifest(env, running)
    env.repository.save(replace(crashed, state="running",
                                tracks=(replace(crashed.tracks[0], state="running"), crashed.tracks[1])))
    paused = create(env, [chapter("PAUSED", 1)], "Paused")
    env.batches.pause(paused)
    stopping = create(env, [chapter("STOPPING", 1)], "Stopping")
    env.repository.save(replace(manifest(env, stopping), state="stopping"))

    restarted = BatchService(env.studio)
    restarted.recover()
    assert manifest(env, running).state == "queued"
    assert manifest(env, running).tracks[0].state == "pending"
    assert manifest(env, paused).state == "paused"
    assert manifest(env, stopping).state == "stopped"
    runner = BatchRunner(restarted, env.engine, env.studio.gate, env.studio.select_voice, encode_mp3)
    assert runner.run_once()
    assert env.engine.offsets == [2, 0]
    assert manifest(env, running).state == "completed"
    assert not runner.run_once()


@pytest.mark.parametrize("field,value", [("synthesis_seed", 99), ("voice_id", "00000000-0000-4000-8000-000000000000")])
def test_incompatible_checkpoint_is_discarded(env, field, value):
    batch_id = create(env, [chapter("ONE")])
    interrupt_after_two_chunks(env, batch_id)
    marker = env.repository.work_dir(batch_id, 1) / "checkpoint.json"
    identity = json.loads(marker.read_text())
    identity[field] = value
    marker.write_text(json.dumps(identity))
    env.batches.resume(batch_id)
    env.runner.run_once()
    assert env.engine.offsets == [0, 1, 2]
    assert manifest(env, batch_id).state == "completed"


def test_unreadable_checkpoint_chunk_restarts_from_that_chunk(env):
    batch_id = create(env, [chapter("ONE")])
    interrupt_after_two_chunks(env, batch_id)
    (env.repository.work_dir(batch_id, 1) / "chunk-00001.wav").write_bytes(b"broken")
    env.batches.resume(batch_id)
    env.runner.run_once()
    assert env.engine.offsets == [1, 2]


def test_model_not_ready_keeps_batch_queued(env):
    batch_id = create(env, [chapter("ONE", 1)])
    env.engine.ready = False
    assert not env.runner.run_once()
    waiting = manifest(env, batch_id)
    assert waiting.state == "queued" and waiting.message == "Waiting for the voice engine"
    env.engine.ready = True
    assert env.runner.run_once()
    assert manifest(env, batch_id).state == "completed"


def test_late_progress_never_overwrites_pause_or_stop(env):
    paused_id = create(env, [chapter("ONE")], "Pause")
    assert env.batches.claim_next(True).batch_id == paused_id
    assert env.batches.begin_track(paused_id, 1, 0) == "running"
    env.batches.pause(paused_id)
    assert env.batches.chunk_done(paused_id, 1, 1, 0.5) == "paused"
    result = manifest(env, paused_id)
    assert result.state == "paused" and result.tracks[0].chunks_done == 1
    with pytest.raises(InvalidTransition):
        env.batches.resume(create(env, [chapter("X", 1)], "Other"))
    assert env.batches.stop(paused_id)["state"] == "stopped"
    assert manifest(env, paused_id).tracks[0].chunks_done == 0

    other_id = env.batches.list()[0]["batch_id"]
    env.batches.pause(other_id)
    stopped_id = create(env, [chapter("ONE")], "Stop")
    assert env.batches.claim_next(True).batch_id == stopped_id
    env.batches.begin_track(stopped_id, 1, 0)
    env.batches.stop(stopped_id)
    assert manifest(env, stopped_id).state == "stopping"
    with pytest.raises(InvalidTransition):
        env.batches.delete(stopped_id)
    assert env.batches.complete_track(stopped_id, 1, "c" * 64, 1.0) == "stopped"
    assert manifest(env, stopped_id).tracks[0].state == "completed"


def test_paused_batch_releases_the_model_for_interactive_jobs(env):
    batch_id = create(env, [chapter("ONE")])
    env.batches.pause(batch_id)
    assert not env.studio.gate.interactive_reserved
    with env.studio.gate.batch_step():
        pass
    assert not env.runner.run_once()
