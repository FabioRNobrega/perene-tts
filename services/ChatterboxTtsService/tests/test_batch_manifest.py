from dataclasses import asdict, replace
import json
from uuid import uuid4

import pytest

from app.batch_manifest import BatchManifest, BatchManifestError, BatchRepository, BatchTrack

REVISION = "a" * 40
DIGEST = "b" * 64


def manifest(batch_id=None, **values):
    tracks = (BatchTrack(1, "Intro.txt", DIGEST, 3), BatchTrack(2, "chapter-001.txt", DIGEST, 5))
    base = BatchManifest(1, batch_id or str(uuid4()), "Lumky", str(uuid4()), "Reader", "en", REVISION, REVISION,
                         2, 1234, "queued", "Waiting to start", "2026-10-08T00:00:00+00:00",
                         "2026-10-08T00:00:00+00:00", 0.0, 0, tracks)
    return replace(base, **values)


def saved(repository, value):
    repository.batch_dir(value.batch_id).mkdir()
    repository.save(value)
    return value


def test_round_trip_save_and_load(tmp_path):
    repository = BatchRepository(tmp_path / "batches")
    value = saved(repository, manifest())
    assert repository.load(value.batch_id) == value
    completed = replace(value, state="completed", tracks=(
        replace(value.tracks[0], state="completed", chunks_done=3, output_sha256=DIGEST, duration_seconds=1.5,
                completed_at="2026-10-08T01:00:00+00:00"), value.tracks[1]))
    repository.save(completed)
    assert repository.load(value.batch_id) == completed
    assert [item.batch_id for item in repository.list()] == [value.batch_id]


def test_exact_field_set_is_required(tmp_path):
    repository = BatchRepository(tmp_path / "batches")
    value = saved(repository, manifest())
    path = repository.manifest_path(value.batch_id)
    for mutate in (lambda raw: raw.update(extra=1), lambda raw: raw.pop("message"),
                   lambda raw: raw["tracks"][0].update(extra=1), lambda raw: raw["tracks"][0].pop("error")):
        raw = asdict(value)
        mutate(raw)
        path.write_text(json.dumps(raw))
        with pytest.raises(BatchManifestError):
            repository.load(value.batch_id)


@pytest.mark.parametrize("change", [
    {"state": "sleeping"},
    {"language_id": "de"},
    {"model_revision": "xyz"},
    {"synthesis_seed": True},
    {"chunks_done_total": -1},
    {"tracks": ()},
])
def test_invalid_batch_values_are_rejected(change):
    with pytest.raises(ValueError):
        manifest(**change).validate()


@pytest.mark.parametrize("track_change", [
    {"number": 2},
    {"state": "done"},
    {"source_sha256": "nothex"},
    {"chunks_done": 4},
    {"state": "completed"},
    {"output_sha256": DIGEST},
])
def test_invalid_tracks_are_rejected(track_change):
    value = manifest()
    with pytest.raises(ValueError):
        replace(value, tracks=(replace(value.tracks[0], **track_change), value.tracks[1])).validate()


def test_failed_save_keeps_previous_manifest(tmp_path, monkeypatch):
    repository = BatchRepository(tmp_path / "batches")
    value = saved(repository, manifest())
    before = repository.manifest_path(value.batch_id).read_bytes()
    with pytest.raises(ValueError):
        repository.save(replace(value, state="unknown"))

    def interrupted(data, stream, **options):
        stream.write('{"partial": ')
        raise OSError("disk full")

    monkeypatch.setattr("app.batch_manifest.json.dump", interrupted)
    with pytest.raises(OSError):
        repository.save(replace(value, state="paused"))
    monkeypatch.undo()
    assert repository.manifest_path(value.batch_id).read_bytes() == before
    assert [path.name for path in repository.batch_dir(value.batch_id).iterdir()] == ["manifest.json"]


def test_paths_are_contained_and_canonical(tmp_path):
    repository = BatchRepository(tmp_path / "batches")
    batch_id = str(uuid4())
    assert repository.source_path(batch_id, 1) == repository.root / batch_id / "sources" / "001.txt"
    assert repository.track_path(batch_id, 18).name == "018.mp3"
    for bad in ("../escape", batch_id.upper(), "not-a-uuid"):
        with pytest.raises(ValueError):
            repository.batch_dir(bad)
    outside = tmp_path / "outside"
    outside.mkdir()
    (repository.root / batch_id).symlink_to(outside, target_is_directory=True)
    with pytest.raises(BatchManifestError):
        repository.batch_dir(batch_id)
    assert repository.list() == []


def test_unreadable_batches_are_skipped(tmp_path):
    repository = BatchRepository(tmp_path / "batches")
    good = saved(repository, manifest(created_at="2026-10-08T00:00:00+00:00"))
    newer = saved(repository, manifest(created_at="2026-10-09T00:00:00+00:00"))
    broken = repository.batch_dir(str(uuid4()))
    broken.mkdir()
    (broken / "manifest.json").write_text("{")
    (repository.root / ".new-staging").mkdir()
    assert [item.batch_id for item in repository.list()] == [newer.batch_id, good.batch_id]
