"""Durable batch manifest schema and atomic, contained on-disk repository."""
from __future__ import annotations

from dataclasses import asdict, dataclass
import json
import logging
import os
from pathlib import Path
import shutil
import tempfile

from .studio import canonical_id

SCHEMA_VERSION = 1
MANIFEST_NAME = "manifest.json"
BATCH_STATES = ("queued", "running", "pausing", "paused", "stopping", "stopped", "failed", "completed")
TRACK_STATES = ("pending", "running", "completed", "failed")
LOGGER = logging.getLogger(__name__)


class BatchManifestError(RuntimeError):
    pass


@dataclass(frozen=True)
class BatchTrack:
    number: int
    display_name: str
    source_sha256: str
    chunk_count: int
    chunks_done: int = 0
    state: str = "pending"
    error: str | None = None
    output_sha256: str | None = None
    duration_seconds: float | None = None
    completed_at: str | None = None


@dataclass(frozen=True)
class BatchManifest:
    schema_version: int
    batch_id: str
    name: str
    voice_id: str
    voice_name: str
    language_id: str
    model_revision: str
    source_revision: str
    conditioning_format_version: int
    synthesis_seed: int
    state: str
    message: str | None
    created_at: str
    updated_at: str
    chunk_seconds_total: float
    chunks_done_total: int
    tracks: tuple[BatchTrack, ...]

    @classmethod
    def from_path(cls, path: Path) -> "BatchManifest":
        try:
            raw = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(raw, dict) or set(raw) != set(cls.__dataclass_fields__):
                raise ValueError("Unexpected manifest fields")
            raw_tracks = raw.pop("tracks")
            if not isinstance(raw_tracks, list):
                raise ValueError("Manifest tracks must be a list")
            track_fields = set(BatchTrack.__dataclass_fields__)
            tracks = []
            for item in raw_tracks:
                if not isinstance(item, dict) or set(item) != track_fields:
                    raise ValueError("Unexpected manifest track fields")
                tracks.append(BatchTrack(**item))
            manifest = cls(tracks=tuple(tracks), **raw)
            manifest.validate()
            return manifest
        except (OSError, json.JSONDecodeError, TypeError, ValueError) as error:
            raise BatchManifestError("Batch manifest is invalid") from error

    def validate(self) -> None:
        if self.schema_version != SCHEMA_VERSION:
            raise ValueError("Unsupported manifest schema")
        canonical_id(self.batch_id)
        canonical_id(self.voice_id)
        if not all(isinstance(value, str) and value for value in
                   (self.name, self.voice_name, self.created_at, self.updated_at)):
            raise ValueError("Invalid manifest identity")
        if self.language_id not in ("en", "pt", "sv"):
            raise ValueError("Invalid manifest language")
        for revision in (self.model_revision, self.source_revision):
            _validate_hex(revision, 40)
        for value in (self.conditioning_format_version, self.synthesis_seed):
            if not _is_int(value) or value < 1:
                raise ValueError("Invalid manifest synthesis settings")
        if self.state not in BATCH_STATES:
            raise ValueError("Invalid manifest state")
        if self.message is not None and not isinstance(self.message, str):
            raise ValueError("Invalid manifest message")
        if not _is_number(self.chunk_seconds_total) or self.chunk_seconds_total < 0:
            raise ValueError("Invalid manifest timing")
        if not _is_int(self.chunks_done_total) or self.chunks_done_total < 0:
            raise ValueError("Invalid manifest timing")
        if not self.tracks or len(self.tracks) > 999:
            raise ValueError("Invalid manifest track count")
        for expected, track in enumerate(self.tracks, start=1):
            if not _is_int(track.number) or track.number != expected:
                raise ValueError("Manifest track order is invalid")
            if not isinstance(track.display_name, str) or not track.display_name:
                raise ValueError("Manifest track name is invalid")
            _validate_hex(track.source_sha256, 64)
            if not _is_int(track.chunk_count) or track.chunk_count < 1:
                raise ValueError("Manifest chunk count is invalid")
            if not _is_int(track.chunks_done) or not 0 <= track.chunks_done <= track.chunk_count:
                raise ValueError("Manifest chunk progress is invalid")
            if track.state not in TRACK_STATES:
                raise ValueError("Manifest track state is invalid")
            if track.error is not None and not isinstance(track.error, str):
                raise ValueError("Manifest track error is invalid")
            if track.state == "completed":
                _validate_hex(track.output_sha256, 64)
                if not _is_number(track.duration_seconds) or track.duration_seconds <= 0:
                    raise ValueError("Completed track has invalid duration")
                if not isinstance(track.completed_at, str) or not track.completed_at:
                    raise ValueError("Completed track has no completion time")
            elif any(value is not None for value in
                     (track.output_sha256, track.duration_seconds, track.completed_at)):
                raise ValueError("Unfinished track contains output metadata")


class BatchRepository:
    """All paths are server-generated from canonical UUIDs and checked to stay under the root."""

    def __init__(self, root: Path) -> None:
        root.mkdir(parents=True, exist_ok=True)
        self.root = root.resolve()

    def batch_dir(self, batch_id: str) -> Path:
        directory = (self.root / canonical_id(batch_id)).resolve()
        if directory.parent != self.root:
            raise BatchManifestError("Batch path escaped the batch root")
        return directory

    def manifest_path(self, batch_id: str) -> Path:
        return self.batch_dir(batch_id) / MANIFEST_NAME

    def source_path(self, batch_id: str, number: int) -> Path:
        return self._child(batch_id, "sources", f"{number:03d}.txt")

    def track_path(self, batch_id: str, number: int) -> Path:
        return self._child(batch_id, "tracks", f"{number:03d}.mp3")

    def work_dir(self, batch_id: str, number: int) -> Path:
        return self._child(batch_id, "work", f"{number:03d}")

    def exists(self, batch_id: str) -> bool:
        return self.manifest_path(batch_id).is_file()

    def load(self, batch_id: str) -> BatchManifest:
        path = self.manifest_path(batch_id)
        if not path.is_file():
            raise KeyError("Batch not found")
        manifest = BatchManifest.from_path(path)
        if manifest.batch_id != batch_id:
            raise BatchManifestError("Stored batch ID does not match its directory")
        return manifest

    def save(self, manifest: BatchManifest, directory: Path | None = None) -> None:
        """Atomic temp + fsync + re-validate + replace; a crash leaves the previous or next manifest."""
        manifest.validate()
        directory = directory or self.batch_dir(manifest.batch_id)
        descriptor, name = tempfile.mkstemp(prefix=".manifest-", suffix=".json", dir=directory)
        temporary = Path(name)
        try:
            with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
                json.dump(asdict(manifest), stream, indent=2, sort_keys=True, ensure_ascii=False)
                stream.write("\n")
                stream.flush()
                os.fsync(stream.fileno())
            BatchManifest.from_path(temporary)
            os.replace(temporary, directory / MANIFEST_NAME)
        finally:
            temporary.unlink(missing_ok=True)

    def list(self) -> list[BatchManifest]:
        manifests = []
        for candidate in self.root.iterdir():
            try:
                canonical_id(candidate.name)
            except ValueError:
                continue
            try:
                manifests.append(self.load(candidate.name))
            except (KeyError, BatchManifestError):
                LOGGER.warning("Skipping unreadable batch %s", candidate.name)
        return sorted(manifests, key=lambda manifest: manifest.created_at, reverse=True)

    def delete(self, batch_id: str) -> None:
        directory = self.batch_dir(batch_id)
        if not directory.is_dir():
            raise KeyError("Batch not found")
        shutil.rmtree(directory)

    def _child(self, batch_id: str, folder: str, name: str) -> Path:
        directory = self.batch_dir(batch_id) / folder
        path = (directory / name).resolve()
        if path.parent != directory.resolve():
            raise BatchManifestError("Batch file escaped its batch directory")
        return path


def _is_int(value) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def _is_number(value) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _validate_hex(value, length: int) -> None:
    if not isinstance(value, str) or len(value) != length or any(
        character not in "0123456789abcdef" for character in value
    ):
        raise ValueError("Manifest checksum or revision is invalid")
